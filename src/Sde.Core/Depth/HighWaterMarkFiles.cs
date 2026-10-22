using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sde.Core.Depth;

/// <summary>High-water marks read from a file, and the rows that couldn't be used (with plain-English reasons).</summary>
public sealed class HighWaterMarkFile
{
	public List<HighWaterMark> Marks { get; } = new List<HighWaterMark>();

	/// <summary>What was wrong with the file or with rows that were skipped, at most <see cref="HighWaterMarkFiles.MaxProblems"/> of them.</summary>
	public List<string> Problems { get; } = new List<string>();

	/// <summary>How many rows or features were skipped in all.</summary>
	public int Skipped { get; set; }
}

/// <summary>
/// Reads high-water marks from a CSV file or GeoJSON points. Columns and properties are found by their usual names
/// (latitude, lat, latitude_dd; longitude, lon, long; elev_ft, elevation; vertical_datum_name, datum;
/// hwm_quality_id, quality), ignoring case, spaces and underscores, so USGS's own exports read as they are.
/// Elevations must be in feet.
/// </summary>
public static class HighWaterMarkFiles
{
	/// <summary>At most this many problems are listed; the rest are only counted.</summary>
	public const int MaxProblems = 20;

	private static readonly string[] LatitudeNames = { "latitude", "latitudedd", "lat", "latdd", "y", "pointy" };
	private static readonly string[] LongitudeNames = { "longitude", "longitudedd", "long", "lon", "lng", "londd", "x", "pointx" };
	private static readonly string[] ElevationNames = { "elevft", "elevationft", "elevation", "elev", "hwmelevft", "hwmelevation", "hwmelev", "wse", "wsel", "watersurfaceelevation" };
	private static readonly string[] DatumNames = { "verticaldatumname", "verticaldatum", "vdatum", "vdatumname", "datum" };
	private static readonly string[] QualityNames = { "hwmqualityid", "qualityid", "hwmqualityname", "hwmquality", "quality" };
	private static readonly string[] IdNames = { "hwmid", "id", "objectid", "fid" };
	private static readonly string[] LabelNames = { "hwmlabel", "label", "name" };
	private static readonly string[] KindNames = { "hwmtypename", "hwmtype", "type" };

	/// <summary>
	/// Reads a file by its extension: .geojson or .json as GeoJSON, anything else as CSV (comma, tab or semicolon
	/// separated). <paramref name="defaultDatum"/> is used for marks whose row doesn't name a datum.
	/// </summary>
	public static HighWaterMarkFile Read(string path, string defaultDatum = null)
	{
		string ext = Path.GetExtension(path).ToLowerInvariant();
		if (ext is ".geojson" or ".json")
		{
			return ReadGeoJson(File.ReadAllText(path), defaultDatum);
		}
		using StreamReader reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return ReadCsv(reader, defaultDatum);
	}

	/// <summary>Reads CSV text with a header row.</summary>
	public static HighWaterMarkFile ReadCsv(TextReader reader, string defaultDatum = null)
	{
		HighWaterMarkFile result = new HighWaterMarkFile();
		string text = reader.ReadToEnd().TrimStart('﻿');
		List<List<string>> rows = ParseCsv(text);
		if (rows.Count == 0)
		{
			result.Problems.Add("The file is empty.");
			return result;
		}
		List<string> header = rows[0].Select(Squash).ToList();
		int lat = Find(header, LatitudeNames);
		int lon = Find(header, LongitudeNames);
		int elev = Find(header, ElevationNames);
		if (lat < 0 || lon < 0 || elev < 0)
		{
			result.Problems.Add(MissingColumns(lat < 0, lon < 0, elev < 0, "column"));
			return result;
		}
		int datum = Find(header, DatumNames);
		int quality = Find(header, QualityNames);
		int id = Find(header, IdNames);
		int label = Find(header, LabelNames);
		int kind = Find(header, KindNames);
		for (int r = 1; r < rows.Count; r++)
		{
			List<string> row = rows[r];
			if (row.All(string.IsNullOrWhiteSpace))
			{
				continue;
			}
			string Cell(int i) => i >= 0 && i < row.Count ? row[i]?.Trim() : null;
			string where = "Row " + (r + 1).ToString(CultureInfo.InvariantCulture);
			HighWaterMark mark = Make(where, Cell(lat), Cell(lon), Cell(elev), Cell(datum), Cell(quality),
				Cell(id) ?? (r + 1).ToString(CultureInfo.InvariantCulture), Cell(label), Cell(kind), defaultDatum, result);
			if (mark != null)
			{
				result.Marks.Add(mark);
			}
		}
		return result;
	}

