using Avalonia.Headless.XUnit;
using Dapper;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>
/// The open editor and other writers: viewing never saves, outside changes are loaded or merged instead of overwritten,
/// undo never puts someone else's change back, and lists and counts catch up.
/// </summary>
public class ConcurrencyUiTests
{
	private static AssessmentEditorViewModel OpenRanch(UiSession ui)
	{
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		return ui.Main.Editor;
	}

	private static T Field<T>(AssessmentEditorViewModel e, string member)
		where T : FieldViewModel =>
		e.Sections.SelectMany(s => s.AllFields).OfType<T>().First(f => f.Member == member);

	/// <summary>Another program with the same database open (the AI assistant server, SDE on another Mac).</summary>
	private static SdeStore OtherWriter(UiSession ui) => new SdeStore(SdeDatabase.Open(ui.Store.Database.FilePath));

	[AvaloniaFact]
	public void LookingThroughAnAssessment_ChangesNothing()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		DateTime old = new DateTime(2026, 9, 1, 8, 0, 0);
		using (SqliteConnection c = ui.Store.Database.Connect())
		{
			c.Execute("UPDATE Assessment SET LastUpdate = @old; UPDATE Property SET LastUpdate = @old", new { old });
		}
		int history = ui.Store.GetHistory(f.Assessment.AssessmentID).Count;
		VersionStamp stamp = ui.Store.GetStamp(f.Assessment.AssessmentID);

		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		AssessmentEditorViewModel e = ui.Main.Editor;
		foreach (SectionViewModel step in e.Sections.ToList())
		{
			e.SelectedSection = step;
			ui.Pump();
		}
		ui.Main.ShowCommand.Execute(Page.Map);
		ui.Main.ShowCommand.Execute(Page.Structures);
		ui.Pump();

