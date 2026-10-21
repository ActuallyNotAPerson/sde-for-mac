using System.Data;
using System.Globalization;
using Dapper;

namespace Sde.Data.Infrastructure;

/// <summary>
/// How .NET values are stored in SQLite:
///   Guid     → TEXT, lowercase "D" format ("1af6e85e-1ebc-446a-88a5-20f4ce2850c1")
///   DateTime → TEXT "yyyy-MM-dd HH:mm:ss" (read back as DateTimeKind.Unspecified, like OleDb gave)
///   decimal  → REAL, read back from whatever storage class SQLite returns
/// Microsoft.Data.Sqlite would otherwise store Guids as BLOBs, and Dapper can't read TEXT back into Guid.
/// </summary>
internal static class SqliteTypeHandlers
{
	public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

	private static int _registered;

	public static void Register()
	{
		if (Interlocked.Exchange(ref _registered, 1) == 1)
		{
			return;
		}
		SqlMapper.RemoveTypeMap(typeof(Guid));
		SqlMapper.RemoveTypeMap(typeof(Guid?));
		SqlMapper.AddTypeHandler(new GuidHandler());
		SqlMapper.RemoveTypeMap(typeof(DateTime));
		SqlMapper.RemoveTypeMap(typeof(DateTime?));
		SqlMapper.AddTypeHandler(new DateTimeHandler());
		// SQLite may return a number column as Int64, Double or text; Dapper caches one conversion per query, so mixed rows would fail.
		SqlMapper.RemoveTypeMap(typeof(decimal));
		SqlMapper.RemoveTypeMap(typeof(decimal?));
		SqlMapper.AddTypeHandler(new DecimalHandler());
	}

	private sealed class DecimalHandler : SqlMapper.TypeHandler<decimal>
	{
		public override void SetValue(IDbDataParameter parameter, decimal value)
		{
			parameter.DbType = DbType.Decimal;
			parameter.Value = value;
		}

		public override decimal Parse(object value) => value switch
		{
			decimal d => d,
			double d => Convert.ToDecimal(d),
			long l => l,
			string s => decimal.Parse(s, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture),
			_ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
		};
	}

	private sealed class GuidHandler : SqlMapper.TypeHandler<Guid>
	{
		public override void SetValue(IDbDataParameter parameter, Guid value)
		{
			parameter.DbType = DbType.String;
			parameter.Value = value.ToString("D");
		}

		public override Guid Parse(object value) => value switch
		{
			Guid g => g,
			byte[] b => new Guid(b),
			string s => ParseGuid(s),
			_ => Guid.Empty,
		};
	}

	private sealed class DateTimeHandler : SqlMapper.TypeHandler<DateTime>
	{
		public override void SetValue(IDbDataParameter parameter, DateTime value)
		{
			parameter.DbType = DbType.String;
			parameter.Value = value.ToString(DateFormat, CultureInfo.InvariantCulture);
		}

		public override DateTime Parse(object value) => value switch
		{
			DateTime d => d,
			string s => ParseDate(s),
			_ => DateTime.MinValue,
		};
	}

	public static Guid ParseGuid(string s)
	{
		if (Guid.TryParse(s, out Guid g))
		{
			return g;
		}
		// mdbtools prints Jet GUIDs as {8-4-4-16} (missing the 4th hyphen); the bytes are in the right order.
		string hex = s.Trim('{', '}').Replace("-", "");
		return hex.Length == 32 ? Guid.ParseExact(hex, "N") : throw new FormatException("Not a GUID: " + s);
	}

	public static DateTime ParseDate(string s)
	{
		string[] formats = { DateFormat, "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd" };
		return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)
			? d
			: DateTime.Parse(s, CultureInfo.InvariantCulture);
	}
}
