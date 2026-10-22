using Sde.Gis.Projections;

namespace Sde.Gis.Tests;

/// <summary>
/// The projection formulas against EPSG Guidance Note 7-2's worked examples (IOGP 373-7-2, September 2019), Snyder's
/// Albers example (USGS Professional Paper 1395, p. 292), and NGS's own State Plane and UTM conversions.
/// </summary>
public class ProjectionTests
{
	private const double UsFoot = 1200.0 / 3937.0;

	private static double Dms(int d, int m, double s) => Math.Sign(d == 0 ? 1 : d) * (Math.Abs(d) + m / 60.0 + s / 3600.0);

	[Fact]
	public void LambertConic2SP_MatchesGuidanceNoteExample_TexasSouthCentral()
	{
		// GN 7-2 section 3.1.1.1: NAD27 / Texas South Central on Clarke 1866, false origin in US survey feet.
		LambertConformalConic p = LambertConformalConic.TwoStandardParallels(Ellipsoid.Clarke1866,
			Dms(27, 50, 0), -Dms(99, 0, 0), Dms(28, 23, 0), Dms(30, 17, 0), 2000000.0 * UsFoot, 0.0);
		(double x, double y) = p.Forward(-96.0, 28.5);
		Assert.Equal(2963503.91, x / UsFoot, 0.01);
		Assert.Equal(254759.80, y / UsFoot, 0.01);
		(double lon, double lat) = p.Inverse(2963503.91 * UsFoot, 254759.80 * UsFoot);
		Assert.Equal(-96.0, lon, 1e-7);
		Assert.Equal(28.5, lat, 1e-7);
	}

	[Fact]
	public void LambertConic1SP_MatchesGuidanceNoteExample_Jamaica()
	{
		// GN 7-2 section 3.1.1.2: JAD69 / Jamaica National Grid on Clarke 1866.
		LambertConformalConic p = LambertConformalConic.OneStandardParallel(Ellipsoid.Clarke1866, 18.0, -77.0, 1.0, 250000.0, 150000.0);
		(double x, double y) = p.Forward(-Dms(76, 56, 37.26), Dms(17, 55, 55.80));
		Assert.Equal(255966.58, x, 0.01);
		Assert.Equal(142493.51, y, 0.01);
		(double lon, double lat) = p.Inverse(255966.58, 142493.51);
		Assert.Equal(-Dms(76, 56, 37.26), lon, 1e-6);
		Assert.Equal(Dms(17, 55, 55.80), lat, 1e-6);
	}

	[Fact]
	public void TransverseMercator_MatchesGuidanceNoteExample_BritishNationalGrid()
	{
		// GN 7-2 section 3.2.3.1 (JHS formulas): OSGB 1936 / British National Grid on Airy 1830.
		TransverseMercator p = new TransverseMercator(Ellipsoid.Airy1830, 49.0, -2.0, 0.9996012717, 400000.0, -100000.0);
		(double x, double y) = p.Forward(0.5, 50.5);
		Assert.Equal(577274.99, x, 0.01);
		Assert.Equal(69740.50, y, 0.01);
		(double lon, double lat) = p.Inverse(577274.99, 69740.50);
		Assert.Equal(0.5, lon, 1e-6);
		Assert.Equal(50.5, lat, 1e-6);
	}

	[Fact]
	public void WebMercator_MatchesGuidanceNoteExample()
	{
		// GN 7-2 section 3.2.1.2: WGS 84 / Pseudo-Mercator.
		(double x, double y) = WebMercator.Epsg3857.Forward(-Dms(100, 20, 0), Dms(24, 22, 54.433));
		Assert.Equal(-11169055.58, x, 0.01);
		Assert.Equal(2800000.00, y, 0.01);
		(double lon, double lat) = WebMercator.Epsg3857.Inverse(-11169055.58, 2810000.00);
		Assert.Equal(-Dms(100, 20, 0), lon, 1e-7);
		Assert.Equal(Dms(24, 27, 48.889), lat, 1e-6);
	}

