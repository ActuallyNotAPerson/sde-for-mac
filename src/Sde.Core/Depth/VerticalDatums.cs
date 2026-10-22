namespace Sde.Core.Depth;

/// <summary>The vertical datum an elevation is measured from. Elevations in different datums can't be compared without a conversion.</summary>
public enum VerticalDatum
{
	/// <summary>Not given.</summary>
	Unknown,

	/// <summary>North American Vertical Datum of 1988, the datum of current flood maps.</summary>
	Navd88,

	/// <summary>National Geodetic Vertical Datum of 1929, used by older flood maps and elevation certificates.</summary>
	Ngvd29,

	/// <summary>Anything else (a local datum, a tidal datum).</summary>
	Other,
}

/// <summary>Reads and names vertical datums however a file, a form or a service writes them.</summary>
public static class VerticalDatums
{
	/// <summary>
	/// The datum a text names: "NAVD88", "NAVD 1988" and "North American Vertical Datum of 1988" are NAVD88; "NGVD29",
	/// "NGVD 1929", "National Geodetic Vertical Datum of 1929" and "Sea Level Datum of 1929" are NGVD29; blank is
	/// Unknown; anything else is Other.
	/// </summary>
	public static VerticalDatum Parse(string text)
	{
		string t = new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
		if (t.Length == 0)
		{
			return VerticalDatum.Unknown;
		}
		if (t.Contains("NAVD") || t.Contains("NORTHAMERICANVERTICAL"))
		{
			return VerticalDatum.Navd88;
		}
		if (t.Contains("NGVD") || t.Contains("NATIONALGEODETICVERTICAL") || t.Contains("SEALEVELDATUM"))
		{
			return VerticalDatum.Ngvd29;
		}
		return VerticalDatum.Other;
	}

	/// <summary>The short name SDE stores and shows: "NAVD88", "NGVD29"; null for Unknown and Other (use the source's own text).</summary>
	public static string ShortName(VerticalDatum datum) => datum switch
	{
		VerticalDatum.Navd88 => "NAVD88",
		VerticalDatum.Ngvd29 => "NGVD29",
		_ => null,
	};

	/// <summary>
	/// True when elevations written with these two datum names can't be compared as they are: both are known and they
	/// differ (two "other" datums differ when their names do). An unknown datum never counts as a mismatch here; callers
	/// warn about it separately.
	/// </summary>
	public static bool Differ(string first, string second)
	{
		VerticalDatum a = Parse(first);
		VerticalDatum b = Parse(second);
		if (a == VerticalDatum.Unknown || b == VerticalDatum.Unknown)
		{
			return false;
		}
		if (a == VerticalDatum.Other && b == VerticalDatum.Other)
		{
			return !string.Equals(Squash(first), Squash(second), StringComparison.Ordinal);
		}
		return a != b;
	}

	/// <summary>A datum name for messages: the short name, or the source's own text for another datum.</summary>
	public static string Describe(string text)
	{
		VerticalDatum d = Parse(text);
		return ShortName(d) ?? (d == VerticalDatum.Unknown ? "no datum" : text.Trim());
	}

	private static string Squash(string text) => new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
