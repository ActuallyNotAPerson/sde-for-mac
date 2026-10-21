using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Reports;

namespace Sde.App.ViewModels;

/// <summary>
/// Export ▾ → Submission folder on the Structures page: a panel with the options, then a progress bar while the
/// folder is written (one PDF per assessment, the two lists, the FEMA SDE package and results.csv). Wraps
/// Sde.Reports.SubmissionFolder.
/// </summary>
public sealed partial class SubmissionViewModel(AppServices app, MainViewModel main) : ObservableObject
{
	private IReadOnlyList<Guid> _ids = Array.Empty<Guid>();
	private int _ready;
	private int _incomplete;
	private CancellationTokenSource _cancel;

	[ObservableProperty]
	public partial bool IsOpen { get; set; }

	/// <summary>0 = Summary report, 1 = Detailed report.</summary>
	[ObservableProperty]
	public partial int ReportIndex { get; set; }

	[ObservableProperty]
	public partial bool IncludePhotos { get; set; } = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanWrite), nameof(Count))]
	public partial bool IncludeIncomplete { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanWrite), nameof(CancelText))]
	public partial bool IsWriting { get; set; }

	/// <summary>0–100.</summary>
	[ObservableProperty]
	public partial double ProgressValue { get; set; }

	[ObservableProperty]
	public partial string ProgressText { get; set; }

	/// <summary>"3 assessments: 2 ready, 1 incomplete (left out)".</summary>
	public string Count
	{
		get
		{
			int total = _ready + _incomplete;
			string head = total == 1 ? "1 assessment" : $"{total} assessments";
			if (_incomplete == 0)
			{
				return head;
			}
			return $"{head}: {_ready} ready, {_incomplete} incomplete" + (IncludeIncomplete ? " (included)" : " (left out)");
		}
	}

	public bool CanWrite => !IsWriting && (_ready > 0 || (IncludeIncomplete && _incomplete > 0));

	public string CancelText => IsWriting ? "Stop" : "Close";

	/// <summary>Opens the panel for these assessments (the checked ones, or the whole list).</summary>
	public void Open(IReadOnlyList<Guid> assessmentIds, IEnumerable<StructureRowViewModel> rows)
	{
		_ids = assessmentIds;
		HashSet<Guid> ids = assessmentIds.ToHashSet();
		List<StructureRowViewModel> chosen = rows.Where(r => r.AssessmentID is Guid id && ids.Contains(id)).ToList();
		_incomplete = chosen.Count(r => r.Summary.DamageMessage is null or "" or "Data Entry Incomplete");
		_ready = chosen.Count - _incomplete;
		ProgressText = null;
		ProgressValue = 0;
		IsOpen = true;
		OnPropertyChanged(nameof(Count));
		OnPropertyChanged(nameof(CanWrite));
	}

	[RelayCommand]
	private async Task Write()
	{
		if (!CanWrite)
		{
			return;
		}
		if (!await SampleWarning.ConfirmAsync(app, "a submission folder"))
		{
			return;
		}
		string parent = await app.Dialogs.PickFolderAsync("Choose where to put the submission folder");
		if (parent == null)
		{
			return;
		}
		main.Editor?.Save(); // write what's on screen, not the last autosave
		if (!await ExportWarnings.ConfirmNotFinalAsync(app, _ids.ToList()))
		{
			return;
		}
		SubmissionOptions options = new SubmissionOptions
		{
			PerAssessmentReport = ReportIndex == 1 ? ReportKind.Detail : ReportKind.Summary,
			IncludePhotos = IncludePhotos,
			IncludeIncomplete = IncludeIncomplete,
		};
		_cancel = new CancellationTokenSource();
		CancellationToken token = _cancel.Token;
		Progress<SubmissionProgress> progress = new(p =>
		{
			ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
			ProgressText = p.Step;
		});
		IsWriting = true;
		ProgressText = "Starting…";
		IReadOnlyList<Guid> ids = _ids;
		SubmissionResult result;
		try
		{
			result = await Task.Run(() => SubmissionFolder.Write(app.Store, ids, parent, options, progress, token));
		}
		catch (OperationCanceledException)
		{
			ProgressText = "Stopped. Nothing was left behind.";
			return;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			app.Dialogs.Notify("Couldn't write the submission folder", ex.Message, isError: true);
			ProgressText = null;
			return;
		}
		finally
		{
			IsWriting = false;
			_cancel.Dispose();
			_cancel = null;
		}
		app.Settings.LastExportFolder = parent;
		app.Settings.Save();
		IsOpen = false;
		string message = $"{result.Assessments} report{(result.Assessments == 1 ? "" : "s")}, the percent damaged list, the community report, the FEMA SDE package (SDE_1) and results.csv are in “{Path.GetFileName(result.Folder)}”."
			+ (result.Skipped.Count > 0 ? $"\n\nLeft out because they're incomplete: {string.Join(", ", result.Skipped.Take(5))}{(result.Skipped.Count > 5 ? $" and {result.Skipped.Count - 5} more" : "")}." : "")
			+ (result.MissingPhotos.Count > 0 ? $"\n\n{result.MissingPhotos.Count} photo file{(result.MissingPhotos.Count == 1 ? " was" : "s were")} missing." : "");
		app.Dialogs.Notify("Submission folder written", message);
		app.Dialogs.Reveal(result.Folder);
	}

	/// <summary>Stops writing (the partly written folder is removed), or closes the panel.</summary>
	[RelayCommand]
	private void Cancel()
	{
		if (IsWriting)
		{
			_cancel?.Cancel();
			ProgressText = "Stopping…";
		}
		else
		{
			IsOpen = false;
		}
	}
}