	/// <summary>
	/// Reads a GeoJSON FeatureCollection (or a single Feature) of points. Coordinates must be longitude and latitude
	/// (WGS 84, as GeoJSON requires); a file that names another coordinate system is refused.
	/// </summary>
	public static HighWaterMarkFile ReadGeoJson(string json, string defaultDatum = null)
	{
		HighWaterMarkFile result = new HighWaterMarkFile();
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		}
		catch (JsonException)
		{
			result.Problems.Add("The file isn't valid GeoJSON.");
			return result;
		}
		using (doc)
		{
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				result.Problems.Add("The file isn't valid GeoJSON.");
				return result;
			}
			if (root.TryGetProperty("crs", out JsonElement crs) && crs.ValueKind == JsonValueKind.Object && !IsLongitudeLatitude(crs))
			{
				result.Problems.Add("The file uses another coordinate system. Save it as GeoJSON in WGS 84 (longitude and latitude) and try again.");
				return result;
			}
			List<JsonElement> features = new List<JsonElement>();
			string type = root.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
			if (type == "FeatureCollection" && root.TryGetProperty("features", out JsonElement fs) && fs.ValueKind == JsonValueKind.Array)
			{
				features.AddRange(fs.EnumerateArray());
			}
			else if (type == "Feature")
			{
				features.Add(root);
			}
			else
			{
				result.Problems.Add("The file has no GeoJSON features.");
				return result;
			}

			for (int i = 0; i < features.Count; i++)
			{
				JsonElement f = features[i];
				string where = "Feature " + (i + 1).ToString(CultureInfo.InvariantCulture);
				if (!f.TryGetProperty("geometry", out JsonElement g) || g.ValueKind != JsonValueKind.Object
					|| !g.TryGetProperty("type", out JsonElement gt) || gt.GetString() != "Point"
					|| !g.TryGetProperty("coordinates", out JsonElement c) || c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2)
				{
					Skip(result, where + ": not a point.");
					continue;
				}
				Dictionary<string, string> props = new Dictionary<string, string>(StringComparer.Ordinal);
				if (f.TryGetProperty("properties", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty prop in p.EnumerateObject())
					{
						props.TryAdd(Squash(prop.Name), prop.Value.ValueKind switch
						{
							JsonValueKind.String => prop.Value.GetString(),
							JsonValueKind.Number => prop.Value.GetRawText(),
							_ => null,
						});
					}
				}
				string Prop(string[] names) => names.Select(n => props.TryGetValue(n, out string v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
				string id = Prop(IdNames)
					?? (f.TryGetProperty("id", out JsonElement fid) && fid.ValueKind is JsonValueKind.String or JsonValueKind.Number ? fid.ToString() : (i + 1).ToString(CultureInfo.InvariantCulture));
				HighWaterMark mark = Make(where, c[1].ToString(), c[0].ToString(), Prop(ElevationNames), Prop(DatumNames), Prop(QualityNames),
					id, Prop(LabelNames), Prop(KindNames), defaultDatum, result);
				if (mark != null)
				{
					result.Marks.Add(mark);
				}
			}
		}
		return result;
	}

