namespace Sde.Gis.Projections;

/// <summary>
/// A map projection: converts longitude and latitude (degrees) on an ellipsoid to easting and northing (metres, with
/// the false easting and northing already added) and back. The formulas follow EPSG Guidance Note 7-2 (IOGP
/// publication 373-7-2, September 2019). Units other than metres are handled by <see cref="CoordinateSystem"/>.
/// </summary>
public abstract class MapProjection
{
	protected const double DegToRad = Math.PI / 180.0;
	protected const double RadToDeg = 180.0 / Math.PI;

	protected MapProjection(Ellipsoid ellipsoid)
	{
		Ellipsoid = ellipsoid ?? throw new ArgumentNullException(nameof(ellipsoid));
	}

	public Ellipsoid Ellipsoid { get; }

	/// <summary>A short name for the method, such as "Transverse Mercator".</summary>
	public abstract string MethodName { get; }

	/// <summary>Longitude and latitude in degrees to easting and northing in metres.</summary>
	public abstract (double X, double Y) Forward(double longitude, double latitude);

	/// <summary>Easting and northing in metres to longitude and latitude in degrees.</summary>
	public abstract (double Longitude, double Latitude) Inverse(double x, double y);

	/// <summary>Wraps a longitude difference in radians into -π to π.</summary>
	protected static double WrapRadians(double angle)
	{
		if (angle > Math.PI || angle < -Math.PI)
		{
			angle = Math.IEEERemainder(angle, 2.0 * Math.PI);
		}
		return angle;
	}

	/// <summary>Wraps a longitude in degrees into -180 to 180.</summary>
	protected static double WrapDegrees(double longitude)
	{
		if (longitude > 180.0 || longitude < -180.0)
		{
			longitude = Math.IEEERemainder(longitude, 360.0);
		}
		return longitude;
	}
}
