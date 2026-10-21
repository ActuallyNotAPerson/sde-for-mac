using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Sde.Data.Infrastructure;

/// <summary>
/// Loads mdb-export CSV into a SQLite table with the same name. Only columns the SQLite table has are copied.
/// Conversions: empty unquoted cell → NULL; mdbtools GUID text → lowercase "D" GUID; everything else as text
/// (SQLite's column affinity turns numbers into INTEGER/REAL).
/// </summary>
internal static class CsvTableLoader
{
	private static readonly Regex JetGuid = new(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{12}\}$", RegexOptions.Compiled);

	public static int Load(SqliteConnection c, IDbTransaction tx, string table, TextReader csv)
	{
		HashSet<string> tableColumns = c.Query<string>($"SELECT name FROM pragma_table_info(@t)", new { t = table }, tx)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (tableColumns.Count == 0)
		{
			return 0;
		}
		using IEnumerator<List<CsvCell>> rows = CsvReader.Read(csv).GetEnumerator();
		if (!rows.MoveNext())
		{
			return 0;
		}
		List<string> header = rows.Current.Select(h => h.Text).ToList();
		List<int> keep = Enumerable.Range(0, header.Count).Where(i => tableColumns.Contains(header[i])).ToList();
		string sql = $"INSERT INTO \"{table}\" ({string.Join(", ", keep.Select(i => $"\"{header[i]}\""))}) VALUES ({string.Join(", ", keep.Select(i => "$p" + i))})";
		using SqliteCommand cmd = c.CreateCommand();
		cmd.CommandText = sql;
		cmd.Transaction = (SqliteTransaction)tx;
		foreach (int i in keep)
		{
			cmd.Parameters.Add(new SqliteParameter("$p" + i, DBNull.Value));
		}
		int count = 0;
		while (rows.MoveNext())
		{
			List<CsvCell> row = rows.Current;
			foreach (int i in keep)
			{
				CsvCell cell = i < row.Count ? row[i] : new CsvCell("", false);
				cmd.Parameters["$p" + i].Value = Convert(cell);
			}
			cmd.ExecuteNonQuery();
			count++;
		}
		return count;
	}

	private static object Convert(CsvCell cell)
	{
		if (cell.IsNull)
		{
			return DBNull.Value;
		}
		if (JetGuid.IsMatch(cell.Text))
		{
			return SqliteTypeHandlers.ParseGuid(cell.Text).ToString("D");
		}
		return cell.Text;
	}
}
