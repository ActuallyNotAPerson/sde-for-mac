using Sde.Core.Addresses;
using Sde.Core.Records;

namespace Sde.Interop.Tests;

/// <summary>Matching records to structures (addresses, parcel numbers), old and shipped profiles.</summary>
public class SourceMatchingTests
{
	public static string Fixture(string name) => Path.Combine(TestPaths.Root, "tests", "fixtures", "import", name);

	[Theory]
	[InlineData("123 N. Main St.", "123 North Main Street")]
	[InlineData("123 main st", "123 MAIN STREET")]
	[InlineData("123 Main Av", "123 Main Avenue")]
	[InlineData("123 Main Ave Apt 4", "123 Main Avenue")]           // units are ignored by default
	[InlineData("123 Main Ave #4", "123 Main Avenue Unit 7")]
	[InlineData("0123 Main", "123 Main St")]                         // a missing suffix doesn't stop a match
	[InlineData("15 First Ave", "15 1st Avenue")]
	[InlineData("15 Placeholder St N", "15 Placeholder Street North")]
	public void Address_SameBuilding(string a, string b)
	{
		using TempDatabase t = new TempDatabase();
		AddressMatcher m = new AddressMatcher(t.Store.Reference);
		Assert.True(m.Same(m.KeyFor(null, a, null), m.KeyFor(null, b, null)), $"{a} = {b}");
	}

	[Theory]
	[InlineData("123 Main St", "125 Main St")]
	[InlineData("123 Main St", "123 Main Ave")]
	[InlineData("123 Main St", "123 Maine St")]
	[InlineData("123 N Main St", "123 S Main St")]
	[InlineData("Main St", "Main St")]                               // no house number: never a match
	public void Address_DifferentBuildings(string a, string b)
	{
		using TempDatabase t = new TempDatabase();
		AddressMatcher m = new AddressMatcher(t.Store.Reference);
		Assert.False(m.Same(m.KeyFor(null, a, null), m.KeyFor(null, b, null)), $"{a} ≠ {b}");
	}

	[Fact]
	public void Address_UnitsAndZipCodes()
	{
		using TempDatabase t = new TempDatabase();
		AddressMatcher units = new AddressMatcher(t.Store.Reference, matchUnit: true);
		Assert.True(units.Same(units.KeyFor("12 Apt 4", "Elm St", null), units.KeyFor(null, "12 Elm Street Apt. 4", null)));
		Assert.False(units.Same(units.KeyFor("12 Apt 4", "Elm St", null), units.KeyFor(null, "12 Elm Street Apt 5", null)));
		Assert.False(units.Same(units.KeyFor(null, "12 Elm St", null), units.KeyFor(null, "12 Elm Street Apt 5", null)));

		AddressMatcher m = new AddressMatcher(t.Store.Reference);
		Assert.False(m.Same(m.KeyFor(null, "12 Elm St", null, "99999"), m.KeyFor(null, "12 Elm St", null, "99998-1234")));
		Assert.True(m.Same(m.KeyFor(null, "12 Elm St", null, "99999-0001"), m.KeyFor(null, "12 Elm St", null)));

		// A structure's own suffix (FEMA's choice) counts the same as one written in the name.
		int street = t.Store.Reference.OfType(Sde.Core.LookupTypes.StreetSuffix).Single(l => l.LookupFieldName == "Street").LookupID;
		PropertyRecord p = new PropertyRecord { StreetNumber = "12", StreetName = "Elm", StreetSuffixID = street };
		Assert.True(m.Same(m.KeyFor(p), m.KeyFor(null, "12 elm st.", null)));
		AddressIndex<PropertyRecord> index = m.Index(new[] { p, new PropertyRecord { StreetNumber = "12", StreetName = "Elm Avenue" } }, m.KeyFor);
		Assert.Same(p, Assert.Single(index.Find(m.KeyFor(null, "12 Elm Street", null))));
		Assert.Equal(2, index.Find(m.KeyFor(null, "12 Elm", null)).Count);   // no suffix: both, so ambiguous
	}

	[Theory]
	[InlineData("0123-0045-0067-00", false, "0123004500670 0")]
	[InlineData("0123.0045 0067-00", false, "01230045006700")]
	[InlineData(" 0123-0045-0067-00 ", true, "123004500670 0")]
	[InlineData("ab-12", false, "AB12")]
	[InlineData("000", true, "0")]
	[InlineData(" - ", false, null)]
	public void Parcel_Normalize(string parcel, bool ignoreLeadingZeros, string expected) =>
		Assert.Equal(expected?.Replace(" ", ""), ParcelNumbers.Normalize(parcel, ignoreLeadingZeros));

