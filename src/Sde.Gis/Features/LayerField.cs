using System.Collections;
using System.Globalization;

namespace Sde.Gis.Features;

/// <summary>The type of an attribute column. Values are held as string, long, double, bool or DateTime.</summary>
public enum FieldType
{
	/// <summary>Held as <see cref="string"/>.</summary>
	Text,

	/// <summary>Held as <see cref="long"/>.</summary>
	Integer,

	/// <summary>Held as <see cref="double"/>.</summary>
	Real,

	/// <summary>Held as <see cref="bool"/>.</summary>
	Boolean,

	/// <summary>A calendar date, held as <see cref="DateTime"/> with no time of day.</summary>
	Date,

	/// <summary>A date and time, held as <see cref="DateTime"/> (UTC when the source says so).</summary>
	DateTime,
}

/// <summary>One attribute column of a layer.</summary>
public sealed class LayerField
{
	public LayerField(string name, FieldType type, string alias = null)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("A field needs a name.", nameof(name));
		}
		Name = name;
		Type = type;
		Alias = alias;
	}

	/// <summary>The column name as stored in the file or table.</summary>
	public string Name { get; }

	public FieldType Type { get; }

	/// <summary>A friendlier display name when the source has one (ArcGIS field aliases), or null.</summary>
	public string Alias { get; set; }

	/// <summary>The alias when there is one, otherwise the name.</summary>
	public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;

	/// <summary>Converts a value to this field's type (see <see cref="FieldValues.Convert"/>).</summary>
	public object Convert(object value) => FieldValues.Convert(Type, value, Name);

	public override string ToString() => $"{Name} ({Type})";
}

/// <summary>The fields of a layer in order, found by name without regard to case.</summary>
public sealed class FieldList : IReadOnlyList<LayerField>
{
	private readonly List<LayerField> _fields = new List<LayerField>();
	private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	public int Count => _fields.Count;

	public LayerField this[int index] => _fields[index];

	/// <summary>The field with this name, or null.</summary>
	public LayerField this[string name] => name != null && _index.TryGetValue(name, out int i) ? _fields[i] : null;

	/// <summary>The position of the named field, or -1.</summary>
	public int IndexOf(string name) => name != null && _index.TryGetValue(name, out int i) ? i : -1;

	public bool Contains(string name) => IndexOf(name) >= 0;

	/// <summary>Adds a field. Throws when a field of the same name (in any case) is already there.</summary>
	public LayerField Add(LayerField field)
	{
		ArgumentNullException.ThrowIfNull(field);
		if (_index.ContainsKey(field.Name))
		{
			throw new ArgumentException($"There's already a field called {field.Name}.", nameof(field));
		}
		_index[field.Name] = _fields.Count;
		_fields.Add(field);
		return field;
	}

	public IEnumerator<LayerField> GetEnumerator() => _fields.GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Conversions between raw values (from files, JSON or a person typing) and a field's type.</summary>
public static class FieldValues
{
	private static readonly string[] DateFormats = { "yyyy-MM-dd", "yyyyMMdd", "yyyy/MM/dd", "M/d/yyyy", "MM/dd/yyyy" };

	/// <summary>
	/// The value as the field type's CLR type, or null for null, DBNull and blank text. Throws
	/// <see cref="FormatException"/> with a plain-English message when the value can't be read as that type.
	/// </summary>
	public static object Convert(FieldType type, object value, string fieldName = null)
	{
		if (TryConvert(type, value, out object result))
		{
			return result;
		}
		string label = string.IsNullOrEmpty(fieldName) ? "This field" : fieldName;
		string expected = type switch
		{
			FieldType.Integer => "a whole number",
			FieldType.Real => "a number",
			FieldType.Boolean => "yes or no",
			FieldType.Date => "a date",
			FieldType.DateTime => "a date and time",
			_ => "text",
		};
		throw new FormatException($"{label} needs {expected}, not \"{value}\".");
	}

