using System.Globalization;
using System.Text;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;
using Sde.Gis.Features;
using Sde.Gis.Projections;

namespace Sde.Gis.Formats;

/// <summary>
/// Reads and writes GeoJSON (RFC 7946) with NetTopologySuite.IO.GeoJSON4STJ (BSD-3) for the geometries. Property types
/// are worked out from the values (whole numbers, numbers, true/false, text). Files in the older 2008 form with a "crs"
/// member naming an EPSG code SDE knows (QGIS writes one for non-WGS 84 data) are converted to WGS 84.
/// </summary>
public static class GeoJsonFormat
{
	private static readonly JsonSerializerOptions Options = CreateOptions();

	/// <summary>Reads a .geojson or .json file.</summary>
	public static FeatureLayer Read(string path)
	{
		if (!File.Exists(path))
		{
			throw new GisException("The GeoJSON file couldn't be found.", path);
		}
		using FileStream stream = File.OpenRead(path);
		return Read(stream, Path.GetFileNameWithoutExtension(path));
	}

	/// <summary>Reads GeoJSON text.</summary>
	public static FeatureLayer ReadString(string json, string name = "layer")
	{
		using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? ""));
		return Read(stream, name);
	}

	/// <summary>Reads GeoJSON from a stream: a FeatureCollection, a single Feature or a bare geometry.</summary>
	public static FeatureLayer Read(Stream stream, string name)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		}
		catch (JsonException ex)
		{
			throw new GisException("This file isn't valid GeoJSON.", ex.Message, ex);
		}
		using (doc)
		{
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				throw new GisException("This file isn't valid GeoJSON (it doesn't start with an object).", name);
			}
			string type = Str(root, "type");
			List<JsonElement> features = new List<JsonElement>();
			switch (type)
			{
				case "FeatureCollection":
					if (root.TryGetProperty("features", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
					{
						features.AddRange(array.EnumerateArray());
					}
					break;
				case "Feature":
					features.Add(root);
					break;
				case "Point":
				case "MultiPoint":
				case "LineString":
				case "MultiLineString":
				case "Polygon":
				case "MultiPolygon":
				case "GeometryCollection":
					break;
				default:
					throw new GisException("This file isn't GeoJSON SDE can read (its type isn't FeatureCollection, Feature or a geometry).", $"type = {type}");
			}

			CoordinateSystem cs = CrsOf(root);
			FeatureLayer layer = new FeatureLayer(Str(root, "name") ?? name);
			layer.SourceCoordinateSystem = cs.IsGeographic ? null : cs;
			if (type != "FeatureCollection" && type != "Feature")
			{
				layer.AddFeature(ReadGeometry(root, cs));
				layer.DetectGeometryKind();
				return layer;
			}

			// First pass: the properties and the type their values share.
			Dictionary<string, PropertyKind> kinds = new Dictionary<string, PropertyKind>(StringComparer.OrdinalIgnoreCase);
			List<string> order = new List<string>();
			foreach (JsonElement f in features)
			{
				if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty p in props.EnumerateObject())
					{
						PropertyKind k = KindOf(p.Value);
						if (!kinds.TryGetValue(p.Name, out PropertyKind existing))
						{
							kinds[p.Name] = k;
							order.Add(p.Name);
						}
						else
						{
							kinds[p.Name] = Combine(existing, k);
						}
					}
				}
			}
			foreach (string p in order)
			{
				layer.AddField(p, kinds[p] switch
				{
					PropertyKind.Integer => FieldType.Integer,
					PropertyKind.Real => FieldType.Real,
					PropertyKind.Boolean => FieldType.Boolean,
					_ => FieldType.Text,
				});
			}

			// Second pass: the features.
			int bad = 0;
			foreach (JsonElement f in features)
			{
				if (f.ValueKind != JsonValueKind.Object)
				{
					bad++;
					continue;
				}
				Geometry g = null;
				if (f.TryGetProperty("geometry", out JsonElement geometry) && geometry.ValueKind == JsonValueKind.Object)
				{
					try
					{
						g = ReadGeometry(geometry, cs);
					}
					catch (GisException)
					{
						bad++;
						continue;
					}
				}
				long id = 0;
				if (f.TryGetProperty("id", out JsonElement idElement))
				{
					if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out long n))
					{
						id = n;
					}
					else if (idElement.ValueKind == JsonValueKind.String && long.TryParse(idElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s))
					{
						id = s;
					}
				}
				Feature feature = layer.NewFeature(g, id);
				if (f.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty p in props.EnumerateObject())
					{
						int index = layer.Fields.IndexOf(p.Name);
						if (index >= 0 && feature[index] == null)
						{
							feature.SetRaw(index, ValueOf(p.Value, layer.Fields[index].Type));
						}
					}
				}
				layer.Features.Add(feature);
			}
			if (bad > 0)
			{
				layer.Warnings.Add($"{bad} features had a geometry SDE couldn't read and were left out.");
			}
			layer.DetectGeometryKind();
			return layer;
		}
	}

	/// <summary>Writes a layer as an RFC 7946 FeatureCollection (WGS 84, polygons wound counter-clockwise).</summary>
	public static void Write(string path, FeatureLayer layer)
	{
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		using FileStream stream = File.Create(path);
		Write(stream, layer);
	}

	/// <summary>The layer as GeoJSON text.</summary>
	public static string WriteString(FeatureLayer layer)
	{
		using MemoryStream stream = new MemoryStream();
		Write(stream, layer);
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>Writes a layer as GeoJSON to a stream.</summary>
	public static void Write(Stream stream, FeatureLayer layer)
	{
		ArgumentNullException.ThrowIfNull(layer);
		using Utf8JsonWriter w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
		w.WriteStartObject();
		w.WriteString("type", "FeatureCollection");
		if (!string.IsNullOrEmpty(layer.Name))
		{
			w.WriteString("name", layer.Name);
		}
		w.WriteStartArray("features");
		foreach (Feature f in layer.Features)
		{
			w.WriteStartObject();
			w.WriteString("type", "Feature");
			if (f.Id > 0)
			{
				w.WriteNumber("id", f.Id);
			}
			w.WritePropertyName("geometry");
			if (f.Geometry == null)
			{
				w.WriteNullValue();
			}
			else
			{
				JsonSerializer.Serialize(w, f.Geometry, Options);
			}
			w.WriteStartObject("properties");
			for (int i = 0; i < layer.Fields.Count; i++)
			{
				LayerField field = layer.Fields[i];
				object value = f[i];
				switch (value)
				{
					case null:
						w.WriteNull(field.Name);
						break;
					case long l:
						w.WriteNumber(field.Name, l);
						break;
					case double d when double.IsFinite(d):
						w.WriteNumber(field.Name, d);
						break;
					case double:
						w.WriteNull(field.Name);
						break;
					case bool b:
						w.WriteBoolean(field.Name, b);
						break;
					case DateTime dt:
						w.WriteString(field.Name, field.Type == FieldType.Date
							? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
							: dt.ToString(dt.Kind == DateTimeKind.Utc ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
						break;
					default:
						w.WriteString(field.Name, Convert.ToString(value, CultureInfo.InvariantCulture));
						break;
				}
			}
			w.WriteEndObject();
			w.WriteEndObject();
		}
		w.WriteEndArray();
		w.WriteEndObject();
	}

	/// <summary>Reads one GeoJSON geometry object (in <paramref name="cs"/>'s coordinates) as a WGS 84 geometry.</summary>
	internal static Geometry ReadGeometry(JsonElement geometry, CoordinateSystem cs)
	{
		Geometry g;
		try
		{
			g = geometry.Deserialize<Geometry>(Options);
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or NotSupportedException)
		{
			throw new GisException("A GeoJSON geometry couldn't be read.", ex.Message, ex);
		}
		if (g == null || g.IsEmpty)
		{
			return null;
		}
		return Reproject.ToWgs84(g, cs);
	}

	/// <summary>The coordinate system named by a 2008-style "crs" member, or WGS 84 when there is none (RFC 7946).</summary>
	private static CoordinateSystem CrsOf(JsonElement root)
	{
		if (!root.TryGetProperty("crs", out JsonElement crs) || crs.ValueKind != JsonValueKind.Object)
		{
			return CoordinateSystem.Wgs84;
		}
		string name = crs.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object ? Str(props, "name") : null;
		if (string.IsNullOrWhiteSpace(name))
		{
			return CoordinateSystem.Wgs84;
		}
		string upper = name.ToUpperInvariant();
		if (upper.Contains("CRS84", StringComparison.Ordinal) || upper.EndsWith(":4326", StringComparison.Ordinal))
		{
			return CoordinateSystem.Wgs84;
		}
		int colon = name.LastIndexOf(':');
		if (upper.Contains("EPSG", StringComparison.Ordinal) && colon >= 0 && int.TryParse(name.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
		{
			return CoordinateSystem.FromEpsg(code);
		}
		throw new UnsupportedCoordinateSystemException(name, $"GeoJSON crs name {name}.");
	}

	private enum PropertyKind
	{
		Null,
		Integer,
		Real,
		Boolean,
		Text,
	}

	private static PropertyKind KindOf(JsonElement v) => v.ValueKind switch
	{
		JsonValueKind.Null or JsonValueKind.Undefined => PropertyKind.Null,
		JsonValueKind.Number => v.TryGetInt64(out _) ? PropertyKind.Integer : PropertyKind.Real,
		JsonValueKind.True or JsonValueKind.False => PropertyKind.Boolean,
		_ => PropertyKind.Text,
	};

	private static PropertyKind Combine(PropertyKind a, PropertyKind b)
	{
		if (a == b || b == PropertyKind.Null)
		{
			return a;
		}
		if (a == PropertyKind.Null)
		{
			return b;
		}
		if ((a == PropertyKind.Integer && b == PropertyKind.Real) || (a == PropertyKind.Real && b == PropertyKind.Integer))
		{
			return PropertyKind.Real;
		}
		return PropertyKind.Text;
	}

	private static object ValueOf(JsonElement v, FieldType type)
	{
		switch (v.ValueKind)
		{
			case JsonValueKind.Null:
			case JsonValueKind.Undefined:
				return null;
			case JsonValueKind.Number:
				if (type == FieldType.Integer && v.TryGetInt64(out long l))
				{
					return l;
				}
				if (type == FieldType.Real)
				{
					return v.GetDouble();
				}
				return v.GetRawText();
			case JsonValueKind.True:
				return type == FieldType.Boolean ? true : "true";
			case JsonValueKind.False:
				return type == FieldType.Boolean ? false : "false";
			case JsonValueKind.String:
				return v.GetString();
			default:
				return v.GetRawText();
		}
	}

	private static string Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	private static JsonSerializerOptions CreateOptions()
	{
		JsonSerializerOptions options = new JsonSerializerOptions();
		options.Converters.Add(new GeoJsonConverterFactory(GisSetup.Factory, false, null, RingOrientationOption.EnforceRfc9746));
		return options;
	}
}
