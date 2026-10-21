using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>A file after filtering and (for checklist exports) combining: one row per record (or record and inspection).</summary>
public sealed class SourceShaped
{
	public SourceTable Table { get; init; }

	/// <summary>Data rows in the file.</summary>
	public int SourceRowCount { get; init; }

	/// <summary>Rows left out by the filter.</summary>
	public int FilteredOut { get; init; }

	/// <summary>Problems with the layout settings (nothing can be imported until they are fixed).</summary>
	public List<string> Problems { get; } = new();

	/// <summary>Things worth knowing, such as a checklist item with two different values for one inspection.</summary>
	public List<string> Warnings { get; } = new();
}

/// <summary>Reading a file to import (.xlsx/.xlsm/.xls/.csv/.dbf) and shaping it into one row per record.</summary>
public sealed partial class SourceTable
{
	/// <summary>Prefix of the columns made from checklist items, e.g. "Checklist: Foundation % Damage".</summary>
	public const string ChecklistPrefix = "Checklist: ";

	/// <summary>The sheets of a workbook (for a .csv or .dbf, its file name).</summary>
	public static IReadOnlyList<string> SheetNames(string path) => SheetReader.SheetNames(path);

	/// <summary>Reads a sheet (null = the first) with its first row as the header.</summary>
	public static SourceTable Read(string path, string sheetName = null) => SheetReader.Read(path, sheetName, firstRowIsHeader: true);

	/// <summary>The distinct non-blank values of a column with their counts, most common first (for choosing a filter).</summary>
	public static IReadOnlyList<(string Value, int Count)> DistinctValues(SourceTable table, string column, int max = 100)
	{
		int i = table.IndexOf(column);
		if (i < 0)
		{
			return Array.Empty<(string, int)>();
		}
		return table.Rows.Select(r => Text.Clean(r[i])).Where(v => v != null)
			.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
			.Select(g => (g.First(), g.Count()))
			.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
			.Take(max).ToList();
	}

