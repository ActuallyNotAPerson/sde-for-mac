namespace Sde.Gis.Projections;

/// <summary>
/// Albers Equal Area (EPSG method 9822), EPSG Guidance Note 7-2 section 3.1.3: used by the CONUS Albers systems (EPSG
/// 5070 and 6350), and by <see cref="Analysis.Measure"/> to measure areas on the ellipsoid.
/// </summary>
public sealed class AlbersEqualArea : MapProjection
{
	private readonly double _lon0;
	private readonly double _a;
	private readonly double _e;
	private readonly double _e2;
	private readonly double _n;
	private readonly double _c;
	private readonly double _rho0;
	private readonly double _fe;
	private readonly double _fn;
	private readonly double _qPole;

	/// <param name="ellipsoid">The ellipsoid.</param>
	/// <param name="latitudeOfOrigin">φO, the latitude of the false origin, degrees.</param>
	/// <param name="longitudeOfOrigin">λO, degrees.</param>
	/// <param name="standardParallel1">φ1, degrees.</param>
	/// <param name="standardParallel2">φ2, degrees.</param>
	/// <param name="falseEasting">EF, metres.</param>
	/// <param name="falseNorthing">NF, metres.</param>
	public AlbersEqualArea(Ellipsoid ellipsoid, double latitudeOfOrigin, double longitudeOfOrigin, double standardParallel1, double standardParallel2,
		double falseEasting, double falseNorthing)
		: base(ellipsoid)
	{
		_lon0 = longitudeOfOrigin * DegToRad;
		_a = ellipsoid.SemiMajorAxis;
		_e = ellipsoid.Eccentricity;
		_e2 = ellipsoid.EccentricitySquared;
		_fe = falseEasting;
		_fn = falseNorthing;
		double phi1 = standardParallel1 * DegToRad;
		double phi2 = standardParallel2 * DegToRad;
		double m1 = M(phi1);
		double a1 = Alpha(phi1);
		if (Math.Abs(standardParallel1 - standardParallel2) < 1e-10)
		{
			_n = Math.Sin(phi1);
		}
		else
		{
			double m2 = M(phi2);
			double a2 = Alpha(phi2);
			_n = (m1 * m1 - m2 * m2) / (a2 - a1);
		}
		_c = m1 * m1 + _n * a1;
		_rho0 = _a * Math.Sqrt(_c - _n * Alpha(latitudeOfOrigin * DegToRad)) / _n;
		_qPole = _e < 1e-12 ? 2.0 : 1.0 - (1.0 - _e2) / (2.0 * _e) * Math.Log((1.0 - _e) / (1.0 + _e));
	}

	public override string MethodName => "Albers Equal Area";

	public override (double X, double Y) Forward(double longitude, double latitude)
	{
		double theta = _n * WrapRadians(longitude * DegToRad - _lon0);
		double rho = _a * Math.Sqrt(Math.Max(0.0, _c - _n * Alpha(latitude * DegToRad))) / _n;
		return (_fe + rho * Math.Sin(theta), _fn + _rho0 - rho * Math.Cos(theta));
	}

	public override (double Longitude, double Latitude) Inverse(double x, double y)
	{
		double dx = x - _fe;
		double dy = _rho0 - (y - _fn);
		double sign = _n < 0 ? -1.0 : 1.0;
		double rho = Math.Sqrt(dx * dx + dy * dy);
		double theta = Math.Atan2(sign * dx, sign * dy);
		double alpha = (_c - rho * rho * _n * _n / (_a * _a)) / _n;
		double beta = Math.Asin(Math.Clamp(alpha / _qPole, -1.0, 1.0));
		double e4 = _e2 * _e2, e6 = e4 * _e2;
		double phi = beta
			+ (_e2 / 3.0 + 31.0 * e4 / 180.0 + 517.0 * e6 / 5040.0) * Math.Sin(2.0 * beta)
			+ (23.0 * e4 / 360.0 + 251.0 * e6 / 3780.0) * Math.Sin(4.0 * beta)
			+ 761.0 * e6 / 45360.0 * Math.Sin(6.0 * beta);
		// GN 7-2's series leaves about a millimetre; two Newton steps on α (Snyder eq. 3-16) take it to the micrometre.
		if (_e > 1e-12)
		{
			for (int i = 0; i < 2; i++)
			{
				double sin = Math.Sin(phi);
				double es = _e * sin;
				double w = 1.0 - es * es;
				phi += w * w / (2.0 * Math.Cos(phi)) * (alpha / (1.0 - _e2) - sin / w + 1.0 / (2.0 * _e) * Math.Log((1.0 - es) / (1.0 + es)));
			}
		}
		double lambda = _lon0 + theta / _n;
		return (WrapDegrees(lambda * RadToDeg), phi * RadToDeg);
	}

	private double M(double phi)
	{
		double s = _e * Math.Sin(phi);
		return Math.Cos(phi) / Math.Sqrt(1.0 - s * s);
	}

	/// <summary>α (Snyder's q) for a latitude in radians.</summary>
	private double Alpha(double phi)
	{
		double sin = Math.Sin(phi);
		if (_e < 1e-12)
		{
			return 2.0 * sin;
		}
		double es = _e * sin;
		return (1.0 - _e2) * (sin / (1.0 - es * es) - 1.0 / (2.0 * _e) * Math.Log((1.0 - es) / (1.0 + es)));
	}
}