	/// <summary>Like <see cref="Convert"/> but returns false instead of throwing.</summary>
	public static bool TryConvert(FieldType type, object value, out object result)
	{
		result = null;
		if (value == null || value is DBNull)
		{
			return true;
		}
		if (value is string text && string.IsNullOrWhiteSpace(text))
		{
			return true;
		}
		switch (type)
		{
			case FieldType.Text:
				result = value switch
				{
					string s => s,
					DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
					bool b => b ? "true" : "false",
					IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
					_ => value.ToString(),
				};
				return true;
			case FieldType.Integer:
				switch (value)
				{
					case long l:
						result = l;
						return true;
					case int i:
						result = (long)i;
						return true;
					case short sh:
						result = (long)sh;
						return true;
					case byte by:
						result = (long)by;
						return true;
					case uint ui:
						result = (long)ui;
						return true;
					case bool b:
						result = b ? 1L : 0L;
						return true;
					case double d when Math.Abs(d) < 9.2e18 && d == Math.Floor(d):
						result = (long)d;
						return true;
					case float f when f == Math.Floor(f):
						result = (long)f;
						return true;
					case decimal m when m == decimal.Truncate(m) && m <= long.MaxValue && m >= long.MinValue:
						result = (long)m;
						return true;
					case string s:
						s = s.Trim().Replace(",", "", StringComparison.Ordinal);
						if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
						{
							result = parsed;
							return true;
						}
						if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double whole) && whole == Math.Floor(whole) && Math.Abs(whole) < 9.2e18)
						{
							result = (long)whole;
							return true;
						}
						return false;
					default:
						return false;
				}
			case FieldType.Real:
				switch (value)
				{
					case double d:
						result = d;
						return true;
					case float f:
						result = (double)f;
						return true;
					case decimal m:
						result = (double)m;
						return true;
					case long l:
						result = (double)l;
						return true;
					case int i:
						result = (double)i;
						return true;
					case short sh:
						result = (double)sh;
						return true;
					case byte by:
						result = (double)by;
						return true;
					case string s when double.TryParse(s.Trim().Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed):
						result = parsed;
						return true;
					default:
						return false;
				}
			case FieldType.Boolean:
				switch (value)
				{
					case bool b:
						result = b;
						return true;
					case long l when l is 0 or 1:
						result = l == 1;
						return true;
					case int i when i is 0 or 1:
						result = i == 1;
						return true;
					case double d when d is 0 or 1:
						result = d == 1;
						return true;
					case string s:
						switch (s.Trim().ToLowerInvariant())
						{
							case "true":
							case "t":
							case "yes":
							case "y":
							case "1":
								result = true;
								return true;
							case "false":
							case "f":
							case "no":
							case "n":
							case "0":
								result = false;
								return true;
							default:
								return false;
						}
					default:
						return false;
				}
			case FieldType.Date:
			case FieldType.DateTime:
				switch (value)
				{
					case DateTime d:
						result = type == FieldType.Date ? d.Date : d;
						return true;
					case DateTimeOffset o:
						result = type == FieldType.Date ? o.UtcDateTime.Date : o.UtcDateTime;
						return true;
					case DateOnly day:
						result = day.ToDateTime(TimeOnly.MinValue);
						return true;
					case string s:
						s = s.Trim();
						if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact))
						{
							result = exact;
							return true;
						}
						if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
						{
							result = type == FieldType.Date ? parsed.Date : parsed;
							return true;
						}
						return false;
					default:
						return false;
				}
			default:
				return false;
		}
	}

	/// <summary>How a value is shown in a table or label: dates as yyyy-MM-dd, numbers in the invariant culture, null as "".</summary>
	public static string Format(object value) => value switch
	{
		null => "",
		DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
		bool b => b ? "Yes" : "No",
		double r => r.ToString("0.######", CultureInfo.InvariantCulture),
		IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
		_ => value.ToString(),
	};
}
