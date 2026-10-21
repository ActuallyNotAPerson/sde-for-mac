using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sde.App.Services;

/// <summary>How far a job has got: <paramref name="Done"/> of <paramref name="Total"/> (0 = unknown), and what it's doing.</summary>
public sealed record JobProgress(int Done, int Total, string Step);

/// <summary>How a job ended.</summary>
public enum JobOutcomeKind
{
	Completed,
	Stopped,
	Failed,
}

public sealed record JobOutcome<T>(JobOutcomeKind Kind, T Value, Exception Error)
{
	public bool Completed => Kind == JobOutcomeKind.Completed;
}

/// <summary>A long task the window shows with a progress bar and a Stop button (exports, imports, look-ups, letters).</summary>
public sealed partial class JobViewModel : ObservableObject
{
	private readonly CancellationTokenSource _cancel = new CancellationTokenSource();

	public JobViewModel(string title, bool canStop)
	{
		Title = title;
		CanStop = canStop;
	}

	public string Title { get; }

	public bool CanStop { get; }

	public CancellationToken Token => _cancel.Token;

	/// <summary>0 to 100.</summary>
	[ObservableProperty]
	public partial double ProgressValue { get; set; }

	[ObservableProperty]
	public partial bool IsIndeterminate { get; set; } = true;

	[ObservableProperty]
	public partial string ProgressText { get; set; } = "Starting…";

	[ObservableProperty]
	public partial bool IsStopping { get; set; }

	public void Report(JobProgress p)
	{
		IsIndeterminate = p.Total <= 0;
		ProgressValue = p.Total <= 0 ? 0 : 100.0 * p.Done / p.Total;
		if (!IsStopping && p.Step != null)
		{
			ProgressText = p.Step;
		}
	}

	[RelayCommand]
	public void Stop()
	{
		if (CanStop && !IsStopping)
		{
			IsStopping = true;
			ProgressText = "Stopping…";
			_cancel.Cancel();
		}
	}

	internal void Finish() => _cancel.Dispose();
}

/// <summary>
/// Runs long tasks off the UI thread with progress and Stop, one at a time, and reports failures through the
/// <see cref="ErrorPresenter"/>. The work is responsible for cleaning up after itself when stopped (deleting a partly
/// written folder, rolling back a transaction).
/// </summary>
public sealed partial class JobRunner(ErrorPresenter errors) : ObservableObject
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBusy))]
	public partial JobViewModel Current { get; private set; }

	public bool IsBusy => Current != null;

	/// <summary>
	/// Runs <paramref name="work"/> on a background thread. Returns Completed with its value, Stopped when the person
	/// pressed Stop, or Failed (already shown to the person, titled <paramref name="failureTitle"/>).
	/// </summary>
	public async Task<JobOutcome<T>> RunAsync<T>(string title, Func<IProgress<JobProgress>, CancellationToken, T> work,
		string failureTitle = null, bool canStop = true)
	{
		if (Current != null)
		{
			InvalidOperationException busy = new InvalidOperationException($"Wait for \"{Current.Title}\" to finish first.");
			errors.Show(failureTitle ?? "Couldn't start " + title.ToLowerInvariant(), busy);
			return new JobOutcome<T>(JobOutcomeKind.Failed, default, busy);
		}
		JobViewModel job = new JobViewModel(title, canStop);
		Current = job;
		Progress<JobProgress> progress = new Progress<JobProgress>(job.Report);
		try
		{
			T value = await Task.Run(() => work(progress, job.Token), CancellationToken.None);
			return new JobOutcome<T>(JobOutcomeKind.Completed, value, null);
		}
		catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
		{
			return new JobOutcome<T>(JobOutcomeKind.Stopped, default, null);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			errors.Show(failureTitle ?? "Couldn't finish " + title.ToLowerInvariant(), ex);
			return new JobOutcome<T>(JobOutcomeKind.Failed, default, ex);
		}
		finally
		{
			Current = null;
			job.Finish();
		}
	}

	/// <summary>Async work (online look-ups); same rules as the synchronous overload.</summary>
	public Task<JobOutcome<T>> RunTaskAsync<T>(string title, Func<IProgress<JobProgress>, CancellationToken, Task<T>> work,
		string failureTitle = null, bool canStop = true) =>
		RunAsync<T>(title, (p, ct) => work(p, ct).GetAwaiter().GetResult(), failureTitle, canStop);
}
