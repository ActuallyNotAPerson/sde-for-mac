namespace Sde.Gis.Projections;

/// <summary>
/// Transverse Mercator (EPSG method 9807) with the JHS formulas of EPSG Guidance Note 7-2 section 3.2.3.1, which EPSG
/// recommends and which stay accurate to about ±40° of the central meridian. Used by UTM and the State Plane zones
/// that run north to south (Indiana, Illinois, New Jersey and others).
/// </summary>
public sealed class TransverseMercator : MapProjection
{
	private readonly double _lon0;
	private readonly double _k0;
	private readonly double _fe;
	private readonly double _fn;
	private readonly double _e;
	private readonly double _b;
	private readonly double _mo;
	private readonly double _h1, _h2, _h3, _h4;
	private readonly double _r1, _r2, _r3, _r4;

	/// <param name="ellipsoid">The ellipsoid.</param>
	/// <param name="latitudeOfOrigin">Latitude of natural origin, degrees.</param>
	/// <param name="centralMeridian">Longitude of natural origin, degrees.</param>
	/// <param name="scaleFactor">Scale factor at the natural origin (k0).</param>
	/// <param name="falseEasting">False easting, metres.</param>
	/// <param name="falseNorthing">False northing, metres.</param>
	public TransverseMercator(Ellipsoid ellipsoid, double latitudeOfOrigin, double centralMeridian, double scaleFactor, double falseEasting, double falseNorthing)
		: base(ellipsoid)
	{
		LatitudeOfOrigin = latitudeOfOrigin;
		CentralMeridian = centralMeridian;
		ScaleFactor = scaleFactor;
		FalseEasting = falseEasting;
		FalseNorthing = falseNorthing;
		_lon0 = centralMeridian * DegToRad;
		_k0 = scaleFactor;
		_fe = falseEasting;
		_fn = falseNorthing;
		_e = ellipsoid.Eccentricity;
		double f = ellipsoid.Flattening;
		double n = f / (2.0 - f);
		double n2 = n * n, n3 = n2 * n, n4 = n3 * n;
		_b = ellipsoid.SemiMajorAxis / (1.0 + n) * (1.0 + n2 / 4.0 + n4 / 64.0);
		_h1 = n / 2.0 - 2.0 / 3.0 * n2 + 5.0 / 16.0 * n3 + 41.0 / 180.0 * n4;
		_h2 = 13.0 / 48.0 * n2 - 3.0 / 5.0 * n3 + 557.0 / 1440.0 * n4;
		_h3 = 61.0 / 240.0 * n3 - 103.0 / 140.0 * n4;
		_h4 = 49561.0 / 161280.0 * n4;
		_r1 = n / 2.0 - 2.0 / 3.0 * n2 + 37.0 / 96.0 * n3 - 1.0 / 360.0 * n4;
		_r2 = 1.0 / 48.0 * n2 + 1.0 / 15.0 * n3 - 437.0 / 1440.0 * n4;
		_r3 = 17.0 / 480.0 * n3 - 37.0 / 840.0 * n4;
		_r4 = 4397.0 / 161280.0 * n4;

		double lat0 = latitudeOfOrigin * DegToRad;
		if (lat0 == 0.0)
		{
			_mo = 0.0;
		}
		else if (Math.Abs(latitudeOfOrigin - 90.0) < 1e-12)
		{
			_mo = _b * (Math.PI / 2.0);
		}
		else if (Math.Abs(latitudeOfOrigin + 90.0) < 1e-12)
		{
			_mo = _b * (-Math.PI / 2.0);
		}
		else
		{
			double qo = Math.Asinh(Math.Tan(lat0)) - _e * Math.Atanh(_e * Math.Sin(lat0));
			double xi = Math.Atan(Math.Sinh(qo));
			_mo = _b * (xi + _h1 * Math.Sin(2.0 * xi) + _h2 * Math.Sin(4.0 * xi) + _h3 * Math.Sin(6.0 * xi) + _h4 * Math.Sin(8.0 * xi));
		}
	}

