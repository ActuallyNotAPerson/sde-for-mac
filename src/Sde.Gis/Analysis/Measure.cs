using NetTopologySuite.Densify;
using NetTopologySuite.Geometries;
using Sde.Gis.Projections;

namespace Sde.Gis.Analysis;

/// <summary>
/// A Transverse Mercator projection (WGS 84, scale 1) centred on an area, for working in metres: within 50 km of its
/// centre, distances are true to better than 1 part in 30,000. Used for "within a distance" and nearest-polygon tests.
/// </summary>
public sealed class LocalProjection
{
	private readonly TransverseMercator _tm;

	public LocalProjection(double centreLongitude, double centreLatitude)
	{
		CentreLongitude = centreLongitude;
		CentreLatitude = centreLatitude;
		_tm = new TransverseMercator(Ellipsoid.Wgs84, centreLatitude, centreLongitude, 1.0, 0.0, 0.0);
	}

	/// <summary>A projection centred on a WGS 84 envelope.</summary>
	public static LocalProjection For(Envelope wgs84)
	{
		if (wgs84 == null || wgs84.IsNull)
		{
			return new LocalProjection(0, 0);
		}
		return new LocalProjection(wgs84.Centre.X, wgs84.Centre.Y);
	}

	public double CentreLongitude { get; }

	public double CentreLatitude { get; }

	/// <summary>A WGS 84 geometry in metres east and north of the centre.</summary>
	public Geometry Project(Geometry wgs84) => Reproject.Transform(wgs84, (lon, lat) => _tm.Forward(lon, lat));

	/// <summary>A geometry in metres back to WGS 84.</summary>
	public Geometry Unproject(Geometry metres) => Reproject.Transform(metres, (x, y) => _tm.Inverse(x, y));

	public Coordinate Project(Coordinate wgs84)
	{
		(double x, double y) = _tm.Forward(wgs84.X, wgs84.Y);
		return new Coordinate(x, y);
	}
}

/// <summary>
/// Distances and areas on the WGS 84 ellipsoid, for geometries in longitude and latitude.
/// <list type="bullet">
/// <item>Distance and length: geodesic, by Vincenty's inverse formula (accurate to 0.5 mm), segment by segment.</item>
/// <item>Area: the shape is projected onto an Albers equal-area projection fitted to its latitudes (equal-area, so
/// areas on the ellipsoid are kept exactly), with edges longer than 0.001° first divided so long edges follow the
/// same path as on a longitude and latitude map. For parcels and flood zones this agrees with QGIS's ellipsoidal
/// area to well under 0.1%.</item>
/// </list>
/// </summary>
public static class Measure
{
	/// <summary>Metres in an international foot (the foot used for distances and areas on screen).</summary>
	public const double MetresPerFoot = 0.3048;

	/// <summary>Square metres in an acre.</summary>
	public const double SquareMetresPerAcre = 4046.8564224;

	/// <summary>A sentence describing how distances and areas are measured, for the measure tool's help text.</summary>
	public const string Method = "Distances are measured along the earth's surface (geodesic, WGS 84). Areas are measured on an equal-area projection fitted to the shape.";

	private const double A = 6378137.0;
	private const double F = 1.0 / 298.257223563;
	private const double B = A * (1 - F);

	/// <summary>The geodesic distance in metres between two longitude and latitude points.</summary>
	public static double Distance(Coordinate a, Coordinate b) => Distance(a.X, a.Y, b.X, b.Y);