	/// <summary>
	/// A USGS quality rating from a number (1 to 6) or its name ("Excellent: +/- 0.05 ft", "Good", "Fair", "Poor",
	/// "VP", "Unknown/Historical"); null when it isn't one.
	/// </summary>
	public static int? ParseQuality(string text)
	{
		string s = (text ?? "").Trim();
		if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
		{
			return n is >= 1 and <= 6 ? n : null;
		}
		string word = new string(s.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
		return word switch
		{
			"excellent" => 1,
			"good" => 2,
			"fair" => 3,
			"poor" => 4,
			"vp" or "very" => 5,
			"unknown" or "historical" => 6,
			_ => null,
		};
	}

	private static HighWaterMark Make(string where, string lat, string lon, string elev, string datum, string quality, string id, string label, string kind,
		string defaultDatum, HighWaterMarkFile result)
	{
		if (!TryNumber(lat, out double la) || !TryNumber(lon, out double lo))
		{
			Skip(result, where + ": no latitude and longitude.");
			return null;
		}
		if (la < -90 || la > 90 || lo < -180 || lo > 180 || (la == 0 && lo == 0))
		{
			Skip(result, where + ": the latitude and longitude aren't a place on the map.");
			return null;
		}
		if (string.IsNullOrWhiteSpace(elev) || !decimal.TryParse(elev, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal e))
		{
			Skip(result, where + ": no elevation.");
			return null;
		}
		return new HighWaterMark
		{
			Id = id,
			Label = string.IsNullOrWhiteSpace(label) ? null : label,
			Latitude = la,
			Longitude = lo,
			ElevationFt = e,
			DatumName = string.IsNullOrWhiteSpace(datum) ? defaultDatum : datum,
			Quality = ParseQuality(quality),
			Kind = string.IsNullOrWhiteSpace(kind) ? null : kind,
		};
	}

	private static bool TryNumber(string text, out double value) =>
		double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

	private static void Skip(HighWaterMarkFile result, string problem)
	{
		result.Skipped++;
		if (result.Problems.Count < MaxProblems)
		{
			result.Problems.Add(problem);
		}
	}

	private static string MissingColumns(bool lat, bool lon, bool elev, string what)
	{
		List<string> missing = new List<string>();
		if (lat)
		{
			missing.Add("latitude (latitude, lat or y)");
		}
		if (lon)
		{
			missing.Add("longitude (longitude, lon or x)");
		}
		if (elev)
		{
			missing.Add("elevation in feet (elev_ft or elevation)");
		}
		return $"Couldn't find a {what} for {string.Join(", ", missing)}.";
	}

	private static bool IsLongitudeLatitude(JsonElement crs)
	{
		string name = crs.TryGetProperty("properties", out JsonElement p) && p.ValueKind == JsonValueKind.Object
			&& p.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
		return name.Contains("CRS84", StringComparison.OrdinalIgnoreCase) || name.EndsWith("4326", StringComparison.Ordinal) || name.EndsWith("4269", StringComparison.Ordinal);
	}

	private static int Find(List<string> header, string[] names)
	{
		foreach (string n in names)
		{
			int i = header.IndexOf(n);
			if (i >= 0)
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>A column or property name without case, spaces or punctuation: "Elev (ft)" and "elev_ft" are both "elevft".</summary>
	private static string Squash(string name) => new string((name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

	/// <summary>Splits CSV text into rows of cells: quoted cells may hold separators, quotes ("") and line breaks.</summary>
	private static List<List<string>> ParseCsv(string text)
	{
		string firstLine = text.Split('\n')[0];
		char sep = firstLine.Contains(',') ? ',' : firstLine.Contains('\t') ? '\t' : firstLine.Contains(';') ? ';' : ',';
		List<List<string>> rows = new List<List<string>>();
		List<string> row = new List<string>();
		StringBuilder cell = new StringBuilder();
		bool quoted = false;
		for (int i = 0; i < text.Length; i++)
		{
			char ch = text[i];
			if (quoted)
			{
				if (ch == '"')
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
					cell.Append(ch);
				}
			}
			else if (ch == '"')
			{
				quoted = true;
			}
			else if (ch == sep)
			{
				row.Add(cell.ToString());
				cell.Clear();
			}
			else if (ch == '\n' || ch == '\r')
			{
				if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
				{
					i++;
				}
				row.Add(cell.ToString());
				cell.Clear();
				rows.Add(row);
				row = new List<string>();
			}
			else
			{
				cell.Append(ch);
			}
		}
		if (cell.Length > 0 || row.Count > 0)
		{
			row.Add(cell.ToString());
			rows.Add(row);
		}
		return rows;
	}
}
