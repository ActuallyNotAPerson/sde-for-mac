using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Interop.Tests;

/// <summary>
/// v2.0's importer beyond v1.5: structures (an assessor roll) linked through StructureLink, matching new records by
/// parcel or address (never guessing between several), the building-value question, Final assessments left alone,
/// and one edit-history batch per import.
/// </summary>
public class SourceImportModesTests
{
	private static string Fixture(string name) => SourceMatchingTests.Fixture(name);

	private static SourcePreview Preview(SdeStore store, SourceTable raw, SourceProfile p) => SourceImport.Preview(SourceTable.Shape(raw, p), p, store, TestContext.Current.CancellationToken);

	private static SourcePreview Preview(SdeStore store, string file, SourceProfile p) => Preview(store, SourceTable.Read(Fixture(file)), p);

	private static ImportRow Row(SourcePreview preview, string recordId) => preview.Rows.Single(r => r.RecordId == recordId);

	/// <summary>A copy of the table with one cell changed (rows found by their first column).</summary>
	private static SourceTable WithCell(SourceTable t, string recordId, string column, string value)
	{
		int c = t.IndexOf(column);
		return new SourceTable(t.Path, t.SheetName, t.Columns, t.Rows.Select(row =>
		{
			List<string> values = row.Values.ToList();
			if (values[0] == recordId)
			{
				values[c] = value;
			}
			return new SourceRow(row.RowNumber, values);
		}).ToList());
	}

	[Fact]
	public void Structures_CreatedLinkedAndListed_OneHistoryBatch()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = ShippedProfiles.Get(ShippedProfiles.CountyParcels);
		SourcePreview preview = Preview(t.Store, "county-parcels.csv", p);
		Assert.Equal(4, preview.Count(ImportRowStatus.NewStructure));
		ImportRow first = Row(preview, "900-01-001");
		Assert.Null(first.File);                                                   // no assessment
		Assert.Equal(("100", "SAMPLE", "ALEX", "EXAMPLE"), (first.Property.StreetNumber, first.Property.StreetName, first.Property.OwnerFirstName, first.Property.OwnerLastName));
		Assert.Equal((1, 104, 1955, 1200m), (first.Property.StructureTypeID, first.Property.StoryTypeID, first.Property.YearOfConstruction, first.Property.SquareFootage));
		Assert.Equal(2, Row(preview, "900-02-010").Property.StructureTypeID);       // "Commercial", translated

		SourceImportResult result = SourceImport.Import(t.Store, preview, "/somewhere/county-parcels.csv");
		Assert.Equal((4, 0, 0, 0), (result.Created, result.Linked, result.Updated, result.Unchanged));
		Assert.Empty(result.AssessmentIds);
		Assert.Equal(4, result.StructureIds.Count);
		Assert.Equal(4, t.Store.Counts().Properties);
		Assert.Equal(0, t.Store.Counts().Assessments);
		StructureLinkRecord link = t.Store.GetStructureLink("County parcels", "900-01-001");
		Assert.Equal(("county-parcels.csv", first.Property.PropertyID), (link.SourceFile, link.PropertyID));
		Assert.Equal(4, t.Store.GetStructureLinks("County parcels").Count);
		Assert.Empty(t.Store.GetStructureLinks("Permits"));
		Assert.Single(t.Store.GetStructureLinksFor(first.Property.PropertyID));
		Assert.Contains(t.Store.ListStructures(), s => (s.ExternalSource, s.ExternalId) == ("County parcels", "900-01-001"));
		Assert.Equal(1200m, Assert.Single(t.Store.GetShapes(first.Property.PropertyID)).TotalSqFt);

