using ClosedXML.Excel;
using Sde.Core;
using Sde.Core.Records;

namespace Sde.Interop.Tests;

/// <summary>Enterprise Import: csv/xlsx sources, parsing modes, lookups, preview problems, import, saved mappings.</summary>
public class EnterpriseImportTests
{
	private const string Csv =
		"Owner,Address,Unit,City,ST,County,Zip,Lat,Lon,Type,SqFt,Zone,Floodway,Year,Phone,Panel Date,BFE\r\n"
		+ "\"Doe, John\",123 Martin Luther King Blvd,Apt 2,Exampleville,OH,Union County,43702,39.1031,-84.512,Residential,\"1,450\",AE,No,1962,(555) 555-1212,9/30/2015,512.5\r\n"
		+ "\"Smith, Jane Q\",45 Elm St.,,Exampleville,ohio,union,43703,,,non-residential,,ae,,abc,,,\r\n"
		+ "\"Roe, Rick\",77 Oak Lane,,Dayton,ZZ,Montgomery,45402,95,-84,Shed,,QQ,,1950,,not a date,\r\n"
		+ "\"Nobody\",,,,,,,,,,,,,,,,\r\n"
		+ ",,,,,,,,,,,,,,,,\r\n";

	private static EnterpriseMapping CsvMapping()
	{
		EnterpriseMapping m = new EnterpriseMapping
		{
			FirstNameParsing = NameParsing.LastCommaFirst,
			LastNameParsing = NameParsing.LastCommaFirst,
			StreetNumberParsing = StreetNumberParsing.NumberNameSuffix,
		};
		m[EnterpriseField.OwnerFirstName] = "Owner";
		m[EnterpriseField.OwnerLastName] = "Owner";
		m[EnterpriseField.StreetNumber] = "Address";
		m[EnterpriseField.ApartmentUnit] = "Unit";
		m[EnterpriseField.City] = "City";
		m[EnterpriseField.State] = "ST";
		m[EnterpriseField.County] = "County";
		m[EnterpriseField.Zip] = "Zip";
		m[EnterpriseField.Latitude] = "Lat";
		m[EnterpriseField.Longitude] = "Lon";
		m[EnterpriseField.StructureType] = "Type";
		m[EnterpriseField.TotalSquareFootage] = "SqFt";
		m[EnterpriseField.FirmZone] = "Zone";
		m[EnterpriseField.RegulatoryFloodway] = "Floodway";
		m[EnterpriseField.YearOfConstruction] = "Year";
		m[EnterpriseField.PhoneNumber] = "Phone";
		m[EnterpriseField.FirmPanelDate] = "Panel Date";
		m[EnterpriseField.BaseFloodElevation] = "BFE";
		return m;
	}

	private static int LookupId(ReferenceData r, int type, string name) => r.Lookups.Single(l => l.LookupTypeID == type && l.LookupFieldName == name).LookupID;

