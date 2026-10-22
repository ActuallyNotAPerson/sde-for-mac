using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// The files that belong to a database beside it (photos, documents, the layers GeoPackage, the lease file): backups,
/// restores, and moving or copying a database take them along, and restores and moves wait for the database to be free.
/// </summary>
public class DatabaseFilesTests
{
	private static void MakeLayers(SdeDatabase db, string name)
	{
		using SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.LayersPath, Pooling = false }.ToString());
		c.Open();
		c.Execute("CREATE TABLE IF NOT EXISTS layer_names (name TEXT); DELETE FROM layer_names; INSERT INTO layer_names VALUES (@name)", new { name });
	}

	private static string LayerName(string gpkg)
	{
		using SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = gpkg, Pooling = false }.ToString());
		c.Open();
		return c.ExecuteScalar<string>("SELECT name FROM layer_names");
	}

	private static void AddDocument(TempDatabase t, AssessmentFile f, string fileName)
	{
		Directory.CreateDirectory(t.Database.DocumentsFolder);
		File.WriteAllText(Path.Combine(t.Database.DocumentsFolder, fileName), "pdf");
		using SqliteConnection c = t.Database.Connect();
		c.Execute("INSERT INTO Document (DocumentID, PropertyID, Kind, FileName) VALUES (@id, @pid, 'ElevationCertificate', @fileName)",
			new { id = Guid.NewGuid().ToString("D"), pid = f.Property.PropertyID.ToString("D"), fileName });
	}

	private static void AddPhoto(TempDatabase t, AssessmentFile f, string fileName)
	{
		Directory.CreateDirectory(t.Store.AttachmentsFolder);
		File.WriteAllText(Path.Combine(t.Store.AttachmentsFolder, fileName), "photo");
		AssessmentFile loaded = t.Store.LoadAssessment(f.Assessment.AssessmentID);
		loaded.Attachments.Add(new AttachmentRecord { FileName = fileName, Active = true, AttachmentType = "Photo" });
		t.Store.SaveAssessment(loaded);
	}

	[Fact]
	public void Lease_SharedLeasesLiveTogether_ButExclusiveNeedsTheDatabaseToItself()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}
		using TempDatabase t = new TempDatabase();
		using DatabaseLease app = DatabaseLease.TryShared(t.Database.LockPath);
		Assert.NotNull(app);
		using (DatabaseLease assistant = DatabaseLease.TryShared(t.Database.LockPath))
		{
			Assert.NotNull(assistant);
			Assert.Null(DatabaseLease.TryExclusive(t.Database.LockPath));
			Assert.False(app.TryUpgrade());
		}
		Assert.True(app.TryUpgrade());
		Assert.Null(DatabaseLease.TryShared(t.Database.LockPath));
		app.Downgrade();
		using DatabaseLease later = DatabaseLease.TryShared(t.Database.LockPath);
		Assert.NotNull(later);
	}

	[Fact]
	public void Restore_IsRefused_WhileAnotherCopyOfSdeHasTheDatabaseOpen()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}
		using TempDatabase t = new TempDatabase();
		BackupService backups = new BackupService(t.Database);
		BackupInfo empty = backups.BackUp("test");
		SampleCases.RanchFlood(t.Store);
		using DatabaseLease app = DatabaseLease.TryShared(t.Database.LockPath);
		using (DatabaseLease assistant = DatabaseLease.TryShared(t.Database.LockPath))
		{
			Assert.Throws<InvalidOperationException>(() => backups.Restore(empty, app));
			Assert.Equal((1, 1), t.Store.Counts());
		}
		backups.Restore(empty, app);
		Assert.Equal((0, 0), new SdeStore(SdeDatabase.Open(t.Database.FilePath)).Counts());
		Assert.False(app.IsExclusive);
	}

	[Fact]
	public void BackUp_TakesTheLayersAndDocuments_AndLinksUnchangedLayers()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		AddDocument(t, f, "ec.pdf");
		MakeLayers(t.Database, "first");
		BackupService backups = new BackupService(t.Database, () => new DateTime(2026, 10, 1, 9, 0, 0));
		BackupInfo one = backups.BackUp("one");
		Assert.True(File.Exists(Path.Combine(one.Folder, "documents", "ec.pdf")));
		Assert.Equal("first", LayerName(Path.Combine(one.Folder, BackupService.LayersFileName)));

		Thread.Sleep(1100);
		BackupInfo two = new BackupService(t.Database, () => new DateTime(2026, 10, 1, 10, 0, 0)).BackUp("two");
		string copyOne = Path.Combine(one.Folder, BackupService.LayersFileName);
		string copyTwo = Path.Combine(two.Folder, BackupService.LayersFileName);
		// Unchanged since the last backup: the same file, not a second copy.
		Assert.Equal(File.GetLastWriteTimeUtc(copyOne), File.GetLastWriteTimeUtc(copyTwo));
		Assert.Equal("first", LayerName(copyTwo));
	}

	[Fact]
	public void Restore_PutsBackTheLayers_AndListingIsCheapUnlessCountsAreAsked()
	{
		using TempDatabase t = new TempDatabase();
		SampleCases.RanchFlood(t.Store);
		MakeLayers(t.Database, "before");
		BackupService backups = new BackupService(t.Database);
		BackupInfo taken = backups.BackUp("test");
		MakeLayers(t.Database, "after");

		BackupInfo listed = Assert.Single(backups.List());
		Assert.Null(listed.Structures);
		Assert.Equal(1, Assert.Single(backups.List(withCounts: true)).Structures);

		backups.Restore(taken);
		Assert.Equal("before", LayerName(t.Database.LayersPath));
		Assert.False(File.Exists(t.Database.LayersPath + ".restoring"));
		Assert.False(File.Exists(t.Database.FilePath + ".restoring"));
	}

	[Fact]
	public void CopyingADatabase_TakesItsPhotosDocumentsAndLayers()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		AddPhoto(t, f, "a.jpg");
		AddDocument(t, f, "ec.pdf");
		MakeLayers(t.Database, "layers");
		File.WriteAllText(Path.Combine(t.Store.AttachmentsFolder, "someone-elses.jpg"), "photo");
		string copy = Path.Combine(t.Folder, "elsewhere", "copy.sqlite");

		DatabaseFiles.Copy(t.Database, copy);

		string folder = Path.GetDirectoryName(copy)!;
		Assert.True(File.Exists(Path.Combine(folder, "attachments", "a.jpg")));
		Assert.False(File.Exists(Path.Combine(folder, "attachments", "someone-elses.jpg")));
		Assert.True(File.Exists(Path.Combine(folder, "documents", "ec.pdf")));
		Assert.Equal("layers", LayerName(copy + ".layers.gpkg"));
		SdeDatabase opened = SdeDatabase.Open(copy);
		Assert.Equal((1, 1), new SdeStore(opened).Counts());
		Assert.Equal(t.Database.DatabaseId, opened.DatabaseId);
		Assert.True(File.Exists(t.Database.FilePath));
		Assert.Throws<IOException>(() => DatabaseFiles.Copy(t.Database, copy));
	}

	[Fact]
	public void MovingADatabase_RemovesTheOriginal_ButKeepsPhotosAnotherDatabaseUses()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		AddPhoto(t, f, "mine.jpg");
		AddPhoto(t, f, "shared.jpg");
		// Another database in the same folder uses one of the photos too (they share the attachments folder).
		SdeStore neighbour = new SdeStore(SdeDatabase.Create(Path.Combine(t.Folder, "neighbour.sqlite")));
		AssessmentFile n = SampleCases.RanchFlood(neighbour);
		n.Attachments.Add(new AttachmentRecord { FileName = "shared.jpg", Active = true, AttachmentType = "Photo" });
		neighbour.SaveAssessment(n);
		string moved = Path.Combine(t.Folder, "moved", "sde.sqlite");

		using (DatabaseLease other = DatabaseLease.TryShared(t.Database.LockPath))
		{
			Assert.Throws<InvalidOperationException>(() => DatabaseFiles.Move(t.Database, moved));
			Assert.False(File.Exists(moved));
		}
		DatabaseFiles.Move(t.Database, moved);

		Assert.False(File.Exists(t.Database.FilePath));
		Assert.False(File.Exists(Path.Combine(t.Store.AttachmentsFolder, "mine.jpg")));
		Assert.True(File.Exists(Path.Combine(t.Store.AttachmentsFolder, "shared.jpg")));
		Assert.True(File.Exists(Path.Combine(t.Folder, "moved", "attachments", "mine.jpg")));
		Assert.Equal((1, 1), new SdeStore(SdeDatabase.Open(moved)).Counts());
	}
}
