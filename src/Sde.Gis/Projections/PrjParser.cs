using System.Globalization;
using System.Text;

namespace Sde.Gis.Projections;

/// <summary>
/// Reads the WKT1 in a .prj file, in either ESRI's form (PROJECTION["Lambert_Conformal_Conic"], "Foot_US") or the OGC
/// form GDAL and QGIS write (PROJECTION["Lambert_Conformal_Conic_2SP"], "US survey foot"), into a
/// <see cref="CoordinateSystem"/>. Anything it can't convert exactly is refused with a plain-English message.
/// </summary>
internal static class PrjParser
{
	public static CoordinateSystem Parse(string wkt)
	{
		if (string.IsNullOrWhiteSpace(wkt))
		{
			throw new UnsupportedCoordinateSystemException("an empty .prj file", "The coordinate system text is empty.");
		}
		WktNode root;
		try
		{
			root = WktNode.Parse(wkt);
		}
		catch (FormatException ex)
		{
			throw new UnsupportedCoordinateSystemException("unreadable", "The .prj text isn't valid WKT: " + ex.Message + " Text: " + Truncate(wkt));
		}

		string keyword = root.Keyword.ToUpperInvariant();
		if (keyword == "COMPD_CS")
		{
			root = root.Child("PROJCS") ?? root.Child("GEOGCS")
				?? throw new UnsupportedCoordinateSystemException(root.Text(0) ?? "a compound system", "COMPD_CS without PROJCS or GEOGCS.");
			keyword = root.Keyword.ToUpperInvariant();
		}
		switch (keyword)
		{
			case "GEOGCS":
				return ParseGeographic(root, wkt);
			case "PROJCS":
				return ParseProjected(root, wkt);
			case "GEOCCS":
				throw new UnsupportedCoordinateSystemException((root.Text(0) ?? "geocentric") + ", a geocentric system", "GEOCCS isn't a map coordinate system.");
			case "LOCAL_CS":
				throw new UnsupportedCoordinateSystemException((root.Text(0) ?? "a local grid") + ", a local grid with no tie to the earth", "LOCAL_CS can't be placed on a map.");
			default:
				throw new UnsupportedCoordinateSystemException(root.Text(0) ?? root.Keyword,
					$"Only WKT1 (PROJCS and GEOGCS) is read; this text starts with {root.Keyword}. Text: {Truncate(wkt)}");
		}
	}

	private static CoordinateSystem ParseGeographic(WktNode geogcs, string wkt)
	{
		(string datum, Ellipsoid ellipsoid) = ReadDatum(geogcs);
		return CoordinateSystem.Geographic(geogcs.Text(0) ?? datum, datum, ellipsoid, Authority(geogcs), wkt.Trim());
	}

	private static (string Datum, Ellipsoid Ellipsoid) ReadDatum(WktNode geogcs)
	{
		string gcsName = geogcs.Text(0) ?? "";
		WktNode datumNode = geogcs.Child("DATUM")
			?? throw new UnsupportedCoordinateSystemException(Readable(gcsName), "GEOGCS has no DATUM.");
		string datumName = datumNode.Text(0) ?? "";
		WktNode spheroid = datumNode.Child("SPHEROID") ?? datumNode.Child("ELLIPSOID");
		double a = spheroid?.Number(1) ?? double.NaN;
		double invf = spheroid?.Number(2) ?? double.NaN;
		if (double.IsNaN(a) || double.IsNaN(invf) || a <= 0)
		{
			throw new UnsupportedCoordinateSystemException(Readable(datumName), "The datum has no usable SPHEROID.");
		}
		Ellipsoid ellipsoid = new Ellipsoid(spheroid.Text(0) ?? "ellipsoid", a, invf);

		WktNode primem = geogcs.Child("PRIMEM");
		double primeMeridian = primem?.Number(1) ?? 0.0;
		if (Math.Abs(primeMeridian) > 1e-9)
		{
			throw new UnsupportedCoordinateSystemException(Readable(gcsName) + " with a prime meridian other than Greenwich", $"PRIMEM {primem?.Text(0)} {primeMeridian}.");
		}
		WktNode unit = geogcs.Child("UNIT");
		double angular = unit?.Number(1) ?? Math.PI / 180.0;
		if (Math.Abs(angular - Math.PI / 180.0) > 1e-12)
		{
			throw new UnsupportedCoordinateSystemException(Readable(gcsName) + " measured in " + (unit?.Text(0) ?? "an unknown unit"), $"Angular unit factor {angular} isn't degrees.");
		}

		string datum = ClassifyDatum(datumName, gcsName, ellipsoid);
		return (datum, ellipsoid);
	}

