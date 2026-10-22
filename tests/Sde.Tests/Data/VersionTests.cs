using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// v2.5's versions and optimistic concurrency: every write bumps the versions of what it changed, a save that expects
/// older versions is refused, nothing is written when nothing changed, and the change watcher tells this copy of the
/// app about other programs' saves.
/// </summary>
public class VersionTests
{
	private static (TempDatabase T, AssessmentFile File) Setup()
	{
		TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		return (t, t.Store.LoadAssessment(f.Assessment.AssessmentID));
	}

	/// <summary>Another program using the same database file (the AI assistant server, another Mac).</summary>
	private static SdeStore OtherWriter(TempDatabase t) => new SdeStore(SdeDatabase.Open(t.Database.FilePath));

	private static void SetLastUpdate(TempDatabase t, AssessmentFile f, DateTime when)
	{
		using SqliteConnection c = t.Database.Connect();
		c.Execute("UPDATE Assessment SET LastUpdate = @when WHERE AssessmentID = @AssessmentID", new { when, f.Assessment.AssessmentID });
		c.Execute("UPDATE Property SET LastUpdate = @when WHERE PropertyID = @PropertyID", new { when, f.Property.PropertyID });
	}

	[Fact]
	public void SavingAnUnchangedAssessment_WritesNothing()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		DateTime old = new DateTime(2026, 9, 1, 8, 0, 0);
		SetLastUpdate(t, f, old);
		AssessmentFile loaded = t.Store.LoadAssessment(f.Assessment.AssessmentID);
		VersionStamp before = t.Store.GetStamp(f.Assessment.AssessmentID);
		int history = t.Store.GetHistory(f.Assessment.AssessmentID).Count;

		VersionStamp after = t.Store.SaveAssessment(loaded);

