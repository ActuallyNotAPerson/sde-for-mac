using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>Recently deleted, undo, statuses with locks and history, and restoring a backup, through the app.</summary>
public class DataSafetyUiTests
{
	private static AssessmentEditorViewModel OpenRanch(UiSession ui)
	{
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		return ui.Main.Editor;
	}

	private static TextFieldViewModel Text(AssessmentEditorViewModel e, string member) =>
		e.Sections.SelectMany(s => s.AllFields).OfType<TextFieldViewModel>().First(f => f.Member == member);

	[AvaloniaFact]
	public void Delete_MovesToRecentlyDeleted_AndRestoreBringsItBack()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		e.DeleteAssessmentCommand.Execute(null);
		ui.Pump();
		Assert.Empty(ui.Main.Structures.Rows);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Moved to Recently deleted"));

		ui.Main.ShowCommand.Execute(ViewModels.Page.Trash);
		ui.Pump();
		DeletedItemViewModel item = Assert.Single(ui.Main.Trash.Items);
		Assert.Equal("Structure", item.Kind);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("52-recently-deleted");

		ui.Main.Trash.RestoreCommand.Execute(item);
		Assert.Empty(ui.Main.Trash.Items);
		ui.Main.ShowCommand.Execute(ViewModels.Page.Structures);
		Assert.Single(ui.Main.Structures.Rows);
	}

	[AvaloniaFact]
	public void Undo_PutsBackTheLastBurstOfEdits_AndRedoReappliesIt()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		Guid id = e.Assessment.AssessmentID;
		Text(e, nameof(AssessmentRecord.InspectorName)).Text = "Lee";
		Text(e, nameof(AssessmentRecord.InspectorPhoneNumber)).Text = "555-0100";
		e.Save();
		Assert.True(e.CanUndo);

		ui.Main.UndoCommand.Execute(null);
		ui.Pump();
		AssessmentFile stored = ui.Store.LoadAssessment(id);
		Assert.Equal("Inspector A", stored.Assessment.InspectorName);
		Assert.Null(stored.Assessment.InspectorPhoneNumber);
		Assert.Equal("Inspector A", ui.Main.Editor.Assessment.InspectorName);
		Assert.True(ui.Main.Editor.CanRedo);
		Assert.Contains(ui.Store.GetHistory(id), h => h.Via == SaveVia.Undo);

		ui.Main.RedoCommand.Execute(null);
		ui.Pump();
		Assert.Equal("Lee", ui.Store.LoadAssessment(id).Assessment.InspectorName);
		Assert.Equal("Lee", ui.Main.Editor.Assessment.InspectorName);
	}

	[AvaloniaFact]
	public async Task Final_LocksTheEditor_ReopeningNeedsAReason_AndHistoryShowsIt()
	{
		using UiSession ui = new UiSession();
		ui.Services.SetYourName("Reviewer B");
		AssessmentEditorViewModel e = OpenRanch(ui);
		await e.SetStatusCommand.ExecuteAsync("Final");
		ui.Pump();
		Assert.True(e.IsLocked);
		Assert.Equal("Final", e.StatusLabel);
		Assert.Contains("Reopen it", e.LockText);
		ContentControl step = ui.Window.GetVisualDescendants().OfType<ContentControl>().First(c => c is not ListBoxItem && c.Content is SectionViewModel);
		Assert.False(step.IsEnabled);
		ui.Snapshot("53-editor-final");

		Task reopen = e.SetStatusCommand.ExecuteAsync("Reviewed");
		ui.Pump();
		ReopenSheetViewModel sheet = Assert.IsType<ReopenSheetViewModel>(ui.Main.Sheets.Current);
		Assert.False(sheet.ReopenCommand.CanExecute(null));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.Reason = "Owner sent a contractor's estimate";
		sheet.ReopenCommand.Execute(null);
		await reopen;
		Assert.False(e.IsLocked);
		Assert.Equal("Reviewed", e.StatusLabel);

		e.ShowHistoryCommand.Execute(null);
		ui.Pump();
		HistorySheetViewModel history = Assert.IsType<HistorySheetViewModel>(ui.Main.Sheets.Current);
		Assert.Contains(history.Rows, r => r.What == "Reopened: Owner sent a contractor's estimate" && r.Who == "Reviewer B");
		Assert.Contains(history.Rows, r => r.What == "Status: Final");
		Assert.Contains(history.Rows, r => r.What == SdeStore.CreatedEvent);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("54-editor-history");
	}

	[AvaloniaFact]
	public async Task RemovedPhoto_CanBeRestored()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		string png = Path.Combine(ui.Folder, "front.png");
		File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
		await e.AddPhotosAsync(new[] { png });
		e.Save();
		PhotoViewModel photo = Assert.Single(e.Photos);
		photo.RemoveCommand.Execute(null);
		e.Save();
		Assert.True(File.Exists(photo.Path));
		Assert.Empty(ui.Store.LoadAssessment(e.Assessment.AssessmentID).Attachments);

		ui.Main.ShowCommand.Execute(ViewModels.Page.Trash);
		DeletedItemViewModel item = Assert.Single(ui.Main.Trash.Items);
		Assert.Equal("Photo", item.Kind);
		ui.Main.Trash.RestoreCommand.Execute(item);
		Assert.Single(ui.Store.LoadAssessment(e.Assessment.AssessmentID).Attachments);
	}

	[AvaloniaFact]
	public async Task RestoreBackup_FromTheFileMenu()
	{
		using UiSession ui = new UiSession();
		string path = Path.Combine(ui.Folder, "event.sqlite");
		ui.Services.Open(path);            // a database opened normally gets automatic backups
		await ui.Services.OpeningBackup;
		SampleCases.RanchFlood(ui.Store);
		ui.Services.RaiseDataChanged();

		ui.Main.RestoreBackupCommand.Execute(null);
		ui.Pump();
		RestoreBackupSheetViewModel sheet = Assert.IsType<RestoreBackupSheetViewModel>(ui.Main.Sheets.Current);
		BackupRowViewModel row = Assert.Single(sheet.Backups);
		Assert.Contains("0 structures", row.Detail);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("55-restore-backup");

		await sheet.RestoreCommand.ExecuteAsync(null);
		ui.Pump();
		Assert.Equal((0, 0), ui.Store.Counts());
		Assert.Empty(ui.Main.Structures.Rows);
		Assert.Contains(ui.Services.Backups.List(withCounts: true), b => b.Reason == "before restore" && b.Structures == 1);
	}

	[AvaloniaFact]
	public async Task MoveTheDatabase_TakesItsPhotos_AndOpensItThere()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}
		using UiSession ui = new UiSession();
		ui.Services.Open(Path.Combine(ui.Folder, "event.sqlite"));
		await ui.Services.OpeningBackup;
		SampleCases.RanchFlood(ui.Store);
		string old = ui.Store.Database.FilePath;
		string target = Path.Combine(ui.Folder, "shared drive", "event.sqlite");
		ui.Dialogs.Answers.Enqueue(target);
		ui.Dialogs.Answers.Enqueue(true);

		await ui.Main.Settings.MoveDatabaseCommand.ExecuteAsync(null);
		ui.Pump();

		Assert.Equal(target, ui.Store.Database.FilePath);
		Assert.False(File.Exists(old));
		Assert.Equal((1, 1), ui.Store.Counts());
		Assert.Equal(target, ui.Services.Settings.DatabasePath);
		Assert.DoesNotContain(old, ui.Services.RecentDatabases());
	}
}

