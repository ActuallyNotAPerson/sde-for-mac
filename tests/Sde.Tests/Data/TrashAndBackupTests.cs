using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Recently deleted (soft delete with FEMA's Active flag, restore, purge after 30 days) and automatic backups.</summary>
public class TrashAndBackupTests
{
	private sealed class TestClock
	{
		public DateTime Now { get; set; } = new DateTime(2026, 10, 1, 9, 0, 0);
	}

	private static string Photo(TempDatabase t, AssessmentFile f, string name)
	{
		string source = Path.Combine(t.Folder, name);
		File.WriteAllBytes(source, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
		AttachmentRecord a = t.Store.ImportPhoto(f.Assessment.AssessmentID, source, name);
		f.Attachments.Add(a);
		t.Store.SaveAssessment(f);
		return t.Store.PhotoPath(a);
	}

	[Fact]
	public void TrashedStructure_IsHiddenEverywhere_AndComesBackWithItsAssessments()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		Guid pid = f.Property.PropertyID;
		AssessmentFile second = t.Store.CreateAssessment(pid, false);
		t.Store.TrashProperty(pid);

		Assert.Equal((0, 0), t.Store.Counts());
		Assert.Empty(t.Store.ListStructures());
		Assert.Empty(t.Store.LoadAllAssessments());
		Assert.Empty(new ExchangeService(t.Store).ReadDataset().Properties);
		DeletedItemRecord item = Assert.Single(t.Store.GetDeletedItems());
		Assert.Equal(DeletedKind.Structure, item.Value);
		Assert.Equal("123 River Street", item.Label);
		Assert.Equal(item.DeletedOn.AddDays(SdeStore.KeepDays), item.PurgeAfter);

		t.Store.Restore(item.ItemKey);
		Assert.Equal((1, 2), t.Store.Counts());
		Assert.Empty(t.Store.GetDeletedItems());
		Assert.Contains(t.Store.GetPropertyHistory(pid), h => h.Label == "Restored from Recently deleted");
		Assert.NotNull(t.Store.LoadAssessment(second.Assessment.AssessmentID));
	}

	[Fact]
	public void TrashedAssessment_RestoresItsStructure_IfThatWasDeletedToo()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		AssessmentFile other = t.Store.CreateAssessment(f.Property.PropertyID, false);
		t.Store.TrashAssessment(other.Assessment.AssessmentID);
		Assert.Equal((1, 1), t.Store.Counts());
		t.Store.TrashProperty(f.Property.PropertyID);
		Assert.Equal(2, t.Store.GetDeletedItems().Count);

		t.Store.Restore("A:" + other.Assessment.AssessmentID.ToString("D"));
		// Its structure was in Recently deleted, so it comes back too, with the assessment that was deleted with it.
		Assert.Equal((1, 2), t.Store.Counts());
		Assert.Empty(t.Store.GetDeletedItems());
	}

	[Fact]
	public void FinalAssessments_CantBeDeleted()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		Assert.Throws<AssessmentLockedException>(() => t.Store.TrashAssessment(f.Assessment.AssessmentID));
		Assert.Throws<AssessmentLockedException>(() => t.Store.TrashProperty(f.Property.PropertyID));
		Assert.Equal((1, 1), t.Store.Counts());
	}

	[Fact]
	public void RemovedPhoto_GoesToRecentlyDeleted_AndCanBeRestored_OrPurged()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		string path = Photo(t, f, "porch.png");
		f = t.Store.LoadAssessment(f.Assessment.AssessmentID);
		f.Attachments.Single().Active = false;      // what the editor's Remove does
		t.Store.SaveAssessment(f);

		Assert.Empty(t.Store.LoadAssessment(f.Assessment.AssessmentID).Attachments);
		Assert.Empty(new ExchangeService(t.Store).ReadDataset().Attachments);
		Assert.True(File.Exists(path));
		DeletedItemRecord item = Assert.Single(t.Store.GetDeletedItems());
		Assert.Equal(DeletedKind.Photo, item.Value);
		Assert.Contains(t.Store.GetHistory(f.Assessment.AssessmentID), h => h.Label == "Photo removed");

		// Saving again (the editor still has the inactive photo, or reloaded without it) keeps it restorable.
		t.Store.SaveAssessment(t.Store.LoadAssessment(f.Assessment.AssessmentID));
		t.Store.Restore(item.ItemKey);
		Assert.Single(t.Store.LoadAssessment(f.Assessment.AssessmentID).Attachments);

		f = t.Store.LoadAssessment(f.Assessment.AssessmentID);
		f.Attachments.Single().Active = false;
		t.Store.SaveAssessment(f);
		t.Store.Purge(Assert.Single(t.Store.GetDeletedItems()).ItemKey);
		Assert.False(File.Exists(path));
		using SqliteConnection c = t.Database.Connect();
		Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Attachment"));
	}

	[Fact]
	public void PurgeExpired_RemovesOnlyWhatIsOlderThan30Days_WithItsFiles()
	{
		using TempDatabase t = new TempDatabase();
		TestClock clock = new TestClock();
		t.Store.Clock = () => clock.Now;
		AssessmentFile old = SampleCases.RanchFlood(t.Store);
		string oldPhoto = Photo(t, old, "old.png");
		t.Store.TrashProperty(old.Property.PropertyID);
		clock.Now = clock.Now.AddDays(10);
		AssessmentFile recent = SampleCases.RanchFlood(t.Store);
		t.Store.TrashProperty(recent.Property.PropertyID);

		clock.Now = clock.Now.AddDays(SdeStore.KeepDays - 5);
		Assert.Equal(1, t.Store.PurgeExpired());
		Assert.False(File.Exists(oldPhoto));
		Assert.Null(t.Store.GetProperty(old.Property.PropertyID));
		Assert.NotNull(t.Store.GetProperty(recent.Property.PropertyID));
		Assert.Single(t.Store.GetDeletedItems());
	}

	[Fact]
	public void PurgeExpired_DropsRowsWhoseTargetAnOlderVersionDeleted()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		t.Store.TrashProperty(f.Property.PropertyID);
		t.Store.DeleteProperty(f.Property.PropertyID);   // what v1.5's Delete does
		t.Store.PurgeExpired();
		Assert.Empty(t.Store.GetDeletedItems());
	}

	[Fact]
	public void Import_ReplacesWhatIsInRecentlyDeleted_InsteadOfSkippingIt()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		ExchangeService x = new ExchangeService(t.Store);
		(string folder, _) = x.Export(x.ReadDataset(), t.Folder);
		t.Store.TrashProperty(f.Property.PropertyID);

		ImportResult r = x.ImportFolder(folder, ExistingRecordAction.Skip);
		Assert.Equal(1, r.AssessmentsAdded);
		Assert.Contains(r.Warnings, w => w.Contains("Recently deleted"));
		Assert.Equal((1, 1), t.Store.Counts());
		Assert.Empty(t.Store.GetDeletedItems());
	}

	[Fact]
	public void SaveImport_TakesTheLinkFromATrashedAssessment()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = new PropertyRecord { Active = true, StreetNumber = "1", StreetName = "Oak", StructureTypeID = 1 };
		AssessmentFile first = AssessmentFile.CreateNew(p);
		t.Store.SaveImport(new[] { new ImportedAssessment { File = first, Link = new ExternalLinkRecord { Source = "Test", RecordId = "R-1" } } });
		t.Store.TrashAssessment(first.Assessment.AssessmentID);
		AssessmentFile again = AssessmentFile.CreateNew(new PropertyRecord { Active = true, StreetNumber = "1", StreetName = "Oak", StructureTypeID = 1 });
		t.Store.SaveImport(new[] { new ImportedAssessment { File = again, Link = new ExternalLinkRecord { Source = "Test", RecordId = "R-1" } } });
		Assert.Equal(again.Assessment.AssessmentID, Assert.Single(t.Store.GetExternalLinks("Test")).AssessmentID);
	}

	// ───────────── Backups ─────────────

	[Fact]
	public void BackUp_CopiesTheDatabaseAndPhotos_AndKeepsTheNewest14()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		string photo = Photo(t, f, "front.png");
		DateTime now = new DateTime(2026, 10, 1, 8, 0, 0);
		BackupService b = new BackupService(t.Database, () => now);

		BackupInfo first = b.BackUp("test");
		Assert.Equal((1, 1), (first.Structures, first.Assessments));
		Assert.Equal("test", first.Reason);
		Assert.True(File.Exists(Path.Combine(first.Folder, "attachments", Path.GetFileName(photo))));
		Assert.StartsWith(Path.Combine(t.Folder, BackupService.FolderName), first.Folder);

		for (int i = 0; i < 20; i++)
		{
			now = now.AddHours(1);
			b.BackUp(null);
		}
		List<BackupInfo> kept = b.List();
		Assert.Equal(BackupService.Keep, kept.Count);
		Assert.Equal(now, kept[0].TakenOn);
		Assert.DoesNotContain(kept, k => k.Reason == "test");
	}

	[Fact]
	public void AutomaticBackups_OncePerDayOnOpen_AndOnQuitOnlyWhenChanged()
	{
		using TempDatabase t = new TempDatabase();
		SampleCases.RanchFlood(t.Store);
		DateTime now = DateTime.Now.AddMinutes(1);
		BackupService b = new BackupService(t.Database, () => now);
		Assert.NotNull(b.BackUpIfNoneToday());
		now = now.AddMinutes(1);
		Assert.Null(b.BackUpIfNoneToday());
		Assert.Null(b.BackUpIfChanged());                 // nothing written since
		now = now.AddDays(1);
		Assert.NotNull(b.BackUpIfNoneToday());
	}

	[Fact]
	public void Restore_PutsTheDataBack_AfterBackingUpWhatIsThere()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		DateTime now = new DateTime(2026, 10, 1, 8, 0, 0);
		BackupService b = new BackupService(t.Database, () => now);
		BackupInfo before = b.BackUp(null);
		t.Store.TrashProperty(f.Property.PropertyID);
		t.Store.EmptyTrash();
		Assert.Equal((0, 0), t.Store.Counts());

		now = now.AddMinutes(5);
		BackupInfo safety = b.Restore(before);
		Assert.Equal("before restore", safety.Reason);
		SdeStore reopened = new SdeStore(SdeDatabase.Open(t.Database.FilePath));
		Assert.Equal((1, 1), reopened.Counts());
		Assert.Equal(2, b.List().Count);
	}
}
