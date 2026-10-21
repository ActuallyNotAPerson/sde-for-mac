using Sde.Core;


namespace Sde.Interop.Tests;

/// <summary>Files to import (here an Accela export): guessing a profile, the checklist pivot, the filter, value matching, profile files.</summary>
public class SourceTableTests
{
	public static string Fixture(string name) => Path.Combine(TestPaths.Root, "tests", "fixtures", "accela", name);

	[Fact]
	public void Guess_WideExport_MapsIdsAndFields()
	{
		SourceTable raw = SourceTable.Read(Fixture("inspections-wide.csv"));
		SourceProfile p = SourceFields.Guess(raw);

		Assert.Equal(SourceLayout.OneRowPerInspection, p.Layout);
		Assert.Equal("Record ID", p.RecordIdColumn);
		Assert.Equal("Inspection ID", p.InspectionIdColumn);
		Dictionary<string, string> expected = new()
		{
			["StreetName"] = "Address", ["City"] = "City", ["State"] = "State", ["Zip"] = "Zip", ["ParcelNumber"] = "Parcel Number",
			["OwnerFirstName"] = "Owner Name", ["OwnerLastName"] = "Owner Name",
			["StructureType"] = "Structure Type", ["ResidenceType"] = "Residence Type", ["Stories"] = "Number of Stories", ["StructureUse"] = "Occupancy",
			["Foundation"] = "Foundation Type", ["Superstructure"] = "Superstructure", ["RoofCovering"] = "Roof Type", ["ExteriorFinish"] = "Exterior Finish",
			["Hvac"] = "HVAC Type", ["StructureQuality"] = "Quality", ["YearOfConstruction"] = "Year Built", ["TotalSquareFootage"] = "Square Feet",
			["InspectorName"] = "Inspector", ["DateOfInspection"] = "Inspection Date", ["CauseOfDamage"] = "Cause of Damage",
			["DateDamageOccurred"] = "Date of Damage", ["FloodDuration"] = "Flood Duration", ["DepthAboveLowestFloor"] = "Water Depth",
			["DepreciationRating"] = "Condition", ["TaxAssessedValue"] = "Improved Value",
			["Element.4"] = "Foundation % Damage", ["Element.7"] = "Exterior Finish % Damage", ["Element.15"] = "HVAC % Damage", ["Element.17"] = "Interiors % Damage",
		};
		foreach ((string key, string column) in expected)
		{
			Assert.True(p.IsMapped(key), key + " is not mapped");
			Assert.Equal(column, p[key].Column);
		}
		Assert.Equal(13, p.Fields.Keys.Count(k => k.StartsWith("Element.", StringComparison.Ordinal)));
		Assert.Equal(StreetNameParsing.NumberNameSuffix, p.StreetNameParsing);
		Assert.Equal(NameParsing.FirstLast, p.FirstNameParsing);
		// Nothing maps to the inspection type or status; the type is for the filter.
		Assert.DoesNotContain(p.Fields.Values, m => m.Column is "Inspection Type" or "Inspection Status");
	}

	[Fact]
	public void Guess_ChecklistExport_PivotsOneRowPerInspection()
	{
		SourceTable raw = SourceTable.Read(Fixture("checklist-long.csv"));
		SourceProfile p = SourceFields.Guess(raw);
		Assert.Equal(SourceLayout.OneRowPerChecklistItem, p.Layout);
		Assert.Equal(("Checklist Item", "Checklist Item Value"), (p.ItemColumn, p.ValueColumn));

		SourceShaped shaped = SourceTable.Shape(raw, p);
		Assert.Empty(shaped.Problems);
		Assert.Equal(raw.Rows.Count, shaped.SourceRowCount);
		Assert.Equal(new[] { "BLD-2026-00123", "BLD-2026-00124", "COM-2026-00031", "", "BLD-2026-00123" },
			shaped.Table.Rows.Select(r => r[shaped.Table.IndexOf("Record ID")]));
		Assert.DoesNotContain("Checklist Item", shaped.Table.Columns);
		Assert.Contains("Checklist: Foundation % Damage", shaped.Table.Columns);
		SourceRow ranch = shaped.Table.Rows[0];
		Assert.Equal("10%", ranch[shaped.Table.IndexOf("Checklist: Foundation % Damage")]);
		Assert.Equal("Crawlspace", ranch[shaped.Table.IndexOf("Checklist: Foundation Type")]);
		Assert.Equal(2, ranch.RowNumber);

		// A checklist item answered twice: the last value is used, with a warning.
		string warning = Assert.Single(shaped.Warnings);
		Assert.Contains("BLD-2026-00124 / 4472", warning);
		Assert.Contains("Plumbing % Damage", warning);
		Assert.Equal("35", shaped.Table.Rows[1][shaped.Table.IndexOf("Checklist: Plumbing % Damage")]);

		// Checklist items are mapped like columns; numbers under a plain element name mean damage.
		Assert.Equal("Checklist: Foundation % Damage", p["Element.4"].Column);
		Assert.Equal("Checklist: Foundation Type", p["Foundation"].Column);
		Assert.Equal("Checklist: Exterior Finish", p["ExteriorFinish"].Column);
		Assert.Equal("Inspector", p["InspectorName"].Column);
	}