	/// <summary>A profile saved by v1.5 (its Accela import) still loads: Inspections, by record ID, from "Accela".</summary>
	[Fact]
	public void OldAccelaProfileJson_StillLoads()
	{
		const string v15 = """
			{
			  "Name": "Example County",
			  "Layout": "OneRowPerChecklistItem",
			  "ItemColumn": "Checklist Item",
			  "ValueColumn": "Checklist Item Value",
			  "RecordIdColumn": "Record ID",
			  "InspectionIdColumn": "Inspection ID",
			  "FilterColumn": "Inspection Type",
			  "FilterValues": [ "Substantial Damage Assessment" ],
			  "FirstNameParsing": "FirstLast",
			  "LastNameParsing": "FirstLast",
			  "StreetNumberParsing": "None",
			  "StreetNameParsing": "NumberNameSuffix",
			  "SuffixParsing": "None",
			  "UseDefaultValues": false,
			  "RecordIdCustomField": 1,
			  "Fields": {
			    "StreetName": { "Column": "Address", "Translate": {} },
			    "ResidenceType": { "Column": "Checklist: Residence Type", "Translate": { "Single Family": "Single Family Residence" } },
			    "BaseCost": { "Constant": "118.75", "Translate": {} },
			    "Element.4": { "Column": "Checklist: Foundation % Damage", "Translate": {} }
			  }
			}
			""";
		SourceProfile p = SourceProfile.FromJson(v15, out List<string> warnings);
		Assert.Empty(warnings);
		Assert.Equal(("Example County", "Accela"), (p.Name, p.SourceName));
		Assert.Equal((SourceMode.Inspections, SourceMatchBy.RecordId, false), (p.Mode, p.MatchBy, p.ValueIsBuildingOnly));
		Assert.Equal(SourceLayout.OneRowPerChecklistItem, p.Layout);
		Assert.Equal(("Checklist Item", "Checklist Item Value", "Inspection ID"), (p.ItemColumn, p.ValueColumn, p.InspectionIdColumn));
		Assert.Equal(new[] { "Substantial Damage Assessment" }, p.FilterValues);
		Assert.Equal((NameParsing.FirstLast, StreetNameParsing.NumberNameSuffix, false, 1), (p.FirstNameParsing, p.StreetNameParsing, p.UseDefaultValues, p.RecordIdCustomField));
		Assert.Equal("Single Family Residence", p["ResidenceType"].Translate["single family"]);
		Assert.Equal("118.75", p["BaseCost"].Constant);

		// Saved again, it says what it is, and reads back the same.
		string json = p.ToJson();
		Assert.Contains("\"SourceName\": \"Accela\"", json);
		Assert.Equal(json, SourceProfile.FromJson(json).ToJson());
		SourceProfile renamed = SourceProfile.FromJson(json.Replace("\"SourceName\": \"Accela\"", "\"SourceName\": \"Permits\""));
		Assert.Equal("Permits", renamed.SourceName);
	}

	[Fact]
	public void ShippedProfiles_LoadAndFitTheirExampleFiles()
	{
		using TempDatabase t = new TempDatabase();
		Assert.Equal(new[] { "Generic inspections", "County parcels", "Accela (example)" }, ShippedProfiles.Names);
		Assert.Null(ShippedProfiles.Get("Nope"));
		foreach ((string name, string file, SourceMode mode) in new[]
		{
			(ShippedProfiles.GenericInspections, "generic-inspections.csv", SourceMode.Inspections),
			(ShippedProfiles.CountyParcels, "county-parcels.csv", SourceMode.Structures),
			(ShippedProfiles.AccelaExample, "accela-example.csv", SourceMode.Inspections),
		})
		{
			SourceProfile p = ShippedProfiles.Get(name);
			Assert.Equal((name, mode), (p.Name, p.Mode));
			SourceTable raw = SourceTable.Read(Fixture(file));
			SourcePreview preview = SourceImport.Preview(SourceTable.Shape(raw, p), p, t.Store, TestContext.Current.CancellationToken);
			Assert.Empty(preview.Unmatched);
			// The generic example uses a building value, so it asks first (Flag 4); nothing else is wrong with any of them.
			Assert.Equal(name == ShippedProfiles.GenericInspections, preview.NeedsBuildingValueConfirmation);
			p.ValueIsBuildingOnly = true;
			preview = SourceImport.Preview(SourceTable.Shape(raw, p), p, t.Store, TestContext.Current.CancellationToken);
			Assert.Empty(preview.MappingProblems);
			Assert.All(preview.Rows, r => Assert.True(r.CanImport, $"{name} row {r.RowNumber}: {string.Join("; ", r.Problems)}"));
			Assert.All(preview.Rows.SelectMany(r => r.Warnings), w => Assert.DoesNotContain("left as it was", w));
		}
	}

	[Fact]
	public void Guess_AnAssessorRoll_IsStructuresByParcel()
	{
		SourceProfile p = SourceFields.Guess(SourceTable.Read(Fixture("county-parcels.csv")));
		Assert.Equal((SourceMode.Structures, SourceMatchBy.Parcel, "Parcel ID"), (p.Mode, p.MatchBy, p.RecordIdColumn));
		Assert.Equal("Parcel ID", p["ParcelNumber"].Column);
		Assert.Equal(("Site Address", "Year Built"), (p["StreetName"].Column, p["YearOfConstruction"].Column));
	}
}
