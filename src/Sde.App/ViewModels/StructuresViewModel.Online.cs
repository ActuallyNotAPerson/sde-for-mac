using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Interop.Online;

namespace Sde.App.ViewModels;

/// <summary>Change, then Look up flood data online, for the checked structures.</summary>
public sealed partial class StructuresViewModel
{
	/// <summary>How many structures are looked up at once, and how long each may take (with one retry).</summary>
	public const int LookupsAtOnce = 4;

	public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);

	/// <summary>
	/// Looks up coordinates (when missing) and flood data for each checked structure, a few at a time with Stop, then
	/// shows everything found for review. Nothing is saved until the review's Apply.
	/// </summary>
	[RelayCommand]
	private async Task LookUpFloodData()
	{
		IReadOnlyList<Guid> ids = CheckedAssessmentIds();
		if (ids.Count == 0)
		{
			_app.Dialogs.Notify("Nothing checked", "Check the structures to look up first.");
			return;
		}
		// One look-up per structure (its fields are shared by all its assessments).
		List<AssessmentFile> structures = _app.Store.LoadAssessments(ids).GroupBy(f => f.Property.PropertyID).Select(g => g.First()).ToList();
		OnlineLookup lookup = new OnlineLookup(_app.HttpClientFactory(), _app.Store.Reference) { Timeout = LookupTimeout };
		JobOutcome<IReadOnlyList<BatchResult<AssessmentFile, OnlineLookupResult>>> outcome = await _app.Jobs.RunTaskAsync(
			"Looking up flood data online",
			(progress, ct) =>
			{
				Progress<int> done = new Progress<int>(n => progress.Report(new JobProgress(n, structures.Count, $"{n} of {structures.Count} structures")));
				return OnlineBatch.RunAsync(structures, (f, token) => lookup.LookupAsync(f.Property, token), LookupsAtOnce, LookupTimeout, 1, done, ct);
			},
			"Couldn't look up flood data");
		if (!outcome.Completed)
		{
			return;
		}
		List<StructureProposalsViewModel> found = outcome.Value.Select(r => new StructureProposalsViewModel(
			r.Input.Assessment.AssessmentID,
			r.Input.Property,
			AssessmentEditorViewModel.Address(r.Input.Property, _app.Store.Reference),
			r.Succeeded ? r.Value : null,
			r.Succeeded ? null : r.ErrorMessage,
			_app.Store.Reference)).ToList();
		await _app.Sheets.ShowAsync(new BulkAutoFillSheetViewModel(_app, found));
	}
}
