using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;


namespace Sde.Interop.Tests;

/// <summary>Importing inspections (here an Accela export): preview statuses, the calculation, parity with a hand-entered assessment, safe re-imports.</summary>
public class SourceImportTests
{
	private const string Sda = "Substantial Damage Assessment";

	/// <summary>The profile a user would end up with for the wide fixture: named, guessed, filtered, two constants, two translations.</summary>
	internal static SourceProfile Profile(SourceTable raw)
	{
		SourceProfile p = SourceFields.Guess(raw);
		p.SourceName = "Accela";
		p.FilterColumn = "Inspection Type";
		p.FilterValues.Add(Sda);
		p.UseDefaultValues = false;
		p.Map("BaseCost").Constant = "118.75";
		p.Map("GeographicAdjustment").Constant = "0.93";
		p.Map("ResidenceType").Translate["Single Family"] = "Single Family Residence";
		p.Map("RoofCovering").Translate["Asphalt Shingles"] = "Shingles - Asphalt, Wood (Standard)";
		return p;
	}

	private static SourcePreview Preview(SdeStore store, SourceTable raw, SourceProfile p) => SourceImport.Preview(SourceTable.Shape(raw, p), p, store);

	private static SourceTable Wide() => SourceTable.Read(SourceTableTests.Fixture("inspections-wide.csv"));

