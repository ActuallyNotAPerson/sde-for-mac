using Sde.Core;
using Sde.Core.Addresses;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>One source row turned into a structure, with anything that went wrong.</summary>
public sealed class EnterpriseRow
{
	/// <summary>The row's number in the sheet or file (the header is row 1).</summary>
	public int RowNumber { get; init; }

	/// <summary>The structure as it would be imported (no ID yet; SquareFootage = the mapped total square footage).</summary>
	public PropertyRecord Property { get; init; }

	/// <summary>Reasons the row can't be imported.</summary>
	public List<string> Problems { get; } = new();

	/// <summary>Values that were left blank because they couldn't be used (unknown state, not a number…); the row still imports.</summary>
	public List<string> Warnings { get; } = new();

	public bool IsValid => Problems.Count == 0;

	/// <summary>Whether <see cref="EnterpriseImport.Import"/> imports this row. Defaults to <see cref="IsValid"/>.</summary>
	public bool Selected { get; set; }

	/// <summary>Set by <see cref="EnterpriseImport.Import"/> to the new structure's ID.</summary>
	public Guid? ImportedPropertyID { get; set; }
}

/// <summary>The result of applying a mapping to a sheet: one row per source row, plus problems with the mapping itself.</summary>
public sealed class EnterprisePreview
{
	public SourceTable Table { get; init; }
	public EnterpriseMapping Mapping { get; init; }
	public List<EnterpriseRow> Rows { get; } = new();

	/// <summary>Problems with the mapping (nothing can be imported until they are fixed).</summary>
	public List<string> MappingProblems { get; } = new();

	public int ValidCount => Rows.Count(r => r.IsValid);
	public bool CanImport => MappingProblems.Count == 0 && Rows.Any(r => r.IsValid && r.Selected);
}

/// <summary>
/// Enterprise Import: create structures (Property records only) from a spreadsheet of parcels, e.g. a county
/// assessor's export, by mapping its columns to SDE fields.
///
/// Port of decomp/wpfSDE/wpfSDE.ImportExport/ucEnterpriseImport.xaml.cs (sheet + header choice, column mapping,
/// saved mappings) and decomp/BLL/BLL.Service/EnterpriseImportService.cs (ImportDataUsingSettings), with DAL
/// PropertyService.InsertManyStructures (one 'c' shape holding the total square footage, PropertyService.cs:184-213).
/// DevExpress's ExcelDataSource is replaced by ClosedXML; .csv files are also accepted.
///
/// Original bugs fixed:
/// <list type="bullet">
/// <item>Address splitting took single words: "123 Martin Luther King Blvd" became number 123, name "Martin",
/// suffix "Luther". Now the number is the first word, the suffix the last word (when it is a known suffix) and the
/// name everything between.</item>
/// <item>The suffix-only and "…(with suffix)" street-name modes looked up the whole cell instead of the split-off
/// suffix (EnterpriseImportService.cs:79-83, 96-98, 106-122), so the suffix was almost never found.</item>
/// <item>A row whose value had fewer words than its mode expected (or "Doe, John" without the comma) threw and was
/// silently dropped; now the row imports with what could be read, and a warning.</item>
/// <item>The apartment/unit column was only read when a post-direction column was mapped, and mapping a direction
/// without an apartment column made every row fail (EnterpriseImportService.cs:46-55).</item>
/// <item>"Make Selection..." as the street-number mode imported no street number; it now means the plain mode.</item>
/// <item>Leading/trailing spaces ("123 " when there is no apartment, " Main" when there is no direction) are trimmed.</item>
/// <item>State, county and lookups are matched ignoring case and spaces; "Union County" finds "Union".</item>
/// <item>A saved mapping naming a column the sheet doesn't have made every row fail; it is now reported up front.</item>
/// <item>Unusable values (unknown state, non-numeric elevation, out-of-range latitude…) were dropped silently; they
/// are now listed per row before importing, and only the selected rows are imported, in one transaction.</item>
/// <item>The original required Street Number, Street Name and Street Suffix columns; one address column is enough
/// now, since a single column can hold the whole address.</item>
/// </list>
/// </summary>
public static class EnterpriseImport
{
	/// <summary>The sheets of a workbook (for a .csv, its file name).</summary>
	public static IReadOnlyList<string> SheetNames(string path) => SheetReader.SheetNames(path);

	/// <summary>
	/// Reads a sheet as text. <paramref name="sheetName"/> null = the first sheet (the original defaulted to "Sheet1").
	/// Without a header row the columns are named Column1, Column2…
	/// </summary>
	public static SourceTable ReadTable(string path, string sheetName = null, bool firstRowIsHeader = true) =>
		SheetReader.Read(path, sheetName, firstRowIsHeader);

