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

	/// <summary>Runs <paramref name="test"/> with the path of app-settings.json in a new temporary folder.</summary>
	private static void WithSettingsFolder(Action<string> test)
	{
		string folder = Path.Combine(Path.GetTempPath(), "sde-settings-" + Guid.NewGuid().ToString("N"));
		try
		{
			test(Path.Combine(folder, "app-settings.json"));
		}
		finally
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
	}

	[Theory]
	[InlineData("cut short")]
	[InlineData("empty")]
	[InlineData("missing")]
	public void Settings_DamagedFile_ComesBackFromTheBackup(string damage)
	{
		WithSettingsFolder(path =>
		{
			new AppSettings { DatabasePath = "/Volumes/Work/first.sqlite", YourName = "Pat Example" }.Save(path);
			new AppSettings { DatabasePath = "/Volumes/Work/second.sqlite", YourName = "Pat Example" }.Save(path);
			Assert.Equal("/Volumes/Work/second.sqlite", AppSettings.Load(path).DatabasePath);

			string text = File.ReadAllText(path);
			switch (damage)
			{
				case "cut short":
					File.WriteAllText(path, text.Substring(0, text.Length / 2));
					break;
				case "empty":
					File.WriteAllText(path, "");
					break;
				default:
					File.Delete(path);
					break;
			}
			AppSettings loaded = AppSettings.Load(path);
			Assert.Equal("/Volumes/Work/first.sqlite", loaded.DatabasePath);   // the settings before the last save
			Assert.Equal("Pat Example", loaded.YourName);
		});
	}

	[Fact]
	public void Settings_NoFileAndNoBackup_AreTheDefaults()
	{
		WithSettingsFolder(path =>
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, "{\"DatabasePath\": \"/Volumes/Wo");
			Assert.Null(AppSettings.Load(path).DatabasePath);
			File.Delete(path);
			Assert.Null(AppSettings.Load(path).DatabasePath);
		});
	}

	[Fact]
	public void Settings_Save_KeepsTheLastOneAsBackup_AndLeavesNoTemporaryFiles()
	{
		WithSettingsFolder(path =>
		{
			new AppSettings { DatabasePath = "/Volumes/Work/first.sqlite" }.Save(path);
			new AppSettings { DatabasePath = "/Volumes/Work/second.sqlite" }.Save(path);
			new AppSettings { DatabasePath = "/Volumes/Work/third.sqlite" }.Save(path);
			string[] files = Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
			Assert.Equal(new[] { "app-settings.json", "app-settings.json.bak" }, files);
			Assert.Equal("/Volumes/Work/third.sqlite", AppSettings.Load(path).DatabasePath);
			Assert.Equal("/Volumes/Work/second.sqlite", AppSettings.Load(path + ".bak").DatabasePath);
		});
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