		// One batch in edit history, one event per structure.
		List<EditHistoryRecord> batch = t.Store.GetBatch(result.BatchId);
		Assert.Equal(4, batch.Count);
		Assert.All(batch, h => Assert.Equal(("Imported from county-parcels.csv", SaveVia.Import), (h.Label, h.Via)));
		Assert.Equal(4, batch.Select(h => h.PropertyID).Distinct().Count());
	}

	[Fact]
	public void Structures_ReImport_UpdatesWhatChanged_KeepsSdeEdits_AndTheSameFileChangesNothing()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = ShippedProfiles.Get(ShippedProfiles.CountyParcels);
		SourceTable raw = SourceTable.Read(Fixture("county-parcels.csv"));
		SourceImport.Import(t.Store, Preview(t.Store, raw, p), "a.csv");
		Guid id = t.Store.GetStructureLink("County parcels", "900-01-001").PropertyID;
		PropertyRecord edited = t.Store.GetProperty(id);
		edited.City = "Exampleville Heights";                                     // corrected in SDE
		t.Store.SaveProperty(edited);
		Dictionary<Guid, DateTime?> before = t.Store.GetProperties().ToDictionary(x => x.PropertyID, x => x.LastUpdate);
		Thread.Sleep(1100);                                                          // LastUpdate has whole seconds

		SourcePreview again = Preview(t.Store, raw, p);
		Assert.Equal(4, again.Count(ImportRowStatus.Unchanged));
		Assert.False(again.CanImport);
		SourceImportResult same = SourceImport.Import(t.Store, again, "a.csv");
		Assert.Equal((0, 0, 4), (same.Created, same.Updated, same.Unchanged));
		Assert.Null(same.BatchId);
		Assert.Equal(before, t.Store.GetProperties().ToDictionary(x => x.PropertyID, x => x.LastUpdate));

		SourcePreview changed = Preview(t.Store, WithCell(raw, "900-01-001", "Year Built", "1956"), p);
		ImportRow row = Row(changed, "900-01-001");
		Assert.Equal(ImportRowStatus.Update, row.Status);
		Assert.Equal(new[] { "Year built: '1955' → '1956'" }, row.Changes);
		SourceImportResult result = SourceImport.Import(t.Store, changed, "b.csv");
		Assert.Equal((0, 1, 3), (result.Created, result.Updated, result.Unchanged));
		PropertyRecord after = t.Store.GetProperty(id);
		Assert.Equal((1956, "Exampleville Heights"), (after.YearOfConstruction, after.City));
		Assert.Contains(t.Store.GetBatch(result.BatchId), h => h.Field == "Property." + nameof(PropertyRecord.YearOfConstruction) && h.NewValue == "1956");
		Assert.Equal(4, t.Store.Counts().Properties);
	}

	[Fact]
	public void Structures_MatchByParcel_LinksExisting_FillsBlanks_AndShowsAmbiguity()
	{
		using TempDatabase t = new TempDatabase();
		// Entered in SDE (or by Enterprise Import) before the roll arrived, parcel numbers written differently.
		PropertyRecord sample = t.Store.SaveProperty(new PropertyRecord { StreetNumber = "100", StreetName = "Sample", ParcelNumber = "900 01 001", YearOfConstruction = 1950 });
		t.Store.SaveProperty(new PropertyRecord { StreetNumber = "200", StreetName = "Test", ParcelNumber = "900.01.002" });
		t.Store.SaveProperty(new PropertyRecord { StreetNumber = "202", StreetName = "Test", ParcelNumber = "90001002" });
		SourceProfile p = ShippedProfiles.Get(ShippedProfiles.CountyParcels);

		SourcePreview preview = Preview(t.Store, "county-parcels.csv", p);
		ImportRow linked = Row(preview, "900-01-001");
		Assert.Equal(ImportRowStatus.Attach, linked.Status);
		Assert.Equal(sample.PropertyID, linked.Property.PropertyID);
		Assert.Equal((1950, 1200m, "ALEX"), (linked.Property.YearOfConstruction, linked.Property.SquareFootage, linked.Property.OwnerFirstName)); // blanks only
		ImportRow ambiguous = Row(preview, "900-01-002");
		Assert.Equal(ImportRowStatus.Ambiguous, ambiguous.Status);
		Assert.False(ambiguous.CanImport);
		Assert.False(ambiguous.Selected);
		Assert.Contains("2 structures have parcel 900-01-002", Assert.Single(ambiguous.Problems));
		Assert.Contains("200 Test", ambiguous.Problems[0]);
		Assert.Equal(ImportRowStatus.NewStructure, Row(preview, "900-01-003").Status);

		SourceImportResult result = SourceImport.Import(t.Store, preview, "roll.csv");
		Assert.Equal((2, 1), (result.Created, result.Linked));
		Assert.Equal(sample.PropertyID, t.Store.GetStructureLink("County parcels", "900-01-001").PropertyID);
		Assert.Null(t.Store.GetStructureLink("County parcels", "900-01-002"));
		Assert.Equal(5, t.Store.Counts().Properties);

		// Leading zeros: off by default, so "0900-02-010" isn't "900-02-010"; on, it is.
		using TempDatabase z = new TempDatabase();
		PropertyRecord office = z.Store.SaveProperty(new PropertyRecord { StreetNumber = "15", StreetName = "Placeholder St N", ParcelNumber = "0900-02-010" });
		Assert.Equal(ImportRowStatus.NewStructure, Row(Preview(z.Store, "county-parcels.csv", p), "900-02-010").Status);
		p.ParcelIgnoreLeadingZeros = true;
		ImportRow zeros = Row(Preview(z.Store, "county-parcels.csv", p), "900-02-010");
		Assert.Equal((ImportRowStatus.Attach, office.PropertyID), (zeros.Status, zeros.Property.PropertyID));
	}

	[Fact]
	public void Structures_MatchByAddress()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord office = t.Store.SaveProperty(new PropertyRecord { StreetNumber = "15", StreetName = "Placeholder Street North", ZipCode = "99999" });
		SourceProfile p = ShippedProfiles.Get(ShippedProfiles.CountyParcels);
		p.MatchBy = SourceMatchBy.Address;
		SourcePreview preview = Preview(t.Store, "county-parcels.csv", p);
		ImportRow row = Row(preview, "900-02-010");
		Assert.Equal((ImportRowStatus.Attach, office.PropertyID), (row.Status, row.Property.PropertyID));
		Assert.Equal("900-02-010", row.Property.ParcelNumber);
		// Two parcels at one address in the same file: each becomes its own structure (there's nothing in SDE to match).
		Assert.Equal(new[] { ImportRowStatus.NewStructure, ImportRowStatus.NewStructure }, new[] { Row(preview, "900-01-002").Status, Row(preview, "900-01-003").Status });
	}

	/// <summary>The usual order: parcels first as structures, then inspections matched to them.</summary>
	[Fact]
	public void Inspections_MatchToImportedParcels_ByParcelOrAddress()
	{
		using TempDatabase t = new TempDatabase();
		SourceImport.Import(t.Store, Preview(t.Store, "county-parcels.csv", ShippedProfiles.Get(ShippedProfiles.CountyParcels)), "roll.csv");
		Guid sampleAve = t.Store.GetStructureLink("County parcels", "900-01-001").PropertyID;

		// The Accela example by parcel (v1.5's rule, exact parcel and no imported assessment yet).
		SourceProfile accela = ShippedProfiles.Get(ShippedProfiles.AccelaExample);
		SourcePreview byRecord = Preview(t.Store, "accela-example.csv", accela);
		Assert.Equal((ImportRowStatus.Attach, sampleAve), (Row(byRecord, "BLD-2026-90001").Status, Row(byRecord, "BLD-2026-90001").Property.PropertyID));
		Assert.Equal(ImportRowStatus.NewStructure, Row(byRecord, "BLD-2026-90003").Status);

		// Generic inspections by address: 200 Test Road is two parcels, so it is ambiguous, never guessed.
		SourceProfile generic = ShippedProfiles.Get(ShippedProfiles.GenericInspections);
		generic.ValueIsBuildingOnly = true;
		SourcePreview byAddress = Preview(t.Store, "generic-inspections.csv", generic);
		Assert.Equal(ImportRowStatus.Attach, Row(byAddress, "INS-0001").Status);
		Assert.Equal(sampleAve, Row(byAddress, "INS-0001").Property.PropertyID);
		Assert.Equal(ImportRowStatus.Attach, Row(byAddress, "INS-0003").Status);       // 15 Placeholder Street North = 15 PLACEHOLDER ST N
		Assert.Equal(ImportRowStatus.NewStructure, Row(byAddress, "INS-0004").Status);
		ImportRow ambiguous = Row(byAddress, "INS-0002");
		Assert.Equal(ImportRowStatus.Ambiguous, ambiguous.Status);
		Assert.Contains("parcel 900-01-002", ambiguous.Problems[0]);
		Assert.Contains("parcel 900-01-003", ambiguous.Problems[0]);

		SourceImportResult result = SourceImport.Import(t.Store, byAddress, "inspections.csv");
		Assert.Equal(3, result.Created);
		Assert.Equal(5, t.Store.Counts().Properties);
		Assert.Single(t.Store.GetAssessmentsForProperty(sampleAve));
		Assert.Equal("Inspections", t.Store.GetExternalLink(Row(byAddress, "INS-0001").AssessmentID!.Value).Source);

		// The same inspections by parcel: only INS-0001 has one.
		using TempDatabase u = new TempDatabase();
		SourceImport.Import(u.Store, Preview(u.Store, "county-parcels.csv", ShippedProfiles.Get(ShippedProfiles.CountyParcels)), "roll.csv");
		generic.MatchBy = SourceMatchBy.Parcel;
		SourcePreview byParcel = Preview(u.Store, "generic-inspections.csv", generic);
		Assert.Equal(ImportRowStatus.Attach, Row(byParcel, "INS-0001").Status);
		Assert.Equal(ImportRowStatus.NewStructure, Row(byParcel, "INS-0002").Status);
		Assert.Contains("No parcel number to match with; a new structure will be created", Row(byParcel, "INS-0002").Warnings);
	}

	/// <summary>Flag 4: a value used as the building value must be the building only, and the person has to say so.</summary>
	[Fact]
	public void BuildingValue_NeedsConfirmation_BeforeItIsUsed()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = ShippedProfiles.Get(ShippedProfiles.GenericInspections);
		SourcePreview refused = Preview(t.Store, "generic-inspections.csv", p);
		Assert.True(refused.NeedsBuildingValueConfirmation);
		Assert.Equal("the column 'Building Value'", refused.BuildingValueSource);
		Assert.Contains(refused.MappingProblems, m => m.Contains("FEMA counts the building only, never the land"));
		Assert.False(refused.CanImport);
		Assert.Throws<InvalidOperationException>(() => SourceImport.Import(t.Store, refused, "x.csv"));
		Assert.Equal(0, t.Store.Counts().Properties);

		p.ValueIsBuildingOnly = true;
		SourcePreview confirmed = Preview(t.Store, "generic-inspections.csv", p);
		Assert.Empty(confirmed.MappingProblems);
		AssessmentRecord a = Row(confirmed, "INS-0001").File.Assessment;
		Assert.Equal((Sde.Core.LookupIds.ValueAdjustedTax, 95000m, 40000m), (a.ValueOfBuildingID, a.TaxAssessedValue, a.CommunityEstimate));
		Assert.Equal(42.1m, a.PercentDamage);
		Assert.True(SourceProfile.FromJson(p.ToJson()).ValueIsBuildingOnly);         // remembered in the profile

		// Without a value column, or with another basis, nothing is asked.
		SourceProfile market = ShippedProfiles.Get(ShippedProfiles.GenericInspections);
		market.Map("ValueBasis").Constant = "Professional Market Appraisal";
		Assert.False(Preview(t.Store, "generic-inspections.csv", market).NeedsBuildingValueConfirmation);
		// A value basis column that says "Adj. Tax Assessed Value" on any row asks too.
		using TempDatabase c = new TempDatabase();
		string path = Path.Combine(c.Folder, "basis.csv");
		File.WriteAllText(path, "Record ID,Address,Basis,Total Value\r\nX-1,1 Test St,Market,100000\r\nX-2,2 Test St,Tax,90000\r\n");
		SourceProfile column = new SourceProfile { RecordIdColumn = "Record ID", StreetNameParsing = StreetNameParsing.NumberNameSuffix };
		column.Map("StreetName").Column = "Address";
		column.Map("ValueBasis").Column = "Basis";
		column.Map("ValueBasis").Translate["Market"] = "Professional Market Appraisal";
		column.Map("ValueBasis").Translate["Tax"] = "Adj. Tax Assessed Value";
		column.Map("TaxAssessedValue").Column = "Total Value";
		Assert.True(SourceImport.Preview(SourceTable.Shape(SourceTable.Read(path), column), column, c.Store, TestContext.Current.CancellationToken).NeedsBuildingValueConfirmation);
	}

	[Fact]
	public void FinalAssessments_AreLeftAlone_AndReported()
	{
		using TempDatabase t = new TempDatabase();
		SourceTable raw = SourceTable.Read(SourceTableTests.Fixture("inspections-wide.csv"));
		SourceProfile p = SourceImportTests.Profile(raw);
		SourcePreview first = SourceImport.Preview(SourceTable.Shape(raw, p), p, t.Store, TestContext.Current.CancellationToken);
		SourceImportResult imported = SourceImport.Import(t.Store, first, "a.csv");
		Assert.Equal(3, t.Store.GetBatch(imported.BatchId).Count(h => h.Label == "Imported from a.csv"));
		Guid ranch = first.Rows.Single(r => r.RecordId == "BLD-2026-00123").AssessmentID!.Value;
		Guid elm = first.Rows.Single(r => r.RecordId == "BLD-2026-00124").AssessmentID!.Value;
		t.Store.SetStatus(ranch, AssessmentStatus.Final);

		// Seen in the preview: "Final, won't change", not importable, counted.
		SourceTable changed = WithRecord(raw, "BLD-2026-00123", "Foundation % Damage", "40%");
		SourcePreview preview = SourceImport.Preview(SourceTable.Shape(changed, p), p, t.Store, TestContext.Current.CancellationToken);
		ImportRow locked = preview.Rows.Single(r => r.RecordId == "BLD-2026-00123");
		Assert.Equal((ImportRowStatus.Update, true, false), (locked.Status, locked.IsLocked, locked.CanImport));
		Assert.Equal(1, preview.LockedCount);
		Assert.False(preview.CanImport);
		SourceImportResult result = SourceImport.Import(t.Store, preview, "b.csv");
		Assert.Equal((0, 0), (result.Created, result.Updated));
		Assert.Equal(new[] { ranch }, result.LockedIds);
		Assert.Equal(10m, t.Store.LoadAssessment(ranch).ElementPercentages.Single(e => e.CalcID == 4).UserDamage);

		// Made Final between the preview and the import: the store skips it and the result says so.
		SourcePreview elmPreview = SourceImport.Preview(SourceTable.Shape(WithRecord(raw, "BLD-2026-00124", "Year Built", "1980"), p), p, t.Store, TestContext.Current.CancellationToken);
		Assert.Equal(ImportRowStatus.Update, elmPreview.Rows.Single(r => r.RecordId == "BLD-2026-00124").Status);
		t.Store.SetStatus(elm, AssessmentStatus.Final);
		SourceImportResult late = SourceImport.Import(t.Store, elmPreview, "c.csv");
		Assert.Equal((0, 1), (late.Updated, late.Locked));
		Assert.Equal(new[] { elm }, late.LockedIds);
		Assert.Equal(1978, t.Store.LoadAssessment(elm).Property.YearOfConstruction);
		Assert.Null(late.BatchId);
	}

	private static SourceTable WithRecord(SourceTable t, string recordId, string column, string value)
	{
		int r = t.IndexOf("Record ID");
		int c = t.IndexOf(column);
		return new SourceTable(t.Path, t.SheetName, t.Columns, t.Rows.Select(row =>
		{
			List<string> values = row.Values.ToList();
			if (values[r] == recordId)
			{
				values[c] = value;
			}
			return new SourceRow(row.RowNumber, values);
		}).ToList());
	}

	[Fact]
	public void SourceName_KeepsTwoSourcesApart()
	{
		using TempDatabase t = new TempDatabase();
		SourceTable raw = SourceTable.Read(SourceTableTests.Fixture("inspections-wide.csv"));
		SourceProfile p = SourceImportTests.Profile(raw);
		SourceImport.Import(t.Store, SourceImport.Preview(SourceTable.Shape(raw, p), p, t.Store, TestContext.Current.CancellationToken), "a.csv");
		Assert.Equal(3, t.Store.GetExternalLinks("Accela").Count);

		// The same record IDs from another system are other records: new assessments (attached by v1.5's parcel rule
		// only to structures without an imported assessment, so new structures here).
		p.SourceName = "Permits";
		SourcePreview other = SourceImport.Preview(SourceTable.Shape(raw, p), p, t.Store, TestContext.Current.CancellationToken);
		Assert.Equal(3, other.Count(ImportRowStatus.NewStructure));
		Assert.Equal(0, other.Count(ImportRowStatus.Unchanged));
	}
}
