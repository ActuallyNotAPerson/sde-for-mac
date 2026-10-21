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
}