	[Fact]
	public void Shape_FiltersRows_AndReportsMissingColumns()
	{
		SourceTable raw = SourceTable.Read(Fixture("inspections-wide.csv"));
		SourceProfile p = SourceFields.Guess(raw);
		p.FilterColumn = "Inspection Type";
		p.FilterValues.Add("substantial damage assessment ");
		SourceShaped shaped = SourceTable.Shape(raw, p);
		Assert.Equal(4, shaped.Table.Rows.Count);
		Assert.Equal(1, shaped.FilteredOut);
		Assert.Equal(("Substantial Damage Assessment", 4), SourceTable.DistinctValues(raw, "Inspection Type")[0]);

		p.FilterColumn = "Nope";
		Assert.Contains("The filter column 'Nope' is not in the sheet.", SourceTable.Shape(raw, p).Problems);

		SourceProfile longProfile = new SourceProfile { Layout = SourceLayout.OneRowPerChecklistItem, RecordIdColumn = "Record ID" };
		Assert.Equal(2, SourceTable.Shape(raw, longProfile).Problems.Count);
	}

	[Theory]
	[InlineData("BaseCost", "$1,234.50", "1234.5")]
	[InlineData("Element.8", "40%", "40")]
	[InlineData("Element.8", " 100 ", "100")]
	[InlineData("DepthAboveLowestFloor", "3 ft", "3")]
	[InlineData("DepthAboveLowestFloor", "18 in", "1.5")]
	[InlineData("DepthAboveLowestFloor", "3' 6\"", "3.5")]
	[InlineData("DepthAboveLowestFloor", "2'", "2")]
	[InlineData("FloodDuration", "48 hrs", "48 hours")]
	[InlineData("FloodDuration", "2 Days", "2 days")]
	[InlineData("FloodDuration", "3", "3")]
	[InlineData("DateOfInspection", "10/7/2026 2:30 PM", "2026-10-07T14:30:00")]
	[InlineData("DateOfInspection", "2026-10-07", "2026-10-07")]
	[InlineData("Foundation", "Slab on grade", "Slab - on - Grade")]
	[InlineData("Foundation", "CRAWLSPACE", "Crawlspace")]
	[InlineData("Superstructure", "stud framed", "Stud-framed (Standard)")]
	[InlineData("DepreciationRating", "4", "4 - Average Condition")]
	[InlineData("DepreciationRating", "2 - Extensive repairs", "2 - Requires Extensive Repairs")]
	[InlineData("DepreciationRating", "Average", "4 - Average Condition")]
	[InlineData("FirmZone", "Zone AE", "AE")]
	[InlineData("Stories", "2 stories", "2")]
	[InlineData("Stories", "Two or More Stories", "Two or More Stories")]
	[InlineData("StructureType", "Commercial", "Non-Residential")]
	[InlineData("StructureType", "residential", "Residential")]
	[InlineData("DamageUndetermined", "Y", "Yes")]
	[InlineData("Sprinkler", "N", "No")]
	[InlineData("InspectorName", "  A. Inspector ", "A. Inspector")]
	public void Resolve_AcceptsHowInspectorsWriteValues(string key, string text, string expected)
	{
		using TempDatabase t = new TempDatabase();
		Assert.True(SourceValues.TryResolve(SourceFields.Get(key), text, null, t.Store.Reference, out string resolved));
		Assert.Equal(expected, resolved);
	}

