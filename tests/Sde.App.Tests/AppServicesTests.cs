using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>The app-wide services: plain-English errors, sheets, long tasks, and writes from outside the editor.</summary>
public class AppServicesTests
{
	[AvaloniaFact]
	public void Error_IsShownInASheet_InPlainEnglish_AndLogged()
	{
		using UiSession ui = new UiSession();
		ui.Services.Errors.Show("Couldn't export", new UnauthorizedAccessException("Access to the path '/Volumes/X' is denied."));
		ui.Pump();
		ErrorSheetViewModel sheet = Assert.IsType<ErrorSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("Couldn't export", sheet.Title);
		Assert.StartsWith("SDE isn't allowed", sheet.Message);
		Assert.False(sheet.ShowDetails);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.ToggleDetailsCommand.Execute(null);
		ui.Pump();
		Assert.Contains("UnauthorizedAccessException", sheet.Details);
		ui.Snapshot("50-error-sheet");
		Assert.Contains("Couldn't export", File.ReadAllText(AppLog.TodayFile));

		sheet.CancelCommand.Execute(null);
		Assert.Null(ui.Main.Sheets.Current);
	}

	[Theory]
	[InlineData(typeof(FileNotFoundException), "isn't there any more")]
	[InlineData(typeof(IOException), "couldn't be read or written")]
	[InlineData(typeof(NullReferenceException), "Nothing was lost")]
	[InlineData(typeof(InvalidOperationException), "custom message")]
	public void Describe_IsPlainEnglish(Type type, string expected)
	{
		Exception ex = (Exception)Activator.CreateInstance(type, "custom message");
		ErrorDescription d = ErrorPresenter.Describe("Couldn't do it", ex);
		Assert.Contains(expected, d.Message);
		Assert.DoesNotContain("—", d.Message);
	}

	[Fact]
	public void Describe_DatabaseBusy_AndLocked()
	{
		Assert.Contains("busy", ErrorPresenter.Describe(null, new SqliteException("database is locked", 5)).Message);
		Assert.Contains("Reopen it first", ErrorPresenter.Describe(null, new AssessmentLockedException(Guid.Empty, AssessmentStatus.Final, new List<FieldDiff>())).Message);
	}

	private sealed class TestSheet() : SheetViewModel("Test");

	[AvaloniaFact]
	public async Task Sheets_Stack_AndReturnTheirResult()
	{
		using UiSession ui = new UiSession();
		TestSheet first = new TestSheet();
		TestSheet second = new TestSheet();
		Task<string> a = ui.Main.Sheets.ShowAsync<string>(first);
		Task<string> b = ui.Main.Sheets.ShowAsync<string>(second);
		Assert.Same(second, ui.Main.Sheets.Current);
		second.Close("done");
		Assert.Equal("done", await b);
		Assert.Same(first, ui.Main.Sheets.Current);
		first.CancelCommand.Execute(null);
		Assert.Null(await a);
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public async Task Job_Completes_Stops_OrFails()
	{
		using UiSession ui = new UiSession();
		JobRunner jobs = ui.Services.Jobs;
		JobOutcome<int> ok = await jobs.RunAsync("Counting", (p, ct) =>
		{
			p.Report(new JobProgress(1, 2, "Halfway"));
			return 42;
		});
		Assert.True(ok.Completed);
		Assert.Equal(42, ok.Value);
		Assert.False(jobs.IsBusy);

		TaskCompletionSource started = new TaskCompletionSource();
		Task<JobOutcome<int>> running = jobs.RunAsync("Waiting", (p, ct) =>
		{
			started.SetResult();
			ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
			ct.ThrowIfCancellationRequested();
			return 0;
		});
		await started.Task;
		Assert.True(jobs.IsBusy);
		JobOutcome<int> refused = await jobs.RunAsync("Second", (p, ct) => 1);
		Assert.Equal(JobOutcomeKind.Failed, refused.Kind);
		ui.Main.Sheets.Current?.Cancel();
		jobs.Current.StopCommand.Execute(null);
		Assert.Equal(JobOutcomeKind.Stopped, (await running).Kind);

		JobOutcome<int> failed = await jobs.RunAsync<int>("Breaking", (p, ct) => throw new IOException("disk"));
		Assert.Equal(JobOutcomeKind.Failed, failed.Kind);
		Assert.Equal("Couldn't finish breaking", Assert.IsType<ErrorSheetViewModel>(ui.Main.Sheets.Current).Title);
	}

	[AvaloniaFact]
	public void OutsideWrite_ReachesTheOpenEditor_AndIsntOverwritten()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		AssessmentEditorViewModel editor = ui.Main.Editor;
		editor.SelectedSection = editor.Sections[2];
		ui.Pump();

		ui.Services.Writes.Apply(f.Assessment.AssessmentID, file => file.Assessment.InspectorName = "From the phone",
			new SaveContext { Via = SaveVia.Phone });
		ui.Pump();
		Assert.NotSame(editor, ui.Main.Editor);
		Assert.Equal("From the phone", ui.Main.Editor.Assessment.InspectorName);
		Assert.Same(ui.Main.Editor.Sections[2], ui.Main.Editor.SelectedSection);
		Assert.Same(ui.Main.Editor, ui.Main.CurrentPage);

		ui.Main.Editor.Save();
		Assert.Equal("From the phone", ui.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment.InspectorName);
		Assert.Contains(ui.Store.GetHistory(f.Assessment.AssessmentID), h => h.Via == SaveVia.Phone && h.NewValue == "From the phone");
	}
}