	/// <summary>The short datum name for NAD83 (any realization) or WGS 84; throws for NAD27 and anything else.</summary>
	internal static string ClassifyDatum(string datumName, string gcsName, Ellipsoid ellipsoid)
	{
		string key = Key(datumName + " " + gcsName);
		if (key.Contains("1927") || key.Contains("NAD27"))
		{
			throw new UnsupportedCoordinateSystemException("NAD27", $"Datum {datumName} ({gcsName}) is NAD 1927.");
		}
		bool nad83 = key.Contains("NAD83") || key.Contains("NAD1983") || key.Contains("NORTHAMERICAN1983") || key.Contains("NORTHAMERICANDATUM1983");
		if (nad83)
		{
			if (!ellipsoid.IsGrs80OrWgs84)
			{
				throw new UnsupportedCoordinateSystemException(Readable(datumName), $"NAD83 named on the {ellipsoid.Name} ellipsoid ({ellipsoid.SemiMajorAxis}, {ellipsoid.InverseFlattening}).");
			}
			if (key.Contains("HARN") || key.Contains("HIGHACCURACY"))
			{
				return "NAD83(HARN)";
			}
			if (key.Contains("2011"))
			{
				return "NAD83(2011)";
			}
			if (key.Contains("NSRS2007") || key.Contains("2007"))
			{
				return "NAD83(NSRS2007)";
			}
			if (key.Contains("CSRS"))
			{
				return "NAD83(CSRS)";
			}
			if (key.Contains("CORS96"))
			{
				return "NAD83(CORS96)";
			}
			return "NAD83";
		}
		if (key.Contains("WGS84") || key.Contains("WGS1984") || key.Contains("WORLDGEODETICSYSTEM1984"))
		{
			if (!ellipsoid.IsGrs80OrWgs84)
			{
				throw new UnsupportedCoordinateSystemException(Readable(datumName), $"WGS 84 named on the {ellipsoid.Name} ellipsoid.");
			}
			return "WGS 84";
		}
		throw new UnsupportedCoordinateSystemException(Readable(string.IsNullOrWhiteSpace(datumName) ? gcsName : datumName),
			$"Datum {datumName} ({gcsName}) is neither NAD83 nor WGS 84.");
	}

