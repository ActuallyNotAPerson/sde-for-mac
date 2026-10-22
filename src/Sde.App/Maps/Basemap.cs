using System.Globalization;

namespace Sde.App.Maps;

/// <summary>A source of map tiles, or "No basemap" (a blank background, which needs no internet).</summary>
/// <remarks>
/// The address holds {z}/{x}/{y} for a tile service, or {bbox} for a map service that draws any area (an ArcGIS
/// MapServer "export"), which is then asked for once per 256 px tile. Such a service can also draw twice as many pixels
/// for a Retina screen: {size} and {dpi} become 512 and 192 there (256 and 96 otherwise).
/// </remarks>
public sealed record Basemap(string Id, string Name, string UrlTemplate, string Attribution, int MaxZoom)
{
	/// <summary>True when there are tiles to load.</summary>
	public bool HasTiles => !string.IsNullOrEmpty(UrlTemplate);

	/// <summary>True when the service draws any area ({bbox} and {size}), so it can send sharper tiles for a Retina screen.</summary>
	public bool HasRetinaTiles => HasTiles && UrlTemplate.Contains("{bbox}") && UrlTemplate.Contains("{size}");

	/// <summary>The web address of one tile; <paramref name="scale"/> 2 asks for a Retina tile where the service has them.</summary>
	public string TileUrl(int z, int x, int y, int scale = 1)
	{
		string url = UrlTemplate.Replace("{z}", z.ToString(CultureInfo.InvariantCulture))
			.Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
			.Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
		if (url.Contains("{bbox}"))
		{
			int factor = HasRetinaTiles && scale > 1 ? 2 : 1;
			url = url.Replace("{bbox}", Bbox(z, x, y))
				.Replace("{size}", (256 * factor).ToString(CultureInfo.InvariantCulture))
				.Replace("{dpi}", (96 * factor).ToString(CultureInfo.InvariantCulture));
		}
		return url;
	}

	/// <summary>
	/// The Web Mercator (EPSG:3857) bounds of tile z/x/y as "minX,minY,maxX,maxY" in metres, worked out from the tile
	/// numbers so the drawn areas line up exactly with the {z}/{x}/{y} basemaps.
	/// </summary>
	public static string Bbox(int z, int x, int y)
	{
		double size = 2 * WebMercator.HalfWorld / (1L << z);
		double minX = -WebMercator.HalfWorld + x * size;
		double maxY = WebMercator.HalfWorld - y * size;
		return string.Join(",", new[] { minX, maxY - size, minX + size, maxY }.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
	}

	public override string ToString() => Name;
}

/// <summary>The basemaps the Map page offers.</summary>
public static class Basemaps
{
	public static readonly Basemap None = new Basemap("none", "No basemap", null, null, 19);

	/// <summary>U.S. Geological Survey topographic map from The National Map (public domain).</summary>
	public static readonly Basemap UsgsTopo = new Basemap("usgs-topo", "USGS topographic map",
		"https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/{z}/{y}/{x}",
		"Map data: U.S. Geological Survey, The National Map", 16);

	/// <summary>U.S. Geological Survey aerial imagery with place names (public domain).</summary>
	public static readonly Basemap UsgsImagery = new Basemap("usgs-imagery", "USGS aerial imagery",
		"https://basemap.nationalmap.gov/arcgis/rest/services/USGSImageryTopo/MapServer/tile/{z}/{y}/{x}",
		"Map data: U.S. Geological Survey, The National Map", 16);

	/// <summary>OpenStreetMap's standard map. Its terms require the attribution to stay visible.</summary>
	public static readonly Basemap OpenStreetMap = new Basemap("osm", "OpenStreetMap",
		"https://tile.openstreetmap.org/{z}/{x}/{y}.png",
		"© OpenStreetMap contributors", 19);

	/// <summary>In the order the drop-down lists them.</summary>
	public static IReadOnlyList<Basemap> All { get; } = new[] { UsgsTopo, UsgsImagery, OpenStreetMap, None };

	/// <summary>A basemap by its id, or USGS topographic when the id is unknown.</summary>
	public static Basemap WithId(string id) => All.FirstOrDefault(b => b.Id == id) ?? UsgsTopo;
}

/// <summary>Tile layers drawn over the basemap.</summary>
public static class TileOverlays
{
	/// <summary>
	/// FEMA's National Flood Hazard Layer, flood hazard zones only (layer 28), drawn by the MapServer export endpoint
	/// once per tile with a transparent background, in Web Mercator (imageSR=3857) so it lines up with the basemaps.
	/// </summary>
	public static readonly Basemap FloodZones = new Basemap("fema-nfhl-28", "FEMA flood zones",
		"https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/export?bbox={bbox}&bboxSR=3857&imageSR=3857&size={size},{size}&dpi={dpi}&format=png32&transparent=true&f=image&layers=show:28",
		"Flood hazard zones: FEMA National Flood Hazard Layer", 19);
}
