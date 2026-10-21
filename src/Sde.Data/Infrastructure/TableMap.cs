using System.Data;
using System.Globalization;
using System.Reflection;
using Dapper;
using Sde.Core.Records;

namespace Sde.Data.Infrastructure;

/// <summary>
/// Column metadata for a record type (its [Table], [Key], [AutoIncrement] attributes), plus generic,
/// parameterized CRUD. Replaces FEMA's SQLFactory, which concatenated values into Jet SQL strings.
/// </summary>
internal static class TableMap<T>
	where T : class, new()
{
	public static readonly string Table = typeof(T).GetCustomAttribute<TableAttribute>()?.Name
		?? throw new InvalidOperationException(typeof(T).Name + " has no [Table]");

	public static readonly PropertyInfo[] Columns = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<NotMappedAttribute>() == null)
		.ToArray();

	public static readonly PropertyInfo[] Keys = Columns.Where(p => p.GetCustomAttribute<KeyAttribute>() != null).ToArray();

	public static readonly PropertyInfo AutoIncrement = Columns.FirstOrDefault(p => p.GetCustomAttribute<AutoIncrementAttribute>() != null);

	private static readonly string SelectSql = $"SELECT {string.Join(", ", Columns.Select(c => Q(c.Name)))} FROM {Q(Table)}";

	private static readonly string KeyWhere = string.Join(" AND ", Keys.Select(k => $"{Q(k.Name)} = @{k.Name}"));

	public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

	public static IEnumerable<T> Select(IDbConnection c, string where = null, object args = null, IDbTransaction tx = null, string orderBy = null)
	{
		string sql = SelectSql + (where == null ? "" : " WHERE " + where) + (orderBy == null ? "" : " ORDER BY " + orderBy);
		return c.Query<T>(sql, args, tx).Select(JetScaling.Apply);
	}

	public static T Get(IDbConnection c, object key, IDbTransaction tx = null) =>
		Select(c, KeyWhere, key, tx).FirstOrDefault();

	/// <summary>Inserts the record (rounding Currency/Numeric values to their Jet scale first). AutoNumber keys are filled in.</summary>
	public static void Insert(IDbConnection c, T record, IDbTransaction tx = null)
	{
		JetScaling.Apply(record);
		IEnumerable<PropertyInfo> cols = Columns;
		bool assignId = AutoIncrement != null && IsUnset(AutoIncrement.GetValue(record));
		if (assignId)
		{
			cols = cols.Where(p => p != AutoIncrement);
		}
		List<PropertyInfo> list = cols.ToList();
		string sql = $"INSERT INTO {Q(Table)} ({string.Join(", ", list.Select(p => Q(p.Name)))}) VALUES ({string.Join(", ", list.Select(p => "@" + p.Name))})";
		c.Execute(sql, Parameters(record, list), tx);
		if (assignId)
		{
			long id = c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
			AutoIncrement.SetValue(record, Convert.ChangeType(id, Nullable.GetUnderlyingType(AutoIncrement.PropertyType) ?? AutoIncrement.PropertyType, CultureInfo.InvariantCulture));
		}
	}

	/// <summary>Updates every column of the row with the record's key. Returns the number of rows changed.</summary>
	public static int Update(IDbConnection c, T record, IDbTransaction tx = null)
	{
		JetScaling.Apply(record);
		List<PropertyInfo> set = Columns.Where(p => !Keys.Contains(p)).ToList();
		string sql = $"UPDATE {Q(Table)} SET {string.Join(", ", set.Select(p => $"{Q(p.Name)} = @{p.Name}"))} WHERE {KeyWhere}";
		return c.Execute(sql, Parameters(record, Columns), tx);
	}

	/// <summary>Update if the key exists, otherwise insert. (Not INSERT OR REPLACE, which would fire cascading deletes.)</summary>
	public static void Upsert(IDbConnection c, T record, IDbTransaction tx = null)
	{
		bool hasKey = Keys.All(k => !IsUnset(k.GetValue(record)));
		if (!hasKey || Update(c, record, tx) == 0)
		{
			Insert(c, record, tx);
		}
	}

	public static int Delete(IDbConnection c, string where, object args, IDbTransaction tx = null) =>
		c.Execute($"DELETE FROM {Q(Table)} WHERE {where}", args, tx);

	private static DynamicParameters Parameters(T record, IEnumerable<PropertyInfo> props)
	{
		DynamicParameters p = new DynamicParameters();
		foreach (PropertyInfo prop in props)
		{
			object value = prop.GetValue(record);
			// [JetScale] string columns (ElementPercentage.ItemCost/DamageValues) hold numbers: store them as numbers.
			if (value is string s && prop.GetCustomAttribute<JetScaleAttribute>() != null)
			{
				value = JetScaling.TryParse(s, out decimal d) ? d : null;
			}
			p.Add(prop.Name, value);
		}
		return p;
	}

	private static bool IsUnset(object v) => v switch
	{
		null => true,
		int i => i == 0,
		long l => l == 0,
		Guid g => g == Guid.Empty,
		_ => false,
	};
}
