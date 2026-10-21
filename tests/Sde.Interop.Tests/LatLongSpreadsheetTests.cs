using ClosedXML.Excel;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Interop.Tests;

/// <summary>Lat/Long spreadsheet: export → edit → read (preview with problems) → apply selected rows.</summary>
public class LatLongSpreadsheetTests
{
	[Fact]
	public void ExportEditImport_RoundTrip()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		Guid ranchId = ranch.Property.PropertyID;
		PropertyRecord other = t.Store.SaveProperty(new PropertyRecord
		{
			StructureTypeID = 1, StreetNumber = "9", StreetName = "Elm", StateID = 36, Latitude = 39.5m, Longitude = -84.4m,
		});
		PropertyRecord third = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1, StreetNumber = "10", StreetName = "Oak", StateID = 36 });

		string path = Path.Combine(t.Folder, "latlong.xlsx");
		Assert.Equal(3, LatLongSpreadsheet.Export(t.Store, path));

		using (XLWorkbook wb = new XLWorkbook(path))
		{
			IXLWorksheet ws = wb.Worksheet(1);
			Assert.Equal(new[] { LatLongSpreadsheet.IdHeader, LatLongSpreadsheet.AddressHeader, "Latitude", "Longitude" },
				Enumerable.Range(1, 4).Select(c => ws.Cell(1, c).GetString()));
			Assert.Equal("SDE Property ID - DON'T CHANGE", ws.Cell(1, 1).GetString());
			Assert.Equal("Address - Any changes will not be recognized", ws.Cell(1, 2).GetString());
			int ranchRow = FindRow(ws, ranchId);
			Assert.Equal("123 River Street", ws.Cell(ranchRow, 2).GetString());
			Assert.True(ws.Cell(ranchRow, 3).IsEmpty());
			Assert.Equal(39.5, ws.Cell(FindRow(ws, other.PropertyID), 3).GetDouble());

			// The user fills in the ranch's coordinates, leaves "Elm" unchanged, and types junk elsewhere.
			ws.Cell(ranchRow, 3).SetValue(39.1031);
			ws.Cell(ranchRow, 4).SetValue("-84.512");
			int thirdRow = FindRow(ws, third.PropertyID);
			ws.Cell(thirdRow, 3).SetValue("north-ish");
			ws.Cell(thirdRow, 4).SetValue(-84.5);
			ws.Cell(5, 1).SetValue(Guid.NewGuid().ToString());
			ws.Cell(5, 3).SetValue(39.0);
			ws.Cell(5, 4).SetValue(-84.0);
			ws.Cell(6, 1).SetValue(other.PropertyID.ToString());
			ws.Cell(6, 3).SetValue(95.0);
			ws.Cell(6, 4).SetValue(-84.0);
			wb.Save();
		}

		List<LatLongRow> rows = LatLongSpreadsheet.Read(t.Store, path);
		Assert.Equal(5, rows.Count);

		LatLongRow r = rows.Single(x => x.PropertyID == ranchId);
		Assert.True(r.IsValid);
		Assert.True(r.IsChanged);
		Assert.True(r.Selected);
		Assert.Null(r.CurrentLatitude);
		Assert.Equal(39.1031m, r.NewLatitude);
		Assert.Equal(-84.512m, r.NewLongitude);
		Assert.Equal("123 River Street", r.Address);
		Assert.Null(r.Warning);

		LatLongRow unchanged = rows.First(x => x.PropertyID == other.PropertyID);
		Assert.True(unchanged.IsValid);
		Assert.False(unchanged.Selected);

		LatLongRow junk = rows.Single(x => x.PropertyID == third.PropertyID);
		Assert.Contains("Latitude 'north-ish' is not a number", junk.Problems);
		Assert.False(junk.Selected);

		LatLongRow unknown = rows.Single(x => x.RowNumber == 5);
		Assert.Null(unknown.PropertyID);
		Assert.Contains("No structure with this ID in the database", unknown.Problems);

		LatLongRow duplicate = rows.Single(x => x.RowNumber == 6);
		Assert.Contains(duplicate.Problems, p => p.StartsWith("Same structure as row"));
		Assert.Contains(duplicate.Problems, p => p.StartsWith("Latitude 95"));

		Assert.Equal(1, LatLongSpreadsheet.Apply(t.Store, rows));
		PropertyRecord saved = t.Store.GetProperty(ranchId);
		Assert.Equal(39.1031m, saved.Latitude);
		Assert.Equal(-84.512m, saved.Longitude);
		Assert.Equal("River", saved.StreetName);
		Assert.Equal(39.5m, t.Store.GetProperty(other.PropertyID).Latitude);
		Assert.Null(t.Store.GetProperty(third.PropertyID).Latitude);
	}

	[Fact]
	public void Read_FindsColumnsByHeader_NotPosition()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		string path = Path.Combine(t.Folder, "reordered.xlsx");
		using (XLWorkbook wb = new XLWorkbook())
		{
			IXLWorksheet ws = wb.Worksheets.Add("Geocoded");
			ws.Cell(1, 1).SetValue("Longitude");
			ws.Cell(1, 2).SetValue("Notes");
			ws.Cell(1, 3).SetValue("Latitude");
			ws.Cell(1, 4).SetValue(LatLongSpreadsheet.IdHeader);
			ws.Cell(2, 1).SetValue(-84.6);
			ws.Cell(2, 2).SetValue("from county GIS");
			ws.Cell(2, 3).SetValue(39.2);
			ws.Cell(2, 4).SetValue(ranch.Property.PropertyID.ToString().ToUpperInvariant());
			wb.SaveAs(path);
		}
		LatLongRow row = Assert.Single(LatLongSpreadsheet.Read(t.Store, path));
		Assert.Equal(ranch.Property.PropertyID, row.PropertyID);
		Assert.Equal(39.2m, row.NewLatitude);
		Assert.Equal(-84.6m, row.NewLongitude);
		Assert.True(row.Selected);
	}

	[Fact]
	public void Read_WrongFormat_Throws()
	{
		using TempDatabase t = new TempDatabase();
		string path = Path.Combine(t.Folder, "wrong.csv");
		File.WriteAllText(path, "a,b,c\n1,2,3\n");
		Assert.Throws<FormatException>(() => LatLongSpreadsheet.Read(t.Store, path));
	}

	private static int FindRow(IXLWorksheet ws, Guid id) =>
		ws.RowsUsed().First(r => r.Cell(1).GetString() == id.ToString()).RowNumber();
}