	/// <summary>A copy of the table with one cell changed.</summary>
	private static SourceTable WithCell(SourceTable t, string recordId, string column, string value)
	{
		int r = t.IndexOf("Record ID"), c = t.IndexOf(column);
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

	private static ImportRow Row(SourcePreview preview, string recordId) => preview.Rows.Single(r => r.RecordId == recordId);

	[Fact]
	public void WideExport_PreviewsAndImports()
	{
		using TempDatabase t = new TempDatabase();
		SourceTable raw = Wide();
		SourcePreview preview = Preview(t.Store, raw, Profile(raw));

		Assert.Empty(preview.MappingProblems);
		Assert.Empty(preview.Unmatched);
		Assert.Equal(4, preview.Rows.Count);
		Assert.Equal(3, preview.Count(ImportRowStatus.NewStructure));
		ImportRow problem = preview.Rows.Single(r => r.Status == ImportRowStatus.Problem);
		Assert.Equal(new[] { "No record ID" }, problem.Problems);
		Assert.False(problem.Selected);

		ImportRow elm = Row(preview, "BLD-2026-00124");
		Assert.Empty(elm.Warnings);
		PropertyRecord p = elm.File.Property;
		Assert.Equal(("45", "Elm", 172), (p.StreetNumber, p.StreetName, p.StreetSuffixID));
		Assert.Equal(("Jane", "Smith"), (p.OwnerFirstName, p.OwnerLastName));
		Assert.Equal((92, 46, 131, 95, 26), (p.ResidenceTypeID, p.FoundationTypeID, p.SuperStructureTypeID, p.RoofCoveringTypeID, p.ExteriorFinishTypeID));
		Assert.Equal(105, p.StoryTypeID);                       // "2 stories", residential
		Assert.Equal((1978, 2100m), (p.YearOfConstruction, p.SquareFootage));
		AssessmentRecord a = elm.File.Assessment;
		Assert.Equal(new DateTime(2026, 10, 7, 15, 10, 0), a.DateofInspection);
		Assert.Equal((36m, 358), (a.DurationOfFlood, a.DurationOfFloodUnitsID));   // 36 hrs
		Assert.Equal(1.5m, a.DepthOfFloodAboveFFE);                                  // 18 in
		Assert.Equal(385, a.DepreciationRatingID);                                   // "3"
		Assert.Equal(210000m, a.TaxAssessedValue);
		Assert.Equal(60m, elm.File.ElementPercentages.Single(e => e.CalcID == 8).UserDamage);

		ImportRow office = Row(preview, "COM-2026-00031");
		Assert.Equal((2, 101, 106), (office.File.Property.StructureTypeID, office.File.Property.StoryTypeID, office.File.Property.StructureUseID));
		Assert.Equal(55m, office.File.ElementPercentages.Single(e => e.CalcID == 17).UserDamage);
		Assert.All(preview.Rows.Where(r => r.CanImport), r => Assert.NotNull(r.PercentDamage));

		SourceImportResult result = SourceImport.Import(t.Store, preview, "/Users/x/Downloads/inspections-wide.csv");
		Assert.Equal((3, 0, 0), (result.Created, result.Updated, result.Unchanged));
		List<StructureSummary> list = t.Store.ListStructures();
		Assert.Equal(new[] { "BLD-2026-00123", "BLD-2026-00124", "COM-2026-00031" }, list.Select(s => s.ExternalId).Order());
		ExternalLinkRecord link = t.Store.GetExternalLink(elm.File.Assessment.AssessmentID);
		Assert.Equal(("4472", "inspections-wide.csv"), (link.InspectionId, link.SourceFile));
		AssessmentFile saved = t.Store.LoadAssessment(elm.File.Assessment.AssessmentID);
		Assert.Equal(elm.PercentDamage, saved.Assessment.PercentDamage);
		Assert.Equal(2100m, Assert.Single(t.Store.GetShapes(saved.Property.PropertyID)).TotalSqFt);
	}

	/// <summary>
	/// The RanchFlood case (tests/cases/ranch-flood.json, 75.0%) imported from an Accela export: everything Accela records
	/// gives the same assessment as typing it in. RanchFlood's garage and shed adjustments aren't in Accela, so the
	/// comparison is with RanchFlood minus those two.
	/// </summary>
	[Fact]
	public void RanchFlood_ThroughAccela_MatchesTheHandEnteredAssessment()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile hand = SampleCases.RanchFlood(t.Store);
		hand.CostAdjustments.ForEach(c => (c.Quantity, c.UnitCost) = (null, null));
		hand.AdditionalAdjustments.ForEach(x => (x.AdjustmentName, x.Quantity, x.UnitCost) = (null, null, null));
		t.Store.SaveAssessment(hand);

		SourceTable raw = Wide();
		ImportRow row = Row(Preview(t.Store, raw, Profile(raw)), "BLD-2026-00123");
		Assert.Equal(ImportRowStatus.NewStructure, row.Status);
		Assert.Empty(row.Warnings);
		AssessmentRecord a = row.File.Assessment, h = hand.Assessment;
		PropertyRecord p = row.File.Property, hp = hand.Property;

		Assert.Equal((hp.StreetNumber, hp.StreetName, hp.StreetSuffixID, hp.City, hp.StateID, hp.ZipCode), (p.StreetNumber, p.StreetName, p.StreetSuffixID, p.City, p.StateID, p.ZipCode));
		Assert.Equal((hp.OwnerFirstName, hp.OwnerLastName), (p.OwnerFirstName, p.OwnerLastName));
		Assert.Equal((hp.StructureTypeID, hp.ResidenceTypeID, hp.FoundationTypeID, hp.SuperStructureTypeID, hp.RoofCoveringTypeID, hp.ExteriorFinishTypeID, hp.HVACTypeID, hp.StoryTypeID),
			(p.StructureTypeID, p.ResidenceTypeID, p.FoundationTypeID, p.SuperStructureTypeID, p.RoofCoveringTypeID, p.ExteriorFinishTypeID, p.HVACTypeID, p.StoryTypeID));
		Assert.Equal((hp.YearOfConstruction, hp.StructureQualityID, hp.SquareFootage), (p.YearOfConstruction, p.StructureQualityID, p.SquareFootage));
		Assert.Equal((h.InspectorName, h.DateDamageOccurred, h.CauseofDamageID, h.DurationOfFlood, h.DurationOfFloodUnitsID),
			(a.InspectorName, a.DateDamageOccurred, a.CauseofDamageID, a.DurationOfFlood, a.DurationOfFloodUnitsID));
		Assert.Equal((h.BaseCost, h.GeographicAdjustment, h.DepreciationRatingID, h.DepreciationValue), (a.BaseCost, a.GeographicAdjustment, a.DepreciationRatingID, a.DepreciationValue));
		Assert.Equal(hand.ElementPercentages.Select(e => (e.CalcID, e.UserDamage, decimal.Parse(e.ItemCost), decimal.Parse(e.DamageValues))),
			row.File.ElementPercentages.Select(e => (e.CalcID, e.UserDamage, decimal.Parse(e.ItemCost), decimal.Parse(e.DamageValues))));

		Assert.Equal(h.TotalReplacementCost, a.TotalReplacementCost);       // 118.75 × 1,450 × 0.93
		Assert.Equal(160134.38m, a.TotalReplacementCost);
		Assert.Equal(h.ComputedActualCashValue, a.ComputedActualCashValue);
		Assert.Equal(h.TotalEstimatedDamages, a.TotalEstimatedDamages);
		Assert.Equal(h.PercentDamage, a.PercentDamage);
		Assert.Equal(75.0m, a.PercentDamage);                               // the adjustments are in both value and damage
		Assert.Equal("Substantially Damaged", a.DamageMessage);
	}