	/// <summary>The geodesic distance in metres between two longitude and latitude points (Vincenty's inverse formula on WGS 84).</summary>
	public static double Distance(double lon1, double lat1, double lon2, double lat2)
	{
		if (lon1 == lon2 && lat1 == lat2)
		{
			return 0.0;
		}
		const double d2r = Math.PI / 180.0;
		double l = (lon2 - lon1) * d2r;
		double u1 = Math.Atan((1 - F) * Math.Tan(lat1 * d2r));
		double u2 = Math.Atan((1 - F) * Math.Tan(lat2 * d2r));
		double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1), sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);
		double lambda = l;
		double sinSigma = 0, cosSigma = 0, sigma = 0, cos2Alpha = 0, cos2SigmaM = 0;
		bool converged = false;
		for (int i = 0; i < 200; i++)
		{
			double sinLambda = Math.Sin(lambda), cosLambda = Math.Cos(lambda);
			double t1 = cosU2 * sinLambda;
			double t2 = cosU1 * sinU2 - sinU1 * cosU2 * cosLambda;
			sinSigma = Math.Sqrt(t1 * t1 + t2 * t2);
			if (sinSigma == 0)
			{
				return 0.0;
			}
			cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
			sigma = Math.Atan2(sinSigma, cosSigma);
			double sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
			cos2Alpha = 1 - sinAlpha * sinAlpha;
			cos2SigmaM = cos2Alpha != 0 ? cosSigma - 2 * sinU1 * sinU2 / cos2Alpha : 0;
			double c = F / 16 * cos2Alpha * (4 + F * (4 - 3 * cos2Alpha));
			double previous = lambda;
			lambda = l + (1 - c) * F * sinAlpha * (sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)));
			if (Math.Abs(lambda - previous) < 1e-12)
			{
				converged = true;
				break;
			}
		}
		if (!converged)
		{
			// Nearly antipodal points: fall back to the great circle on a sphere of the mean radius.
			double r = (2 * A + B) / 3;
			double dLat = (lat2 - lat1) * d2r, dLon = (lon2 - lon1) * d2r;
			double h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * d2r) * Math.Cos(lat2 * d2r) * Math.Pow(Math.Sin(dLon / 2), 2);
			return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
		}
		double uSq = cos2Alpha * (A * A - B * B) / (B * B);
		double bigA = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
		double bigB = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
		double deltaSigma = bigB * sinSigma * (cos2SigmaM + bigB / 4 * (cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)
			- bigB / 6 * cos2SigmaM * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2SigmaM * cos2SigmaM)));
		return B * bigA * (sigma - deltaSigma);
	}

	/// <summary>The geodesic length in metres of a line (or the perimeter of a polygon, holes included), summed segment by segment.</summary>
	public static double Length(Geometry geometry)
	{
		if (geometry == null || geometry.IsEmpty)
		{
			return 0.0;
		}
		double total = 0.0;
		for (int i = 0; i < geometry.NumGeometries; i++)
		{
			Geometry part = geometry.GetGeometryN(i);
			switch (part)
			{
				case LineString line:
					total += LengthOf(line.CoordinateSequence);
					break;
				case Polygon polygon:
					total += LengthOf(polygon.ExteriorRing.CoordinateSequence);
					foreach (LineString hole in polygon.InteriorRings)
					{
						total += LengthOf(hole.CoordinateSequence);
					}
					break;
			}
		}
		return total;
	}

	/// <summary>The area in square metres of a polygon (holes excluded) on the WGS 84 ellipsoid; 0 for points and lines.</summary>
	public static double Area(Geometry geometry)
	{
		if (geometry == null || geometry.IsEmpty || geometry.Dimension != Dimension.Surface)
		{
			return 0.0;
		}
		Envelope e = geometry.EnvelopeInternal;
		double span = Math.Max(e.Height, 1e-6);
		double sp1 = e.MinY + span / 6.0;
		double sp2 = e.MaxY - span / 6.0;
		AlbersEqualArea albers = new AlbersEqualArea(Ellipsoid.Wgs84, e.MinY, e.Centre.X, sp1, Math.Abs(sp2 - sp1) < 1e-9 ? sp1 + 1e-6 : sp2, 0, 0);
		Geometry dense = Densifier.Densify(geometry, 0.001);
		return Reproject.Transform(dense, (lon, lat) => albers.Forward(lon, lat)).Area;
	}

	public static double MetresToFeet(double metres) => metres / MetresPerFoot;

	public static double FeetToMetres(double feet) => feet * MetresPerFoot;

	public static double SquareMetresToAcres(double squareMetres) => squareMetres / SquareMetresPerAcre;

	public static double SquareMetresToSquareFeet(double squareMetres) => squareMetres / (MetresPerFoot * MetresPerFoot);

	private static double LengthOf(CoordinateSequence seq)
	{
		double total = 0.0;
		for (int i = 1; i < seq.Count; i++)
		{
			total += Distance(seq.GetX(i - 1), seq.GetY(i - 1), seq.GetX(i), seq.GetY(i));
		}
		return total;
	}
}
