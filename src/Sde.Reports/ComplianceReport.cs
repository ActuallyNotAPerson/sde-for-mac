using System.Globalization;
using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports.Internal;
using Column = Sde.Reports.Internal.Column;

namespace Sde.Reports;

/// <summary>One compliance case with the structure it is about, as the compliance list, PDF and CSV show it.</summary>
public sealed class ComplianceRow
{
	public ComplianceCaseRecord Case { get; init; }

	public string Address { get; init; }

	public string City { get; init; }

	public string ParcelNumber { get; init; }

	public string Owner { get; init; }

	/// <summary>Open with a deadline before the report date.</summary>
	public bool IsOverdue { get; init; }

	/// <summary>Days past the deadline (0 when not overdue).</summary>
	public int DaysOverdue { get; init; }
}

/// <summary>What to print on the compliance report besides the cases.</summary>
public sealed class ComplianceReportOptions
{
	/// <summary>Shown under the title and in the footer.</summary>
	public string CommunityName { get; init; }

	/// <summary>The event the cases are for, if the list was limited to one.</summary>
	public string EventName { get; init; }

	/// <summary>Date printed in the footer; default DateTime.Now.</summary>
	public DateTime? GeneratedOn { get; init; }
}

/// <summary>
/// The compliance and ICC tracker as a landscape PDF (headline counts, then one line per case, overdue ones marked)
/// and as a CSV for a spreadsheet. Same fonts and styling as the other reports.
/// </summary>
public static class ComplianceReport
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public static readonly string[] CsvHeaders =
	{
		"Address", "City", "Parcel number", "Owner", "Kind", "Path", "Permit number", "Permit issued", "Deadline", "Days overdue",
		"ICC claim number", "ICC status", "ICC amount", "EC received", "Final inspection", "Certificate of compliance", "Status",
		"Closed on", "Closed reason", "Notes",
	};

	/// <summary>
	/// The cases with their structures' addresses and owners, open ones first by deadline. <paramref name="cases"/>
	/// limits the list (for example to one event); null means every case.
	/// </summary>
	public static List<ComplianceRow> Rows(SdeStore store, DateTime today, IEnumerable<ComplianceCaseRecord> cases = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		List<ComplianceCaseRecord> list = (cases ?? store.GetComplianceCases()).ToList();
		Dictionary<Guid, PropertyRecord> properties = store.GetProperties(activeOnly: false).ToDictionary(p => p.PropertyID);
		List<ComplianceRow> rows = new();
		foreach (ComplianceCaseRecord c in list)
		{
			properties.TryGetValue(c.PropertyID, out PropertyRecord p);
			AssessmentView v = p == null ? null : new AssessmentView(new AssessmentFile { Property = p, Assessment = new AssessmentRecord() }, store.Reference);
			bool overdue = c.IsOverdue(today);
			rows.Add(new ComplianceRow
			{
				Case = c,
				Address = v?.StreetAddress ?? "",
				City = v?.City?.Trim() ?? "",
				ParcelNumber = p?.ParcelNumber?.Trim() ?? "",
				Owner = p == null ? "" : Fmt.Join(" ", p.OwnerFirstName, p.OwnerLastName),
				IsOverdue = overdue,
				DaysOverdue = overdue ? (today.Date - c.Deadline.Value.Date).Days : 0,
			});
		}
		return rows
			.OrderBy(r => r.Case.IsOpen ? 0 : 1)
			.ThenBy(r => r.Case.Deadline ?? DateTime.MaxValue)
			.ThenBy(r => r.Address, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	/// <summary>Writes the CSV (UTF-8 with a byte-order mark, for Excel). Dates are yyyy-MM-dd, money a plain number.</summary>
	public static void WriteCsv(IReadOnlyList<ComplianceRow> rows, string path)
	{
		ArgumentNullException.ThrowIfNull(rows);
		ArgumentException.ThrowIfNullOrEmpty(path);
		StringBuilder sb = new StringBuilder();
		ResultsCsv.Line(sb, CsvHeaders);
		foreach (ComplianceRow r in rows)
		{
			ComplianceCaseRecord c = r.Case;
			ResultsCsv.Line(sb, new[]
			{
				r.Address, r.City, r.ParcelNumber, r.Owner, c.Kind ?? "", c.Path == null ? "" : CompliancePaths.Label(c.Path),
				c.PermitNumber ?? "", IsoDate(c.PermitIssuedOn), IsoDate(c.Deadline), r.IsOverdue ? r.DaysOverdue.ToString(CultureInfo.InvariantCulture) : "",
				c.IccClaimNumber ?? "", IccStatuses.Label(c.IccStatus), c.IccAmount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
				IsoDate(c.EcReceivedOn), IsoDate(c.FinalInspectionOn), IsoDate(c.ComplianceCertOn), c.Status ?? ComplianceStatuses.Open,
				IsoDate(c.ClosedOn), c.ClosedReason ?? "", c.Notes ?? "",
			});
		}
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
	}

	/// <summary>Writes the PDF (overwriting it). The file is only replaced once rendering succeeded.</summary>
	public static void RenderToFile(IReadOnlyList<ComplianceRow> rows, ComplianceReportOptions options, string path)
	{
		ArgumentNullException.ThrowIfNull(rows);
		ArgumentException.ThrowIfNullOrEmpty(path);
		options ??= new ComplianceReportOptions();
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		Document doc = Build(rows, options);
		PdfDocumentRenderer renderer = new PdfDocumentRenderer { Document = doc };
		renderer.RenderDocument();
		using MemoryStream buffer = new MemoryStream();
		renderer.PdfDocument.Save(buffer, false);
		File.WriteAllBytes(path, buffer.ToArray());
	}

	private static Document Build(IReadOnlyList<ComplianceRow> rows, ComplianceReportOptions o)
	{
		const string title = "Compliance and ICC";
		DateTime printed = o.GeneratedOn ?? DateTime.Now;
		Document doc = Theme.NewDocument(title, $"{rows.Count} case(s)");
		string community = string.IsNullOrWhiteSpace(o.CommunityName) ? null : o.CommunityName.Trim();
		Section sec = Kit.NewSection(doc, landscape: true, title, printed, community, restartPageNumbers: false);

		Paragraph heading = sec.AddParagraph(title);
		heading.Format.Font.Size = 18;
		heading.Format.Font.Bold = true;
		heading.Format.Font.Color = Theme.Accent;
		List<string> sub = new();
		if (community != null)
		{
			sub.Add(community);
		}
		if (!string.IsNullOrWhiteSpace(o.EventName))
		{
			sub.Add(o.EventName.Trim());
		}
		sub.Add("As of " + printed.ToString("MMMM d, yyyy", Us));
		Paragraph s = sec.AddParagraph(string.Join("   |   ", sub));
		s.Style = Theme.NoteStyle;
		s.Format.Font.Size = 8.5;
		s.Format.SpaceBefore = 2;
		Kit.Gap(sec, 10);

		Headline(sec, rows);
		Kit.Gap(sec, 10);

		List<ComplianceRow> open = rows.Where(r => r.Case.IsOpen).ToList();
		List<ComplianceRow> closed = rows.Where(r => !r.Case.IsOpen).ToList();
		Column[] columns =
		{
			new Column("Address", 1.9), new Column("Owner", 1.3), new Column("Kind", 0.45), new Column("Path", 1.25),
			new Column("Permit", 0.85), new Column("Deadline", 1.0), new Column("ICC", 0.85), new Column("ICC amount", 0.85, ParagraphAlignment.Right),
			new Column("EC", 0.75), new Column("Final insp.", 0.8),
		};
		Kit.Data(sec, "Open cases", columns, open.Select(Cells));
		if (open.Any(r => r.IsOverdue))
		{
			Kit.Note(sec, "Overdue: the deadline has passed and the case is still open.");
		}
		if (closed.Count > 0)
		{
			Kit.Gap(sec, 10);
			Kit.Data(sec, "Closed cases", columns, closed.Select(Cells));
		}
		return doc;
	}

	private static string[] Cells(ComplianceRow r)
	{
		ComplianceCaseRecord c = r.Case;
		string deadline = ShortDate(c.Deadline);
		if (r.IsOverdue)
		{
			deadline += $" (overdue {r.DaysOverdue} d)";
		}
		return new[]
		{
			Fmt.Join(", ", r.Address, r.City),
			r.Owner,
			c.Kind ?? "",
			c.Path == null ? "Not chosen" : CompliancePaths.Label(c.Path),
			c.PermitNumber ?? "",
			deadline,
			IccStatuses.Label(c.IccStatus),
			c.IccAmount is decimal m ? m.ToString("C0", Us) : "",
			ShortDate(c.EcReceivedOn),
			ShortDate(c.FinalInspectionOn),
		};
	}

	private static void Headline(Section sec, IReadOnlyList<ComplianceRow> rows)
	{
		int open = rows.Count(r => r.Case.IsOpen);
		int overdue = rows.Count(r => r.IsOverdue);
		int iccOpen = rows.Count(r => r.Case.IccStatus is IccStatuses.Filed or IccStatuses.Approved);
		decimal iccPaid = rows.Where(r => r.Case.IccStatus == IccStatuses.Paid).Sum(r => r.Case.IccAmount ?? 0m);
		(string Value, string Label, string Note)[] tiles =
		{
			(open.ToString("N0", Us), "Open cases", $"{rows.Count - open:N0} closed"),
			(overdue.ToString("N0", Us), "Overdue", "deadline passed, still open"),
			(iccOpen.ToString("N0", Us), "ICC claims in progress", "filed or approved, not paid"),
			(iccPaid.ToString("C0", Us), "ICC paid", $"{rows.Count(r => r.Case.IccStatus == IccStatuses.Paid):N0} claim(s)"),
		};
		double w = Kit.LandscapeWidth / tiles.Length;
		Table t = Kit.NewTable(sec, tiles.Select(_ => w));
		Row numbers = t.AddRow();
		Row labels = t.AddRow();
		Row notes = t.AddRow();
		for (int i = 0; i < tiles.Length; i++)
		{
			Paragraph n = numbers.Cells[i].AddParagraph(tiles[i].Value);
			n.Format.Font.Size = 17;
			n.Format.Font.Bold = true;
			n.Format.Font.Color = i == 1 && overdue > 0 ? Theme.SubstantialInk : Theme.Accent;
			Paragraph l = labels.Cells[i].AddParagraph(tiles[i].Label);
			l.Style = Theme.LabelStyle;
			l.Format.Font.Bold = true;
			Paragraph d = notes.Cells[i].AddParagraph(tiles[i].Note);
			d.Style = Theme.NoteStyle;
			foreach (Row r in new[] { numbers, labels, notes })
			{
				r.Cells[i].Shading.Color = Theme.Faint;
			}
		}
		t.Rows.LeftIndent = 0;
	}

	private static string ShortDate(DateTime? d) => d is DateTime x ? x.ToString("MM/dd/yyyy", Us) : "";

	private static string IsoDate(DateTime? d) => d is DateTime x ? x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
}
