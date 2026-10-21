using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Reports.Internal;

namespace Sde.Reports;

/// <summary>
/// Renders SDE's reports to PDF. Replaces the Windows-only Microsoft ReportViewer + 12 RDLC layouts
/// (extracted/Reports/*.rdlc) fed by BLL ReportsService (decomp/BLL/BLL.Service/ReportsService.cs).
/// <para>
/// The original searched the database itself (inspection date, % range, owner/address/… filters); here the caller
/// passes the assessments to print, in the order to print them. Filtering belongs to the UI.
/// </para>
/// </summary>
public static class ReportGenerator
{
	/// <summary>Writes the report as PDF to <paramref name="pdfOutput"/> (left open).</summary>
	public static void Render(ReportRequest request, ReferenceData reference, Stream pdfOutput)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(reference);
		ArgumentNullException.ThrowIfNull(pdfOutput);

		Document doc = Build(request, reference);
		PdfDocumentRenderer renderer = new PdfDocumentRenderer { Document = doc };
		renderer.RenderDocument();
		renderer.PdfDocument.Save(pdfOutput, false);
	}

	/// <summary>Writes the report to a PDF file (overwriting it). The file is only replaced once rendering succeeded.</summary>
	public static void RenderToFile(ReportRequest request, ReferenceData reference, string path)
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

	/// <summary>The report title (PDF metadata; also a sensible default file name).</summary>
	public static string Title(ReportKind kind) => kind switch
	{
		ReportKind.Summary => "Substantial Damage Summary Report",
		ReportKind.Detail => "Substantial Damage Detail Report",
		ReportKind.PercentDamaged => "SDE Structure and Percent Damage Report",
		ReportKind.Community => "SDE Community Report",
		_ => throw new ArgumentOutOfRangeException(nameof(kind)),
	};

	internal static Document Build(ReportRequest request, ReferenceData reference)
	{
		DateTime printed = request.GeneratedOn ?? DateTime.Now;
		List<AssessmentView> items = (request.Assessments ?? Array.Empty<AssessmentFile>())
			.Where(f => f != null)
			.Select(f => new AssessmentView(f, reference))
			.ToList();
		PhotoLibrary photos = new PhotoLibrary(request.PhotosFolder, request.PhotoMarkups);
		Document doc = Theme.NewDocument(Title(request.Kind), $"{items.Count} assessment(s)");

		if (items.Count == 0)
		{
			Section sec = Kit.NewSection(doc, landscape: request.Kind is ReportKind.PercentDamaged or ReportKind.Community, Title(request.Kind), printed, null, false);
			Kit.Note(sec, "No assessments to report.", 12);
			return doc;
		}

		switch (request.Kind)
		{
			case ReportKind.Summary:
				SummaryReport.Build(doc, items, photos, request.IncludePhotos, printed);
				break;
			case ReportKind.Detail:
				DetailReport.Build(doc, items, photos, request.IncludePhotos, printed);
				break;
			case ReportKind.PercentDamaged:
				ListReports.BuildPercentDamaged(doc, items, photos, request.IncludePhotos, printed);
				break;
			case ReportKind.Community:
				ListReports.BuildCommunity(doc, items, photos, request.IncludePhotos, printed);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown report kind");
		}
		return doc;
	}
}
