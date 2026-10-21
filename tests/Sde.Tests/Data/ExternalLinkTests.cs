using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>The Mac-only ExternalLink table (imported inspections): migration, saving, cascading deletes, never exported.</summary>
public class ExternalLinkTests
{
	private static ImportedAssessment Imported(SdeStore store, string recordId, string inspectionId = "", PropertyRecord property = null)
	{
		property ??= new PropertyRecord { Active = true, StreetNumber = "1", StreetName = recordId, StructureTypeID = 1 };
		return new ImportedAssessment
		{
			File = AssessmentFile.CreateNew(property),
			Link = new ExternalLinkRecord { Source = "Accela", RecordId = recordId, InspectionId = inspectionId, SourceValues = "{}" },
		};
	}

	[Fact]
	public void OpeningAv10Database_AddsTheTable_AndLeavesFemaTablesAlone()
	{
		using TempDatabase t = new TempDatabase();
		SampleCases.RanchFlood(t.Store);
		string Columns(SqliteConnection c) => string.Join("\n", c.Query<string>("SELECT name FROM sqlite_master WHERE type IN ('table', 'index') AND name NOT LIKE 'sqlite_%' ORDER BY name")
			.Where(n => !n.Contains("ExternalLink"))
			.Select(n => n + ": " + string.Join(",", c.Query("SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info(@n)", new { n })
				.Select(r => $"{r.name} {r.type} {r.notnull} {r.dflt_value} {r.pk}"))));
		string before;
		using (SqliteConnection c = t.Database.Connect())
		{
			before = Columns(c);
			// What a v1.0 database looks like (versions before 2.0 didn't record MacSchemaVersion).
			c.Execute("DROP INDEX UX_ExternalLink_Key; DROP TABLE ExternalLink; DELETE FROM SchemaInfo WHERE Key = 'MacSchemaVersion';");
		}
		SqliteConnection.ClearAllPools();

		SdeDatabase reopened = SdeDatabase.Open(t.Database.FilePath);
		using SqliteConnection after = reopened.Connect();
		Assert.Equal(1, after.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ExternalLink'"));
		Assert.Equal(1, after.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'UX_ExternalLink_Key'"));
		Assert.Equal(before, Columns(after));
		Assert.Equal("1", after.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'SchemaVersion'"));
		Assert.Equal(SdeDatabase.MacSchemaVersion.ToString(), after.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'MacSchemaVersion'"));
		Assert.Single(new SdeStore(reopened).ListStructures());
	}

	[Fact]
	public void SaveImport_WritesStructuresAssessmentsAndLinks_AndListShowsTheRecordId()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord shared = new PropertyRecord { Active = true, StreetNumber = "5", StreetName = "Elm", StructureTypeID = 1, SquareFootage = 1200m };
		ImportedAssessment first = Imported(t.Store, "BLD-1", "11", shared);
		ImportedAssessment second = Imported(t.Store, "BLD-1", "12", shared);
		t.Store.SaveImport(new[] { first, new ImportedAssessment { File = second.File, Link = second.Link, ShapeTotal = 1200m } });

		Assert.NotEqual(Guid.Empty, shared.PropertyID);
		Assert.Single(t.Store.GetProperties());
		Assert.Equal(2, t.Store.GetAssessmentsForProperty(shared.PropertyID).Count);
		ShapeRecord shape = Assert.Single(t.Store.GetShapes(shared.PropertyID));
		Assert.Equal(("c", 1200m), (shape.ShapeType, shape.TotalSqFt));

		ExternalLinkRecord link = t.Store.GetExternalLink(first.File.Assessment.AssessmentID);
		Assert.Equal(("Accela", "BLD-1", "11"), (link.Source, link.RecordId, link.InspectionId));
		Assert.NotNull(link.ImportedOn);
		List<ExternalLinkRecord> links = t.Store.GetExternalLinks("Accela");
		Assert.Equal(2, links.Count);
		Assert.All(links, l => Assert.Equal(shared.PropertyID, l.PropertyID));
		Assert.All(t.Store.ListStructures(), s => Assert.Equal(("Accela", "BLD-1"), (s.ExternalSource, s.ExternalId)));
		Assert.Contains(shared.PropertyID, t.Store.LinkedPropertyIds());
		Assert.Empty(t.Store.FindPropertiesByParcel(null));

		// Saving again updates the link in place.
		first.Link.SourceValues = "{\"InspectorName\":\"A\"}";
		t.Store.SaveImport(new[] { first });
		Assert.Equal(2, t.Store.GetExternalLinks("Accela").Count);
		Assert.Equal("{\"InspectorName\":\"A\"}", t.Store.GetExternalLink(first.File.Assessment.AssessmentID).SourceValues);
	}

	[Fact]
	public void SaveImport_IsAllOrNothing()
	{
		using TempDatabase t = new TempDatabase();
		ImportedAssessment ok = Imported(t.Store, "BLD-1");
		ImportedAssessment clash = Imported(t.Store, "bld-1");   // same record (IDs match ignoring case)
		Assert.ThrowsAny<SqliteException>(() => t.Store.SaveImport(new[] { ok, clash }));
		Assert.Empty(t.Store.GetProperties());
		Assert.Equal((0, 0), t.Store.Counts());
		Assert.Empty(t.Store.GetExternalLinks("Accela"));
	}

	[Fact]
	public void DeletingAnAssessmentOrStructure_DeletesItsLink_EvenFromCodeThatDoesntKnowTheTable()
	{
		using TempDatabase t = new TempDatabase();
		ImportedAssessment a = Imported(t.Store, "A");
		ImportedAssessment b = Imported(t.Store, "B");
		ImportedAssessment c = Imported(t.Store, "C");
		t.Store.SaveImport(new[] { a, b, c });

		t.Store.DeleteAssessment(a.File.Assessment.AssessmentID);
		t.Store.DeleteProperty(b.File.Property.PropertyID);
		Assert.Equal(new[] { "C" }, t.Store.GetExternalLinks("Accela").Select(l => l.RecordId));

		// v1.0 deletes the assessment's own rows and then the assessment; the link must not block it.
		using SqliteConnection conn = t.Database.Connect();
		Guid id = c.File.Assessment.AssessmentID;
		conn.Execute("""
			DELETE FROM ElementPercentage WHERE AssessmentID = @id; DELETE FROM CostAdjustmentResult WHERE AssessmentID = @id;
			DELETE FROM AdditionalAdjustment WHERE AssessmentID = @id; DELETE FROM CustomField_Results WHERE ObjectIdentifier = @id;
			DELETE FROM LastFiveAssesments WHERE AssessmentID = @id; DELETE FROM Assessment WHERE AssessmentID = @id;
			""", new { id = id.ToString() });
		Assert.Equal(0, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM ExternalLink"));
	}

	[Fact]
	public void ExportJson_IsIdenticalWithAndWithoutLinks()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		ExchangeService exchange = new ExchangeService(t.Store);
		string plain = exchange.Export(exchange.ReadDataset(), Path.Combine(t.Folder, "plain")).Folder;

		t.Store.SaveImport(new[]
		{
			new ImportedAssessment
			{
				File = t.Store.LoadAssessment(f.Assessment.AssessmentID),
				Link = new ExternalLinkRecord { Source = "Accela", RecordId = "BLD-2026-00123", InspectionId = "4471" },
			},
		});
		// Saving touched LastUpdate; put it back so only the link differs.
		using (SqliteConnection c = t.Database.Connect())
		{
			c.Execute("UPDATE Assessment SET LastUpdate = @u WHERE AssessmentID = @id; UPDATE Property SET LastUpdate = @p WHERE PropertyID = @pid",
				new { u = f.Assessment.LastUpdate, id = f.Assessment.AssessmentID.ToString(), p = f.Property.LastUpdate, pid = f.Property.PropertyID.ToString() });
		}
		Assert.NotNull(t.Store.GetExternalLink(f.Assessment.AssessmentID));
		string linked = exchange.Export(exchange.ReadDataset(), Path.Combine(t.Folder, "linked")).Folder;

		Assert.Equal(File.ReadAllBytes(Path.Combine(plain, "Export.json")), File.ReadAllBytes(Path.Combine(linked, "Export.json")));
	}
}