	/// <summary>When Accela has an appraisal and a contractor's estimate, SDE's shortcut bases give % damaged directly.</summary>
	[Fact]
	public void AppraisalAndContractorEstimate_GiveTheirRatio()
	{
		using TempDatabase t = new TempDatabase();
		string path = Path.Combine(t.Folder, "shortcut.csv");
		File.WriteAllText(path, "Record ID,Address,Structure Type,Market Value,Contractor Estimate\r\nX-1,1 Test St,Residential,\"$200,000\",\"$110,000\"\r\n");
		SourceTable raw = SourceTable.Read(path);
		SourceProfile p = SourceFields.Guess(raw);
		p.Map("ValueBasis").Constant = "Professional Market Appraisal";
		p.Map("RepairBasis").Constant = "Contractor Estimate";
		ImportRow row = Assert.Single(Preview(t.Store, raw, p).Rows);
		Assert.Equal((391, 394), (row.File.Assessment.ValueOfBuildingID, row.File.Assessment.CostOfRepairsID));
		Assert.Equal(55.0m, row.PercentDamage);
		Assert.Equal("Substantially Damaged", row.DamageMessage);
	}

	[Fact]
	public void ImportingTheSameFileAgain_ChangesNothing()
	{
		using TempDatabase t = new TempDatabase();
		SourceTable raw = Wide();
		SourceProfile p = Profile(raw);
		SourceImport.Import(t.Store, Preview(t.Store, raw, p), "a.csv");
		Dictionary<Guid, DateTime?> before = t.Store.LoadAllAssessments().ToDictionary(f => f.Assessment.AssessmentID, f => f.Assessment.LastUpdate);
		Thread.Sleep(1100);   // LastUpdate has whole seconds

		SourcePreview again = Preview(t.Store, raw, p);
		Assert.Equal(3, again.Count(ImportRowStatus.Unchanged));
		Assert.False(again.CanImport);
		SourceImportResult result = SourceImport.Import(t.Store, again, "a.csv");
		Assert.Equal((0, 0, 3), (result.Created, result.Updated, result.Unchanged));
		Assert.Equal(3, result.AssessmentIds.Count);
		Assert.Equal(before, t.Store.LoadAllAssessments().ToDictionary(f => f.Assessment.AssessmentID, f => f.Assessment.LastUpdate));
		Assert.Equal(3, t.Store.Counts().Properties);
	}

