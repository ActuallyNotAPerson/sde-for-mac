using Sde.Gis.Projections;

namespace Sde.Gis.Tests;

/// <summary>
/// Real .prj text (Fixtures/prj, written by GDAL 3 from the EPSG Dataset in both ESRI and OGC WKT1, plus the form
/// ArcGIS itself writes) read into coordinate systems and checked against NGS's conversions.
/// </summary>
public class PrjParserTests
{
	private static string Prj(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prj", name));

	/// <summary>File, datum, a point (lat, lon) and its NGS northing and easting in the file's unit.</summary>
	public static TheoryData<string, string, double, double, double, double> Accepted => new TheoryData<string, string, double, double, double, double>
	{
		{ "ohio-south-ftus.arcgis.prj", "NAD83", 39.75, -84.2, 641885.159, 1490539.693 },
		{ "ohio-south-ftus.esri.prj", "NAD83", 39.75, -84.2, 641885.159, 1490539.693 },
		{ "ohio-south-ftus.ogc.prj", "NAD83", 39.75, -84.2, 641885.159, 1490539.693 },
		{ "ohio-south-ftus-harn.esri.prj", "NAD83(HARN)", 39.75, -84.2, 641885.159, 1490539.693 },
		{ "ohio-south-ftus-harn.ogc.prj", "NAD83(HARN)", 39.75, -84.2, 641885.159, 1490539.693 },
		{ "ohio-south-ftus-nad83-2011.esri.prj", "NAD83(2011)", 39.75, -84.2, 641885.159, 1490539.693 },
		{ "ohio-south-ftus-nad83-2011.ogc.prj", "NAD83(2011)", 39.75, -84.2, 641885.159, 1490539.693 },
		{ "ohio-south-m.esri.prj", "NAD83", 39.75, -84.2, 195646.988, 454317.407 },
		{ "ohio-north-ftus.esri.prj", "NAD83", 41.25, -81.65, 577986.598, 2202222.967 },
		{ "ohio-north-ftus.ogc.prj", "NAD83", 41.25, -81.65, 577986.598, 2202222.967 },
		{ "ohio-north-m.ogc.prj", "NAD83", 40.85, -83.9, 132351.642, 481955.515 },
		{ "west-virginia-north-m.ogc.prj", "NAD83", 39.10, -81.55, 225174.720, 1386757.685 },
		{ "indiana-east-ftus.esri.prj", "NAD83", 39.95, -85.3, 1712695.040, 430881.355 },
		{ "indiana-east-ftus.ogc.prj", "NAD83", 39.95, -85.3, 1712695.040, 430881.355 },
		{ "new-jersey-ftus.esri.prj", "NAD83", 40.35, -74.65, 552441.875, 450320.066 },
		{ "new-jersey-m.ogc.prj", "NAD83", 40.35, -74.65, 168384.620, 137257.831 },
		{ "north-carolina-ftus.esri.prj", "NAD83", 35.6, -78.9, 673314.581, 2029726.620 },
		{ "california-iii-ftus.esri.prj", "NAD83", 37.8, -121.9, 2116785.473, 6157187.944 },
		{ "arizona-central-ft.esri.prj", "NAD83", 33.4, -112.1, 873114.614, 644049.646 },
		{ "arizona-central-ft.ogc.prj", "NAD83", 33.4, -112.1, 873114.614, 644049.646 },
		{ "oregon-north-ft.esri.prj", "NAD83", 45.3, -122.7, 603174.486, 7636101.094 },
		{ "nad83-utm17n.esri.prj", "NAD83", 41.25, -81.65, 4566713.720, 445540.926 },
		{ "wgs84-utm17n.ogc.prj", "WGS 84", 41.25, -81.65, 4566713.720, 445540.926 },
	};

	[Theory]
	[MemberData(nameof(Accepted))]
	public void StatePlaneAndUtm_ReadAndConvert(string file, string datum, double lat, double lon, double northing, double easting)
	{
		CoordinateSystem cs = CoordinateSystem.FromWkt(Prj(file));
		Assert.False(cs.IsGeographic);
		Assert.Equal(datum, cs.Datum);
		(double x, double y) = cs.FromWgs84(lon, lat);
		// 5 mm: NGS publishes to the millimetre in each unit, and GDAL writes the parallels with 15 significant digits.
		Assert.Equal(easting, x, 0.005);
		Assert.Equal(northing, y, 0.005);
		(double lon2, double lat2) = cs.ToWgs84(easting, northing);
		Assert.Equal(lon, lon2, 1e-7);
		Assert.Equal(lat, lat2, 1e-7);
	}

	[Fact]
	public void UnitsAreRecognised()
	{
		Assert.Same(LinearUnit.UsSurveyFoot, CoordinateSystem.FromWkt(Prj("ohio-south-ftus.arcgis.prj")).Unit);
		Assert.Same(LinearUnit.UsSurveyFoot, CoordinateSystem.FromWkt(Prj("ohio-south-ftus.ogc.prj")).Unit);
		Assert.Same(LinearUnit.InternationalFoot, CoordinateSystem.FromWkt(Prj("arizona-central-ft.esri.prj")).Unit);
		Assert.Same(LinearUnit.Metre, CoordinateSystem.FromWkt(Prj("ohio-south-m.esri.prj")).Unit);
	}

	[Fact]
	public void Methods_AreRecognised()
	{
		Assert.Equal(ProjectionMethod.LambertConformalConic2SP, CoordinateSystem.FromWkt(Prj("ohio-south-ftus.esri.prj")).Definition.Method);
		Assert.Equal(ProjectionMethod.LambertConformalConic2SP, CoordinateSystem.FromWkt(Prj("ohio-south-ftus.ogc.prj")).Definition.Method);
		Assert.Equal(ProjectionMethod.TransverseMercator, CoordinateSystem.FromWkt(Prj("indiana-east-ftus.esri.prj")).Definition.Method);
		Assert.Equal(ProjectionMethod.AlbersEqualArea, CoordinateSystem.FromWkt(Prj("conus-albers.esri.prj")).Definition.Method);
		Assert.Equal(ProjectionMethod.AlbersEqualArea, CoordinateSystem.FromWkt(Prj("conus-albers.ogc.prj")).Definition.Method);
		Assert.Equal(ProjectionMethod.WebMercator, CoordinateSystem.FromWkt(Prj("web-mercator.esri.prj")).Definition.Method);
		Assert.Equal(ProjectionMethod.WebMercator, CoordinateSystem.FromWkt(Prj("web-mercator.ogc.prj")).Definition.Method);
		Assert.Equal(3735, CoordinateSystem.FromWkt(Prj("ohio-south-ftus.ogc.prj")).EpsgCode);
	}

	[Fact]
	public void AlbersAndWebMercator_FromPrj_MatchTheEpsgTable()
	{
		foreach ((string file, int epsg) in new[] { ("conus-albers.esri.prj", 5070), ("conus-albers.ogc.prj", 5070), ("web-mercator.esri.prj", 3857), ("web-mercator.ogc.prj", 3857) })
		{
			CoordinateSystem fromPrj = CoordinateSystem.FromWkt(Prj(file));
			CoordinateSystem fromTable = CoordinateSystem.FromEpsg(epsg);
			(double x1, double y1) = fromPrj.FromWgs84(-84.2, 39.75);
			(double x2, double y2) = fromTable.FromWgs84(-84.2, 39.75);
			Assert.Equal(x2, x1, 1e-6);
			Assert.Equal(y2, y1, 1e-6);
		}
		// PROJ 9 gives 999452.5703, 1921093.7563 for this point in EPSG:5070.
		(double x, double y) = CoordinateSystem.FromEpsg(5070).FromWgs84(-84.2, 39.75);
		Assert.Equal(999452.5703, x, 0.001);
		Assert.Equal(1921093.7563, y, 0.001);
	}

	[Theory]
	[InlineData("nad83.esri.prj", "NAD83")]
	[InlineData("nad83.ogc.prj", "NAD83")]
	[InlineData("nad83-2011.esri.prj", "NAD83(2011)")]
	[InlineData("wgs84.esri.prj", "WGS 84")]
	[InlineData("wgs84.ogc.prj", "WGS 84")]
	public void Geographic_IsTreatedAsWgs84(string file, string datum)
	{
		CoordinateSystem cs = CoordinateSystem.FromWkt(Prj(file));
		Assert.True(cs.IsGeographic);
		Assert.Equal(datum, cs.Datum);
		Assert.Equal((-84.2, 39.75), cs.ToWgs84(-84.2, 39.75));
	}

	[Theory]
	[InlineData("ohio-south-ftus-nad27.esri.prj")]
	[InlineData("ohio-south-ftus-nad27.ogc.prj")]
	[InlineData("nad27.esri.prj")]
	public void Nad27_IsRefusedInPlainEnglish(string file)
	{
		UnsupportedCoordinateSystemException ex = Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromWkt(Prj(file)));
		Assert.Equal("This file's coordinate system (NAD27) isn't supported. Reproject it to NAD83 or WGS 84 in QGIS first.", ex.Message);
		Assert.Equal("NAD27", ex.SystemName);
	}