		Assert.Equal(old, ui.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment.LastUpdate);
		Assert.Equal(old, ui.Store.GetProperty(f.Property.PropertyID).LastUpdate);
		Assert.Equal(history, ui.Store.GetHistory(f.Assessment.AssessmentID).Count);
		Assert.Equal(stamp, ui.Store.GetStamp(f.Assessment.AssessmentID));
	}

	[AvaloniaFact]
	public void UndoAfterSomeoneElsesChange_DoesNotPutTheirChangeBack()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		Guid id = e.Assessment.AssessmentID;
		Field<TextFieldViewModel>(e, nameof(AssessmentRecord.InspectorName)).Text = "Mine";
		e.Save();
		Assert.True(e.CanUndo);

		OtherWriter(ui).Apply(id, f => f.Property.OwnerLastName = "Theirs");
		ui.Services.Changes.PollNow();
		ui.Pump();

		AssessmentEditorViewModel reloaded = ui.Main.Editor;
		Assert.NotSame(e, reloaded);
		Assert.Equal("Theirs", reloaded.Property.OwnerLastName);
		Assert.False(reloaded.CanUndo);
		ui.Main.UndoCommand.Execute(null);
		AssessmentFile now = ui.Store.LoadAssessment(id);
		Assert.Equal("Theirs", now.Property.OwnerLastName);
		Assert.Equal("Mine", now.Assessment.InspectorName);
	}

	[AvaloniaFact]
	public void UnsavedEdits_AndSomeoneElsesSave_AreMerged()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		Guid id = e.Assessment.AssessmentID;
		Field<TextFieldViewModel>(e, nameof(AssessmentRecord.InspectorName)).Text = "Mine";
		Assert.True(e.IsDirty);

		OtherWriter(ui).Apply(id, f => f.Property.OwnerLastName = "Theirs");
		ui.Services.Changes.PollNow();
		ui.Pump();

		AssessmentFile now = ui.Store.LoadAssessment(id);
		Assert.Equal("Mine", now.Assessment.InspectorName);
		Assert.Equal("Theirs", now.Property.OwnerLastName);
		Assert.False(ui.Main.Editor.HasConflict);
		Assert.Equal("Theirs", ui.Main.Editor.Property.OwnerLastName);
		Assert.Equal("Mine", ui.Main.Editor.Assessment.InspectorName);
	}

	[AvaloniaFact]
	public void BothChangingTheSameField_AsksWhichToKeep()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		Guid id = e.Assessment.AssessmentID;
		Field<NumberFieldViewModel>(e, nameof(AssessmentRecord.DepthOfFlood)).Value = 3m;
		Field<TextFieldViewModel>(e, nameof(AssessmentRecord.InspectorName)).Text = "Mine";

		OtherWriter(ui).Apply(id, f => f.Assessment.DepthOfFlood = 4m);
		e.Save();
		ui.Pump();

		AssessmentEditorViewModel merged = ui.Main.Editor;
		Assert.True(merged.HasConflict);
		Assert.Contains("flood", merged.ConflictText, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(4m, ui.Store.LoadAssessment(id).Assessment.DepthOfFlood);
		Assert.Equal("Mine", ui.Store.LoadAssessment(id).Assessment.InspectorName);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		merged.KeepMineCommand.Execute(null);
		ui.Pump();
		Assert.Equal(3m, ui.Store.LoadAssessment(id).Assessment.DepthOfFlood);
		Assert.False(ui.Main.Editor.HasConflict);
	}

	[AvaloniaFact]
	public void SettingALocation_CatchesUpAnEditorShowingAnotherAssessmentOfTheStructure()
	{
		using UiSession ui = new UiSession();
		AssessmentFile older = SampleCases.RanchFlood(ui.Store);
		AssessmentFile newer = ui.Store.CreateAssessment(older.Property.PropertyID, applyDefaults: false);
		ui.Main.OpenAssessment(older.Assessment.AssessmentID);
		ui.Pump();

		PropertyRecord p = ui.Store.GetProperty(older.Property.PropertyID);
		p.Latitude = 39.7m;
		p.Longitude = -84.2m;
		ui.Services.Writes.WriteForStructures(new[] { p.PropertyID }, () => ui.Store.SaveProperty(p, new SaveContext { Via = SaveVia.Map }));
		ui.Pump();

		Assert.Equal(39.7m, ui.Main.Editor.Property.Latitude);
		Field<TextFieldViewModel>(ui.Main.Editor, nameof(AssessmentRecord.InspectorName)).Text = "After";
		ui.Main.Editor.Save();
		Assert.Equal(39.7m, ui.Store.GetProperty(p.PropertyID).Latitude);
		Assert.NotEqual(Guid.Empty, newer.Assessment.AssessmentID);
	}

	[AvaloniaFact]
	public async Task AnAssessmentOpenedDuringAnImport_IsCaughtUpWhenItEnds()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		await ui.Services.Writes.WriteAsync<bool>(null, async () =>
		{
			// Opened while the import runs (the job card doesn't block the window), then the import reaches it.
			ui.Main.OpenAssessment(f.Assessment.AssessmentID);
			await Task.Run(() => ui.Store.Apply(f.Assessment.AssessmentID, x => x.Assessment.InspectorName = "Imported", SaveContext.Batch(SaveVia.Import)));
			return true;
		});
		ui.Pump();
		Assert.Equal("Imported", ui.Main.Editor.Assessment.InspectorName);
	}

	[AvaloniaFact]
	public async Task PhotosStillCopying_WhenTheEditorIsReplaced_AreAttachedAnyway()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		Guid id = e.Assessment.AssessmentID;
		string a = Path.Combine(ui.Folder, "a.jpg");
		string b = Path.Combine(ui.Folder, "b.jpg");
		File.WriteAllBytes(a, FieldPhotos.Jpeg(SKColors.SteelBlue));
		File.WriteAllBytes(b, FieldPhotos.Jpeg(SKColors.Olive));

		Task adding = e.AddPhotosAsync(new[] { a, b });
		// Someone else saves, and the window shows their copy before the photos are in.
		OtherWriter(ui).Apply(id, f => f.Property.OwnerLastName = "Theirs");
		ui.Main.RefreshEditor();
		await adding;
		ui.Pump();

		AssessmentFile now = ui.Store.LoadAssessment(id);
		Assert.Equal(2, now.Attachments.Count(x => x.Active));
		Assert.Equal("Theirs", now.Property.OwnerLastName);
		Assert.Equal(2, ui.Main.Editor.Photos.Count);
	}

	[AvaloniaFact]
	public void TheSidebarCounts_CatchUpOnEveryPage()
	{
		using UiSession ui = new UiSession();
		// Saved without telling the window (another program, or a test helper).
		SampleCases.RanchFlood(ui.Store);
		ui.Main.ShowCommand.Execute(Page.Map);
		Assert.StartsWith("1 structure", ui.Main.CountsText);
	}

	[AvaloniaFact]
	public async Task RestoringABackup_IsRefused_WhileAnotherCopyOfSdeHasTheDatabaseOpen()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}
		using UiSession ui = new UiSession();
		string path = Path.Combine(ui.Folder, "event.sqlite");
		ui.Services.Open(path);
		await ui.Services.OpeningBackup;
		SampleCases.RanchFlood(ui.Store);
		BackupInfo backup = Assert.Single(ui.Services.Backups.List());

		using (DatabaseLease assistant = DatabaseLease.TryShared(ui.Store.Database.LockPath))
		{
			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ui.Services.RestoreBackup(backup));
			Assert.Contains("another copy of SDE", ex.Message);
			Assert.Equal((1, 1), ui.Store.Counts());
		}
		ui.Services.RestoreBackup(backup);
		Assert.Equal((0, 0), ui.Store.Counts());
	}
}
