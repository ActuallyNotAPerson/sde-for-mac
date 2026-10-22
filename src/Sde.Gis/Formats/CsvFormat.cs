using System.Globalization;
using System.Text;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Sde.Gis.Features;
using Sde.Gis.Projections;

namespace Sde.Gis.Formats;

/// <summary>Which columns of a CSV hold the location, and in what coordinate system.</summary>
public sealed class CsvOptions
{
	/// <summary>The latitude (or northing) column; found by name when null.</summary>
	public string LatitudeColumn { get; init; }

	/// <summary>The longitude (or easting) column; found by name when null.</summary>
	public string LongitudeColumn { get; init; }

	/// <summary>A column of WKT geometries, used instead of latitude and longitude; found by name when null.</summary>
	public string WktColumn { get; init; }

	/// <summary>The system the coordinates are in; WGS 84 longitude and latitude when null. Use a State Plane system for X and Y columns in feet.</summary>
	public CoordinateSystem CoordinateSystem { get; init; }

	/// <summary>The separator; worked out from the header line (comma, semicolon, tab or pipe) when null.</summary>
	public char? Delimiter { get; init; }
}

/// <summary>The location columns found in a CSV header.</summary>
public sealed record CsvLocationColumns(string Latitude, string Longitude, string Wkt);

/// <summary>
/// Reads a CSV of points (latitude and longitude columns, found by their usual names) or of any geometry (a WKT
/// column). Column types are worked out from the values; codes with leading zeros, such as parcel numbers, stay text.
/// Rows without a usable location are left out and counted in <see cref="FeatureLayer.Warnings"/>.
/// </summary>
public static class CsvFormat
{
	private static readonly string[] LatitudeNames = { "latitude", "lat", "y", "pointy", "latdd", "latitudedd", "ycoord", "ycoordinate", "latwgs84", "latitudewgs84", "gpslat", "gpslatitude", "northing" };
	private static readonly string[] LongitudeNames = { "longitude", "lon", "long", "lng", "x", "pointx", "londd", "longdd", "longitudedd", "xcoord", "xcoordinate", "lonwgs84", "longitudewgs84", "gpslon", "gpslong", "gpslongitude", "easting" };
	private static readonly string[] WktNames = { "wkt", "geometry", "geom", "thegeom", "shape", "wktgeometry", "geometrywkt" };

	/// <summary>Reads a CSV file. Throws <see cref="GisException"/> when no location columns can be found.</summary>
	public static FeatureLayer Read(string path, CsvOptions options = null)
	{
		if (!File.Exists(path))
		{
			throw new GisException("The CSV file couldn't be found.", path);
		}
		return ReadString(DecodeText(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path), options);
	}

	/// <summary>Reads CSV text.</summary>
	public static FeatureLayer ReadString(string text, string name = "layer", CsvOptions options = null)
	{
		options ??= new CsvOptions();
		char delimiter = options.Delimiter ?? DetectDelimiter(text);
		List<string[]> rows = Parse(text, delimiter);
		if (rows.Count == 0)
		{
			throw new GisException("This CSV file is empty.", name);
		}
		string[] header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToArray();
		CsvLocationColumns found = DetectColumns(header);
		string latName = options.LatitudeColumn ?? found.Latitude;
		string lonName = options.LongitudeColumn ?? found.Longitude;
		string wktName = options.WktColumn ?? (options.LatitudeColumn == null && options.LongitudeColumn == null ? found.Wkt : null);
		int lat = IndexOf(header, latName);
		int lon = IndexOf(header, lonName);
		int wkt = IndexOf(header, wktName);
		if (wkt < 0 && (lat < 0 || lon < 0))
		{
			throw new GisException("SDE couldn't find the location columns in this CSV. Name them Latitude and Longitude (or give a WKT column), or choose them.",
				$"Columns: {string.Join(", ", header)}");
		}
		bool useWkt = wkt >= 0;
		CoordinateSystem cs = options.CoordinateSystem ?? CoordinateSystem.Wgs84;

		FeatureLayer layer = new FeatureLayer(name);
		layer.SourceCoordinateSystem = cs.IsGeographic ? null : cs;
		List<int> attributeColumns = new List<int>();
		for (int i = 0; i < header.Length; i++)
		{
			if (i == wkt || header[i].Length == 0 || layer.Fields.Contains(header[i]))
			{
				continue;
			}
			attributeColumns.Add(i);
			layer.AddField(header[i], InferType(rows.Skip(1).Select(r => i < r.Length ? r[i] : "")));
		}

		WKTReader wktReader = new WKTReader(new NetTopologySuite.NtsGeometryServices(GisSetup.Factory.CoordinateSequenceFactory, GisSetup.Factory.PrecisionModel, 4326));
		int skipped = 0;
		long rowNumber = 0;
		foreach (string[] row in rows.Skip(1))
		{
			rowNumber++;
			if (row.All(string.IsNullOrWhiteSpace))
			{
				continue;
			}
			Geometry g = useWkt ? ReadWkt(wktReader, Cell(row, wkt), cs) : ReadPoint(Cell(row, lat), Cell(row, lon), cs);
			if (g == null)
			{
				skipped++;
				continue;
			}
			Feature feature = layer.NewFeature(g, rowNumber);
			for (int k = 0; k < attributeColumns.Count; k++)
			{
				FieldValues.TryConvert(layer.Fields[k].Type, Cell(row, attributeColumns[k]), out object value);
				feature.SetRaw(k, value);
			}
			layer.Features.Add(feature);
		}
		if (skipped > 0)
		{
			layer.Warnings.Add(skipped == 1
				? "1 row was left out because it had no usable location."
				: $"{skipped} rows were left out because they had no usable location.");
		}
		layer.DetectGeometryKind();
		return layer;
	}

