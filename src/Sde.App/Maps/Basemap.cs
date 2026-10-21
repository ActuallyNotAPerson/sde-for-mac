namespace Sde.App.Maps;

/// <summary>A source of map tiles, or "No basemap" (a blank background, which needs no internet).</summary>
public sealed record Basemap(string Id, string Name, string UrlTemplate, string Attribution, int MaxZoom)
{
	/// <summary>True when there are tiles to load.</summary>
	public bool HasTiles => !string.IsNullOrEmpty(UrlTemplate);

	/// <summary>The web address of one tile.</summary>
	public string TileUrl(int z, int x, int y) => UrlTemplate.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());

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
}
