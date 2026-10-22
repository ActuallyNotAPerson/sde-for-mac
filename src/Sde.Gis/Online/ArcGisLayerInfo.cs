using System.Text.Json;
using NetTopologySuite.Geometries;
using Sde.Gis.Features;

namespace Sde.Gis.Online;

/// <summary>One field of an ArcGIS layer, as its metadata describes it.</summary>
public sealed class ArcGisField
{
	public string Name { get; init; }

	/// <summary>The Esri type, such as esriFieldTypeString.</summary>
	public string EsriType { get; init; }

	public string Alias { get; init; }

	/// <summary>The field type SDE holds the values as; null for types SDE leaves out (geometry, blob, raster).</summary>
	public FieldType? Type => MapType(EsriType);

	/// <summary>The SDE field type for an Esri field type.</summary>
	public static FieldType? MapType(string esriType) => esriType switch
	{
		"esriFieldTypeOID" or "esriFieldTypeSmallInteger" or "esriFieldTypeInteger" or "esriFieldTypeBigInteger" => FieldType.Integer,
		"esriFieldTypeSingle" or "esriFieldTypeDouble" => FieldType.Real,
		"esriFieldTypeDate" => FieldType.DateTime,
		"esriFieldTypeDateOnly" => FieldType.Date,
		"esriFieldTypeGeometry" or "esriFieldTypeBlob" or "esriFieldTypeRaster" => null,
		_ => FieldType.Text,
	};
}

/// <summary>What an ArcGIS FeatureServer or MapServer layer says about itself (its "?f=json" metadata).</summary>
public sealed class ArcGisLayerInfo
{
	/// <summary>The layer's address, ending in /FeatureServer/n or /MapServer/n.</summary>
	public string Url { get; init; }

	public string Name { get; init; }

	/// <summary>"Feature Layer", "Raster Layer", "Group Layer" and so on.</summary>
	public string LayerType { get; init; }

	/// <summary>esriGeometryPoint, esriGeometryMultipoint, esriGeometryPolyline or esriGeometryPolygon.</summary>
	public string GeometryType { get; init; }

	public GeometryKind GeometryKind => GeometryType switch
	{
		"esriGeometryPoint" or "esriGeometryMultipoint" => GeometryKind.Point,
		"esriGeometryPolyline" => GeometryKind.Line,
		"esriGeometryPolygon" or "esriGeometryEnvelope" => GeometryKind.Polygon,
		_ => GeometryKind.Unknown,
	};

	/// <summary>The most features the server returns for one request.</summary>
	public int MaxRecordCount { get; init; }

	/// <summary>True when the layer can be paged with resultOffset and resultRecordCount.</summary>
	public bool SupportsPagination { get; init; }

	/// <summary>True when the layer can answer queries as GeoJSON (f=geojson); otherwise SDE reads Esri JSON.</summary>
	public bool SupportsGeoJson { get; init; }

	/// <summary>The object id field, such as OBJECTID.</summary>
	public string ObjectIdField { get; init; }

	public IReadOnlyList<ArcGisField> Fields { get; init; } = Array.Empty<ArcGisField>();

	/// <summary>The layer's extent in its own coordinates (see <see cref="ExtentWkid"/>), or null.</summary>
	public Envelope Extent { get; init; }

	public int? ExtentWkid { get; init; }

	/// <summary>The copyright or attribution text the layer gives, for the map's attribution line.</summary>
	public string Copyright { get; init; }

	public string Description { get; init; }

	/// <summary>Map scales the service says the layer is drawn between (0 when unlimited).</summary>
	public double MinScale { get; init; }

	public double MaxScale { get; init; }

	/// <summary>Reads the metadata JSON.</summary>
	internal static ArcGisLayerInfo Parse(string url, JsonElement root)
	{
		List<ArcGisField> fields = new List<ArcGisField>();
		string oid = Text(root, "objectIdField");
		if (root.TryGetProperty("fields", out JsonElement fs) && fs.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement f in fs.EnumerateArray())
			{
				ArcGisField field = new ArcGisField { Name = Text(f, "name"), EsriType = Text(f, "type"), Alias = Text(f, "alias") };
				if (!string.IsNullOrEmpty(field.Name))
				{
					fields.Add(field);
				}
				if (oid == null && field.EsriType == "esriFieldTypeOID")
				{
					oid = field.Name;
				}
			}
		}
		bool paging = false;
		if (root.TryGetProperty("advancedQueryCapabilities", out JsonElement aqc) && aqc.ValueKind == JsonValueKind.Object
			&& aqc.TryGetProperty("supportsPagination", out JsonElement sp) && sp.ValueKind == JsonValueKind.True)
		{
			paging = true;
		}
		string formats = Text(root, "supportedQueryFormats") ?? "";
		Envelope extent = null;
		int? wkid = null;
		if (root.TryGetProperty("extent", out JsonElement e) && e.ValueKind == JsonValueKind.Object)
		{
			double? x0 = Number(e, "xmin"), y0 = Number(e, "ymin"), x1 = Number(e, "xmax"), y1 = Number(e, "ymax");
			if (x0 is double a && y0 is double b && x1 is double c && y1 is double d)
			{
				extent = new Envelope(a, c, b, d);
			}
			if (e.TryGetProperty("spatialReference", out JsonElement sr) && sr.ValueKind == JsonValueKind.Object)
			{
				double? latest = Number(sr, "latestWkid") ?? Number(sr, "wkid");
				wkid = latest is double w ? (int)w : null;
			}
		}
		return new ArcGisLayerInfo
		{
			Url = url,
			Name = Text(root, "name"),
			LayerType = Text(root, "type"),
			GeometryType = Text(root, "geometryType"),
			MaxRecordCount = Number(root, "maxRecordCount") is double max && max > 0 ? (int)max : 1000,
			SupportsPagination = paging,
			SupportsGeoJson = formats.Split(',').Any(s => string.Equals(s.Trim(), "geoJSON", StringComparison.OrdinalIgnoreCase)),
			ObjectIdField = oid,
			Fields = fields,
			Extent = extent,
			ExtentWkid = wkid,
			Copyright = Text(root, "copyrightText"),
			Description = Text(root, "description"),
			MinScale = Number(root, "minScale") ?? 0,
			MaxScale = Number(root, "maxScale") ?? 0,
		};
	}

	internal static string Text(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

	internal static double? Number(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
