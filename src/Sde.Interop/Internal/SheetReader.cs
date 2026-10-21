using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ExcelDataReader;

namespace Sde.Interop.Internal;

/// <summary>
/// Reads .xlsx/.xlsm sheets (ClosedXML), legacy .xls sheets (ExcelDataReader), .csv files and dBASE attribute tables
/// (.dbf, or the .shp next to one) into <see cref="SourceTable"/>s of text.
/// </summary>
internal static class SheetReader
{
	static SheetReader()
	{
		// Old .xls workbooks name their text encoding by Windows code page, which .NET only knows once this is registered.
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
	}

	public static bool IsCsv(string path) => string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);

	public static bool IsXls(string path) => string.Equals(Path.GetExtension(path), ".xls", StringComparison.OrdinalIgnoreCase);

	/// <summary>True for a dBASE table (.dbf) or a shapefile (.shp), whose attributes live in the .dbf beside it.</summary>
	public static bool IsDbf(string path)
	{
		string ext = Path.GetExtension(path);
		return string.Equals(ext, ".dbf", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".shp", StringComparison.OrdinalIgnoreCase);
	}

	public static IReadOnlyList<string> SheetNames(string path)
	{
		CheckSupported(path);
		if (IsCsv(path) || IsDbf(path))
		{
			return new[] { Path.GetFileNameWithoutExtension(path) };
		}
		if (IsXls(path))
		{
			return SheetNamesXls(path);
		}
		using FileStream fs = OpenShared(path);
		using XLWorkbook wb = new XLWorkbook(fs);
		return wb.Worksheets.Select(w => w.Name).ToList();
	}

	/// <param name="sheetName">The sheet to read (ignoring case); null reads the first sheet. Ignored for .csv and .dbf.</param>
	public static SourceTable Read(string path, string sheetName, bool firstRowIsHeader)
	{
		CheckSupported(path);
		List<(int RowNumber, List<string> Values)> raw = IsCsv(path) ? ReadCsv(path)
			: IsDbf(path) ? ReadDbf(path)
			: IsXls(path) ? ReadXls(path, ref sheetName)
			: ReadXlsx(path, ref sheetName);
		if (IsCsv(path) || IsDbf(path))
		{
			sheetName = Path.GetFileNameWithoutExtension(path);
		}

		int width = raw.Count == 0 ? 0 : raw.Max(r => r.Values.Count);
		List<string> columns = new();
		int start = 0;
		if (firstRowIsHeader && raw.Count > 0)
		{
			columns = HeaderNames(raw[0].Values, width);
			start = 1;
		}
		else
		{
			for (int i = 1; i <= width; i++)
			{
				columns.Add("Column" + i);
			}
		}

		List<SourceRow> rows = new();
		for (int i = start; i < raw.Count; i++)
		{
			List<string> values = raw[i].Values;
			if (values.All(string.IsNullOrWhiteSpace))
			{
				continue;
			}
			while (values.Count < width)
			{
				values.Add("");
			}
			rows.Add(new SourceRow(raw[i].RowNumber, values));
		}
		return new SourceTable(path, sheetName, columns, rows);
	}

	private static List<string> HeaderNames(List<string> header, int width)
	{
		List<string> names = new();
		for (int i = 0; i < width; i++)
		{
			string name = i < header.Count && !string.IsNullOrWhiteSpace(header[i]) ? header[i].Trim() : "Column" + (i + 1);
			string unique = name;
			for (int n = 2; names.Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
			{
				unique = $"{name} ({n})";
			}
			names.Add(unique);
		}
		return names;
	}

	private static void CheckSupported(string path)
	{
		string ext = Path.GetExtension(path).ToLowerInvariant();
		if (ext is not (".xlsx" or ".xlsm" or ".xls" or ".csv" or ".dbf" or ".shp"))
		{
			throw new NotSupportedException($"'{Path.GetFileName(path)}' is not an Excel workbook (.xlsx or .xls), a .csv file or a shapefile table (.dbf).");
		}
	}

	private static FileStream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

	// ───────────────────────── .xlsx ─────────────────────────

	private static List<(int, List<string>)> ReadXlsx(string path, ref string sheetName)
	{
		using FileStream fs = OpenShared(path);
		using XLWorkbook wb = new XLWorkbook(fs);
		IXLWorksheet ws;
		if (string.IsNullOrWhiteSpace(sheetName))
		{
			ws = wb.Worksheets.First();
		}
		else
		{
			string wanted = sheetName.Trim();
			ws = wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, wanted, StringComparison.OrdinalIgnoreCase))
				?? throw new ArgumentException($"There is no sheet named '{wanted}'. The sheets are: {string.Join(", ", wb.Worksheets.Select(w => "'" + w.Name + "'"))}.");
		}
		sheetName = ws.Name;

		List<(int, List<string>)> rows = new();
		IXLRange used = ws.RangeUsed();
		if (used == null)
		{
			return rows;
		}
		int firstRow = used.FirstRow().RowNumber(), lastRow = used.LastRow().RowNumber();
		int firstCol = used.FirstColumn().ColumnNumber(), lastCol = used.LastColumn().ColumnNumber();
		for (int r = firstRow; r <= lastRow; r++)
		{
			List<string> values = new();
			for (int c = firstCol; c <= lastCol; c++)
			{
				values.Add(CellText(ws.Cell(r, c)));
			}
			rows.Add((r, values));
		}
		return rows;
	}

	private static readonly Regex ZeroPadded = new(@"^0+(-0+)?$", RegexOptions.Compiled);

	/// <summary>
	/// A cell as text, as the original's data sources turned values into strings: numbers in invariant form
	/// ("39.1031", "45202"), dates as ISO dates. Zero-padded number formats (ZIP codes like "00501") keep their padding.
	/// </summary>
	internal static string CellText(IXLCell cell)
	{
		XLCellValue v = cell.HasFormula ? cell.CachedValue : cell.Value;
		switch (v.Type)
		{
			case XLDataType.Text:
				return v.GetText();
			case XLDataType.Number:
				string format = cell.Style.NumberFormat.Format;
				if (!string.IsNullOrEmpty(format) && ZeroPadded.IsMatch(format))
				{
					return cell.GetFormattedString();
				}
				return v.GetNumber().ToString("R", CultureInfo.InvariantCulture);
			case XLDataType.DateTime:
				DateTime d = v.GetDateTime();
				return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
			case XLDataType.Boolean:
				return v.GetBoolean() ? "TRUE" : "FALSE";
			case XLDataType.TimeSpan:
				return v.GetTimeSpan().ToString(null, CultureInfo.InvariantCulture);
			default:
				return "";
		}
	}

	// ───────────────────────── .xls ─────────────────────────

	private static IReadOnlyList<string> SheetNamesXls(string path)
	{
		using FileStream fs = OpenShared(path);
		using IExcelDataReader reader = ExcelReaderFactory.CreateBinaryReader(fs);
		List<string> names = new();
		do
		{
			names.Add(reader.Name);
		}
		while (reader.NextResult());
		return names;
	}

	/// <summary>
	/// Reads one sheet of a legacy workbook the way <see cref="ReadXlsx"/> does: rows keep their sheet row numbers, and
	/// the blank rows and columns before the first filled cell are dropped, so the first row left is the header.
	/// </summary>
	private static List<(int, List<string>)> ReadXls(string path, ref string sheetName)
	{
		using FileStream fs = OpenShared(path);
		using IExcelDataReader reader = ExcelReaderFactory.CreateBinaryReader(fs);
		List<string> names = new();
		bool found = false;
		string wanted = string.IsNullOrWhiteSpace(sheetName) ? null : sheetName.Trim();
		List<(int, List<string>)> rows = new();
		do
		{
			names.Add(reader.Name);
			if (found || (wanted != null && !string.Equals(reader.Name, wanted, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			found = true;
			sheetName = reader.Name;
			int rowNumber = 0;
			while (reader.Read())
			{
				rowNumber++;
				List<string> values = new(reader.FieldCount);
				for (int c = 0; c < reader.FieldCount; c++)
				{
					values.Add(XlsCellText(reader, c));
				}
				rows.Add((rowNumber, values));
			}
		}
		while (reader.NextResult());
		if (!found)
		{
			throw new ArgumentException($"There is no sheet named '{wanted}'. The sheets are: {string.Join(", ", names.Select(n => "'" + n + "'"))}.");
		}

		// Drop the blank rows above the header and the blank columns on either side.
		while (rows.Count > 0 && rows[0].Item2.All(string.IsNullOrEmpty))
		{
			rows.RemoveAt(0);
		}
		int firstCol = int.MaxValue, lastCol = -1;
		foreach ((int _, List<string> values) in rows)
		{
			int first = values.FindIndex(v => !string.IsNullOrEmpty(v));
			if (first >= 0)
			{
				firstCol = Math.Min(firstCol, first);
				lastCol = Math.Max(lastCol, values.FindLastIndex(v => !string.IsNullOrEmpty(v)));
			}
		}
		if (lastCol < 0)
		{
			return new List<(int, List<string>)>();
		}
		List<(int, List<string>)> trimmed = new();
		foreach ((int number, List<string> values) in rows)
		{
			trimmed.Add((number, values.Skip(firstCol).Take(lastCol - firstCol + 1).ToList()));
		}
		return trimmed;
	}

	/// <summary>A legacy cell as text, formatted exactly as <see cref="CellText"/> formats an .xlsx cell.</summary>
	private static string XlsCellText(IExcelDataReader reader, int column)
	{
		object v = reader.GetValue(column);
		switch (v)
		{
			case string s:
				return s;
			case double n:
				string format = reader.GetNumberFormatString(column);
				if (!string.IsNullOrEmpty(format) && ZeroPadded.IsMatch(format))
				{
					return PadToFormat(n, format);
				}
				return n.ToString("R", CultureInfo.InvariantCulture);
			case int i:
				return i.ToString(CultureInfo.InvariantCulture);
			case DateTime d:
				if (d.Year < 1900)
				{
					// A time of day on its own (the format has no date part): the reader dates it 1899-12-30.
					return d.TimeOfDay.ToString(null, CultureInfo.InvariantCulture);
				}
				return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
			case bool b:
				return b ? "TRUE" : "FALSE";
			default:
				// Empty cells are null; formula errors (#N/A and the like) come back as error objects and read as empty.
				return "";
		}
	}

	/// <summary>A whole number shown with a zero-padded format such as "00000" or "00000-0000" (ZIP and ZIP+4 codes).</summary>
	private static string PadToFormat(double value, string format)
	{
		string[] parts = format.Split('-');
		string digits = Math.Abs(Math.Round(value)).ToString("F0", CultureInfo.InvariantCulture).PadLeft(parts.Sum(p => p.Length), '0');
		StringBuilder sb = new();
		if (value < 0)
		{
			sb.Append('-');
		}
		int at = 0;
		for (int p = 0; p < parts.Length; p++)
		{
			if (p > 0)
			{
				sb.Append('-');
			}
			sb.Append(digits, at, Math.Min(parts[p].Length, digits.Length - at));
			at += parts[p].Length;
		}
		return sb.ToString();
	}

	// ───────────────────────── .dbf / .shp ─────────────────────────

	/// <summary>The attribute table as rows of text: the field names first (as the header row), then one row per record.</summary>
	private static List<(int, List<string>)> ReadDbf(string path)
	{
		DbfTable table = DbfReader.Read(path);
		List<(int, List<string>)> rows = new() { (1, table.Fields.ToList()) };
		foreach ((int recordNumber, List<string> values) in table.Records)
		{
			rows.Add((recordNumber + 1, values));
		}
		return rows;
	}

	// ───────────────────────── .csv ─────────────────────────

	private static List<(int, List<string>)> ReadCsv(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		string text;
		try
		{
			text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			// Not UTF-8: Excel on Windows saves CSV in the ANSI code page; Latin-1 is the closest built-in match.
			text = Encoding.Latin1.GetString(bytes);
		}
		if (text.Length > 0 && text[0] == '﻿')
		{
			text = text[1..];
		}
		return ParseCsv(text);
	}

	/// <summary>RFC 4180: comma-separated, double-quoted fields may contain commas, quotes ("") and line breaks.</summary>
	internal static List<(int, List<string>)> ParseCsv(string text)
	{
		List<(int, List<string>)> rows = new();
		List<string> fields = new();
		StringBuilder field = new();
		bool quoted = false, any = false;
		int line = 1, rowStart = 1;
		for (int i = 0; i < text.Length; i++)
		{
			char ch = text[i];
			if (quoted)
			{
				if (ch == '"')
				{
					if (i + 1 < text.Length && text[i + 1] == '"')
					{
						field.Append('"');
						i++;
					}
					else
					{
						quoted = false;
					}
				}
				else
				{
					if (ch == '\n')
					{
						line++;
					}
					field.Append(ch);
				}
				continue;
			}
			switch (ch)
			{
				case '"':
					quoted = true;
					any = true;
					break;
				case ',':
					fields.Add(field.ToString());
					field.Clear();
					any = true;
					break;
				case '\r':
					break;
				case '\n':
					fields.Add(field.ToString());
					field.Clear();
					rows.Add((rowStart, fields));
					fields = new();
					any = false;
					line++;
					rowStart = line;
					break;
				default:
					field.Append(ch);
					any = true;
					break;
			}
		}
		if (any || field.Length > 0)
		{
			fields.Add(field.ToString());
			rows.Add((rowStart, fields));
		}
		return rows;
	}
}
