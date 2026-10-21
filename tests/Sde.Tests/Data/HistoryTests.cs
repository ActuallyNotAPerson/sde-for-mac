using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Edit history, statuses and locks, and per-database settings (v2.0, Mac-only tables).</summary>
public class HistoryTests
{
	private sealed class TestClock
	{
		public DateTime Now { get; set; } = new DateTime(2026, 10, 1, 9, 0, 0);

		public void Advance(int minutes) => Now = Now.AddMinutes(minutes);
	}

	private static (TempDatabase Db, TestClock Clock, AssessmentFile File) Setup()
	{
		TempDatabase t = new TempDatabase();
		TestClock clock = new TestClock();
		t.Store.Clock = () => clock.Now;
		t.Store.UserName = "Inspector A";
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		return (t, clock, t.Store.LoadAssessment(f.Assessment.AssessmentID));
	}

	private static List<EditHistoryRecord> Fields(SdeStore store, Guid assessmentId, string key) =>
		store.GetHistory(assessmentId).Where(h => h.Field == key).ToList();

	[Fact]
	public void NewAssessment_RecordsCreatedEvent()
	{
		(TempDatabase t, _, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		List<EditHistoryRecord> h = t.Store.GetHistory(f.Assessment.AssessmentID);
		EditHistoryRecord created = Assert.Single(h, r => r.Label == SdeStore.CreatedEvent);
		Assert.True(created.IsEvent);
		Assert.Equal("Inspector A", created.ChangedBy);
		Assert.Equal(SaveVia.App, created.Via);
		// The sample's own edits after creation are recorded as field changes.
		Assert.Contains(h, r => r.Field == "Assessment.InspectorName" && r.NewValue == "Inspector A");
	}

	[Fact]
	public void FieldEdit_IsRecorded_AndRepeatedEditsMerge_WithinTenMinutes()
	{
		(TempDatabase t, TestClock clock, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		clock.Advance(30);
		f.Assessment.InspectorName = "Lee";
		t.Store.SaveAssessment(f);
		clock.Advance(2);
		f.Assessment.InspectorName = "Lee Park";
		t.Store.SaveAssessment(f);

		EditHistoryRecord row = Assert.Single(Fields(t.Store, id, "Assessment.InspectorName"), r => r.ChangedOn > new DateTime(2026, 10, 1, 9, 1, 0));
		Assert.Equal("Inspector A", row.OldValue);
		Assert.Equal("Lee Park", row.NewValue);
		Assert.Equal(clock.Now, row.ChangedOn);

		clock.Advance(11);
		f.Assessment.InspectorName = "L. Park";
		t.Store.SaveAssessment(f);
		Assert.Equal(2, Fields(t.Store, id, "Assessment.InspectorName").Count(r => r.ChangedOn > new DateTime(2026, 10, 1, 9, 1, 0)));
	}

	[Fact]
	public void EditUndoneWithinTheWindow_LeavesNoRow()
	{
		(TempDatabase t, TestClock clock, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		clock.Advance(30);
		int before = t.Store.GetHistory(f.Assessment.AssessmentID).Count;
		f.Property.City = "Dayton";
		t.Store.SaveAssessment(f);
		clock.Advance(1);
		f.Property.City = "Exampleville";
		t.Store.SaveAssessment(f);
		Assert.Equal(before, t.Store.GetHistory(f.Assessment.AssessmentID).Count);
	}

	[Fact]
	public void DifferentPeople_DontMerge()
	{
		(TempDatabase t, TestClock clock, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		clock.Advance(30);
		f.Assessment.InspectorName = "Lee";
		t.Store.SaveAssessment(f);
		clock.Advance(1);
		f.Assessment.InspectorName = "Kim";
		t.Store.SaveAssessment(f, new SaveContext { User = "Reviewer B" });
		List<EditHistoryRecord> rows = Fields(t.Store, f.Assessment.AssessmentID, "Assessment.InspectorName").Where(r => r.ChangedOn > new DateTime(2026, 10, 1, 9, 1, 0)).ToList();
		Assert.Equal(new[] { "Reviewer B", "Inspector A" }, rows.Select(r => r.ChangedBy));
	}

	[Fact]
	public void BatchEdits_NeverMerge_AndCanBeListed()
	{
		(TempDatabase t, TestClock clock, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		clock.Advance(30);
		f.Assessment.InspectorName = "Lee";
		t.Store.SaveAssessment(f);
		SaveContext batch = SaveContext.Batch(SaveVia.BulkEdit);
		f.Assessment.InspectorName = "Kim";
		t.Store.SaveAssessment(f, batch);
		List<EditHistoryRecord> rows = t.Store.GetBatch(batch.BatchId);
		EditHistoryRecord row = Assert.Single(rows);
		Assert.Equal("Lee", row.OldValue);
		Assert.Equal("Kim", row.NewValue);
		Assert.Equal(SaveVia.BulkEdit, row.Via);
	}

	[Fact]
	public void PropertySavedOnItsOwn_ShowsInTheAssessmentsHistory()
	{
		(TempDatabase t, TestClock clock, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		clock.Advance(30);
		PropertyRecord p = t.Store.GetProperty(f.Property.PropertyID);
		p.Latitude = 39.1m;
		t.Store.SaveProperties(new[] { p });
		EditHistoryRecord row = Assert.Single(t.Store.GetHistory(f.Assessment.AssessmentID), r => r.Field == "Property.Latitude");
		Assert.Null(row.AssessmentID);
		Assert.Equal(SaveVia.Import, row.Via);
		Assert.NotNull(row.BatchId);
	}

	[Fact]
	public void Final_RefusesChanges_ButAllowsUnchangedSaves()
	{
		(TempDatabase t, TestClock clock, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		t.Store.SetStatus(id, AssessmentStatus.Final);
		Assert.True(t.Store.IsLocked(id));
		DateTime? lastUpdate = t.Store.LoadAssessment(id).Assessment.LastUpdate;

		clock.Advance(5);
		t.Store.SaveAssessment(t.Store.LoadAssessment(id));       // nothing changed: allowed, and not written
		Assert.Equal(lastUpdate, t.Store.LoadAssessment(id).Assessment.LastUpdate);
		Assert.False(t.Store.ChangedSinceLocked(id));

		f = t.Store.LoadAssessment(id);
		f.Assessment.InspectorName = "Someone else";
		AssessmentLockedException e = Assert.Throws<AssessmentLockedException>(() => t.Store.SaveAssessment(f));
		Assert.Equal(AssessmentStatus.Final, e.Status);
		Assert.Contains(e.Changes, d => d.Key == "Assessment.InspectorName");
		Assert.DoesNotContain("—", e.Message);
		Assert.Equal("Inspector A", t.Store.LoadAssessment(id).Assessment.InspectorName);

		t.Store.SaveAssessment(f, new SaveContext { AllowLocked = true, Via = SaveVia.Restore });
		Assert.Equal("Someone else", t.Store.LoadAssessment(id).Assessment.InspectorName);
	}

	[Fact]
	public void Reopening_NeedsAReason_AndIsRecorded()
	{
		(TempDatabase t, _, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		Assert.Equal(AssessmentStatus.Draft, t.Store.GetStatus(id).Value);
		t.Store.SetStatus(id, AssessmentStatus.Reviewed);
		t.Store.SetStatus(id, AssessmentStatus.LetterSent);
		Assert.Throws<ArgumentException>(() => t.Store.SetStatus(id, AssessmentStatus.Appeal));
		Assert.Throws<ArgumentException>(() => t.Store.SetStatus(id, AssessmentStatus.Draft, "  "));
		AssessmentStatusRecord s = t.Store.SetStatus(id, AssessmentStatus.Appeal, "Owner sent a contractor bid");
		Assert.False(s.IsLocked);
		Assert.Null(s.LockedLastUpdate);

		List<EditHistoryRecord> status = t.Store.GetHistory(id).Where(h => h.Field == "Status").ToList();
		Assert.Equal(new[] { "Reopened: Owner sent a contractor bid", "Status: Letter sent", "Status: Reviewed" }, status.Select(h => h.Label));
		Assert.Equal("Letter sent", status[0].OldValue);
		Assert.Equal("Appeal", status[0].NewValue);
		Assert.Single(t.Store.GetStatuses());
	}

	[Fact]
	public void Final_IsBlocked_WhileDepthSuggestionsAreUnaccepted()
	{
		(TempDatabase t, _, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		using (SqliteConnection c = t.Database.Connect())
		{
			c.Execute("INSERT INTO DamageSuggestion (AssessmentID, ElementName, SuggestedPercent) VALUES (@id, 'Foundation', 10)", new { id = id.ToString() });
		}
		InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => t.Store.SetStatus(id, AssessmentStatus.Final));
		Assert.Contains("depth table", e.Message);
		t.Store.SetStatus(id, AssessmentStatus.Reviewed);
	}

	[Fact]
	public void EditByAnOlderVersion_IsDetected()
	{
		(TempDatabase t, TestClock clock, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		t.Store.SetStatus(id, AssessmentStatus.Final);
		using (SqliteConnection c = t.Database.Connect())
		{
			// What v1.5, which doesn't know about locks, does when it saves.
			c.Execute("UPDATE Assessment SET InspectorName = 'v1.5 user', LastUpdate = '2030-01-01 00:00:00' WHERE AssessmentID = @id", new { id = id.ToString() });
		}
		Assert.True(t.Store.ChangedSinceLocked(id));
	}

	[Fact]
	public void SaveImport_SkipsLockedAssessments_AndIsOneBatch()
	{
		(TempDatabase t, _, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		t.Store.SetStatus(id, AssessmentStatus.Final);
		AssessmentFile changed = t.Store.LoadAssessment(id);
		changed.Assessment.InspectorName = "From import";
		AssessmentFile fresh = AssessmentFile.CreateNew(new PropertyRecord { Active = true, StreetNumber = "9", StreetName = "Elm", StructureTypeID = 1 });
		SaveContext ctx = SaveContext.Batch(SaveVia.Import, "Imported from inspections.csv");
		IReadOnlyList<Guid> locked = t.Store.SaveImport(new[]
		{
			new ImportedAssessment { File = changed, Link = new ExternalLinkRecord { Source = "Test", RecordId = "R-1" } },
			new ImportedAssessment { File = fresh, Link = new ExternalLinkRecord { Source = "Test", RecordId = "R-2" } },
		}, ctx);
		Assert.Equal(new[] { id }, locked);
		Assert.Equal("Inspector A", t.Store.LoadAssessment(id).Assessment.InspectorName);
		Assert.Null(t.Store.GetExternalLink(id));
		EditHistoryRecord created = Assert.Single(t.Store.GetBatch(ctx.BatchId));
		Assert.Equal("Imported from inspections.csv", created.Label);
		Assert.Equal(fresh.Assessment.AssessmentID, created.AssessmentID);
	}

	[Fact]
	public void SdeImport_DoesntReplaceFinal_AndRecordsTheImport()
	{
		(TempDatabase t, _, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		ExchangeService x = new ExchangeService(t.Store);
		(string folder, _) = x.Export(x.ReadDataset(), t.Folder);

		ImportResult first = x.ImportFolder(folder, ExistingRecordAction.Overwrite);
		Assert.Equal(1, first.AssessmentsUpdated);
		Assert.Contains(t.Store.GetHistory(id), h => h.Label == "Imported from SDE_1" && h.Via == SaveVia.Import);

		t.Store.SetStatus(id, AssessmentStatus.Final);
		ImportResult second = x.ImportFolder(folder, ExistingRecordAction.Overwrite);
		Assert.Equal(0, second.AssessmentsUpdated);
		Assert.Contains(second.Warnings, w => w.Contains("Final"));
	}

	[Fact]
	public void Settings_AndSavedViews_RoundTrip()
	{
		using TempDatabase t = new TempDatabase();
		Assert.Equal(40, t.Store.GetSetting("Cumulative.LookbackYears", 40));
		t.Store.SetSetting("Cumulative.LookbackYears", 10);
		Assert.Equal(10, t.Store.GetSetting("Cumulative.LookbackYears", 40));
		t.Store.SetSetting("List.Columns", new List<string> { "Address", "Owner" });
		Assert.Equal(new[] { "Address", "Owner" }, t.Store.GetSetting<List<string>>("List.Columns"));
		t.Store.SetSetting<List<string>>("List.Columns", null);
		Assert.Null(t.Store.GetSetting<List<string>>("List.Columns"));
		using (SqliteConnection c = t.Database.Connect())
		{
			c.Execute("INSERT INTO MacSetting (Key, Value) VALUES ('Broken', 'not json {')");
		}
		Assert.Equal(7, t.Store.GetSetting("Broken", 7));

		t.Store.SaveView("Zone AE", "{\"zone\":\"AE\"}");
		t.Store.SaveView("zone ae", "{\"zone\":\"AE\",\"min\":50}");
		SavedViewRecord v = Assert.Single(t.Store.GetSavedViews());
		Assert.Equal("zone ae", v.Name);
		t.Store.DeleteView("ZONE AE");
		Assert.Empty(t.Store.GetSavedViews());
	}

	[Fact]
	public void MacTables_NeverReachTheExport()
	{
		(TempDatabase t, _, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		ExchangeService x = new ExchangeService(t.Store);
		(string plain, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "a"));
		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		t.Store.SetSetting("Anything", 1);
		t.Store.SaveView("v", "{}");
		(string withMac, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "b"));
		Assert.Equal(File.ReadAllText(Path.Combine(plain, "Export.json")), File.ReadAllText(Path.Combine(withMac, "Export.json")));
	}
}