	[Theory]
	[InlineData("BaseCost", "about 100")]
	[InlineData("Element.8", "Major")]
	[InlineData("DepthAboveLowestFloor", "3 m")]
	[InlineData("FloodDuration", "a while")]
	[InlineData("DateOfInspection", "last Tuesday")]
	[InlineData("Foundation", "Pier and beam")]
	[InlineData("StructureType", "Barn")]
	public void Resolve_RejectsWhatItCantUse(string key, string text)
	{
		using TempDatabase t = new TempDatabase();
		Assert.False(SourceValues.TryResolve(SourceFields.Get(key), text, null, t.Store.Reference, out _));
	}

	[Fact]
	public void Translations_TurnTheFilesWordsIntoFemaChoicesAndNumbers()
	{
		using TempDatabase t = new TempDatabase();
		ReferenceData r = t.Store.Reference;
		SourceFieldMap damage = new SourceFieldMap { Translate = { ["major"] = "75" } };
		Assert.True(SourceValues.TryResolve(SourceFields.Get("Element.8"), "Major", damage, r, out string pct));
		Assert.Equal("75", pct);
		SourceFieldMap foundation = new SourceFieldMap { Translate = { ["Pier and beam"] = "Piers and Posts" } };
		Assert.True(SourceValues.TryResolve(SourceFields.Get("Foundation"), "PIER AND BEAM", foundation, r, out string f));
		Assert.Equal("Piers and Posts", f);
		// A translation to something that isn't a FEMA choice is still unusable.
		SourceFieldMap bad = new SourceFieldMap { Translate = { ["Pier and beam"] = "Stilts" } };
		Assert.False(SourceValues.TryResolve(SourceFields.Get("Foundation"), "Pier and beam", bad, r, out _));
	}

	[Fact]
	public void Unmatched_ListsEachUnusableValueOnce_WithItsCount()
	{
		using TempDatabase t = new TempDatabase();
		SourceTable raw = SourceTable.Read(Fixture("inspections-wide.csv"));
		SourceProfile p = SourceFields.Guess(raw);
		p.Map("CauseOfDamage").Constant = "Flod";
		List<SourceUnmatched> unmatched = SourceValues.Unmatched(SourceTable.Shape(raw, p), p, t.Store.Reference);
		Assert.Equal(new[] { "Residence type: Single Family (1)", "Roof covering: Asphalt Shingles (1)", "Cause of damage: Flod (0)" },
			unmatched.Select(u => $"{u.FieldLabel}: {u.Text} ({u.Count})"));
		Assert.Contains("Single Family Residence", unmatched[0].Choices);

		p.Map("ResidenceType").Translate["single family"] = "Single Family Residence";
		Assert.DoesNotContain(SourceValues.Unmatched(SourceTable.Shape(raw, p), p, t.Store.Reference), u => u.FieldKey == "ResidenceType");
	}

	[Fact]
	public void Profile_RoundTripsThroughJson_AndDropsWhatItDoesntKnow()
	{
		SourceProfile p = SourceFields.Guess(SourceTable.Read(Fixture("checklist-long.csv")));
		p.Name = "Example County 2026";
		p.FilterColumn = "Inspection Type";
		p.FilterValues.Add("Substantial Damage Assessment");
		p.Map("BaseCost").Constant = "118.75";
		p.Map("RoofCovering").Translate["Asphalt Shingles"] = "Shingles - Asphalt, Wood (Standard)";
		p.RecordIdCustomField = 2;
		p.UseDefaultValues = false;
		string json = p.ToJson();
		Assert.Contains("\"OneRowPerChecklistItem\"", json);
		Assert.DoesNotContain("Unknown", json);

		SourceProfile back = SourceProfile.FromJson(json, out List<string> warnings);
		Assert.Empty(warnings);
		Assert.Equal(json, back.ToJson());
		Assert.Equal("Shingles - Asphalt, Wood (Standard)", back["RoofCovering"].Translate["asphalt shingles"]);

		string tampered = json.Replace("\"Name\":", "\"Colour\": \"blue\",\n  \"Name\":").Replace("\"BaseCost\":", "\"Element.99\": { \"Column\": \"x\" },\n    \"BaseCost\":");
		SourceProfile partial = SourceProfile.FromJson(tampered, out warnings);
		Assert.Equal(2, warnings.Count);
		Assert.Equal(json, partial.ToJson());
	}
}