	/// <summary>The latitude, longitude and WKT columns a header names (any may be null).</summary>
	public static CsvLocationColumns DetectColumns(IReadOnlyList<string> header)
	{
		string Find(string[] names)
		{
			foreach (string n in names)
			{
				string match = header.FirstOrDefault(h => Key(h) == n);
				if (match != null)
				{
					return match;
				}
			}
			return null;
		}
		return new CsvLocationColumns(Find(LatitudeNames), Find(LongitudeNames), Find(WktNames));
	}

	private static Geometry ReadPoint(string latText, string lonText, CoordinateSystem cs)
	{
		if (!TryNumber(latText, out double y) || !TryNumber(lonText, out double x))
		{
			return null;
		}
		if (cs.IsGeographic)
		{
			if (Math.Abs(y) > 90 || Math.Abs(x) > 180 || (x == 0 && y == 0))
			{
				return null;
			}
			return GisSetup.Factory.CreatePoint(new Coordinate(x, y));
		}
		(double lonW, double latW) = cs.ToWgs84(x, y);
		return GisSetup.Factory.CreatePoint(new Coordinate(lonW, latW));
	}

	private static Geometry ReadWkt(WKTReader reader, string text, CoordinateSystem cs)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		try
		{
			Geometry g = reader.Read(text.Trim());
			return g == null || g.IsEmpty ? null : Reproject.ToWgs84(g, cs);
		}
		catch (Exception ex) when (ex is ParseException or ArgumentException or FormatException or InvalidOperationException)
		{
			return null;
		}
	}

	private static bool TryNumber(string text, out double value) =>
		double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

	/// <summary>Integer when every value is a whole number without leading zeros, Real when every value is a number, otherwise Text.</summary>
	internal static FieldType InferType(IEnumerable<string> values)
	{
		bool allInteger = true;
		bool any = false;
		foreach (string raw in values)
		{
			string v = (raw ?? "").Trim();
			if (v.Length == 0)
			{
				continue;
			}
			any = true;
			bool leadingZero = v.Length > 1 && v[0] == '0' && char.IsAsciiDigit(v[1]);
			bool tooLong = v.Count(char.IsAsciiDigit) > 15;
			if (leadingZero || tooLong || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
			{
				return FieldType.Text;
			}
			if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
			{
				allInteger = false;
			}
		}
		if (!any)
		{
			return FieldType.Text;
		}
		return allInteger ? FieldType.Integer : FieldType.Real;
	}

	/// <summary>UTF-8 (with or without a byte order mark), falling back to Windows-1252 when the bytes aren't valid UTF-8.</summary>
	internal static string DecodeText(byte[] bytes)
	{
		try
		{
			return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
		}
		catch (DecoderFallbackException)
		{
			GisSetup.EnsureInitialized();
			return Encoding.GetEncoding(1252).GetString(bytes);
		}
	}

	private static char DetectDelimiter(string text)
	{
		int end = text.IndexOf('\n');
		string first = end < 0 ? text : text.Substring(0, end);
		char best = ',';
		int bestCount = 0;
		foreach (char c in new[] { ',', ';', '\t', '|' })
		{
			int count = 0;
			bool quoted = false;
			foreach (char ch in first)
			{
				if (ch == '"')
				{
					quoted = !quoted;
				}
				else if (ch == c && !quoted)
				{
					count++;
				}
			}
			if (count > bestCount)
			{
				best = c;
				bestCount = count;
			}
		}
		return best;
	}

	/// <summary>RFC 4180 parsing: quoted fields may hold the delimiter, doubled quotes and line breaks.</summary>
	internal static List<string[]> Parse(string text, char delimiter)
	{
		List<string[]> rows = new List<string[]>();
		List<string> row = new List<string>();
		StringBuilder cell = new StringBuilder();
		bool quoted = false;
		bool rowHasContent = false;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (quoted)
			{
				if (c == '"')
				{
					if (i + 1 < text.Length && text[i + 1] == '"')
					{
						cell.Append('"');
						i++;
					}
					else
					{
						quoted = false;
					}
				}
				else
				{
					cell.Append(c);
				}
				continue;
			}
			if (c == '"')
			{
				quoted = true;
				rowHasContent = true;
			}
			else if (c == delimiter)
			{
				row.Add(cell.ToString());
				cell.Clear();
				rowHasContent = true;
			}
			else if (c == '\r' || c == '\n')
			{
				if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
				{
					i++;
				}
				if (rowHasContent || cell.Length > 0)
				{
					row.Add(cell.ToString());
					rows.Add(row.ToArray());
				}
				row.Clear();
				cell.Clear();
				rowHasContent = false;
			}
			else
			{
				cell.Append(c);
				rowHasContent = true;
			}
		}
		if (rowHasContent || cell.Length > 0)
		{
			row.Add(cell.ToString());
			rows.Add(row.ToArray());
		}
		return rows;
	}

	private static string Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : null;

	private static int IndexOf(string[] header, string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return -1;
		}
		for (int i = 0; i < header.Length; i++)
		{
			if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		return -1;
	}

	private static string Key(string s)
	{
		StringBuilder b = new StringBuilder();
		foreach (char c in s ?? "")
		{
			if (char.IsAsciiLetterOrDigit(c))
			{
				b.Append(char.ToLowerInvariant(c));
			}
		}
		return b.ToString();
	}
}
