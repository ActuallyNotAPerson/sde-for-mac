using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Reports.Internal;
using Column = Sde.Reports.Internal.Column;

namespace Sde.Reports;

/// <summary>What goes on a substantial improvement determination report.</summary>
public sealed class SiDeterminationRequest
{
	/// <summary>The structure (address, owner, parcel, NFIP community and flood zone are printed).</summary>
	public PropertyRecord Property { get; init; }

	/// <summary>The project being determined (it is also the first line of <see cref="Result"/>).</summary>
	public ImprovementProjectRecord Project { get; init; }

	/// <summary>The calculation from <see cref="ImprovementCalculator.Evaluate"/>.</summary>
	public SiResult Result { get; init; }

	/// <summary>Where the market value came from, e.g. "Adjusted tax assessed value" or "Professional appraisal".</summary>
	public string MarketValueBasis { get; init; }

	/// <summary>The official who made the determination (printed under the signature line when given).</summary>
	public string DeterminedBy { get; init; }

	/// <summary>The date of the determination (default: the print date).</summary>
	public DateTime? DeterminedOn { get; init; }

	public string Notes { get; init; }

	/// <summary>The time printed in the footer (default: now).</summary>
	public DateTime? GeneratedOn { get; init; }
}

/// <summary>
/// A one or two page substantial improvement (SI) determination for one structure, laid out like the SI worksheets
/// in FEMA P-758: the property, the improvement projects counted (and those not counted, with the reason), the
/// cost, the market value and its basis, the percentage, the determination and signature lines.
/// It reports an SI determination only; the structure's substantial damage determination is not changed.
/// </summary>
public static class SiDeterminationReport
{
	public const string Title = "Substantial Improvement Determination";

	/// <summary>Writes the report as PDF to <paramref name="pdfOutput"/> (left open).</summary>
	public static void Render(SiDeterminationRequest request, ReferenceData reference, Stream pdfOutput)
	{
		ArgumentNullException.ThrowIfNull(pdfOutput);
		Document doc = Build(request, reference);
		PdfDocumentRenderer renderer = new PdfDocumentRenderer { Document = doc };
		renderer.RenderDocument();
		renderer.PdfDocument.Save(pdfOutput, false);
	}

	/// <summary>Writes the report to a PDF file (overwriting it). The file is only replaced once rendering succeeded.</summary>
	public static void RenderToFile(SiDeterminationRequest request, ReferenceData reference, string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		using MemoryStream buffer = new MemoryStream();
		Render(request, reference, buffer);
		File.WriteAllBytes(path, buffer.ToArray());
	}

	internal static Document Build(SiDeterminationRequest request, ReferenceData reference)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(reference);
		if (request.Property == null || request.Result == null)
		{
			throw new ArgumentException("The report needs the structure and the SI calculation.", nameof(request));
		}
		DateTime printed = request.GeneratedOn ?? DateTime.Now;
		DateTime determinedOn = request.DeterminedOn ?? printed;
		SiResult r = request.Result;
		AssessmentView v = new AssessmentView(new AssessmentFile { Property = request.Property, Assessment = new AssessmentRecord() }, reference);
		ImprovementProjectRecord project = request.Project ?? r.Projects.FirstOrDefault(p => p.IsCurrent)?.Project;

		Document doc = Theme.NewDocument(Title, v.StreetAddress);
		Section sec = Kit.NewSection(doc, landscape: false, Title, printed, footerCenter: null, restartPageNumbers: false);

		Hero(sec, v, r, project);
		Kit.Gap(sec, 2);

		Kit.Panels(sec,
			new Panel("Structure", new[]
			{
				new Field("Owner's Name", v.OwnerName),
				new Field("Street Address", v.StreetAddress),
				new Field("City, State Zip", v.CityStateZip),
				new Field("County/Parish", v.County),
				new Field("Parcel #", v.P.ParcelNumber),
			}, 1.25, 2.25),
			new Panel("Community", new[]
			{
				new Field("NFIP Community Name", v.P.NFIPCommunityName),
				new Field("NFIP Community ID #", v.P.NFIPCID),
				new Field("Firm Zone", v.Name(v.P.FIRMZone)),
				new Field("BFE", Fmt.Number2(v.P.BFE)),
				new Field("Year of Construction", v.P.YearOfConstruction?.ToString()),
			}, 1.25, 2.25));

		double[] pair = { 1.55, 1.95, 1.25, 2.25 };
		Kit.Fields(sec, "Proposed Improvement", pair, new[]
		{
			new[] { new Field("Permit #", project?.PermitNumber), new Field("Project Date", Fmt.Date(project?.ProjectDate)) },
			new[] { new Field("Cost", Fmt.Money(project?.Cost)), new Field("Basis of Cost", project?.CostBasis) },
			new[] { new Field("Description", project?.Description, Wide: true) },
		});

		Column[] columns =
		{
			new Column("Permit #", 1.0),
			new Column("Description", 2.15),
			new Column("Date", 0.75),
			new Column("Cost", 0.95, ParagraphAlignment.Right),
			new Column("Counted", 2.45),
		};
		IEnumerable<string[]> rows = r.Projects.Select(p => new[]
		{
			p.Project.PermitNumber ?? "",
			p.Project.Description ?? "",
			Fmt.Date(p.Project.ProjectDate),
			Fmt.Money(p.Project.Cost),
			p.Counted ? (p.IsCurrent ? "Yes (this project)" : "Yes") : p.Reason,
		});
		Kit.Data(sec, LookbackTitle(r), columns, rows, new[] { new[] { "Total counted", "", "", Fmt.Money(r.CostCounted), "" } });

