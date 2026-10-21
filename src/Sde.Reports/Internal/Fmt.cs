using System.Globalization;

namespace Sde.Reports.Internal;

/// <summary>
/// Value formatting used by the RDLC layouts. Always en-US (SDE is a U.S. program), whatever the Mac's locale.
/// Null prints as an empty string, as an empty RDLC textbox did.
/// </summary>
internal static class Fmt
{
	public static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>Format "c2" / FormatCurrency(x, 2): $1,234.56.</summary>
	public static string Money(decimal? v) => v is decimal d ? d.ToString("C2", Us) : "";

	/// <summary>Currency from FEMA's string-typed money columns (ElementPercentage.ItemCost / DamageValues).</summary>
	public static string Money(string s) => ParseDecimal(s) is decimal d ? Money(d) : "";

	/// <summary>Format "#0.0 \%" applied to a value that is already a percentage (75.0 → "75.0%").</summary>
	public static string Percent1(decimal? v) => v is decimal d ? d.ToString("#0.0", Us) + "%" : "";

	/// <summary>Format "#0.00 \%" (the detail report's depreciation percentage).</summary>
	public static string Percent2(decimal? v) => v is decimal d ? d.ToString("#0.00", Us) + "%" : "";

	/// <summary>Format "#,##0.00" (BFE, flood depths).</summary>
	public static string Number2(decimal? v) => v is decimal d ? d.ToString("#,##0.00", Us) : "";

	/// <summary>A plain number without trailing zeros (quantities, square footage, coordinates, multipliers).</summary>
	public static string Plain(decimal? v) => v is decimal d ? d.ToString("#,##0.############", Us) : "";

	/// <summary>Like <see cref="Plain(decimal?)"/> but without grouping (latitude/longitude, multipliers).</summary>
	public static string Raw(decimal? v) => v is decimal d ? d.ToString("0.############", Us) : "";

	/// <summary>Format "MM/dd/yyyy".</summary>
	public static string Date(DateTime? v) => v is DateTime d ? d.ToString("MM/dd/yyyy", Us) : "";

	/// <summary>Format "D" (the footer's print date): Wednesday, October 7, 2026.</summary>
	public static string LongDate(DateTime d) => d.ToString("dddd, MMMM d, yyyy", Us);

	/// <summary>
	/// Port of the RDLC code.FormatZipCode: 5 characters print as they are, otherwise the value is formatted
	/// "#####-####". The original converted to Double first (a ZIP+4 already containing "-" broke it); here
	/// only a 9-digit value is reformatted and anything else prints unchanged.
	/// </summary>
	public static string Zip(string zip)
	{
		if (string.IsNullOrEmpty(zip))
		{
			return zip ?? "";
		}
		string z = zip.Trim();
		return z.Length == 9 && z.All(char.IsAsciiDigit) ? z[..5] + "-" + z[5..] : z;
	}

	/// <summary>"Last, First" as the RDLC owner textboxes concatenated it, without a dangling comma when one part is empty.</summary>
	public static string LastFirst(string last, string first)
	{
		last = last?.Trim();
		first = first?.Trim();
		if (string.IsNullOrEmpty(last))
		{
			return first ?? "";
		}
		return string.IsNullOrEmpty(first) ? last : last + ", " + first;
	}

	/// <summary>Joins the non-empty parts with a separator.</summary>
	public static string Join(string separator, params string[] parts) =>
		string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

	public static decimal? ParseDecimal(string s) =>
		decimal.TryParse(s?.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : null;
}