		Assert.Equal(before, after);
		Assert.Equal(old, t.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment.LastUpdate);
		Assert.Equal(old, t.Store.GetProperty(f.Property.PropertyID).LastUpdate);
		Assert.Equal(history, t.Store.GetHistory(f.Assessment.AssessmentID).Count);
		// Saving the structure on its own unchanged is the same.
		t.Store.SaveProperty(t.Store.GetProperty(f.Property.PropertyID));
		Assert.Equal(old, t.Store.GetProperty(f.Property.PropertyID).LastUpdate);
		Assert.Equal(before, t.Store.GetStamp(f.Assessment.AssessmentID));
	}

	[Fact]
	public void AnAssessmentField_BumpsTheAssessment_AndAStructureFieldBumpsBoth()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		VersionStamp v0 = t.Store.GetStamp(f.Assessment.AssessmentID);
		f.Assessment.InspectorName = "Lee";
		VersionStamp v1 = t.Store.SaveAssessment(f);
		Assert.Equal(v0.Assessment + 1, v1.Assessment);
		Assert.Equal(v0.Property, v1.Property);
		f.Property.OwnerLastName = "Other";
		VersionStamp v2 = t.Store.SaveAssessment(f);
		Assert.Equal(v1.Assessment + 1, v2.Assessment);
		Assert.Equal(v1.Property + 1, v2.Property);
		Assert.Equal(v2, t.Store.GetStamp(f.Assessment.AssessmentID));
	}

	[Theory]
	[InlineData("SaveAssessment", "A")]
	[InlineData("SaveAssessmentWithShapes", "P")]
	[InlineData("Apply", "A")]
	[InlineData("SaveProperty", "P")]
	[InlineData("SaveProperties", "P")]
	[InlineData("SaveShapes", "P")]
	[InlineData("SaveAssessments", "A")]
	[InlineData("ApplyChanges", "A")]
	[InlineData("RevertBatch", "A")]
	[InlineData("ReplaceStructureField", "P")]
	[InlineData("ReplaceAssessmentField", "A")]
	[InlineData("AttachPhotos", "A")]
	[InlineData("SaveImport", "A")]
	[InlineData("SaveStructureImport", "P")]
	[InlineData("TrashAssessment", "A")]
	[InlineData("TrashProperty", "AP")]
	[InlineData("Restore", "A")]
	[InlineData("EmptyTrash", "A")]
	[InlineData("DeleteAssessment", "A")]
	[InlineData("DeleteProperty", "AP")]
	[InlineData("SetStatus", "A")]
	[InlineData("MarkLetterSent", "A")]
	[InlineData("SaveCustomFieldResults", "P")]
	[InlineData("ExchangeImport", "AP")]
	public void EveryPublicWriteMethod_BumpsTheVersions(string method, string bumps)
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		SdeStore s = t.Store;
		Guid aid = f.Assessment.AssessmentID;
		Guid pid = f.Property.PropertyID;
		if (method is "Restore" or "EmptyTrash")
		{
			s.TrashAssessment(aid);
		}
		string batch = method == "RevertBatch" ? s.ApplyChanges(new[] { aid }, new[] { new FieldChange("Assessment.InspectorName", "Batch") }, SaveContext.Batch(SaveVia.BulkEdit)).BatchId : null;
		long a0 = s.GetVersion(aid);
		long p0 = s.GetVersion(pid);

		switch (method)
		{
			case "SaveAssessment":
				f.Assessment.InspectorName = "Lee";
				s.SaveAssessment(f);
				break;
			case "SaveAssessmentWithShapes":
				s.SaveAssessment(f, shapes: new[] { new ShapeRecord { ShapeType = "c", TotalSqFt = 1450m } });
				break;
			case "Apply":
				s.Apply(aid, x => x.Assessment.InspectorName = "Lee");
				break;
			case "SaveProperty":
				PropertyRecord p = s.GetProperty(pid);
				p.City = "Elsewhere";
				s.SaveProperty(p);
				break;
			case "SaveProperties":
				PropertyRecord q = s.GetProperty(pid);
				q.Latitude = 39.5m;
				s.SaveProperties(new[] { q });
				break;
			case "SaveShapes":
				s.SaveShapes(pid, new[] { new ShapeRecord { ShapeType = "c", TotalSqFt = 1500m } });
				break;
			case "SaveAssessments":
				f.Assessment.InspectorName = "Lee";
				s.SaveAssessments(new[] { f }, SaveContext.Batch(SaveVia.BulkEdit));
				break;
			case "ApplyChanges":
				s.ApplyChanges(new[] { aid }, new[] { new FieldChange("Assessment.InspectorName", "Lee") }, SaveContext.Batch(SaveVia.BulkEdit));
				break;
			case "RevertBatch":
				s.RevertBatch(batch);
				break;
			case "ReplaceStructureField":
				s.Replace(s.SearchFields().Single(x => x.Field.Member == nameof(PropertyRecord.City)), "Exampleville", "Elsewhere");
				break;
			case "ReplaceAssessmentField":
				s.Replace(s.SearchFields().Single(x => x.Field.Member == nameof(AssessmentRecord.InspectorName)), "Inspector A", "Lee");
				break;
			case "AttachPhotos":
				AttachmentRecord photo = new AttachmentRecord { FileName = "added.jpg", AttachmentType = "Photo" };
				s.AttachPhotos(new[] { new AttachedPhoto(aid, photo, null) }, SaveContext.App);
				break;
			case "SaveImport":
				f.Assessment.InspectorName = "Imported";
				s.SaveImport(new[] { new ImportedAssessment { File = f, Link = new ExternalLinkRecord { Source = "Test", RecordId = "R-1" } } });
				break;
			case "SaveStructureImport":
				PropertyRecord r = s.GetProperty(pid);
				r.ParcelNumber = "P-77";
				s.SaveStructureImport(new[] { new ImportedStructure { Property = r, Link = new StructureLinkRecord { Source = "Test", RecordId = "P-77" } } });
				break;
			case "TrashAssessment":
				s.TrashAssessment(aid);
				break;
			case "TrashProperty":
				s.TrashProperty(pid);
				break;
			case "Restore":
				s.Restore("A:" + aid.ToString("D"));
				break;
			case "EmptyTrash":
				s.EmptyTrash();
				break;
			case "DeleteAssessment":
				s.DeleteAssessment(aid);
				break;
			case "DeleteProperty":
				s.DeleteProperty(pid);
				break;
			case "SetStatus":
				s.SetStatus(aid, AssessmentStatus.Reviewed);
				break;
			case "MarkLetterSent":
				s.MarkLetterSent(aid);
				break;
			case "SaveCustomFieldResults":
				CustomFieldRecord cf = s.Reference.CustomFields.First();
				s.SaveCustomFieldResults(pid, "property", new[] { new CustomFieldResultRecord { CustomFieldID = cf.CustomFieldID, Result = "x", zOrder = cf.zOrder } });
				break;
			case "ExchangeImport":
				ExchangeService x = new ExchangeService(s);
				SdeDataset data = x.ReadDataset();
				data.Assessments.Single().InspectorName = "From SDE_1";
				x.Import(data, null, ExistingRecordAction.Overwrite, "SDE_1");
				break;
		}

		if (bumps.Contains('A'))
		{
			Assert.True(s.GetVersion(aid) > a0, method + " didn't bump the assessment's version");
		}
		if (bumps.Contains('P'))
		{
			Assert.True(s.GetVersion(pid) > p0, method + " didn't bump the structure's version");
		}
	}

	[Fact]
	public void AStaleSave_IsRefused_AndWritesNothing()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid aid = f.Assessment.AssessmentID;
		VersionStamp loaded = t.Store.GetStamp(aid);
		SdeStore other = OtherWriter(t);
		other.Apply(aid, x => x.Assessment.InspectorName = "Other");

		f.Assessment.CostData = "Mine";
		ConcurrencyException ex = Assert.Throws<ConcurrencyException>(() => t.Store.SaveAssessment(f, SaveContext.App.Expecting(loaded)));

		Assert.Equal(loaded.Assessment + 1, ex.Current.Assessment);
		AssessmentFile now = t.Store.LoadAssessment(aid);
		Assert.Equal("Other", now.Assessment.InspectorName);
		Assert.NotEqual("Mine", now.Assessment.CostData);
		// With the current versions it goes through.
		t.Store.SaveAssessment(f, SaveContext.App.Expecting(ex.Current));
		Assert.Equal("Mine", t.Store.LoadAssessment(aid).Assessment.CostData);
	}

	[Fact]
	public void AStructureChangedElsewhere_RefusesASaveOfAnotherAssessmentOfIt()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		VersionStamp loaded = t.Store.GetStamp(f.Assessment.AssessmentID);
		PropertyRecord p = OtherWriter(t).GetProperty(f.Property.PropertyID);
		p.Latitude = 40m;
		OtherWriter(t).SaveProperty(p, new SaveContext { Via = SaveVia.Map });

		f.Assessment.InspectorName = "Lee";
		Assert.Throws<ConcurrencyException>(() => t.Store.SaveAssessment(f, SaveContext.App.Expecting(loaded)));
		Assert.Equal(40m, t.Store.GetProperty(f.Property.PropertyID).Latitude);
	}

	[Fact]
	public void SavingASiblingAssessment_IsNotAConflict()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		AssessmentFile sibling = t.Store.CreateAssessment(f.Property.PropertyID, applyDefaults: false);
		VersionStamp loaded = t.Store.GetStamp(f.Assessment.AssessmentID);

		sibling.Assessment.InspectorName = "Second visit";
		OtherWriter(t).SaveAssessment(sibling);

		f.Assessment.InspectorName = "Lee";
		t.Store.SaveAssessment(f, SaveContext.App.Expecting(loaded));
		Assert.Equal("Lee", t.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment.InspectorName);
	}

	[Fact]
	public void AnOlderVersionsSave_IsCaughtByLastUpdate()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		VersionStamp loaded = t.Store.GetStamp(f.Assessment.AssessmentID);
		using ChangeWatcher watcher = new ChangeWatcher(t.Database);
		// What v2.0 does when it saves: no RowVersion row, a new LastUpdate.
		using (SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = t.Database.FilePath, Pooling = false }.ToString()))
		{
			c.Open();
			c.Execute("UPDATE Assessment SET InspectorName = 'v2.0', LastUpdate = '2030-01-01 00:00:00' WHERE AssessmentID = @AssessmentID", new { f.Assessment.AssessmentID });
		}

		ExternalChanges changes = watcher.Poll();
		Assert.NotNull(changes);
		Assert.True(changes.Unversioned);
		f.Assessment.CostData = "Mine";
		Assert.Throws<ConcurrencyException>(() => t.Store.SaveAssessment(f, SaveContext.App.Expecting(loaded)));
	}

	[Fact]
	public void ChangeWatcher_ReportsOthersSaves_NotItsOwn()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		using ChangeWatcher watcher = new ChangeWatcher(t.Database);
		Assert.Null(watcher.Poll());

		f.Assessment.InspectorName = "Mine";
		t.Store.SaveAssessment(f);
		t.Store.SetSetting("Anything", 1);
		Assert.Null(watcher.Poll());

		OtherWriter(t).Apply(f.Assessment.AssessmentID, x => x.Property.OwnerLastName = "Theirs");
		ExternalChanges changes = watcher.Poll();
		Assert.NotNull(changes);
		Assert.Contains(f.Assessment.AssessmentID, changes.Assessments);
		Assert.Contains(f.Property.PropertyID, changes.Properties);
		Assert.False(changes.Unversioned);
		Assert.Null(watcher.Poll());
	}

	[Fact]
	public void Transactions_TakeTheWriteLockWhenTheyStart()
	{
		using TempDatabase t = new TempDatabase();
		t.Database.InTransaction((c, tx) =>
		{
			// Nothing written yet, but another connection already can't start writing.
			using SqliteConnection other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = t.Database.FilePath, Pooling = false, DefaultTimeout = 1 }.ToString());
			other.Open();
			other.Execute("PRAGMA busy_timeout = 0");
			SqliteException busy = Assert.Throws<SqliteException>(() => other.Execute("BEGIN IMMEDIATE"));
			Assert.Equal(5, busy.SqliteErrorCode);   // SQLITE_BUSY
		});
	}

	[Fact]
	public void LoadAssessments_UsesOneConnection()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		List<Guid> ids = new List<Guid> { f.Assessment.AssessmentID };
		for (int i = 0; i < 5; i++)
		{
			ids.Add(t.Store.CreateAssessment(f.Property.PropertyID, applyDefaults: false).Assessment.AssessmentID);
		}
		long before = t.Database.ConnectionsOpened;
		Assert.Equal(6, t.Store.LoadAssessments(ids).Count);
		Assert.Equal(before + 1, t.Database.ConnectionsOpened);
		before = t.Database.ConnectionsOpened;
		Assert.Equal(6, t.Store.LoadAllAssessments().Count);
		Assert.Equal(before + 1, t.Database.ConnectionsOpened);
	}

	[Fact]
	public void Apply_ChangesAndSavesInOneTransaction_WithHistory()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		SaveContext ctx = SaveContext.Batch(SaveVia.AiAssistant);
		AssessmentFile saved = t.Store.Apply(f.Assessment.AssessmentID, x => x.Assessment.InspectorName = "Assistant", ctx);
		Assert.Equal("Assistant", saved.Assessment.InspectorName);
		EditHistoryRecord row = Assert.Single(t.Store.GetBatch(ctx.BatchId), h => h.Field == "Assessment.InspectorName");
		Assert.Equal(SaveVia.AiAssistant, row.Via);
		Assert.Throws<ArgumentException>(() => t.Store.Apply(Guid.NewGuid(), x => { }));
	}

	[Fact]
	public void ASchemaChange_RerunsTheSchema_OnOpening()
	{
		using TempDatabase t = new TempDatabase();
		using (SqliteConnection c = t.Database.Connect())
		{
			c.Execute("DROP TABLE MapBookmark; UPDATE SchemaInfo SET Value = 'old' WHERE Key = 'MacSchemaHash'");
		}
		Assert.True(SdeDatabase.NeedsUpgrade(t.Database.FilePath));
		SdeDatabase reopened = SdeDatabase.Open(t.Database.FilePath);
		using SqliteConnection again = reopened.Connect();
		Assert.Equal(1, again.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'MapBookmark'"));
		Assert.Equal(SdeDatabase.SchemaHash, again.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'MacSchemaHash'"));
		Assert.False(SdeDatabase.NeedsUpgrade(t.Database.FilePath));
	}

	[Fact]
	public void ADatabase_HasItsOwnId_ThatSurvivesReopening()
	{
		using TempDatabase t = new TempDatabase();
		using TempDatabase u = new TempDatabase();
		Guid id = t.Database.DatabaseId;
		Assert.NotEqual(Guid.Empty, id);
		Assert.NotEqual(id, u.Database.DatabaseId);
		Assert.Equal(id, SdeDatabase.Open(t.Database.FilePath).DatabaseId);
	}
}
