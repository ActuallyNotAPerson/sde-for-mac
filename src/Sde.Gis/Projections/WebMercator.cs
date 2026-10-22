namespace Sde.Gis.Projections;

/// <summary>
/// Popular Visualisation Pseudo-Mercator, "Web Mercator" (EPSG method 1024), EPSG Guidance Note 7-2 section 3.2.1.2:
/// the grid of web map tiles (EPSG 3857). Latitudes are held within ±89.5° so the poles don't give infinities.
/// </summary>
public sealed class WebMercator : MapProjection
{
	/// <summary>The largest latitude converted; web tiles stop at about 85.05°.</summary>
	public const double MaxLatitude = 89.5;

	private readonly double _a;
	private readonly double _lon0;
	private readonly double _fe;
	private readonly double _fn;

	/// <param name="ellipsoid">Only the semi-major axis is used: the method works on a sphere of that radius.</param>
	/// <param name="longitudeOfOrigin">λO, degrees.</param>
	/// <param name="falseEasting">FE, metres.</param>
	/// <param name="falseNorthing">FN, metres.</param>
	public WebMercator(Ellipsoid ellipsoid, double longitudeOfOrigin = 0.0, double falseEasting = 0.0, double falseNorthing = 0.0)
		: base(ellipsoid)
	{
		_a = ellipsoid.SemiMajorAxis;
		_lon0 = longitudeOfOrigin * DegToRad;
		_fe = falseEasting;
		_fn = falseNorthing;
	}

	/// <summary>EPSG 3857 itself: WGS 84, origin at 0,0.</summary>
	public static WebMercator Epsg3857 { get; } = new WebMercator(Ellipsoid.Wgs84);

	public override string MethodName => "Popular Visualisation Pseudo-Mercator";

	public override (double X, double Y) Forward(double longitude, double latitude)
	{
		double phi = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * DegToRad;
		double x = _fe + _a * WrapRadians(longitude * DegToRad - _lon0);
		double y = _fn + _a * Math.Log(Math.Tan(Math.PI / 4.0 + phi / 2.0));
		return (x, y);
	}

	public override (double Longitude, double Latitude) Inverse(double x, double y)
	{
		double d = (_fn - y) / _a;
		double phi = Math.PI / 2.0 - 2.0 * Math.Atan(Math.Exp(d));
		double lambda = (x - _fe) / _a + _lon0;
		return (WrapDegrees(lambda * RadToDeg), phi * RadToDeg);
	}
}