	private static CoordinateSystem ParseProjected(WktNode projcs, string wkt)
	{
		string name = projcs.Text(0) ?? "projected system";
		WktNode geogcs = projcs.Child("GEOGCS")
			?? throw new UnsupportedCoordinateSystemException(Readable(name), "PROJCS has no GEOGCS.");
		(string datum, Ellipsoid ellipsoid) = ReadDatum(geogcs);

		string methodName = projcs.Child("PROJECTION")?.Text(0)
			?? throw new UnsupportedCoordinateSystemException(Readable(name), "PROJCS has no PROJECTION.");
		Dictionary<string, double> p = new Dictionary<string, double>(StringComparer.Ordinal);
		foreach (WktNode parameter in projcs.Children("PARAMETER"))
		{
			string key = Key(parameter.Text(0) ?? "");
			double value = parameter.Number(1);
			if (key.Length > 0 && !double.IsNaN(value))
			{
				p[key] = value;
			}
		}
		WktNode unitNode = projcs.Child("UNIT");
		LinearUnit unit = unitNode == null ? LinearUnit.Metre : LinearUnit.FromFactor(unitNode.Text(0), unitNode.Number(1));
		double m = unit.MetresPerUnit;
		string extension = projcs.Child("EXTENSION")?.Text(1) ?? "";

		double Get(string what, params string[] keys)
		{
			foreach (string k in keys)
			{
				if (p.TryGetValue(k, out double v))
				{
					return v;
				}
			}
			throw new UnsupportedCoordinateSystemException(Readable(name), $"The {methodName} projection has no {what} parameter.");
		}
		double Optional(double fallback, params string[] keys)
		{
			foreach (string k in keys)
			{
				if (p.TryGetValue(k, out double v))
				{
					return v;
				}
			}
			return fallback;
		}

		double fe = Optional(0.0, "FALSEEASTING", "EASTINGATFALSEORIGIN") * m;
		double fn = Optional(0.0, "FALSENORTHING", "NORTHINGATFALSEORIGIN") * m;
		string[] lat0Keys = { "LATITUDEOFORIGIN", "LATITUDEOFNATURALORIGIN", "LATITUDEOFFALSEORIGIN", "LATITUDEOFCENTER", "LATITUDEOFCENTRE" };
		string[] lon0Keys = { "CENTRALMERIDIAN", "LONGITUDEOFNATURALORIGIN", "LONGITUDEOFFALSEORIGIN", "LONGITUDEOFORIGIN", "LONGITUDEOFCENTER", "LONGITUDEOFCENTRE" };
		string[] scaleKeys = { "SCALEFACTOR", "SCALEFACTORATNATURALORIGIN" };

		string method = Key(methodName);
		ProjectionDefinition definition;
		switch (method)
		{
			case "TRANSVERSEMERCATOR":
			case "GAUSSKRUGER":
				definition = new ProjectionDefinition
				{
					Method = ProjectionMethod.TransverseMercator,
					LatitudeOfOrigin = Optional(0.0, lat0Keys),
					CentralMeridian = Get("central meridian", lon0Keys),
					ScaleFactor = Optional(1.0, scaleKeys),
					FalseEasting = fe,
					FalseNorthing = fn,
				};
				break;
			case "LAMBERTCONFORMALCONIC":
			case "LAMBERTCONFORMALCONIC2SP":
			case "LAMBERTCONFORMALCONIC1SP":
			{
				bool hasSp1 = p.TryGetValue("STANDARDPARALLEL1", out double sp1) || p.TryGetValue("LATITUDEOF1STSTANDARDPARALLEL", out sp1);
				bool hasSp2 = p.TryGetValue("STANDARDPARALLEL2", out double sp2) || p.TryGetValue("LATITUDEOF2NDSTANDARDPARALLEL", out sp2);
				double k = Optional(1.0, scaleKeys);
				double lat0 = Optional(double.NaN, lat0Keys);
				bool oneParallel = method == "LAMBERTCONFORMALCONIC1SP" || !hasSp2 || (hasSp1 && Math.Abs(sp1 - sp2) < 1e-10 && Math.Abs(k - 1.0) > 1e-12);
				if (oneParallel)
				{
					double origin = !double.IsNaN(lat0) ? lat0 : hasSp1 ? sp1 : Get("latitude of origin", lat0Keys);
					definition = new ProjectionDefinition
					{
						Method = ProjectionMethod.LambertConformalConic1SP,
						LatitudeOfOrigin = origin,
						CentralMeridian = Get("central meridian", lon0Keys),
						ScaleFactor = k,
						FalseEasting = fe,
						FalseNorthing = fn,
					};
				}
				else
				{
					if (!hasSp1)
					{
						throw new UnsupportedCoordinateSystemException(Readable(name), "Lambert Conformal Conic without a first standard parallel.");
					}
					definition = new ProjectionDefinition
					{
						Method = ProjectionMethod.LambertConformalConic2SP,
						LatitudeOfOrigin = double.IsNaN(lat0) ? Get("latitude of origin", lat0Keys) : lat0,
						CentralMeridian = Get("central meridian", lon0Keys),
						StandardParallel1 = sp1,
						StandardParallel2 = sp2,
						ScaleFactor = k,
						FalseEasting = fe,
						FalseNorthing = fn,
					};
				}
				break;
			}
			case "ALBERS":
			case "ALBERSCONICEQUALAREA":
			case "ALBERSEQUALAREA":
				definition = new ProjectionDefinition
				{
					Method = ProjectionMethod.AlbersEqualArea,
					LatitudeOfOrigin = Optional(0.0, lat0Keys),
					CentralMeridian = Get("central meridian", lon0Keys),
					StandardParallel1 = Get("first standard parallel", "STANDARDPARALLEL1", "LATITUDEOF1STSTANDARDPARALLEL"),
					StandardParallel2 = Get("second standard parallel", "STANDARDPARALLEL2", "LATITUDEOF2NDSTANDARDPARALLEL"),
					FalseEasting = fe,
					FalseNorthing = fn,
				};
				break;
			case "MERCATORAUXILIARYSPHERE":
				if (Math.Abs(Optional(0.0, "AUXILIARYSPHERETYPE")) > 1e-9)
				{
					throw new UnsupportedCoordinateSystemException(Readable(name), "Mercator_Auxiliary_Sphere with Auxiliary_Sphere_Type other than 0.");
				}
				definition = WebMercatorDefinition(Optional(0.0, lon0Keys), fe, fn);
				break;
			case "POPULARVISUALISATIONPSEUDOMERCATOR":
			case "POPULARVISUALIZATIONPSEUDOMERCATOR":
			case "PSEUDOMERCATOR":
				definition = WebMercatorDefinition(Optional(0.0, lon0Keys), fe, fn);
				break;
			case "MERCATOR1SP":
			case "MERCATOR":
			{
				string hint = Key(name) + " " + extension.Replace(" ", "", StringComparison.Ordinal);
				bool web = hint.Contains("PSEUDOMERCATOR") || hint.Contains("WEBMERCATOR") || extension.Contains("+nadgrids=@null", StringComparison.Ordinal)
					|| extension.Replace("  ", " ", StringComparison.Ordinal).Contains("+a=6378137 +b=6378137", StringComparison.Ordinal);
				if (!web)
				{
					throw new UnsupportedCoordinateSystemException(Readable(name) + ", a Mercator projection", "Only Web (Pseudo) Mercator is supported, not ellipsoidal Mercator.");
				}
				definition = WebMercatorDefinition(Optional(0.0, lon0Keys), fe, fn);
				break;
			}
			default:
				throw new UnsupportedCoordinateSystemException(Readable(name) + ", which uses the " + Readable(methodName) + " projection",
					$"Projection method {methodName} isn't implemented.");
		}
		return CoordinateSystem.Projected(name, datum, ellipsoid, definition, unit, Authority(projcs), wkt.Trim());
	}