	[Theory]
	[InlineData("ed50-utm31n.esri.prj", "European 1950")]
	[InlineData("nzgd2000-nztm.ogc.prj", "New Zealand Geodetic Datum 2000")]
	[InlineData("local-grid.prj", "Site grid, a local grid with no tie to the earth")]
	public void OtherSystems_AreRefusedByName(string file, string name)
	{
		UnsupportedCoordinateSystemException ex = Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromWkt(Prj(file)));
		Assert.Equal($"This file's coordinate system ({name}) isn't supported. Reproject it to NAD83 or WGS 84 in QGIS first.", ex.Message);
		Assert.DoesNotContain("—", ex.Message);
	}

	[Fact]
	public void Garbage_AndWkt2_AreRefused()
	{
		Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromWkt("this is not WKT"));
		Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromWkt(""));
		Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromWkt("PROJCRS[\"NAD83 / Ohio South (ftUS)\",BASEGEOGCRS[\"NAD83\"]]"));
		UnsupportedCoordinateSystemException ex = Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromWkt(
			"PROJCS[\"Odd\",GEOGCS[\"GCS_North_American_1983\",DATUM[\"D_North_American_1983\",SPHEROID[\"GRS_1980\",6378137.0,298.257222101]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Polyconic\"],PARAMETER[\"Central_Meridian\",-82.5],UNIT[\"Meter\",1.0]]"));
		Assert.Contains("Polyconic", ex.Message);
	}

	[Fact]
	public void Epsg_RefusesNad27AndUnknownCodes()
	{
		Assert.Equal("NAD27", Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromEpsg(32023)).SystemName);
		Assert.Equal("NAD27", Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromEpsg(4267)).SystemName);
		Assert.Equal("EPSG:2193", Assert.Throws<UnsupportedCoordinateSystemException>(() => CoordinateSystem.FromEpsg(2193)).SystemName);
	}

	[Fact]
	public void GeneratedEsriWkt_ReadsBackToTheSameSystem()
	{
		foreach (int code in new[] { 3735, 2965, 2223, 5070, 3857, 26917, 6551, 2857 })
		{
			CoordinateSystem cs = CoordinateSystem.FromEpsg(code);
			string wkt = cs.ToEsriWkt();
			CoordinateSystem back = CoordinateSystem.FromWkt(wkt);
			Assert.Equal(cs.Datum, back.Datum);
			Assert.Same(cs.Unit, back.Unit);
			(double x1, double y1) = cs.FromWgs84(-84.2, 39.75);
			(double x2, double y2) = back.FromWgs84(-84.2, 39.75);
			Assert.Equal(x1, x2, 1e-6);
			Assert.Equal(y1, y2, 1e-6);
		}
	}
}
