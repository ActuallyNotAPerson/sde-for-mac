using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// v2.0 and v2.5 add Mac-only tables but must stay compatible both ways: nothing in them reaches FEMA's export, and an
/// older version of this app (which doesn't know them) can still open, change and delete structures in a newer database.
/// </summary>
public class CompatibilityTests
{
	/// <summary>Puts a row in every Mac-only table for this assessment, the way the app would.</summary>
	private static void FillMacTables(TempDatabase t, AssessmentFile f)
	{
		string aid = f.Assessment.AssessmentID.ToString("D");
		string pid = f.Property.PropertyID.ToString("D");
		using SqliteConnection c = t.Database.Connect();
		c.Execute("""
			INSERT INTO MacSetting (Key, Value) VALUES ('Anything', '1');
			INSERT INTO AssessmentStatus (AssessmentID, Status) VALUES (@aid, 'Reviewed');
			INSERT INTO EditHistory (PropertyID, AssessmentID, ChangedOn, Label) VALUES (@pid, @aid, '2026-10-01 09:00:00', 'Test');
			INSERT INTO SavedView (Name, State) VALUES ('v', '{}');
			INSERT INTO PhotoInfo (FileName, AssessmentID) VALUES ('x.jpg', @aid);
			INSERT INTO DamageSuggestion (AssessmentID, ElementName, SuggestedPercent) VALUES (@aid, 'Foundation', 10);
			INSERT INTO ContractorBid (BidID, AssessmentID, Amount) VALUES ('b1', @aid, 1000);
			INSERT INTO Appeal (AssessmentID, Outcome) VALUES (@aid, 'Pending');
			INSERT INTO LetterLog (LetterID, AssessmentID, Kind) VALUES ('l1', @aid, 'Notice');
			INSERT INTO ImprovementProject (ProjectID, PropertyID, Cost) VALUES ('p1', @pid, 5000);
			INSERT INTO SIDetermination (DeterminationID, PropertyID, DeterminedOn, ThresholdPercent, CumulativeCost) VALUES ('d1', @pid, '2026-10-01', 50, 5000);
			INSERT INTO StructureLink (Source, RecordId, PropertyID) VALUES ('Test', 'R-1', @pid);
			INSERT INTO ExternalLink (AssessmentID, Source, RecordId) VALUES (@aid, 'Test', 'R-1');
			INSERT INTO DeletedItem (ItemKey, Kind, DeletedOn, PurgeAfter) VALUES ('P:gone.jpg', 'Photo', '2026-10-01', '2026-10-31');
			UPDATE RowVersion SET Version = Version + 1 WHERE ObjectID = @aid;
			INSERT INTO Event (EventID, Name) VALUES ('e1', 'September flood');
			INSERT INTO EventAssessment (AssessmentID, EventID) VALUES (@aid, 'e1');
			INSERT INTO Team (TeamID, Name) VALUES ('t1', 'Team 1');
			INSERT INTO Assignment (AssignmentID, PropertyID, TeamID, EventID) VALUES ('as1', @pid, 't1', 'e1');
			INSERT INTO ReviewComment (CommentID, AssessmentID, Body, CreatedOn) VALUES ('c1', @aid, 'Check the depth', '2026-10-01');
			INSERT INTO QaDismissal (RuleId, ObjectID, Reason) VALUES ('DuplicateParcel', @aid, 'Two buildings');
			INSERT INTO ComplianceCase (CaseID, PropertyID, AssessmentID, Kind) VALUES ('cc1', @pid, @aid, 'SD');
			INSERT INTO ComplianceEntry (EntryID, CaseID, EntryOn, Text) VALUES ('ce1', 'cc1', '2026-10-01', 'Called the owner');
			INSERT INTO CheckOut (AssessmentID, PackageID, CheckedOutOn) VALUES (@aid, 'pk1', '2026-10-01');
			INSERT INTO PhotoMarkup (FileName, Markup) VALUES ('x.jpg', '[]');
			INSERT INTO Document (DocumentID, PropertyID, AssessmentID, Kind, FileName) VALUES ('d1', @pid, @aid, 'ElevationCertificate', 'ec.pdf');
			INSERT INTO MapLayer (LayerID, Name, SourceKind) VALUES ('l1', 'Parcels', 'File');
			INSERT INTO MapBookmark (BookmarkID, Name, MinX, MinY, MaxX, MaxY) VALUES ('b1', 'Downtown', -84.2, 39.6, -84.1, 39.7);
			""", new { aid, pid });
	}

