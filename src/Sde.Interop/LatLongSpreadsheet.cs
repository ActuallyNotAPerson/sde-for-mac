using ClosedXML.Excel;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>One row of a Lat/Long spreadsheet being imported: what is in the database, what the sheet says, and whether it can be applied.</summary>
public sealed class LatLongRow
{
	/// <summary>The row's number in the sheet (the header is row 1).</summary>
	public int RowNumber { get; init; }

	/// <summary>The ID cell as written in the sheet.</summary>
	public string PropertyIdText { get; init; }

	/// <summary>The structure the row refers to (null when the ID is not valid or not in the database).</summary>
	public Guid? PropertyID { get; init; }

	/// <summary>The structure's address from the database (the sheet's address column when the structure is unknown).</summary>
	public string Address { get; init; }

	public decimal? CurrentLatitude { get; init; }
	public decimal? CurrentLongitude { get; init; }
	public decimal? NewLatitude { get; init; }
	public decimal? NewLongitude { get; init; }

	/// <summary>Reasons the row can't be applied (unknown ID, not a number, out of range…).</summary>
	public List<string> Problems { get; } = new();

	/// <summary>A note that doesn't stop the row being applied (e.g. the point is outside the structure's state).</summary>
	public string Warning { get; set; }

	public bool IsValid => Problems.Count == 0;

	/// <summary>The sheet's coordinates differ from the database's.</summary>
	public bool IsChanged => IsValid && (NewLatitude != CurrentLatitude || NewLongitude != CurrentLongitude);

	/// <summary>Whether <see cref="LatLongSpreadsheet.Apply"/> saves this row. Defaults to valid and changed.</summary>
	public bool Selected { get; set; }
}

/// <summary>
/// "Create Spreadsheet for Latitude / Longitude" and its re-import: export every active structure's ID, address and
/// coordinates to .xlsx, let the user fill in coordinates (e.g. from a geocoder), then read the sheet back.
///
/// Ports decomp/wpfSDE/wpfSDE.Tools.LatLong/CreateExport.xaml(.cs) (headers in CreateExport.xaml:19-22; the address is
/// BLL PropertyService.GetAllProperties' StreetNumber + StreetName + suffix name, PropertyService.cs:82-85) and
/// Import.xaml.cs + DAL PropertyService.UpdateLatLon (decomp/DAL/DAL.Services/PropertyService.cs:215-224).
///
/// Changes from the original:
/// <list type="bullet">
/// <item>Columns are found by their headers (the ID column contains "Property"), not by position, so reordered or
/// extra columns work; the original renamed columns 0/1 and read cells 0, 2 and 3 (Import.xaml.cs:52-57, 101-102).</item>
/// <item>Rows are checked before anything is saved (unknown ID, not a number, out of range, duplicate rows) and shown
/// as a preview; the original silently skipped rows that threw.</item>
/// <item>Only selected rows are saved, in one transaction; the original's SQL was built by string concatenation.</item>
/// <item>Latitude/longitude are written as numbers (6 decimals) rather than text, so they can be edited in Excel.</item>
/// </list>
/// </summary>
public static class LatLongSpreadsheet
{
	public const string IdHeader = "SDE Property ID - DON'T CHANGE";
	public const string AddressHeader = "Address - Any changes will not be recognized";
	public const string LatitudeHeader = "Latitude";
	public const string LongitudeHeader = "Longitude";

	/// <summary>Writes the spreadsheet for every active structure; returns the number of rows.</summary>
	public static int Export(SdeStore store, string xlsxPath)
	{
		ReferenceData reference = store.Reference;
		List<PropertyRecord> properties = store.GetProperties(activeOnly: true);
		using XLWorkbook wb = new XLWorkbook();
		IXLWorksheet ws = wb.Worksheets.Add("Sheet1");
		ws.Cell(1, 1).SetValue(IdHeader);
		ws.Cell(1, 2).SetValue(AddressHeader);
		ws.Cell(1, 3).SetValue(LatitudeHeader);
		ws.Cell(1, 4).SetValue(LongitudeHeader);
		int r = 2;
		foreach (PropertyRecord p in properties)
		{
			ws.Cell(r, 1).SetValue(p.PropertyID.ToString());
			ws.Cell(r, 2).SetValue(Address(p, reference));
			if (p.Latitude is decimal lat)
			{
				ws.Cell(r, 3).SetValue((double)lat);
			}
			if (p.Longitude is decimal lon)
			{
				ws.Cell(r, 4).SetValue((double)lon);
			}
			r++;
		}
		ws.Range(2, 3, Math.Max(2, r - 1), 4).Style.NumberFormat.Format = "0.000000";
		ws.Row(1).Style.Font.Bold = true;
		ws.SheetView.FreezeRows(1);
		ws.Column(1).Width = 40;
		ws.Column(2).Width = 48;
		ws.Column(3).Width = 14;
		ws.Column(4).Width = 14;
		wb.SaveAs(xlsxPath);
		return properties.Count;
	}

