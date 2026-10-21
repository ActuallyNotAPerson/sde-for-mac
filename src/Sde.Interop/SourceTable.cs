namespace Sde.Interop;

/// <summary>
/// One sheet of a spreadsheet (.xlsx/.xlsm/.xls), a .csv file or a shapefile attribute table (.dbf), as text.
/// Replaces the DevExpress ExcelDataSource the original Enterprise Import bound to its grid (decomp/wpfSDE/wpfSDE.ImportExport/ucEnterpriseImport.xaml.cs:283-326)
/// and the ACE OLEDB reader of the Lat/Long import (wpfSDE.Tools.LatLong/Import.xaml.cs:84-108).
/// </summary>
public sealed partial class SourceTable
{
	public SourceTable(string path, string sheetName, IReadOnlyList<string> columns, IReadOnlyList<SourceRow> rows)
	{
		Path = path;
		SheetName = sheetName;
		Columns = columns;
		Rows = rows;
	}

	public string Path { get; }
	public string SheetName { get; }

	/// <summary>
	/// Column names: the header text when the first row is a header (blank headers become "Column{n}", repeated
	/// ones get " (2)", " (3)"…), otherwise "Column1", "Column2"…
	/// </summary>
	public IReadOnlyList<string> Columns { get; }

	/// <summary>Data rows (blank rows are left out).</summary>
	public IReadOnlyList<SourceRow> Rows { get; }

	/// <summary>Index of a column by exact name, then ignoring case and surrounding spaces; -1 when absent.</summary>
	public int IndexOf(string column)
	{
		if (string.IsNullOrWhiteSpace(column))
		{
			return -1;
		}
		for (int i = 0; i < Columns.Count; i++)
		{
			if (Columns[i] == column)
			{
				return i;
			}
		}
		for (int i = 0; i < Columns.Count; i++)
		{
			if (string.Equals(Columns[i].Trim(), column.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		return -1;
	}
}

/// <summary>One data row of a <see cref="SourceTable"/>.</summary>
public sealed class SourceRow
{
	public SourceRow(int rowNumber, IReadOnlyList<string> values)
	{
		RowNumber = rowNumber;
		Values = values;
	}

	/// <summary>The row's number in the sheet or file (1-based, counting the header row), for messages.</summary>
	public int RowNumber { get; }

	public IReadOnlyList<string> Values { get; }

	/// <summary>The value in a column ("" when the row is short or the index is -1).</summary>
	public string this[int column] => column >= 0 && column < Values.Count ? Values[column] ?? "" : "";
}
