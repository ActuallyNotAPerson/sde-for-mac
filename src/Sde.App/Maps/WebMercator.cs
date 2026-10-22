namespace Sde.App.Maps;

/// <summary>
/// Web Mercator (EPSG:3857), the projection every web basemap uses. Positions are kept as "world" fractions: 0 to 1
/// across the whole map from west to east (U) and from north to south (V). Multiply by 256 x 2^zoom for pixels.
/// </summary>
public static class WebMercator
{
	/// <summary>The map stops at this latitude, where the projected world becomes a square.</summary>
	public const double MaxLatitude = 85.05112878;

	/// <summary>Size of one tile in pixels.</summary>
	public const double TileSize = 256;

	/// <summary>Half the width of the world in Web Mercator metres (the map runs from minus this to plus this).</summary>
	public const double HalfWorld = 20037508.342789244;

	/// <summary>Metres per pixel at zoom 0 (the whole world in one 256 px tile).</summary>
	public const double Zoom0Resolution = 2 * HalfWorld / TileSize;

	/// <summary>West-to-east position, 0 at 180 degrees west and 1 at 180 degrees east.</summary>
	public static double U(double longitude) => (longitude + 180.0) / 360.0;

	/// <summary>North-to-south position, 0 at the top of the map and 1 at the bottom.</summary>
	public static double V(double latitude)
	{
		double lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180.0;
		return (1.0 - Math.Log(Math.Tan(lat) + 1.0 / Math.Cos(lat)) / Math.PI) / 2.0;
	}

	public static double Longitude(double u) => u * 360.0 - 180.0;

	public static double Latitude(double v)
	{
		double n = Math.PI * (1.0 - 2.0 * v);
		return Math.Atan(Math.Sinh(n)) * 180.0 / Math.PI;
	}

	/// <summary>The width of the whole world in pixels at a zoom level (which may be fractional).</summary>
	public static double WorldPixels(double zoom) => TileSize * Math.Pow(2.0, zoom);

	/// <summary>Web Mercator metres from a longitude and latitude.</summary>
	public static (double X, double Y) ToMetres(double longitude, double latitude) =>
		((U(longitude) - 0.5) * 2 * HalfWorld, (0.5 - V(latitude)) * 2 * HalfWorld);

	/// <summary>Longitude and latitude from Web Mercator metres.</summary>
	public static (double Longitude, double Latitude) FromMetres(double x, double y) =>
		(Longitude(x / (2 * HalfWorld) + 0.5), Latitude(0.5 - y / (2 * HalfWorld)));

	/// <summary>Metres per pixel at a zoom level (which may be fractional).</summary>
	public static double Resolution(double zoom) => Zoom0Resolution / Math.Pow(2.0, zoom);

	/// <summary>The zoom level of a resolution in metres per pixel.</summary>
	public static double ZoomOf(double resolution) => Math.Log2(Zoom0Resolution / resolution);

	/// <summary>
	/// The map scale denominator (24000 for 1:24,000) at a latitude, for a resolution in Web Mercator metres per pixel
	/// and the usual 96 pixels per inch.
	/// </summary>
	public static double Scale(double resolution, double latitude) =>
		resolution * Math.Cos(Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180.0) / (0.0254 / 96.0);
}