	[Fact]
	public void ReImport_UpdatesOnlyWhatChangedInTheSource_KeepsEditsMadeInSde_AndBlanksClearNothing()
	{
		using TempDatabase t = new TempDatabase();
		SourceTable raw = Wide();
		SourceProfile p = Profile(raw);
		SourcePreview first = Preview(t.Store, raw, p);
		SourceImport.Import(t.Store, first, "a.csv");

		// Edits in SDE after the import.
		AssessmentFile ranch = t.Store.LoadAssessment(Row(first, "BLD-2026-00123").File.Assessment.AssessmentID);
		ranch.Assessment.InspectorName = "Inspector A (corrected)";
		t.Store.SaveAssessment(ranch);
		AssessmentFile elm = t.Store.LoadAssessment(Row(first, "BLD-2026-00124").File.Assessment.AssessmentID);
		elm.Property.YearOfConstruction = 1979;
		t.Store.SaveAssessment(elm);

		// Accela now has more foundation damage at the ranch, and the Elm year built is blank.
		SourceTable changed = WithCell(WithCell(raw, "BLD-2026-00123", "Foundation % Damage", "40%"), "BLD-2026-00124", "Year Built", "");
		SourcePreview second = Preview(t.Store, changed, p);
		ImportRow ranchRow = Row(second, "BLD-2026-00123");
		Assert.Equal(ImportRowStatus.Update, ranchRow.Status);
		Assert.Equal(new[] { "Foundation % damaged: '10' → '40'" }, ranchRow.Changes);
		Assert.Equal(ImportRowStatus.Unchanged, Row(second, "BLD-2026-00124").Status);
		Assert.Equal(ImportRowStatus.Unchanged, Row(second, "COM-2026-00031").Status);

		SourceImportResult result = SourceImport.Import(t.Store, second, "b.csv");
		Assert.Equal((0, 1, 2), (result.Created, result.Updated, result.Unchanged));
		AssessmentFile ranchAfter = t.Store.LoadAssessment(ranch.Assessment.AssessmentID);
		Assert.Equal(40m, ranchAfter.ElementPercentages.Single(e => e.CalcID == 4).UserDamage);
		Assert.Equal("Inspector A (corrected)", ranchAfter.Assessment.InspectorName);
		Assert.True(ranchAfter.Assessment.PercentDamage > 75.0m);
		Assert.Equal(1979, t.Store.GetProperty(elm.Property.PropertyID).YearOfConstruction);
		Assert.Equal("b.csv", t.Store.GetExternalLink(ranch.Assessment.AssessmentID).SourceFile);
		Assert.Equal("a.csv", t.Store.GetExternalLink(elm.Assessment.AssessmentID).SourceFile);

		// The remembered values now include the change, so the same file is unchanged.
		Assert.Equal(3, Preview(t.Store, changed, p).Count(ImportRowStatus.Unchanged));
	}

	[Fact]
	public void NewInspections_AttachByParcel_AndProblems()
	{
		using TempDatabase t = new TempDatabase();
		// An existing structure (e.g. from Enterprise Import) with the office's parcel and part of its details.
		PropertyRecord office = t.Store.SaveProperty(new PropertyRecord { StreetNumber = "800", StreetName = "Main Street", ParcelNumber = " 0145-0001-0001-00", StructureTypeID = 2 });
		SourceTable raw = Wide();
		SourceProfile p = Profile(raw);
		p.FilterValues.Clear();   // include the second inspection of the ranch

		SourcePreview preview = Preview(t.Store, raw, p);
		ImportRow attach = Row(preview, "COM-2026-00031");
		Assert.Equal(ImportRowStatus.Attach, attach.Status);
		Assert.Equal(office.PropertyID, attach.File.Property.PropertyID);
		Assert.Equal("Main Street", attach.File.Property.StreetName);            // blanks only are filled
		Assert.Equal(("Main Street", "Holdings"), (attach.File.Property.OwnerFirstName, attach.File.Property.OwnerLastName));
		Assert.Equal(1985, attach.File.Property.YearOfConstruction);
		List<ImportRow> ranch = preview.Rows.Where(r => r.RecordId == "BLD-2026-00123").ToList();
		Assert.Equal(new[] { ImportRowStatus.NewStructure, ImportRowStatus.NewInspection }, ranch.Select(r => r.Status));
		Assert.Same(ranch[0].File.Property, ranch[1].File.Property);
		SourceImport.Import(t.Store, preview, "a.csv");
		Assert.Equal(2, t.Store.GetAssessmentsForProperty(ranch[0].File.Property.PropertyID).Count);
		Assert.Single(t.Store.GetAssessmentsForProperty(office.PropertyID));

		// A later export with another inspection of a linked record adds an assessment to that structure.
		SourceTable later = WithCell(raw, "BLD-2026-00124", "Inspection ID", "4499");
		SourcePreview next = Preview(t.Store, later, p);
		ImportRow reinspection = Row(next, "BLD-2026-00124");
		Assert.Equal(ImportRowStatus.NewInspection, reinspection.Status);
		Assert.NotEqual(Guid.Empty, reinspection.File.Property.PropertyID);

		// The same record and inspection twice in one file.
		SourceTable twice = new SourceTable(raw.Path, raw.SheetName, raw.Columns, raw.Rows.Concat(new[] { new SourceRow(99, raw.Rows[0].Values) }).ToList());
		ImportRow duplicate = SourceImport.Preview(SourceTable.Shape(twice, p), p, t.Store).Rows.Single(r => r.RowNumber == 99);
		Assert.Equal(ImportRowStatus.Problem, duplicate.Status);
		Assert.Equal("Same record and inspection as row 2", Assert.Single(duplicate.Problems));
	}