	[Fact]
	public void Csv_PreviewParsesAndResolves_ImportCreatesPropertiesAndShapes()
	{
		using TempDatabase t = new TempDatabase();
		ReferenceData r = t.Store.Reference;
		string path = Path.Combine(t.Folder, "parcels.csv");
		File.WriteAllText(path, Csv);

		Assert.Equal(new[] { "parcels" }, EnterpriseImport.SheetNames(path));
		SourceTable table = EnterpriseImport.ReadTable(path);
		Assert.Equal(17, table.Columns.Count);
		Assert.Equal("Owner", table.Columns[0]);
		Assert.Equal(4, table.Rows.Count);                       // the all-blank row is left out
		Assert.Equal(2, table.Rows[0].RowNumber);

		EnterprisePreview preview = EnterpriseImport.Preview(table, CsvMapping(), r);
		Assert.Empty(preview.MappingProblems);
		Assert.Equal(4, preview.Rows.Count);

		// Row 2: "Doe, John" / "123 Martin Luther King Blvd" + unit, every lookup resolved.
		EnterpriseRow a = preview.Rows[0];
		Assert.True(a.IsValid);
		Assert.Empty(a.Warnings);
		PropertyRecord p = a.Property;
		Assert.Equal("John", p.OwnerFirstName);
		Assert.Equal("Doe", p.OwnerLastName);
		Assert.Equal("123 Apt 2", p.StreetNumber);
		Assert.Equal("Martin Luther King", p.StreetName);
		Assert.Equal(LookupId(r, LookupTypes.StreetSuffix, "Boulevard"), p.StreetSuffixID);
		Assert.Equal(36, p.StateID);
		Assert.Equal("Union", r.CountyName(p.CountyID));
		Assert.Equal(36, r.Counties.Single(c => c.CountyID == p.CountyID).StateID);
		Assert.Equal("43702", p.ZipCode);
		Assert.Equal(39.1031m, p.Latitude);
		Assert.Equal(-84.512m, p.Longitude);
		Assert.Equal(1, p.StructureTypeID);
		Assert.Equal(1450m, p.SquareFootage);
		Assert.Equal(LookupId(r, LookupTypes.FirmZone, "AE"), p.FIRMZone);
		Assert.Equal(LookupId(r, LookupTypes.RegulatoryFloodway, "No"), p.RegulatoryFloodway);
		Assert.Equal(1962, p.YearOfConstruction);
		Assert.Equal("5555551212", p.PhoneNumber);
		Assert.Equal(new DateTime(2015, 9, 30), p.FIRMPanelDate);
		Assert.Equal(512.5m, p.BFE);

		// Row 3: case-insensitive state/county/zone/type, "St." suffix, middle initial stays with the first name.
		EnterpriseRow b = preview.Rows[1];
		Assert.True(b.IsValid);
		Assert.Equal("Jane Q", b.Property.OwnerFirstName);
		Assert.Equal("Smith", b.Property.OwnerLastName);
		Assert.Equal("45", b.Property.StreetNumber);
		Assert.Equal("Elm", b.Property.StreetName);
		Assert.Equal(LookupId(r, LookupTypes.StreetSuffix, "Street"), b.Property.StreetSuffixID);
		Assert.Equal(36, b.Property.StateID);
		Assert.Equal("Union", r.CountyName(b.Property.CountyID));
		Assert.Equal(2, b.Property.StructureTypeID);
		Assert.Equal(LookupId(r, LookupTypes.FirmZone, "AE"), b.Property.FIRMZone);
		Assert.Null(b.Property.YearOfConstruction);
		Assert.Contains("Year of Construction 'abc' is not a whole number", b.Warnings);

		// Row 4: imports, but the unusable values are reported and left blank.
		EnterpriseRow c = preview.Rows[2];
		Assert.True(c.IsValid);
		Assert.Equal("Lane", r.LookupName(c.Property.StreetSuffixID));
		Assert.Null(c.Property.StateID);
		Assert.Null(c.Property.Latitude);
		Assert.Null(c.Property.StructureTypeID);
		Assert.Null(c.Property.FIRMZone);
		Assert.Null(c.Property.FIRMPanelDate);
		Assert.Contains("State 'ZZ' is not recognised", c.Warnings);
		Assert.Contains(c.Warnings, w => w.StartsWith("Latitude 95 is outside"));
		Assert.Contains(c.Warnings, w => w.StartsWith("Structure type 'Shed'"));
		Assert.Contains("FIRM Zone 'QQ' is not recognised", c.Warnings);
		Assert.Contains("Date of FIRM Panel 'not a date' is not a date", c.Warnings);

		// Row 5: no address → can't be imported.
		EnterpriseRow d = preview.Rows[3];
		Assert.False(d.IsValid);
		Assert.False(d.Selected);
		Assert.Contains("No street number or street name", d.Problems);
		Assert.Equal("Nobody", d.Property.OwnerLastName);       // no comma: whole value as the last name
		Assert.Contains(d.Warnings, w => w.Contains("has no comma"));

		// Deselect row 4, import the rest.
		preview.Rows[2].Selected = false;
		Assert.True(preview.CanImport);
		int before = t.Store.GetProperties().Count;
		Assert.Equal(2, EnterpriseImport.Import(t.Store, preview));
		Assert.Equal(before + 2, t.Store.GetProperties().Count);

		PropertyRecord saved = t.Store.GetProperty(a.ImportedPropertyID!.Value);
		Assert.True(saved.Active);
		Assert.NotEqual(default, saved.ImportDate);
		Assert.NotNull(saved.LastUpdate);
		Assert.Equal("Martin Luther King", saved.StreetName);
		Assert.Equal(1450m, saved.SquareFootage);
		ShapeRecord shape = Assert.Single(t.Store.GetShapes(saved.PropertyID));
		Assert.Equal("c", shape.ShapeType);
		Assert.Equal(1450m, shape.TotalSqFt);
		Assert.Empty(t.Store.GetShapes(b.ImportedPropertyID!.Value));   // no square footage → no shape
		Assert.Null(c.ImportedPropertyID);
	}