	public override string MethodName => "Transverse Mercator";

	public double LatitudeOfOrigin { get; }
	public double CentralMeridian { get; }
	public double ScaleFactor { get; }
	public double FalseEasting { get; }
	public double FalseNorthing { get; }

	public override (double X, double Y) Forward(double longitude, double latitude)
	{
		double phi = latitude * DegToRad;
		double dLambda = WrapRadians(longitude * DegToRad - _lon0);
		double q = Math.Asinh(Math.Tan(phi)) - _e * Math.Atanh(_e * Math.Sin(phi));
		double beta = Math.Atan(Math.Sinh(q));
		double eta0 = Math.Atanh(Math.Cos(beta) * Math.Sin(dLambda));
		double xi0 = Math.Asin(Math.Clamp(Math.Sin(beta) * Math.Cosh(eta0), -1.0, 1.0));
		double xi = xi0
			+ _h1 * Math.Sin(2.0 * xi0) * Math.Cosh(2.0 * eta0)
			+ _h2 * Math.Sin(4.0 * xi0) * Math.Cosh(4.0 * eta0)
			+ _h3 * Math.Sin(6.0 * xi0) * Math.Cosh(6.0 * eta0)
			+ _h4 * Math.Sin(8.0 * xi0) * Math.Cosh(8.0 * eta0);
		double eta = eta0
			+ _h1 * Math.Cos(2.0 * xi0) * Math.Sinh(2.0 * eta0)
			+ _h2 * Math.Cos(4.0 * xi0) * Math.Sinh(4.0 * eta0)
			+ _h3 * Math.Cos(6.0 * xi0) * Math.Sinh(6.0 * eta0)
			+ _h4 * Math.Cos(8.0 * xi0) * Math.Sinh(8.0 * eta0);
		return (_fe + _k0 * _b * eta, _fn + _k0 * (_b * xi - _mo));
	}

	public override (double Longitude, double Latitude) Inverse(double x, double y)
	{
		double etaP = (x - _fe) / (_b * _k0);
		double xiP = ((y - _fn) + _k0 * _mo) / (_b * _k0);
		double xi0 = xiP
			- _r1 * Math.Sin(2.0 * xiP) * Math.Cosh(2.0 * etaP)
			- _r2 * Math.Sin(4.0 * xiP) * Math.Cosh(4.0 * etaP)
			- _r3 * Math.Sin(6.0 * xiP) * Math.Cosh(6.0 * etaP)
			- _r4 * Math.Sin(8.0 * xiP) * Math.Cosh(8.0 * etaP);
		double eta0 = etaP
			- _r1 * Math.Cos(2.0 * xiP) * Math.Sinh(2.0 * etaP)
			- _r2 * Math.Cos(4.0 * xiP) * Math.Sinh(4.0 * etaP)
			- _r3 * Math.Cos(6.0 * xiP) * Math.Sinh(6.0 * etaP)
			- _r4 * Math.Cos(8.0 * xiP) * Math.Sinh(8.0 * etaP);
		double beta = Math.Asin(Math.Clamp(Math.Sin(xi0) / Math.Cosh(eta0), -1.0, 1.0));
		double qp = Math.Asinh(Math.Tan(beta));
		double qpp = qp + _e * Math.Atanh(_e * Math.Tanh(qp));
		for (int i = 0; i < 15; i++)
		{
			double next = qp + _e * Math.Atanh(_e * Math.Tanh(qpp));
			bool done = Math.Abs(next - qpp) < 1e-14;
			qpp = next;
			if (done)
			{
				break;
			}
		}
		double phi = Math.Atan(Math.Sinh(qpp));
		// The same as GN 7-2's asin(tanh(η0') / cos β'), written with atan2 so it can't leave asin's domain.
		double lambda = _lon0 + Math.Atan2(Math.Sinh(eta0), Math.Cos(xi0));
		return (WrapDegrees(lambda * RadToDeg), phi * RadToDeg);
	}
}