	[Fact]
	public void Albers_MatchesSnyderExample()
	{
		// Snyder, Map Projections: A Working Manual, p. 292: Clarke 1866, standard parallels 29.5 and 45.5, origin 23N 96W.
		AlbersEqualArea p = new AlbersEqualArea(Ellipsoid.Clarke1866, 23.0, -96.0, 29.5, 45.5, 0.0, 0.0);
		(double x, double y) = p.Forward(-75.0, 35.0);
		Assert.Equal(1885472.7, x, 0.1);
		Assert.Equal(1535925.0, y, 0.1);
		// PROJ 9 gives 1885472.7258, 1535925.0050 for the same point.
		Assert.Equal(1885472.7258, x, 0.001);
		Assert.Equal(1535925.0050, y, 0.001);
		(double lon, double lat) = p.Inverse(x, y);
		Assert.Equal(-75.0, lon, 1e-8);
		Assert.Equal(35.0, lat, 1e-8);
	}

	/// <summary>
	/// NGS conversions from NCAT (geodesy.noaa.gov/api/ncat, NAD83(2011) in and out, retrieved 2026-10-21) at made-up
	/// points: latitude, longitude, EPSG code (metres), northing, easting, published to the millimetre.
	/// </summary>
	public static TheoryData<double, double, int, double, double> NgsStatePlaneMetres => new TheoryData<double, double, int, double, double>
	{
		{ 39.75, -84.2, 32123, 195646.988, 454317.407 },      // Ohio South 3402 (Lambert)
		{ 41.25, -81.65, 32122, 176170.668, 671238.903 },     // Ohio North 3401 (Lambert)
		{ 40.85, -83.9, 32122, 132351.642, 481955.515 },      // Ohio North 3401 (Lambert)
		{ 39.10, -81.55, 32150, 68633.392, 422684.588 },      // West Virginia North 4701 (Lambert)
		{ 39.95, -85.3, 26973, 522030.492, 131332.900 },      // Indiana East 1301 (Transverse Mercator)
		{ 40.35, -74.65, 32111, 168384.620, 137257.831 },     // New Jersey 2900 (Transverse Mercator)
		{ 35.6, -78.9, 32119, 205226.695, 618661.911 },       // North Carolina 3200 (Lambert)
		{ 37.8, -121.9, 26943, 645197.503, 1876714.639 },     // California III 0403 (Lambert)
		{ 33.4, -112.1, 26949, 266125.334, 196306.332 },      // Arizona Central 0202 (Transverse Mercator)
		{ 45.3, -122.7, 32126, 183847.583, 2327483.614 },     // Oregon North 3601 (Lambert)
	};

	[Theory]
	[MemberData(nameof(NgsStatePlaneMetres))]
	public void StatePlaneMetres_MatchNgs(double lat, double lon, int epsg, double northing, double easting)
	{
		CoordinateSystem cs = CoordinateSystem.FromEpsg(epsg);
		(double x, double y) = cs.FromWgs84(lon, lat);
		Assert.Equal(easting, x, 0.0015);
		Assert.Equal(northing, y, 0.0015);
		(double lon2, double lat2) = cs.ToWgs84(easting, northing);
		Assert.Equal(lon, lon2, 2e-8);
		Assert.Equal(lat, lat2, 2e-8);
	}

	/// <summary>The same NGS conversions in the feet each zone is published in (US survey feet, or international feet for Arizona and Oregon).</summary>
	public static TheoryData<double, double, int, double, double> NgsStatePlaneFeet => new TheoryData<double, double, int, double, double>
	{
		{ 39.75, -84.2, 3735, 641885.159, 1490539.693 },      // Ohio South, US survey feet
		{ 39.75, -84.2, 6551, 641885.159, 1490539.693 },      // the same on NAD83(2011)
		{ 41.25, -81.65, 3734, 577986.598, 2202222.967 },     // Ohio North, US survey feet
		{ 39.95, -85.3, 2965, 1712695.040, 430881.355 },      // Indiana East, US survey feet
		{ 40.35, -74.65, 3424, 552441.875, 450320.066 },      // New Jersey, US survey feet
		{ 33.4, -112.1, 2223, 873114.614, 644049.646 },       // Arizona Central, international feet
		{ 45.3, -122.7, 2269, 603174.486, 7636101.094 },      // Oregon North, international feet
	};