	[Fact]
	public void ChecklistExport_ImportsTheSameAssessments()
	{
		using TempDatabase wideDb = new TempDatabase();
		using TempDatabase longDb = new TempDatabase();
		SourceTable wide = Wide();
		SourceTable checklist = SourceTable.Read(SourceTableTests.Fixture("checklist-long.csv"));
		SourceProfile lp = SourceFields.Guess(checklist);
		lp.FilterColumn = "Inspection Type";
		lp.FilterValues.Add(Sda);
		lp.UseDefaultValues = false;
		lp.Map("BaseCost").Constant = "118.75";
		lp.Map("GeographicAdjustment").Constant = "0.93";
		lp.Map("ResidenceType").Translate["Single Family"] = "Single Family Residence";
		lp.Map("RoofCovering").Translate["Asphalt Shingles"] = "Shingles - Asphalt, Wood (Standard)";

		SourcePreview w = Preview(wideDb.Store, wide, Profile(wide));
		SourcePreview l = Preview(longDb.Store, checklist, lp);
		Assert.Empty(l.MappingProblems);
		Assert.Equal(w.Rows.Select(r => (r.RecordId, r.InspectionId, r.Status)), l.Rows.Select(r => (r.RecordId, r.InspectionId, r.Status)));
		foreach (string id in new[] { "BLD-2026-00123", "COM-2026-00031" })
		{
			Assert.Equal(Row(w, id).PercentDamage, Row(l, id).PercentDamage);
			Assert.Equal(Row(w, id).File.Assessment.ComputedActualCashValue, Row(l, id).File.Assessment.ComputedActualCashValue);
		}
		// The Elm checklist's corrected plumbing value (35, not 30) is the one used.
		Assert.Equal(35m, Row(l, "BLD-2026-00124").File.ElementPercentages.Single(e => e.CalcID == 12).UserDamage);
	}

	[Fact]
	public void DefaultValues_CustomFieldsAndUnusableValues()
	{
		using TempDatabase t = new TempDatabase();
		t.Store.SaveDefaults(new DefaultValuesRecord { DefaultValueID = 1, LocalMultiplier = "1.08", CostDataRef = "RSMeans 2026", City = "Default City", NFIPCommunityID = "399990" });
		SourceTable raw = Wide();
		SourceProfile p = Profile(raw);
		p.UseDefaultValues = true;
		p.Fields.Remove("GeographicAdjustment");
		p.RecordIdCustomField = 1;
		p.Map("Custom.2").Column = "Inspection Status";
		p.Map("ResidenceType").Translate.Clear();

		SourcePreview preview = Preview(t.Store, raw, p);
		ImportRow elm = Row(preview, "BLD-2026-00124");
		Assert.Equal((1.08m, "RSMeans 2026"), (elm.File.Assessment.GeographicAdjustment, elm.File.Assessment.CostData));
		Assert.Equal(("Exampleville", "399990"), (elm.File.Property.City, elm.File.Property.NFIPCID));   // Accela over the defaults
		Assert.Equal(new[] { "BLD-2026-00124 / 4472", "Completed" }, elm.File.CustomFields.OrderBy(c => c.zOrder).Select(c => c.Result).Take(2));
		Assert.Contains("Residence type 'Single Family' isn't one of FEMA's choices; left as it was", elm.Warnings);
		Assert.Null(elm.File.Property.ResidenceTypeID);
		Assert.True(elm.MissingRequired > 0);
		Assert.Equal(ImportRowStatus.NewStructure, elm.Status);   // a warning doesn't stop the import

		SourceImport.Import(t.Store, preview, "a.csv");
		Assert.Contains(new ExchangeService(t.Store).ReadDataset().CustomFieldsResults, r => r.Result == "BLD-2026-00124 / 4472");
	}

