using Dapper;
using Sde.Data;
using Sde.Data.Infrastructure;

namespace Sde.Tests.Data;

/// <summary>Data migration: a new Mac database holds exactly FEMA's reference data.</summary>
public class DatabaseSeedTests
{
	/// <summary>Every seeded table has the same rows as FEMA's SDEDatabase.mdb (read live with mdbtools), value for value.</summary>
	[Fact]
	public void SeededTables_MatchFemaMdb_RowForRow()
	{
		Assert.True(MdbTools.IsAvailable, "mdbtools is needed for this test (brew install mdbtools)");
		using TempDatabase t = new TempDatabase();
		foreach (string table in SdeDatabase.SeedTables)
		{
			List<List<CsvCell>> mdb = CsvReader.Read(new StringReader(MdbTools.ExportCsv(TestPaths.FemaMdb, table))).ToList();
			List<string> header = mdb[0].Select(c => c.Text).ToList();
			List<IDictionary<string, object>> rows = t.Database.Read(c =>
				c.Query($"SELECT * FROM \"{table}\" ORDER BY rowid").Cast<IDictionary<string, object>>().ToList());
			Assert.True(mdb.Count - 1 == rows.Count, $"{table}: mdb has {mdb.Count - 1} rows, SQLite has {rows.Count}");
			for (int i = 0; i < rows.Count; i++)
			{
				for (int col = 0; col < header.Count; col++)
				{
					CsvCell expected = mdb[i + 1][col];
					object actual = rows[i][header[col]];
					AssertSameValue(table, header[col], i, expected, actual);
				}
			}
		}
	}

	private static void AssertSameValue(string table, string column, int row, CsvCell expected, object actual)
	{
		string where = $"{table}.{column} row {row}";
		if (expected.IsNull)
		{
			Assert.True(actual is null, $"{where}: expected NULL, got {actual}");
			return;
		}
		switch (actual)
		{
			case long l:
				Assert.Equal(decimal.Parse(expected.Text, System.Globalization.CultureInfo.InvariantCulture), l);
				break;
			case double d:
				Assert.Equal(double.Parse(expected.Text, System.Globalization.CultureInfo.InvariantCulture), d);
				break;
			case string s when expected.Text.StartsWith('{'):
				Assert.Equal(SqliteTypeHandlers.ParseGuid(expected.Text).ToString("D"), s);
				break;
			default:
				Assert.True(expected.Text == actual?.ToString(), $"{where}: expected '{expected.Text}', got '{actual}'");
				break;
		}
	}

	[Fact]
	public void ReferenceData_LoadsWithExpectedCounts()
	{
		using TempDatabase t = new TempDatabase();
		var r = t.Store.Reference;
		Assert.Equal(414, r.Lookups.Count);
		Assert.Equal(59, r.States.Count);
		Assert.Equal(3251, r.Counties.Count);
		Assert.Equal(210, r.Depreciation.Count);
		Assert.Equal(49, r.ValidationRules.Count);
		Assert.Equal(3, r.CustomFields.Count);
		Assert.Equal(new Guid("1AF6E85E-1EBC-446A-88A5-20F4CE2850C1"), r.CustomFields[0].CustomFieldID); // GUID hard-coded in FEMA's ModifyCustomField.xaml.cs:71
		Assert.Equal("Ohio", r.StateName(36));
	}

	[Fact]
	public void Depreciation_MatchesFemaTable()
	{
		using TempDatabase t = new TempDatabase();
		var r = t.Store.Reference;
		Assert.Equal(24.2m, r.DepreciationPercent(104, 0, 1, 386));      // residential, 4 - Average
		Assert.Equal(21.1m, r.DepreciationPercent(101, 106, 2, 386));    // 1-story office, 4 - Average (decomp/database/csv/DepreciationValue.csv)
		Assert.Equal(0m, r.DepreciationPercent(101, 106, 2, 9999));       // no row → 0, as ExecuteScalar(null) → 0
	}

	[Fact]
	public void CostAdjustments_FollowFemaRules()
	{
		using TempDatabase t = new TempDatabase();
		var r = t.Store.Reference;
		List<string> manufactured = r.CostAdjustmentsFor(1, 94).Select(l => l.LookupFieldName).ToList();
		Assert.Contains("Expando", manufactured);
		Assert.DoesNotContain("Garage", manufactured);
		List<string> nonRes = r.CostAdjustmentsFor(2, null).Select(l => l.LookupFieldName).ToList();
		// LookupValue2 = '2'; NULL zOrder sorts first in Jet, so Roofing (zOrder 1) comes last.
		Assert.Equal(new[] { "Built-In Equipment", "Built-In Security/Communications", "Conveyance System", "Wall Covering", "Windows/Skylights", "Roofing" }, nonRes);
	}
}