	private static ProjectionDefinition WebMercatorDefinition(double lon0, double fe, double fn) =>
		new ProjectionDefinition { Method = ProjectionMethod.WebMercator, CentralMeridian = lon0, FalseEasting = fe, FalseNorthing = fn };

	private static int? Authority(WktNode node)
	{
		WktNode authority = node.Child("AUTHORITY");
		if (authority != null && string.Equals(authority.Text(0), "EPSG", StringComparison.OrdinalIgnoreCase)
			&& int.TryParse(authority.Text(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
		{
			return code;
		}
		return null;
	}

	/// <summary>Upper-case letters and digits only, so "False_Easting" and "false easting" compare equal.</summary>
	internal static string Key(string s)
	{
		StringBuilder b = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (char.IsAsciiLetterOrDigit(c))
			{
				b.Append(char.ToUpperInvariant(c));
			}
		}
		return b.ToString();
	}

	/// <summary>A name for messages: "D_North_American_1927" becomes "North American 1927".</summary>
	internal static string Readable(string s)
	{
		if (string.IsNullOrWhiteSpace(s))
		{
			return "unknown";
		}
		s = s.Trim();
		if (s.StartsWith("D_", StringComparison.Ordinal) || s.StartsWith("GCS_", StringComparison.Ordinal))
		{
			s = s.Substring(s.IndexOf('_') + 1);
		}
		return s.Replace('_', ' ');
	}

	private static string Truncate(string s) => s.Length > 300 ? s.Substring(0, 300) + "..." : s;
}

/// <summary>One WKT element: a keyword and its bracketed items (quoted text, bare words, numbers and nested elements).</summary>
internal sealed class WktNode
{
	private WktNode(string keyword)
	{
		Keyword = keyword;
	}

	public string Keyword { get; }

	/// <summary>Items in order: <see cref="string"/> (quoted text), <see cref="WktWord"/> (bare word), <see cref="double"/>, or <see cref="WktNode"/>.</summary>
	public List<object> Items { get; } = new List<object>();

	/// <summary>The quoted text at an item position, or null.</summary>
	public string Text(int index) => index < Items.Count && Items[index] is string s ? s : null;

	/// <summary>The number at an item position, or NaN.</summary>
	public double Number(int index) => index < Items.Count && Items[index] is double d ? d : double.NaN;

	/// <summary>Every number among the items.</summary>
	public double[] Numbers() => Items.OfType<double>().ToArray();

	/// <summary>The first nested element with the keyword (any case), or null.</summary>
	public WktNode Child(string keyword) => Children(keyword).FirstOrDefault();

	public IEnumerable<WktNode> Children(string keyword) =>
		Items.OfType<WktNode>().Where(n => string.Equals(n.Keyword, keyword, StringComparison.OrdinalIgnoreCase));

	/// <summary>Parses WKT. Throws <see cref="FormatException"/> when the text isn't well formed.</summary>
	public static WktNode Parse(string text)
	{
		int pos = 0;
		WktNode node = ReadNode(text, ref pos);
		SkipSpace(text, ref pos);
		if (pos < text.Length)
		{
			throw new FormatException($"Unexpected text after position {pos}.");
		}
		return node;
	}

	private static WktNode ReadNode(string s, ref int pos)
	{
		SkipSpace(s, ref pos);
		string keyword = ReadWord(s, ref pos);
		if (keyword.Length == 0)
		{
			throw new FormatException($"Expected a keyword at position {pos}.");
		}
		WktNode node = new WktNode(keyword);
		SkipSpace(s, ref pos);
		if (pos >= s.Length || (s[pos] != '[' && s[pos] != '('))
		{
			throw new FormatException($"Expected [ after {keyword}.");
		}
		char close = s[pos] == '[' ? ']' : ')';
		pos++;
		while (true)
		{
			SkipSpace(s, ref pos);
			if (pos >= s.Length)
			{
				throw new FormatException($"{keyword} isn't closed.");
			}
			char c = s[pos];
			if (c == close)
			{
				pos++;
				return node;
			}
			if (c == '"')
			{
				node.Items.Add(ReadQuoted(s, ref pos));
			}
			else if (c == '-' || c == '+' || c == '.' || char.IsAsciiDigit(c))
			{
				node.Items.Add(ReadNumber(s, ref pos));
			}
			else if (char.IsAsciiLetter(c) || c == '_')
			{
				int start = pos;
				string word = ReadWord(s, ref pos);
				SkipSpace(s, ref pos);
				if (pos < s.Length && (s[pos] == '[' || s[pos] == '('))
				{
					pos = start;
					node.Items.Add(ReadNode(s, ref pos));
				}
				else
				{
					node.Items.Add(new WktWord(word));
				}
			}
			else
			{
				throw new FormatException($"Unexpected '{c}' at position {pos}.");
			}
			SkipSpace(s, ref pos);
			if (pos < s.Length && s[pos] == ',')
			{
				pos++;
			}
			else if (pos < s.Length && s[pos] != close)
			{
				throw new FormatException($"Expected , or {close} at position {pos}.");
			}
		}
	}

	private static void SkipSpace(string s, ref int pos)
	{
		while (pos < s.Length && char.IsWhiteSpace(s[pos]))
		{
			pos++;
		}
	}

	private static string ReadWord(string s, ref int pos)
	{
		int start = pos;
		while (pos < s.Length && (char.IsAsciiLetterOrDigit(s[pos]) || s[pos] == '_'))
		{
			pos++;
		}
		return s.Substring(start, pos - start);
	}

	private static string ReadQuoted(string s, ref int pos)
	{
		StringBuilder b = new StringBuilder();
		pos++;
		while (pos < s.Length)
		{
			char c = s[pos++];
			if (c == '"')
			{
				if (pos < s.Length && s[pos] == '"')
				{
					b.Append('"');
					pos++;
					continue;
				}
				return b.ToString();
			}
			b.Append(c);
		}
		throw new FormatException("A quoted name isn't closed.");
	}

	private static double ReadNumber(string s, ref int pos)
	{
		int start = pos;
		while (pos < s.Length && (char.IsAsciiDigit(s[pos]) || s[pos] is '-' or '+' or '.' or 'e' or 'E'))
		{
			pos++;
		}
		string text = s.Substring(start, pos - start);
		if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
		{
			throw new FormatException($"'{text}' isn't a number.");
		}
		return v;
	}
}

/// <summary>An unquoted word inside a WKT element, such as EAST in AXIS["Easting",EAST].</summary>
internal sealed record WktWord(string Value);