	/// <summary>Every FEMA choice the import can set is one FEMA's engine accepts (a bad one would be a row problem, never a crash).</summary>
	[Fact]
	public void EveryChoiceTheImportCanSet_Calculates()
	{
		using TempDatabase t = new TempDatabase();
		ReferenceData r = t.Store.Reference;
		List<string> lines = new() { "Record ID,Address,Type,Residence,Stories,Use,Foundation,Superstructure,Roof,Exterior,HVAC,Condition,Cause,Undetermined Reason" };
		int n = 0;
		string Line(string type, string residence = "", string stories = "", string use = "", string foundation = "", string superstructure = "", string roof = "",
			string exterior = "", string hvac = "", string condition = "", string cause = "", string reason = "") =>
			$"R-{++n},{n} Test St,{type},{residence},{stories},\"{use}\",\"{foundation}\",\"{superstructure}\",\"{roof}\",\"{exterior}\",\"{hvac}\",\"{condition}\",\"{cause}\",\"{reason}\"";
		IEnumerable<string> Names(int type) => r.OfType(type).Select(l => l.LookupFieldName);
		foreach (string residence in Names(LookupTypes.ResidenceType))
		{
			foreach (string foundation in Names(LookupTypes.Foundation))
			{
				lines.Add(Line("Residential", residence, "1", foundation: foundation));
			}
		}
		foreach (string v in Names(LookupTypes.Superstructure)) lines.Add(Line("Residential", "Single Family Residence", "2", superstructure: v));
		foreach (string v in Names(LookupTypes.RoofCovering)) lines.Add(Line("Residential", "Town or Row House", "2", roof: v));
		foreach (string v in Names(LookupTypes.ExteriorFinish)) lines.Add(Line("Residential", "Single Family Residence", "1", exterior: v));
		foreach (string v in Names(LookupTypes.Hvac)) lines.Add(Line("Residential", "Manufactured House", "1", hvac: v));
		foreach (string v in Names(LookupTypes.DepreciationRating)) lines.Add(Line("Residential", "Single Family Residence", "1", condition: v));
		foreach (string v in Names(LookupTypes.CauseOfDamage)) lines.Add(Line("Residential", "Single Family Residence", "1", cause: v));
		foreach (string v in Names(LookupTypes.UndeterminedDamageType)) lines.Add(Line("Residential", "Single Family Residence", "1", reason: v));
		foreach (LookupRecord use in r.OfType(LookupTypes.StructureUse))
		{
			foreach (string story in use.LookupValue1.Split(',').Select(s => r.LookupName(int.Parse(s))))
			{
				foreach (string v in Names(LookupTypes.DepreciationRating))
				{
					lines.Add(Line("Non-Residential", stories: story, use: use.LookupFieldName, condition: v));
				}
			}
		}
		string path = Path.Combine(t.Folder, "choices.csv");
		File.WriteAllLines(path, lines);
		SourceTable raw = SourceTable.Read(path);
		SourceProfile p = SourceFields.Guess(raw);
		p.Map("StructureType").Column = "Type";
		p.Map("ResidenceType").Column = "Residence";
		p.Map("Foundation").Column = "Foundation";
		p.Map("Superstructure").Column = "Superstructure";
		p.Map("RoofCovering").Column = "Roof";
		p.Map("ExteriorFinish").Column = "Exterior";
		p.Map("Hvac").Column = "HVAC";
		p.Map("UndeterminedReason").Column = "Undetermined Reason";
		p.Map("BaseCost").Constant = "100";
		p.Map("Element.5").Constant = "50";

		SourcePreview preview = Preview(t.Store, raw, p);
		Assert.Empty(preview.MappingProblems);
		Assert.Empty(preview.Unmatched);
		Assert.Equal(lines.Count - 1, preview.Rows.Count);
		Assert.All(preview.Rows, row =>
		{
			Assert.True(row.Status == ImportRowStatus.NewStructure, $"row {row.RowNumber}: {string.Join("; ", row.Problems)}");
			Assert.Empty(row.Warnings);
			Assert.NotNull(row.PercentDamage);
		});
	}
}
