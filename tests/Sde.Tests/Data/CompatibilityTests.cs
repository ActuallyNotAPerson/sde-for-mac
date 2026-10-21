using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// v2.0 adds Mac-only tables but must stay compatible both ways: nothing in them reaches FEMA's export, and an older
/// version of this app (which doesn't know them) can still open, change and delete structures in a v2.0 database.
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
		foreach (string table in new[] { "AssessmentStatus", "EditHistory", "PhotoInfo", "DamageSuggestion", "ContractorBid", "Appeal", "LetterLog", "ImprovementProject", "SIDetermination", "StructureLink", "ExternalLink" })
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
}
