using System.Globalization;

namespace Sde.Interop.Internal;

/// <summary>
/// Display formats and lenient parsing shared by the import/export features. FEMA's SDE ran on US-English
/// Windows; its grids formatted values with the en-US culture, so the same formats are used here explicitly
/// (independent of the Mac's region settings).
/// </summary>
internal static class Text
{
	public static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>
	/// en-US currency as Windows formats it: "$1,234.57" and "($1.00)" for negatives (ICU's en-US uses "-$1.00").
	/// </summary>
	private static readonly NumberFormatInfo UsCurrency = CreateUsCurrency();

	private static NumberFormatInfo CreateUsCurrency()
	{
		NumberFormatInfo f = (NumberFormatInfo)Us.NumberFormat.Clone();
		f.CurrencyNegativePattern = 0;
		return f;
	}

	/// <summary>DevExpress mask "c" (currency, 2 decimals).</summary>
	public static string Money(decimal value) => value.ToString("C2", UsCurrency);

	/// <summary>DevExpress mask "###,###,##0.00".</summary>
	public static string Number2(decimal value) => value.ToString("#,##0.00", Us);

	/// <summary>DevExpress mask "###,###,##0.0".</summary>
	public static string Number1(decimal value) => value.ToString("#,##0.0", Us);

	/// <summary>DevExpress grid default for a DateTime column: the en-US short date.</summary>
	public static string ShortDate(DateTime? value) => value?.ToString("M/d/yyyy", Us) ?? "";

	/// <summary>Plain invariant number text, as a grid shows an unmasked decimal (keeps the stored scale).</summary>
	public static string Plain(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

	/// <summary>Non-empty parts, trimmed, joined by single spaces (null-safe, unlike the original "+" concatenations).</summary>
	public static string Join(params string[] parts) =>
		string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

	public static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	/// <summary>
	/// Parses a number as a US-English user would type it: thousands separators, a leading "$", a trailing "%",
	/// parentheses for negatives and exponents are accepted.
	/// </summary>
	public static bool TryParseDecimal(string text, out decimal value)
	{
		value = 0m;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string s = text.Trim().Replace("%", "").Trim();
		const NumberStyles styles = NumberStyles.Number | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowParentheses | NumberStyles.AllowExponent;
		return decimal.TryParse(s, styles, Us, out value) || decimal.TryParse(s, styles, CultureInfo.InvariantCulture, out value);
	}

	public static decimal? ParseDecimal(string text) => TryParseDecimal(text, out decimal d) ? d : null;

	private static readonly System.Text.RegularExpressions.Regex MeasurePattern = new(
		@"^\s*(?<n>[-+]?(?:\d{1,3}(?:,\d{3})+|\d*)(?:\.\d+)?)\s*(?<u>[a-z'""”″′’.]+)?\s*$",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	private static readonly System.Text.RegularExpressions.Regex FeetInchesPattern = new(
		@"^\s*(?<ft>\d+(?:\.\d+)?)\s*(?:'|’|′|ft\.?|feet|foot)\s*(?<in>\d+(?:\.\d+)?)\s*(?:""|”|″|in\.?|inch|inches)?\s*$",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	/// <summary>
	/// A number with an optional unit, as inspectors write measurements: "3 ft", "2'", "3' 6\"", "36 in", "48 hrs",
	/// "2 days". <paramref name="unit"/> is "ft", "in", "hours" or "days", or null when there is none; feet-and-inches
	/// come back in feet. Unknown units fail.
	/// </summary>
	public static bool TryParseMeasure(string text, out decimal value, out string unit)
	{
		value = 0m;
		unit = null;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		System.Text.RegularExpressions.Match fi = FeetInchesPattern.Match(text);
		if (fi.Success)
		{
			value = decimal.Parse(fi.Groups["ft"].Value, CultureInfo.InvariantCulture) + decimal.Parse(fi.Groups["in"].Value, CultureInfo.InvariantCulture) / 12m;
			unit = "ft";
			return true;
		}
		System.Text.RegularExpressions.Match m = MeasurePattern.Match(text);
		if (!m.Success || m.Groups["n"].Value.Trim('+', '-').Length == 0 || !TryParseDecimal(m.Groups["n"].Value, out value))
		{
			return false;
		}
		if (!m.Groups["u"].Success)
		{
			return true;
		}
		unit = m.Groups["u"].Value.TrimEnd('.').ToLowerInvariant() switch
		{
			"'" or "’" or "′" or "ft" or "feet" or "foot" => "ft",
			"\"" or "”" or "″" or "in" or "inch" or "inches" => "in",
			"h" or "hr" or "hrs" or "hour" or "hours" => "hours",
			"d" or "day" or "days" => "days",
			_ => null,
		};
		return unit != null;
	}

	/// <summary>A whole number, also accepting "1962.0" (how a spreadsheet number can come through).</summary>
	public static bool TryParseInt(string text, out int value)
	{
		value = 0;
		if (!TryParseDecimal(text, out decimal d) || d != decimal.Truncate(d) || d < int.MinValue || d > int.MaxValue)
		{
			return false;
		}
		value = (int)d;
		return true;
	}

	/// <summary>
	/// A date in US or ISO form. The clock time is kept as written: an ISO value with an offset
	/// ("2008-09-14T00:00:00-05:00") is not shifted into the Mac's time zone.
	/// </summary>
	public static bool TryParseDate(string text, out DateTime value)
	{
		value = default;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string s = text.Trim();
		if (DateTimeOffset.TryParse(s, Us, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset dto)
			|| DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dto))
		{
			value = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
			return true;
		}
		return false;
	}

	public static DateTime? ParseDate(string text) => TryParseDate(text, out DateTime d) ? d : null;

	/// <summary>Local time without fractional seconds (Jet DateTime precision), as the data layer stores times.</summary>
	public static DateTime Now()
	{
		DateTime n = DateTime.Now;
		return new DateTime(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, DateTimeKind.Unspecified);
	}
}