	[Fact]
	public void Xlsx_SecondSheetWithoutHeaders_NumberStreetNameSuffixInOneColumn()
	{
		using TempDatabase t = new TempDatabase();
		ReferenceData r = t.Store.Reference;
		string path = Path.Combine(t.Folder, "county.xlsx");
		using (XLWorkbook wb = new XLWorkbook())
		{
			wb.Worksheets.Add("Notes").Cell(1, 1).SetValue("ignore me");
			IXLWorksheet ws = wb.Worksheets.Add("Parcels");
			ws.Cell(1, 1).SetValue("SMITH JOHN");
			ws.Cell(1, 2).SetValue("400 N Main Rd");
			ws.Cell(1, 3).SetValue(501);
			ws.Cell(1, 3).Style.NumberFormat.Format = "00000";
			ws.Cell(1, 4).SetValue(2400.0);
			ws.Cell(1, 5).SetValue(new DateTime(2009, 2, 1));
			ws.Cell(1, 6).SetValue("Ohio");
			ws.Cell(2, 1).SetValue("JONES MARY ANN");
			ws.Cell(2, 2).SetValue("12 Broadway");
			ws.Cell(2, 6).SetValue("OH");
			wb.SaveAs(path);
		}

		Assert.Equal(new[] { "Notes", "Parcels" }, EnterpriseImport.SheetNames(path));
		SourceTable table = EnterpriseImport.ReadTable(path, "parcels", firstRowIsHeader: false);
		Assert.Equal("Parcels", table.SheetName);
		Assert.Equal(new[] { "Column1", "Column2", "Column3", "Column4", "Column5", "Column6" }, table.Columns);
		Assert.Equal("00501", table.Rows[0][2]);
		Assert.Equal("2009-02-01", table.Rows[0][4]);

		EnterpriseMapping m = new EnterpriseMapping
		{
			Name = "County parcels",
			LastNameParsing = NameParsing.FirstLast,
			StreetNameParsing = StreetNameParsing.NumberNameSuffix,
		};
		m[EnterpriseField.OwnerLastName] = "Column1";
		m[EnterpriseField.StreetName] = "Column2";
		m[EnterpriseField.Zip] = "Column3";
		m[EnterpriseField.TotalSquareFootage] = "Column4";
		m[EnterpriseField.FirmPanelDate] = "Column5";
		m[EnterpriseField.State] = "Column6";

		EnterprisePreview preview = EnterpriseImport.Preview(table, m, r);
		Assert.Empty(preview.MappingProblems);
		PropertyRecord p = preview.Rows[0].Property;
		Assert.Equal("JOHN", p.OwnerLastName);
		Assert.Equal("400", p.StreetNumber);
		Assert.Equal("N Main", p.StreetName);
		Assert.Equal(LookupId(r, LookupTypes.StreetSuffix, "Road"), p.StreetSuffixID);
		Assert.Equal("00501", p.ZipCode);
		Assert.Equal(2400m, p.SquareFootage);
		Assert.Equal(new DateTime(2009, 2, 1), p.FIRMPanelDate);
		Assert.Equal(36, p.StateID);

		PropertyRecord q = preview.Rows[1].Property;
		Assert.Equal("ANN", q.OwnerLastName);
		Assert.Equal("12", q.StreetNumber);
		Assert.Equal("Broadway", q.StreetName);                    // last word isn't a suffix: kept in the name
		Assert.Null(q.StreetSuffixID);
		Assert.Contains("No street suffix found", preview.Rows[1].Warnings);

		Assert.Equal(2, EnterpriseImport.Import(t.Store, preview));
	}