	[Theory]
	[MemberData(nameof(NgsStatePlaneFeet))]
	public void StatePlaneFeet_MatchNgs(double lat, double lon, int epsg, double northing, double easting)
	{
		CoordinateSystem cs = CoordinateSystem.FromEpsg(epsg);
		(double x, double y) = cs.FromWgs84(lon, lat);
		Assert.Equal(easting, x, 0.005);
		Assert.Equal(northing, y, 0.005);
	}

	/// <summary>NGS's UTM conversions for the same points (NAD83 UTM zones).</summary>
	public static TheoryData<double, double, int, double, double> NgsUtm => new TheoryData<double, double, int, double, double>
	{
		{ 39.75, -84.2, 26916, 4403760.116, 739894.088 },
		{ 41.25, -81.65, 26917, 4566713.720, 445540.926 },
		{ 40.85, -83.9, 26917, 4526154.300, 255538.209 },
		{ 40.35, -74.65, 26918, 4466663.751, 529722.833 },
		{ 37.8, -121.9, 26910, 4184194.780, 596840.252 },
		{ 33.4, -112.1, 26912, 3696172.992, 397706.266 },
	};

	[Theory]
	[MemberData(nameof(NgsUtm))]
	public void Utm_MatchesNgs(double lat, double lon, int epsg, double northing, double easting)
	{
		CoordinateSystem cs = CoordinateSystem.FromEpsg(epsg);
		(double x, double y) = cs.FromWgs84(lon, lat);
		Assert.Equal(easting, x, 0.0015);
		Assert.Equal(northing, y, 0.0015);
	}

	[Fact]
	public void EveryTableSystem_RoundTripsNearItsOrigin()
	{
		Assert.True(Epsg.ProjectedCodes.Count > 1000);
		foreach (int code in Epsg.ProjectedCodes)
		{
			CoordinateSystem cs = CoordinateSystem.FromEpsg(code);
			ProjectionDefinition d = cs.Definition;
			double lat = d.Method == ProjectionMethod.TransverseMercator && d.LatitudeOfOrigin == 0.0
				? (cs.Name.EndsWith('S') ? -30.0 : 40.0)
				: d.LatitudeOfOrigin + 1.2;
			double lon = d.CentralMeridian + 0.7;
			(double x, double y) = cs.FromWgs84(lon, lat);
			(double lon2, double lat2) = cs.ToWgs84(x, y);
			Assert.True(Math.Abs(lon2 - lon) < 1e-9 && Math.Abs(lat2 - lat) < 1e-9, $"EPSG:{code} {cs.Name} round trip gave {lon2},{lat2} for {lon},{lat}");
		}
	}

	[Fact]
	public void TransverseMercator_StaysAccurateFarFromTheCentralMeridian()
	{
		// UTM zone 17N, 9 degrees east of its central meridian: PROJ 9 (Poder/Engsager) gives these values.
		CoordinateSystem utm = CoordinateSystem.FromEpsg(32617);
		(double x, double y) = utm.FromWgs84(-72.0, 45.0);
		Assert.Equal(1209331.5874, x, 0.001);
		Assert.Equal(5022507.4923, y, 0.001);
		(double lon, double lat) = utm.ToWgs84(x, y);
		Assert.Equal(-72.0, lon, 1e-9);
		Assert.Equal(45.0, lat, 1e-9);
	}

	[Fact]
	public void LinearUnits_AreExact()
	{
		Assert.Equal(1200.0 / 3937.0, LinearUnit.UsSurveyFoot.MetresPerUnit);
		Assert.Equal(0.3048, LinearUnit.InternationalFoot.MetresPerUnit);
		Assert.Same(LinearUnit.UsSurveyFoot, LinearUnit.FromFactor("US survey foot", 0.304800609601219));
		Assert.Same(LinearUnit.UsSurveyFoot, LinearUnit.FromFactor("Foot_US", 0.3048006096012192));
		Assert.Same(LinearUnit.InternationalFoot, LinearUnit.FromFactor("Foot", 0.3048));
		Assert.Same(LinearUnit.Metre, LinearUnit.FromFactor("Meter", 1.0));
	}
}