	/// <summary>Applies a mapping to every row. Nothing is saved.</summary>
	public static EnterprisePreview Preview(SourceTable table, EnterpriseMapping mapping, ReferenceData reference)
	{
		EnterprisePreview preview = new EnterprisePreview { Table = table, Mapping = mapping };
		Dictionary<EnterpriseField, int> index = new();
		foreach ((EnterpriseField field, string column) in mapping.Columns)
		{
			int i = table.IndexOf(column);
			if (i < 0)
			{
				preview.MappingProblems.Add($"Column '{column}' (mapped to {Label(field)}) is not in the sheet.");
			}
			index[field] = i;
		}
		if (!mapping.IsMapped(EnterpriseField.StreetNumber) && !mapping.IsMapped(EnterpriseField.StreetName))
		{
			preview.MappingProblems.Add("Map a column to Street Number or Street Name (or both).");
		}

		PropertyRowParser parser = new PropertyRowParser(mapping, reference);
		foreach (SourceRow source in table.Rows)
		{
			EnterpriseRow row = parser.Parse(source.RowNumber, f => index.TryGetValue(f, out int i) && i >= 0 ? Text.Clean(source[i]) : null);
			if (string.IsNullOrWhiteSpace(row.Property.StreetNumber) && string.IsNullOrWhiteSpace(row.Property.StreetName))
			{
				row.Problems.Add("No street number or street name");
			}
			row.Selected = row.IsValid;
			preview.Rows.Add(row);
		}
		return preview;
	}

	/// <summary>
	/// The same, plus a warning on each row that looks like a structure already in <paramref name="existing"/>: the same
	/// parcel number (ignoring spaces, dashes and dots) or the same address (see <see cref="AddressMatcher"/>). FEMA's
	/// Enterprise Import always creates new structures, so this only tells the person; the row is still importable.
	/// </summary>
	public static EnterprisePreview Preview(SourceTable table, EnterpriseMapping mapping, ReferenceData reference, IReadOnlyCollection<PropertyRecord> existing)
	{
		EnterprisePreview preview = Preview(table, mapping, reference);
		if (existing == null || existing.Count == 0)
		{
			return preview;
		}
		AddressMatcher addresses = new AddressMatcher(reference);
		AddressIndex<PropertyRecord> byAddress = addresses.Index(existing, addresses.KeyFor);
		ILookup<string, PropertyRecord> byParcel = existing.Where(p => ParcelNumbers.Normalize(p.ParcelNumber) != null)
			.ToLookup(p => ParcelNumbers.Normalize(p.ParcelNumber), StringComparer.Ordinal);
		foreach (EnterpriseRow row in preview.Rows.Where(r => r.IsValid))
		{
			string parcel = ParcelNumbers.Normalize(row.Property.ParcelNumber);
			if (parcel != null && byParcel[parcel].Any())
			{
				row.Warnings.Add($"A structure with parcel {row.Property.ParcelNumber.Trim()} is already in SDE");
			}
			else if (byAddress.Find(addresses.KeyFor(row.Property)).Count > 0)
			{
				row.Warnings.Add("A structure at this address is already in SDE");
			}
		}
		return preview;
	}

	/// <summary>
	/// Creates the selected valid rows as new structures (new ID, Active, ImportDate = LastUpdate = now) in one
	/// transaction, then gives each with a total square footage one 'c' shape. Returns the number created.
	/// </summary>
	public static int Import(SdeStore store, EnterprisePreview preview)
	{
		if (preview.MappingProblems.Count > 0)
		{
			throw new InvalidOperationException("The column mapping has problems: " + string.Join(" ", preview.MappingProblems));
		}
		DateTime now = Text.Now();
		List<(EnterpriseRow Row, PropertyRecord Property)> rows = preview.Rows
			.Where(r => r.IsValid && r.Selected)
			.Select(r => (r, Copy(r.Property, now)))
			.ToList();
		if (rows.Count == 0)
		{
			return 0;
		}
		store.SaveProperties(rows.Select(x => x.Property));
		foreach ((EnterpriseRow row, PropertyRecord p) in rows)
		{
			row.ImportedPropertyID = p.PropertyID;
			if (p.SquareFootage is decimal sqft)
			{
				store.SaveShapes(p.PropertyID, new[] { new ShapeRecord { ShapeType = "c", PropertyID = p.PropertyID, TotalSqFt = sqft } });
			}
		}
		return rows.Count;
	}

	public static string Label(EnterpriseField field) => EnterpriseMapping.Fields.First(f => f.Field == field).Label;

	private static PropertyRecord Copy(PropertyRecord source, DateTime now)
	{
		PropertyRecord p = new PropertyRecord();
		foreach (System.Reflection.PropertyInfo prop in typeof(PropertyRecord).GetProperties())
		{
			prop.SetValue(p, prop.GetValue(source));
		}
		p.PropertyID = Guid.Empty;
		p.Active = true;
		p.ImportDate = now;
		p.LastUpdate = now;
		return p;
	}
}