	[Fact]
	public void Preview_MappingProblems_BlockImport()
	{
		using TempDatabase t = new TempDatabase();
		string path = Path.Combine(t.Folder, "parcels.csv");
		File.WriteAllText(path, Csv);
		SourceTable table = EnterpriseImport.ReadTable(path);

		EnterpriseMapping m = new EnterpriseMapping();
		m[EnterpriseField.City] = "City";
		EnterprisePreview none = EnterpriseImport.Preview(table, m, t.Store.Reference);
		Assert.Contains(none.MappingProblems, x => x.Contains("Street Number or Street Name"));

		m[EnterpriseField.StreetName] = "Street";                   // not a column of this file
		EnterprisePreview missing = EnterpriseImport.Preview(table, m, t.Store.Reference);
		Assert.Contains("Column 'Street' (mapped to Street Name) is not in the sheet.", missing.MappingProblems);
		Assert.False(missing.CanImport);
		Assert.Throws<InvalidOperationException>(() => EnterpriseImport.Import(t.Store, missing));
	}

	[Fact]
	public void Mapping_RoundTripsThroughTheSettingsTable()
	{
		using TempDatabase t = new TempDatabase();
		EnterpriseMapping m = CsvMapping();
		m.Name = EnterpriseMapping.DefaultName(new DateTime(2026, 10, 7, 14, 5, 0));
		Assert.Equal("Enterprise Import Setting (10/07/2026 14:05)", m.Name);

		EnterpriseImportSettingsRecord record = m.ToRecord();
		Assert.Equal("Owner", record.OwnersFirstName);
		Assert.Equal("431", record.OwnersFirstNameParsing);
		Assert.Equal("Address", record.AddressLine1);
		Assert.Equal("433", record.AddressLine1Parsing);
		Assert.Equal("0", record.AddressLine2Parsing);              // "Make Selection..."
		Assert.Null(record.AddressLine2);
		Assert.Equal("Zip", record.Zip);
		Assert.Equal("SqFt", record.TotalSqFt);
		Assert.Equal("Year", record.YrOfCon);
		Assert.Equal("Panel Date", record.DateOfFirmPanel);
		Assert.Equal("BFE", record.BFE);
		Assert.Equal("Floodway", record.RegFloodway);

		t.Store.SaveEnterpriseMapping(record);
		Assert.True(record.EIS_ID > 0);
		EnterpriseImportSettingsRecord loaded = Assert.Single(t.Store.GetEnterpriseMappings());
		EnterpriseMapping back = EnterpriseMapping.FromRecord(loaded);
		Assert.Equal(record.EIS_ID, back.Id);
		Assert.Equal(m.Name, back.Name);
		Assert.Equal(m.Columns.OrderBy(x => x.Key), back.Columns.OrderBy(x => x.Key));
		Assert.Equal(NameParsing.LastCommaFirst, back.FirstNameParsing);
		Assert.Equal(StreetNumberParsing.NumberNameSuffix, back.StreetNumberParsing);
		Assert.Equal(StreetNameParsing.None, back.StreetNameParsing);
		Assert.Equal(30, EnterpriseMapping.Fields.Count);

		// A row as the original saved it: blank text for unmapped fields, parsing IDs as text.
		EnterpriseMapping original = EnterpriseMapping.FromRecord(new EnterpriseImportSettingsRecord
		{
			EIS_ID = 3, EISName = "From SDE", AddressLine1 = "Num", AddressLine1Parsing = "432", AddressLine2 = "", AddressLine2Parsing = "436",
			OwnersFirstNameParsing = "", StreetSuffix = "Sfx", StreetSuffixParsing = "439", Zip = "ZIP",
		});
		Assert.Equal(new[] { EnterpriseField.StreetNumber, EnterpriseField.StreetSuffix, EnterpriseField.Zip }, original.Columns.Keys.OrderBy(k => k));
		Assert.Equal(StreetNameParsing.Name, original.StreetNameParsing);
		Assert.Equal(NameParsing.None, original.FirstNameParsing);
	}
}