	[Fact]
	public void MacTables_NeverChangeTheFemaExport()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		ExchangeService x = new ExchangeService(t.Store);
		(string before, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "a"));
		FillMacTables(t, f);
		(string after, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "b"));
		Assert.Equal(File.ReadAllBytes(Path.Combine(before, "Export.json")), File.ReadAllBytes(Path.Combine(after, "Export.json")));
	}

	[Fact]
	public void AnOlderVersion_CanStillDeleteAStructure_InAV2Database()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		FillMacTables(t, f);
		// v1.5's delete removes FEMA's rows only; the Mac-only rows must go with them (ON DELETE CASCADE), not block it.
		using SqliteConnection c = t.Database.Connect();
		object args = new { aid = f.Assessment.AssessmentID.ToString("D"), pid = f.Property.PropertyID.ToString("D") };
		c.Execute("""
			DELETE FROM ElementPercentage WHERE AssessmentID = @aid;
			DELETE FROM CostAdjustmentResult WHERE AssessmentID = @aid;
			DELETE FROM AdditionalAdjustment WHERE AssessmentID = @aid;
			DELETE FROM CustomField_Results WHERE ObjectIdentifier = @aid OR ObjectIdentifier = @pid;
			DELETE FROM Attachment WHERE AssessmentID = @aid;
			DELETE FROM LastFiveAssesments WHERE PropertyID = @pid;
			DELETE FROM Assessment WHERE AssessmentID = @aid;
			DELETE FROM Shapes WHERE PropertyID = @pid;
			DELETE FROM Property WHERE PropertyID = @pid;
			""", args);
		foreach (string table in new[] { "AssessmentStatus", "EditHistory", "PhotoInfo", "DamageSuggestion", "ContractorBid", "Appeal", "LetterLog", "ImprovementProject", "SIDetermination", "StructureLink", "ExternalLink",
			"EventAssessment", "Assignment", "ReviewComment", "ComplianceCase", "ComplianceEntry", "CheckOut", "Document" })
		{
			Assert.Equal(0, c.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}"));
		}
	}

	[Fact]
	public void ReopeningAV2Database_ChangesNothing()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		FillMacTables(t, f);
		string Dump(SqliteConnection c) => string.Join("\n", c.Query<string>("SELECT name || ':' || sql FROM sqlite_master ORDER BY name"))
			+ string.Join("\n", c.Query<string>("SELECT Key || '=' || Value FROM SchemaInfo ORDER BY Key"));
		string before;
		using (SqliteConnection c = t.Database.Connect())
		{
			before = Dump(c);
		}
		SqliteConnection.ClearAllPools();
		SdeDatabase reopened = SdeDatabase.Open(t.Database.FilePath);
		using SqliteConnection again = reopened.Connect();
		Assert.Equal(before, Dump(again));
		Assert.Equal(1, again.ExecuteScalar<int>("SELECT COUNT(*) FROM AssessmentStatus"));
	}

	/// <summary>What v2.0 (MacSchemaVersion 2) checks when it opens a database: the schema version, then its own Mac tables.</summary>
	[Fact]
	public void AV20App_OpensAV25Database_WithoutChangingIt()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		FillMacTables(t, f);
		using SqliteConnection c = t.Database.Connect();
		Assert.Equal(1, c.ExecuteScalar<int>("SELECT CAST(Value AS INTEGER) FROM SchemaInfo WHERE Key = 'SchemaVersion'"));
		// v2.0 runs its Schema.sql only when this is below 2.
		Assert.True(c.ExecuteScalar<int>("SELECT CAST(Value AS INTEGER) FROM SchemaInfo WHERE Key = 'MacSchemaVersion'") >= 2);
	}

	/// <summary>What v2.0's store does when it saves and edits: FEMA's rows only, no versions. v2.5 notices and isn't blocked.</summary>
	[Fact]
	public void AV20AppsWrites_WorkInAV25Database_AndAreNoticed()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		FillMacTables(t, f);
		VersionStamp loaded = t.Store.GetStamp(f.Assessment.AssessmentID);
		using ChangeWatcher watcher = new ChangeWatcher(t.Database);
		using (SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = t.Database.FilePath, Pooling = false, ForeignKeys = true }.ToString()))
		{
			c.Open();
			object args = new { aid = f.Assessment.AssessmentID.ToString("D"), pid = f.Property.PropertyID.ToString("D") };
			c.Execute("""
				UPDATE Property SET OwnerLastName = 'Changed in v2.0', LastUpdate = '2030-01-01 00:00:00' WHERE PropertyID = @pid;
				UPDATE Assessment SET InspectorName = 'v2.0', LastUpdate = '2030-01-01 00:00:00' WHERE AssessmentID = @aid;
				DELETE FROM ElementPercentage WHERE AssessmentID = @aid;
				INSERT INTO EditHistory (PropertyID, AssessmentID, ChangedOn, ChangedBy, Via, Field, Label, OldValue, NewValue)
				VALUES (@pid, @aid, '2030-01-01 00:00:00', 'v2.0', 'App', 'Assessment.InspectorName', 'Inspector', 'Inspector A', 'v2.0');
				""", args);
		}
		Assert.True(watcher.Poll().Unversioned);
		f.Assessment.CostData = "v2.5";
		Assert.Throws<ConcurrencyException>(() => t.Store.SaveAssessment(f, SaveContext.App.Expecting(loaded)));
	}
}
