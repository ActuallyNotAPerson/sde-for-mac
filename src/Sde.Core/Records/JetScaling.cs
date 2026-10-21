using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace Sde.Core.Records;

/// <summary>
/// Reproduces how Jet stored Currency/Numeric(p,s) values: rounded to the column's scale on write, handed back
/// by OleDb with exactly that scale (Currency 1234.5 → 1234.5000). Applied by the data layer after reads and
/// before writes, and by the SDE exchange writer, so numbers look exactly as they did in FEMA's SDE.
/// </summary>
public static class JetScaling
{
	private static readonly ConcurrentDictionary<Type, (PropertyInfo Prop, int Scale)[]> _cache = new();

	/// <summary>Rounds (half-to-even, like Jet's CCur) and sets the scale of every [JetScale] property on <paramref name="record"/>.</summary>
	public static T Apply<T>(T record)
		where T : class
	{
		if (record == null)
		{
			return null;
		}
		foreach (var (prop, scale) in ScaledProperties(record.GetType()))
		{
			object value = prop.GetValue(record);
			if (value is decimal d)
			{
				prop.SetValue(record, WithScale(d, scale));
			}
			else if (value is string s && prop.PropertyType == typeof(string))
			{
				if (TryParse(s, out decimal parsed))
				{
					prop.SetValue(record, WithScale(parsed, scale).ToString(CultureInfo.InvariantCulture));
				}
			}
		}
		return record;
	}

	public static void ApplyAll<T>(IEnumerable<T> records)
		where T : class
	{
		foreach (T r in records)
		{
			Apply(r);
		}
	}

	/// <summary>Rounds to <paramref name="scale"/> decimals and pads with trailing zeros to exactly that scale.</summary>
	public static decimal WithScale(decimal value, int scale)
	{
		decimal rounded = Math.Round(value, scale, MidpointRounding.ToEven);
		// Adding a zero that has the target scale forces the result's scale up to it (1234.5 + 0.0000 = 1234.5000).
		return rounded + new decimal(0, 0, 0, false, (byte)scale);
	}

	public static bool TryParse(string s, out decimal value) =>
		decimal.TryParse(s?.Replace("$", "").Trim(), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

	public static (PropertyInfo Prop, int Scale)[] ScaledProperties(Type type) =>
		_cache.GetOrAdd(type, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => (p, p.GetCustomAttribute<JetScaleAttribute>()))
			.Where(x => x.Item2 != null)
			.Select(x => (x.p, x.Item2.Scale))
			.ToArray());
}
