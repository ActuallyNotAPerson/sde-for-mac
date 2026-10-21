using ClosedXML.Excel;

namespace Sde.Interop.Internal;

/// <summary>Common layout for the workbooks SDE writes: a bold, frozen, wrapped header row and sensible widths.</summary>
internal static class SheetWriter
{
	/// <summary>Writes the header row and text rows (blank strings leave the cell empty).</summary>
	public static void WriteText(IXLWorksheet ws, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
	{
		for (int c = 0; c < headers.Count; c++)
		{
			ws.Cell(1, c + 1).SetValue(headers[c]);
		}
		int r = 2;
		foreach (IReadOnlyList<string> row in rows)
		{
			for (int c = 0; c < row.Count; c++)
			{
				if (!string.IsNullOrEmpty(row[c]))
				{
					// Text, as the original exported (DevExpress TextExportMode.Text).
					ws.Cell(r, c + 1).SetValue(row[c]);
				}
			}
			r++;
		}
	}

	/// <summary>Bold, wrapped, frozen header row; each column as wide as its longest value (header capped so it wraps).</summary>
	public static void Finish(IXLWorksheet ws, int columns, double minWidth = 8, double maxWidth = 60, int headerCap = 24)
	{
		IXLRow header = ws.Row(1);
		header.Style.Font.Bold = true;
		header.Style.Alignment.WrapText = true;
		header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
		ws.SheetView.FreezeRows(1);

		int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
		for (int c = 1; c <= columns; c++)
		{
			int headerLength = ws.Cell(1, c).GetString().Length;
			int longest = 0;
			for (int r = 2; r <= lastRow; r++)
			{
				longest = Math.Max(longest, ws.Cell(r, c).GetFormattedString().Length);
			}
			double width = Math.Max(longest, Math.Min(headerLength, headerCap)) + 2;
			ws.Column(c).Width = Math.Clamp(width, minWidth, maxWidth);
		}
	}
}