	/// <summary>
	/// Reads an edited spreadsheet (.xlsx, or .csv) and compares it with the database. Nothing is saved.
	/// Throws <see cref="FormatException"/> when the ID, Latitude and Longitude columns can't be found.
	/// </summary>
	public static List<LatLongRow> Read(SdeStore store, string xlsxPath)
	{
		SourceTable table = SheetReader.Read(xlsxPath, null, firstRowIsHeader: true);
		int idCol = FindColumn(table, c => c.Contains("Property", StringComparison.OrdinalIgnoreCase));
		int latCol = FindColumn(table, c => c.Equals(LatitudeHeader, StringComparison.OrdinalIgnoreCase));
		int lonCol = FindColumn(table, c => c.Equals(LongitudeHeader, StringComparison.OrdinalIgnoreCase));
		int addressCol = FindColumn(table, c => c.StartsWith("Address", StringComparison.OrdinalIgnoreCase));
		if (idCol < 0 || latCol < 0 || lonCol < 0)
		{
			// The original's message (Import.xaml.cs:110).
			throw new FormatException("The spreadsheet you are using is not in the correct format. Please use the same Excel format in which the export was created "
				+ $"(columns \"{IdHeader}\", \"{LatitudeHeader}\" and \"{LongitudeHeader}\").");
		}

		ReferenceData reference = store.Reference;
		AssessmentValidator validator = new AssessmentValidator(reference);
		Dictionary<Guid, PropertyRecord> properties = store.GetProperties(activeOnly: true).ToDictionary(p => p.PropertyID);
		Dictionary<Guid, int> seen = new();
		List<LatLongRow> rows = new();

		foreach (SourceRow source in table.Rows)
		{
			string idText = source[idCol].Trim();
			Guid? id = Guid.TryParse(idText, out Guid g) ? g : null;
			properties.TryGetValue(id ?? Guid.Empty, out PropertyRecord p);

			decimal? lat = null, lon = null;
			List<string> problems = new();
			if (id == null)
			{
				problems.Add(idText.Length == 0 ? "No SDE Property ID" : $"'{idText}' is not an SDE Property ID");
			}
			else if (p == null)
			{
				problems.Add("No structure with this ID in the database");
			}
			else if (seen.TryGetValue(id.Value, out int firstRow))
			{
				problems.Add($"Same structure as row {firstRow}");
			}
			if (id is Guid key && !seen.ContainsKey(key))
			{
				seen[key] = source.RowNumber;
			}

			lat = Coordinate(source[latCol], "Latitude", 90m, problems);
			lon = Coordinate(source[lonCol], "Longitude", 180m, problems);
			bool latBlank = string.IsNullOrWhiteSpace(source[latCol]), lonBlank = string.IsNullOrWhiteSpace(source[lonCol]);
			if (latBlank != lonBlank)
			{
				problems.Add("Latitude and longitude must both be filled in");
			}
			else if (latBlank && p != null && (p.Latitude != null || p.Longitude != null))
			{
				problems.Add("Latitude and longitude are blank (coordinates can't be cleared this way)");
			}

			LatLongRow row = new LatLongRow
			{
				RowNumber = source.RowNumber,
				PropertyIdText = idText,
				PropertyID = p?.PropertyID,
				Address = p != null ? Address(p, reference) : source[addressCol].Trim(),
				CurrentLatitude = p?.Latitude,
				CurrentLongitude = p?.Longitude,
				NewLatitude = lat,
				NewLongitude = lon,
			};
			row.Problems.AddRange(problems);
			if (row.IsValid && p?.StateID is int stateId && lat is decimal la && lon is decimal lo
				&& (validator.LatitudeOutsideState((double)la, stateId) || validator.LongitudeOutsideState((double)lo, stateId)))
			{
				row.Warning = $"Outside {reference.StateName(stateId)}";
			}
			row.Selected = row.IsChanged;
			rows.Add(row);
		}
		return rows;
	}

	/// <summary>Saves the coordinates of the selected, valid rows in one transaction; returns how many structures were updated.</summary>
	public static int Apply(SdeStore store, IEnumerable<LatLongRow> rows)
	{
		List<PropertyRecord> changed = new();
		foreach (LatLongRow row in rows.Where(r => r.Selected && r.IsValid && r.PropertyID != null))
		{
			PropertyRecord p = store.GetProperty(row.PropertyID.Value);
			if (p == null)
			{
				continue;
			}
			p.Latitude = row.NewLatitude;
			p.Longitude = row.NewLongitude;
			changed.Add(p);
		}
		if (changed.Count > 0)
		{
			store.SaveProperties(changed);
		}
		return changed.Count;
	}

	/// <summary>StreetNumber StreetName Suffix (GetAllProperties joined them with spaces; empty parts are skipped here).</summary>
	internal static string Address(PropertyRecord p, ReferenceData reference) =>
		Text.Join(p.StreetNumber, p.StreetName, reference.LookupName(p.StreetSuffixID));

	private static int FindColumn(SourceTable table, Func<string, bool> match)
	{
		for (int i = 0; i < table.Columns.Count; i++)
		{
			if (match(table.Columns[i].Trim()))
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>A coordinate cell: blank → null; otherwise a number within ±<paramref name="limit"/>, rounded to 6 decimals (Numeric(18,6)).</summary>
	private static decimal? Coordinate(string text, string name, decimal limit, List<string> problems)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		if (!Text.TryParseDecimal(text, out decimal v))
		{
			problems.Add($"{name} '{text.Trim()}' is not a number");
			return null;
		}
		if (v < -limit || v > limit)
		{
			problems.Add($"{name} {v} is outside -{limit} to {limit}");
			return null;
		}
		return Math.Round(v, 6, MidpointRounding.AwayFromZero);
	}
}
