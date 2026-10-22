namespace Sde.Gis.Projections;

/// <summary>
/// Lambert Conic Conformal, EPSG Guidance Note 7-2 section 3.1.1: the two standard parallel variant (EPSG method 9802),
/// used by most State Plane zones that run east to west, and the one standard parallel variant (EPSG method 9801).
/// </summary>
public sealed class LambertConformalConic : MapProjection
{
	private readonly double _lonOrigin;
	private readonly double _e;
	private readonly double _a;
	private readonly double _n;
	private readonly double _f;
	private readonly double _rOrigin;
	private readonly double _k;
	private readonly double _fe;
	private readonly double _fn;

	private LambertConformalConic(Ellipsoid ellipsoid, string method, double lonOrigin, double n, double f, double rOrigin, double k, double fe, double fn)
		: base(ellipsoid)
	{
		MethodName = method;
		_lonOrigin = lonOrigin * DegToRad;
		_e = ellipsoid.Eccentricity;
		_a = ellipsoid.SemiMajorAxis;
		_n = n;
		_f = f;
		_rOrigin = rOrigin;
		_k = k;
		_fe = fe;
		_fn = fn;
	}

	/// <summary>
	/// The two standard parallel variant (EPSG 9802). When the parallels are equal the cone touches at that latitude
	/// (n = sin φ1). <paramref name="scaleFactor"/> is 1 except in rare ESRI definitions that scale the whole grid.
	/// </summary>
	/// <param name="ellipsoid">The ellipsoid.</param>
	/// <param name="latitudeOfFalseOrigin">φF, degrees.</param>
	/// <param name="longitudeOfFalseOrigin">λF, degrees.</param>
	/// <param name="standardParallel1">φ1, degrees.</param>
	/// <param name="standardParallel2">φ2, degrees.</param>
	/// <param name="eastingAtFalseOrigin">EF, metres.</param>
	/// <param name="northingAtFalseOrigin">NF, metres.</param>
	/// <param name="scaleFactor">An extra scale on the grid, normally 1.</param>
	public static LambertConformalConic TwoStandardParallels(Ellipsoid ellipsoid, double latitudeOfFalseOrigin, double longitudeOfFalseOrigin,
		double standardParallel1, double standardParallel2, double eastingAtFalseOrigin, double northingAtFalseOrigin, double scaleFactor = 1.0)
	{
		double e = ellipsoid.Eccentricity;
		double phi1 = standardParallel1 * DegToRad;
		double phi2 = standardParallel2 * DegToRad;
		double m1 = M(phi1, e);
		double t1 = T(phi1, e);
		double n;
		if (Math.Abs(standardParallel1 - standardParallel2) < 1e-10)
		{
			n = Math.Sin(phi1);
		}
		else
		{
			double m2 = M(phi2, e);
			double t2 = T(phi2, e);
			n = (Math.Log(m1) - Math.Log(m2)) / (Math.Log(t1) - Math.Log(t2));
		}
		double f = m1 / (n * Math.Pow(t1, n));
		double rF = ellipsoid.SemiMajorAxis * f * Math.Pow(T(latitudeOfFalseOrigin * DegToRad, e), n) * scaleFactor;
		return new LambertConformalConic(ellipsoid, "Lambert Conic Conformal (2SP)", longitudeOfFalseOrigin, n, f, rF, scaleFactor,
			eastingAtFalseOrigin, northingAtFalseOrigin);
	}

	/// <summary>The one standard parallel variant (EPSG 9801), with the natural origin on the standard parallel.</summary>
	/// <param name="ellipsoid">The ellipsoid.</param>
	/// <param name="latitudeOfOrigin">φO, degrees.</param>
	/// <param name="longitudeOfOrigin">λO, degrees.</param>
	/// <param name="scaleFactor">kO.</param>
	/// <param name="falseEasting">FE, metres.</param>
	/// <param name="falseNorthing">FN, metres.</param>
	public static LambertConformalConic OneStandardParallel(Ellipsoid ellipsoid, double latitudeOfOrigin, double longitudeOfOrigin,
		double scaleFactor, double falseEasting, double falseNorthing)
	{
		double e = ellipsoid.Eccentricity;
		double phi0 = latitudeOfOrigin * DegToRad;
		double n = Math.Sin(phi0);
		double t0 = T(phi0, e);
		double f = M(phi0, e) / (n * Math.Pow(t0, n));
		double r0 = ellipsoid.SemiMajorAxis * f * Math.Pow(t0, n) * scaleFactor;
		return new LambertConformalConic(ellipsoid, "Lambert Conic Conformal (1SP)", longitudeOfOrigin, n, f, r0, scaleFactor, falseEasting, falseNorthing);
	}

	public override string MethodName { get; }

	public override (double X, double Y) Forward(double longitude, double latitude)
	{
		double phi = latitude * DegToRad;
		double t = T(phi, _e);
		double r = _a * _f * Math.Pow(t, _n) * _k;
		double theta = _n * WrapRadians(longitude * DegToRad - _lonOrigin);
		return (_fe + r * Math.Sin(theta), _fn + _rOrigin - r * Math.Cos(theta));
	}

	public override (double Longitude, double Latitude) Inverse(double x, double y)
	{
		double dx = x - _fe;
		double dy = _rOrigin - (y - _fn);
		double sign = _n < 0 ? -1.0 : 1.0;
		double r = sign * Math.Sqrt(dx * dx + dy * dy);
		double t = Math.Pow(r / (_a * _k * _f), 1.0 / _n);
		double theta = Math.Atan2(sign * dx, sign * dy);
		double phi = Math.PI / 2.0 - 2.0 * Math.Atan(t);
		for (int i = 0; i < 20; i++)
		{
			double es = _e * Math.Sin(phi);
			double next = Math.PI / 2.0 - 2.0 * Math.Atan(t * Math.Pow((1.0 - es) / (1.0 + es), _e / 2.0));
			bool done = Math.Abs(next - phi) < 1e-14;
			phi = next;
			if (done)
			{
				break;
			}
		}
		double lambda = theta / _n + _lonOrigin;
		return (WrapDegrees(lambda * RadToDeg), phi * RadToDeg);
	}

	/// <summary>m = cos φ / (1 − e² sin² φ)^0.5.</summary>
	private static double M(double phi, double e)
	{
		double s = e * Math.Sin(phi);
		return Math.Cos(phi) / Math.Sqrt(1.0 - s * s);
	}

	/// <summary>t = tan(π/4 − φ/2) / [(1 − e sin φ)/(1 + e sin φ)]^(e/2).</summary>
	private static double T(double phi, double e)
	{
		double s = e * Math.Sin(phi);
		return Math.Tan(Math.PI / 4.0 - phi / 2.0) / Math.Pow((1.0 - s) / (1.0 + s), e / 2.0);
	}
}