		Kit.Fields(sec, "Determination", pair, new[]
		{
			new[] { new Field("Improvement Cost Counted", Fmt.Money(r.CostCounted)), new Field("Threshold", Pct(r.ThresholdPercent)) },
			new[] { new Field("Market Value of Structure", r.MarketValue is decimal mv ? Fmt.Money(mv) : AssessmentView.NotProvided), new Field("Look-back Period", LookbackText(r)) },
			new[] { new Field("Basis of Market Value", request.MarketValueBasis), new Field("Date of Determination", Fmt.Date(determinedOn)) },
			new[] { new Field("Cost / Market Value", r.Percent is decimal pct ? Fmt.Percent1(pct) : "Cannot be worked out"), new Field("Determination", r.ResultText) },
		});
		Kit.Note(sec, "Market value is the value of the structure only, not the land, before the improvement. A substantial improvement is any "
			+ "reconstruction, rehabilitation, addition or other improvement whose cost equals or exceeds the threshold percentage of that value.");

		Explanation(sec, r);
		if (!string.IsNullOrWhiteSpace(request.Notes))
		{
			Kit.Fields(sec, "Notes", new[] { 1.25, 6.05 }, new[] { new[] { new Field("Notes", request.Notes) } });
		}

		Kit.Gap(sec, 4);
		Kit.Signature(sec, detail: true);
		if (!string.IsNullOrWhiteSpace(request.DeterminedBy))
		{
			Kit.Note(sec, "Determined by: " + request.DeterminedBy.Trim(), 2);
		}
		return doc;
	}

	/// <summary>The address on the left; the percentage and determination in a colored panel on the right.</summary>
	private static void Hero(Section sec, AssessmentView v, SiResult r, ImprovementProjectRecord project)
	{
		Table t = Kit.NewTable(sec, new[] { 3.6, 0.15, 3.55 });
		Row row = t.AddRow();
		row.VerticalAlignment = VerticalAlignment.Center;
		row.TopPadding = Unit.FromPoint(8);
		row.BottomPadding = Unit.FromPoint(8);

		Cell a = row.Cells[0];
		Paragraph street = a.AddParagraph(string.IsNullOrEmpty(v.StreetAddress) ? "(No street address)" : v.StreetAddress);
		street.Format.Font.Size = 15;
		street.Format.Font.Bold = true;
		Paragraph city = a.AddParagraph(v.CityStateZip);
		city.Format.Font.Size = 9.5;
		city.Format.SpaceBefore = 1;
		Paragraph meta = a.AddParagraph(Fmt.Join(": ", string.IsNullOrEmpty(project?.PermitNumber) ? null : "Permit " + project.PermitNumber, project?.Description));
		meta.Style = Theme.LabelStyle;
		meta.Format.SpaceBefore = 6;

		Determination tone = !r.CanDetermine ? Determination.Other : r.IsSubstantialImprovement ? Determination.Substantial : Determination.NotSubstantial;
		Color ink = Theme.ToneInk(tone);
		Cell d = row.Cells[2];
		d.Shading.Color = Theme.ToneFill(tone);
		d.Borders.Left.Width = 3;
		d.Borders.Left.Color = ink;
		Paragraph cap = d.AddParagraph("Improvement Cost / Market Value");
		cap.Format.Font.Size = 7;
		cap.Format.Font.Bold = true;
		cap.Format.Font.Color = ink;
		Paragraph pct = d.AddParagraph(r.Percent is decimal p ? Fmt.Percent1(p) : "Not available");
		pct.Format.Font.Size = r.Percent is null ? 14 : 24;
		pct.Format.Font.Bold = true;
		pct.Format.Font.Color = ink;
		Paragraph msg = d.AddParagraph(r.ResultText);
		msg.Format.Font.Size = 9;
		msg.Format.Font.Bold = true;
		msg.Format.Font.Color = ink;
		foreach (Paragraph para in d.Elements.OfType<Paragraph>())
		{
			para.Format.LeftIndent = Unit.FromPoint(10);
		}
	}

	private static void Explanation(Section sec, SiResult r)
	{
		Table t = Kit.NewTable(sec, new[] { Kit.PortraitWidth });
		Kit.TitleCell(t.AddRow(), 0, 1, "How This Was Worked Out");
		foreach (string line in r.Explanation)
		{
			Row row = t.AddRow();
			Kit.ValueCell(row.Cells[0], line, rule: false);
		}
		t.Rows[0].KeepWith = t.Rows.Count - 1;
	}

	private static string LookbackTitle(SiResult r) => r.LookbackYears > 0 ? "Improvement Projects in the Look-back Period" : "Improvement Projects";

	private static string LookbackText(SiResult r) => r.LookbackYears switch
	{
		0 => "None (this project only)",
		1 => "1 year" + (r.WindowStart is DateTime s1 ? ", from " + Fmt.Date(s1) : ""),
		_ => r.LookbackYears + " years" + (r.WindowStart is DateTime s ? ", from " + Fmt.Date(s) : ""),
	};

	private static string Pct(decimal d) => d.ToString("0.##", Fmt.Us) + "%";
}
