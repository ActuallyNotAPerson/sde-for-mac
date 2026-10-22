using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Sheets;
using Sde.Data;
using Sde.Interop.ElevationCertificates;

namespace Sde.App.ViewModels.Editor;

/// <summary>
/// Field data in: elevation certificates (C11), read into this assessment with the PDF kept with the structure, and the
/// flood depth from high-water marks or a water-surface grid (C4).
/// </summary>
public sealed partial class AssessmentEditorViewModel
{
	/// <summary>
	/// Flood zone step (or ⋯), then Elevation certificate: choose the PDF, tick the values to use (or type them when SDE
	/// can't read it), and the PDF is kept with the structure. A new assessment is saved first.
	/// </summary>
	[RelayCommand]
	private async Task ImportElevationCertificate()
	{
		IReadOnlyList<string> picked = await _app.Dialogs.PickFilesAsync("Choose the elevation certificate", false, "PDF document", "*.pdf");
		if (picked == null || picked.Count == 0)
		{
			return;
		}
		if (IsDraft)
		{
			_dirty = true;
		}
		Save();
		if (IsDraft || SaveState != "Saved")
		{
			return;
		}
		Guid id = Assessment.AssessmentID;
		if (await _app.Sheets.ShowAsync(new EcImportSheetViewModel(_app, id, picked[0])) is EcImportResult result)
		{
			_app.Dialogs.Notify("Elevation certificate kept", result.Changed == 0
				? "The PDF is kept with the structure; no values changed."
				: $"{result.Changed} value{(result.Changed == 1 ? "" : "s")} changed, and the PDF is kept with the structure. History lists them as an EC import.");
		}
	}

	/// <summary>⋯, then Flood depth from high-water marks, for this assessment (a new one is saved first).</summary>
	[RelayCommand]
	private async Task FloodDepth()
	{
		if (IsDraft)
		{
			_dirty = true;
		}
		Save();
		if (IsDraft || SaveState != "Saved")
		{
			return;
		}
		if (await _app.Sheets.ShowAsync(new DepthSheetViewModel(_app, new[] { Assessment.AssessmentID })) is DepthApplyResult result && result.Changed > 0)
		{
			_app.Dialogs.Notify("Flood depth saved", "History lists the change as the depth tool's.");
		}
	}
}
