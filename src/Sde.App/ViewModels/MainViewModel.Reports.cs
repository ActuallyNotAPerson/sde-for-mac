using Sde.App.ViewModels.Editor;
using Sde.Core.Assessments;
using Sde.Reports;

namespace Sde.App.ViewModels;

public sealed partial class MainViewModel
{
	/// <summary>
	/// Renders one of FEMA's reports to a PDF (via the save dialog) and opens it in Preview, where it can be printed.
	/// <paramref name="kind"/> is a ReportKind name, optionally with "-NoPhotos" (the original's "without photos" layouts).
	/// </summary>
	private async Task PrintReportAsync(string kind, IReadOnlyList<Guid> assessmentIds)
	{
		if (assessmentIds.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to print", "There are no assessments in the list. Add a structure, or clear the search.");
			return;
		}
		bool noPhotos = kind.EndsWith("-NoPhotos", StringComparison.Ordinal);
		ReportKind reportKind = Enum.Parse<ReportKind>(noPhotos ? kind[..^"-NoPhotos".Length] : kind);

		string suggested = ReportFileName(reportKind, assessmentIds);
		string path = await _app.Dialogs.SaveFileAsync("Save the report", suggested, "PDF document", "pdf");
		if (path == null)
		{
			return;
		}

		Editor?.Save(); // print what's on screen, not the last autosave
		try
		{
			await Task.Run(() =>
			{
				// Print in the order the user sees (the store returns them in database order).
				Dictionary<Guid, int> order = assessmentIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
				List<AssessmentFile> files = _app.Store.LoadAssessments(assessmentIds).OrderBy(f => order[f.Assessment.AssessmentID]).ToList();
				ReportRequest request = new ReportRequest
				{
					Kind = reportKind,
					Assessments = files,
					IncludePhotos = !noPhotos,
					PhotosFolder = _app.Store.AttachmentsFolder,
					// Photos print with their markup (water lines, arrows, labels); the photo files never change.
					PhotoMarkups = noPhotos ? null : files.SelectMany(f => _app.Store.GetPhotoMarkups(f.Assessment.AssessmentID))
						.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value),
				};
				ReportGenerator.RenderToFile(request, _app.Store.Reference, path);
			});
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't create the report", ex);
			return;
		}
		_app.Dialogs.Open(path);
	}

	/// <summary>e.g. "Summary report – 123 Main St.pdf" for one assessment, "Community report – 12 structures.pdf" for many.</summary>
	private string ReportFileName(ReportKind kind, IReadOnlyList<Guid> ids)
	{
		string name = kind switch
		{
			ReportKind.Summary => "Summary report",
			ReportKind.Detail => "Detailed report",
			ReportKind.PercentDamaged => "Percent damaged list",
			_ => "Community report",
		};
		string what = ids.Count == 1 && _app.Store.LoadAssessment(ids[0]) is AssessmentFile f && AssessmentEditorViewModel.Address(f.Property, _app.Store.Reference, empty: null) is string a
			? a
			: $"{ids.Count} assessment{(ids.Count == 1 ? "" : "s")}";
		string file = $"{name} – {what}.pdf";
		return string.Concat(file.Split(Path.GetInvalidFileNameChars())).Replace(':', '-');
	}
}