	/// <summary>
	/// Applies the profile's filter, then, for a checklist export, combines the rows of each record + inspection into
	/// one: the other columns come from the group's first non-blank values, and each checklist item becomes a
	/// "Checklist: &lt;item&gt;" column holding its last non-blank value.
	/// </summary>
	public static SourceShaped Shape(SourceTable raw, SourceProfile profile)
	{
		List<string> problems = new();
		List<SourceRow> rows = raw.Rows.ToList();
		int filteredOut = 0;
		if (!string.IsNullOrWhiteSpace(profile.FilterColumn))
		{
			int f = raw.IndexOf(profile.FilterColumn);
			if (f < 0)
			{
				problems.Add($"The filter column '{profile.FilterColumn}' is not in the sheet.");
			}
			else if (profile.FilterValues.Count > 0)
			{
				HashSet<string> keep = profile.FilterValues.Select(v => v.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
				rows = rows.Where(r => keep.Contains(r[f].Trim())).ToList();
				filteredOut = raw.Rows.Count - rows.Count;
			}
		}

		if (profile.Layout != SourceLayout.OneRowPerChecklistItem)
		{
			SourceShaped wide = new SourceShaped
			{
				Table = new SourceTable(raw.Path, raw.SheetName, raw.Columns, rows),
				SourceRowCount = raw.Rows.Count,
				FilteredOut = filteredOut,
			};
			wide.Problems.AddRange(problems);
			return wide;
		}

		int item = raw.IndexOf(profile.ItemColumn);
		int value = raw.IndexOf(profile.ValueColumn);
		int record = raw.IndexOf(profile.RecordIdColumn);
		int inspection = raw.IndexOf(profile.InspectionIdColumn);
		if (item < 0)
		{
			problems.Add(string.IsNullOrWhiteSpace(profile.ItemColumn) ? "Choose the column that names the checklist item." : $"The checklist item column '{profile.ItemColumn}' is not in the sheet.");
		}
		if (value < 0)
		{
			problems.Add(string.IsNullOrWhiteSpace(profile.ValueColumn) ? "Choose the column that holds the checklist item's value." : $"The checklist value column '{profile.ValueColumn}' is not in the sheet.");
		}
		if (record < 0)
		{
			problems.Add("Choose the Record ID column; checklist rows are combined by record (and inspection).");
		}
		if (item >= 0 && item == value)
		{
			problems.Add("The checklist item and value columns must be different.");
		}
		if (problems.Count > 0)
		{
			SourceShaped failed = new SourceShaped
			{
				Table = new SourceTable(raw.Path, raw.SheetName, raw.Columns, Array.Empty<SourceRow>()),
				SourceRowCount = raw.Rows.Count,
				FilteredOut = filteredOut,
			};
			failed.Problems.AddRange(problems);
			return failed;
		}

		// Base columns keep their names; each distinct item (in order of first appearance) adds a column.
		List<int> baseColumns = Enumerable.Range(0, raw.Columns.Count).Where(i => i != item && i != value).ToList();
		List<string> columns = baseColumns.Select(i => raw.Columns[i]).ToList();
		Dictionary<string, int> itemColumn = new(StringComparer.OrdinalIgnoreCase);
		foreach (SourceRow r in rows)
		{
			string name = Text.Clean(r[item]);
			if (name == null || itemColumn.ContainsKey(name))
			{
				continue;
			}
			string column = ChecklistPrefix + name;
			string unique = column;
			for (int n = 2; columns.Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
			{
				unique = $"{column} ({n})";
			}
			itemColumn[name] = columns.Count;
			columns.Add(unique);
		}

		// Group by record + inspection, in order of first appearance. Rows with neither ID stay on their own.
		List<List<SourceRow>> groups = new();
		Dictionary<string, List<SourceRow>> byKey = new(StringComparer.OrdinalIgnoreCase);
		foreach (SourceRow r in rows)
		{
			string id = Text.Clean(r[record]);
			string insp = inspection >= 0 ? Text.Clean(r[inspection]) : null;
			if (id == null && insp == null)
			{
				groups.Add(new List<SourceRow> { r });
				continue;
			}
			string key = (id ?? "") + "\u001f" + (insp ?? "");
			if (!byKey.TryGetValue(key, out List<SourceRow> g))
			{
				g = new List<SourceRow>();
				byKey[key] = g;
				groups.Add(g);
			}
			g.Add(r);
		}

		List<string> warnings = new();
		List<SourceRow> output = new();
		foreach (List<SourceRow> g in groups)
		{
			string[] values = new string[columns.Count];
			for (int c = 0; c < baseColumns.Count; c++)
			{
				values[c] = g.Select(r => r[baseColumns[c]]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
			}
			Dictionary<int, List<string>> seen = new();
			foreach (SourceRow r in g)
			{
				string name = Text.Clean(r[item]);
				string v = Text.Clean(r[value]);
				if (name == null || v == null)
				{
					continue;
				}
				int c = itemColumn[name];
				values[c] = v;
				if (!seen.TryGetValue(c, out List<string> list))
				{
					seen[c] = list = new List<string>();
				}
				if (!list.Contains(v, StringComparer.OrdinalIgnoreCase))
				{
					list.Add(v);
				}
			}
			foreach ((int c, List<string> list) in seen.Where(kv => kv.Value.Count > 1))
			{
				string who = Text.Clean(g[0][record]) + (inspection >= 0 && Text.Clean(g[0][inspection]) is string insp ? " / " + insp : "");
				warnings.Add($"{who}: “{columns[c][ChecklistPrefix.Length..]}” has {list.Count} different values ({string.Join(", ", list.Select(x => "'" + x + "'"))}); the last one, '{values[c]}', is used.");
			}
			output.Add(new SourceRow(g[0].RowNumber, values));
		}
		SourceShaped shaped = new SourceShaped
		{
			Table = new SourceTable(raw.Path, raw.SheetName, columns, output),
			SourceRowCount = raw.Rows.Count,
			FilteredOut = filteredOut,
		};
		shaped.Warnings.AddRange(warnings);
		return shaped;
	}
}
