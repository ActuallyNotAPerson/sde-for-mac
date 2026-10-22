using System.Text.Json;
using NetTopologySuite.Geometries;
using Sde.Gis.Analysis;
using Sde.Gis.Features;
using Sde.Gis.Formats;

namespace Sde.Gis.Tests;

/// <summary>Measuring, select by location, select by attribute and the structure-to-polygon join, on the Cedar Ford fixtures.</summary>
public class AnalysisTests
{
	private const double Lon0 = -84.0500;
	private const double Lat0 = 39.6200;
	private const double Dx = 0.0004;
	private const double Dy = 0.0003;

	private static double Dms(int d, int m, double s) => Math.Sign(d) * (Math.Abs(d) + m / 60.0 + s / 3600.0);

	private static Polygon Box(double x0, double y0, double x1, double y1) => GisSetup.Factory.CreatePolygon(new[]
	{
		new Coordinate(x0, y0), new Coordinate(x1, y0), new Coordinate(x1, y1), new Coordinate(x0, y1), new Coordinate(x0, y0),
	});

	[Fact]
	public void Distance_MatchesTheGeodesicReference()
	{
		// Geoscience Australia's worked example (Flinders Peak to Buninyong): 54,972.271 m.
		double d = Measure.Distance(Dms(144, 25, 29.52440), Dms(-37, 57, 3.72030), Dms(143, 55, 35.38390), Dms(-37, 39, 10.15610));
		Assert.Equal(54972.271, d, 0.001);
		// One degree of latitude from the equator on WGS 84: 110,574.389 m.
		Assert.Equal(110574.389, Measure.Distance(0, 0, 0, 1), 0.001);
		Assert.Equal(0.0, Measure.Distance(-84, 39, -84, 39));
		Assert.InRange(Measure.Distance(0, 0, 179.9, 0.1), 19_000_000, 20_100_000);
	}

	[Fact]
	public void Area_MatchesTheEllipsoidalBand()
	{
		// The area of a 1° by 1° cell from 39°N to 40°N, from the closed-form area of a band on the ellipsoid.
		const double a = 6378137.0;
		const double f = 1 / 298.257223563;
		double e2 = f * (2 - f);
		double e = Math.Sqrt(e2);
		double S(double phi)
		{
			double s = Math.Sin(phi * Math.PI / 180);
			return s / (1 - e2 * s * s) + 1 / (2 * e) * Math.Log((1 + e * s) / (1 - e * s));
		}
		double expected = a * a * (1 - e2) * (Math.PI / 180) / 2 * (S(40) - S(39));
		double area = Measure.Area(Box(-84, 39, -83, 40));
		Assert.Equal(1.0, area / expected, 1e-6);
	}

	[Fact]
	public void Area_AndLength_OfAParcel()
	{
		Polygon parcel = Box(Lon0, Lat0, Lon0 + Dx, Lat0 + Dy);
		LocalProjection local = LocalProjection.For(parcel.EnvelopeInternal);
		double flat = local.Project(parcel).Area;
		Assert.Equal(1.0, Measure.Area(parcel) / flat, 1e-4);
		Assert.Equal(local.Project(parcel).Length, Measure.Length(parcel), 0.01);
		Polygon withHole = (Polygon)Fixtures.SourceParcels().Features[7].Geometry;
		Assert.True(Measure.Area(withHole) < Measure.Area(Box(withHole.EnvelopeInternal.MinX, withHole.EnvelopeInternal.MinY, withHole.EnvelopeInternal.MaxX, withHole.EnvelopeInternal.MaxY)));
		Assert.Equal(0.0, Measure.Area(GisSetup.Factory.CreatePoint(new Coordinate(-84, 39))));
		Assert.Equal(100.0, Measure.MetresToFeet(30.48), 1e-9);
		Assert.Equal(1.0, Measure.SquareMetresToAcres(4046.8564224), 1e-12);
	}

	[Fact]
	public void SelectByLocation_IntersectsWithinContains()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		FeatureLayer structures = CsvFormat.Read(Fixtures.Town("structures.csv"));
		// An AE flood zone over the first two rows of parcels.
		Polygon zone = Box(Lon0, Lat0, Math.Round(Lon0 + 6 * Dx, 7), Math.Round(Lat0 + 2 * Dy, 7));

		List<Feature> within = SpatialSelect.ByLocation(parcels.Features, new[] { zone }, SpatialRelation.Within);
		Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), within.Select(f => f.Id));

		List<Feature> touching = SpatialSelect.ByLocation(parcels.Features, new[] { zone }, SpatialRelation.Intersects);
		Assert.Equal(18, touching.Count); // rows 0 and 1, plus row 2 which shares the zone's top edge

		List<Feature> inZone = SpatialSelect.ByLocation(structures.Features, new[] { zone }, SpatialRelation.Intersects);
		Assert.All(inZone, s => Assert.True(((Point)s.Geometry).Y <= Lat0 + 2 * Dy));
		Assert.Equal(structures.Features.Count(s => zone.Covers(s.Geometry)), inZone.Count);

		List<Feature> withStructure = SpatialSelect.ByLocation(parcels.Features, structures.Features.Select(s => s.Geometry), SpatialRelation.Contains);
		Assert.Equal(parcels.Features.Count(p => structures.Features.Any(s => p.Geometry.Covers(s.Geometry))), withStructure.Count);
		Assert.DoesNotContain(withStructure, p => p.Id == 3);
	}

	[Fact]
	public void SelectWithinDistance_IsInMetresAtTheLayersLatitude()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		Point outside = GisSetup.Factory.CreatePoint(new Coordinate(Lon0 + 9 * Dx, Lat0 + 0.5 * Dy));
		// The nearest parcel edge is 3 parcel-widths west, along the same parallel.
		double gap = Measure.Distance(Lon0 + 9 * Dx, Lat0 + 0.5 * Dy, Lon0 + 6 * Dx, Lat0 + 0.5 * Dy);
		Assert.InRange(gap, 100, 105);
		Assert.Empty(SpatialSelect.ByLocation(parcels.Features, new[] { outside }, SpatialRelation.WithinDistance, gap * 0.995));
		Feature nearest = Assert.Single(SpatialSelect.ByLocation(parcels.Features, new[] { outside }, SpatialRelation.WithinDistance, gap * 1.005));
		Assert.Equal("CF-0006", nearest["parcel_id"]);

		// The same layout near the Canadian border and at the equator: a degree of longitude shrinks, metres don't.
		foreach (double lat in new[] { 0.0, 48.9 })
		{
			Polygon box = Box(-100, lat, -100 + Dx, lat + Dy);
			Point p = GisSetup.Factory.CreatePoint(new Coordinate(-100 + 4 * Dx, lat + Dy / 2));
			double d = Measure.Distance(-100 + 4 * Dx, lat + Dy / 2, -100 + Dx, lat + Dy / 2);
			Feature f = new Feature(new FieldList(), box, 1);
			Assert.Empty(SpatialSelect.ByLocation(new[] { f }, new[] { p }, SpatialRelation.WithinDistance, d * 0.995));
			Assert.Single(SpatialSelect.ByLocation(new[] { f }, new[] { p }, SpatialRelation.WithinDistance, d * 1.005));
		}
	}

	[Fact]
	public void SelectByAttribute_AndOr()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		AttributeQuery and = new AttributeQuery
		{
			Conditions = { new AttributeCondition("land_value", AttributeOperator.GreaterThan, "40000"), new AttributeCondition("zone_code", AttributeOperator.Equals, "r-1") },
		};
		// land_value = 20000.25 + 1500 n, so n >= 14; zone R-1 is columns 0 and 3.
		Assert.Equal(new long[] { 16, 19, 22, 25, 28 }, and.Select(parcels).Select(f => f.Id));

		AttributeQuery or = new AttributeQuery
		{
			Combine = AttributeCombine.Or,
			Conditions = { new AttributeCondition("parcel_id", AttributeOperator.Equals, "CF-0003"), new AttributeCondition("house_no", AttributeOperator.GreaterOrEqual, "110") },
		};
		Assert.Equal(new long[] { 3, 6, 12, 18, 24, 30 }, or.Select(parcels).Select(f => f.Id));

		Assert.Equal(6, new AttributeQuery { Conditions = { new AttributeCondition("street", AttributeOperator.Contains, "élan") } }.Select(parcels).Count);
		Assert.Equal(6, new AttributeQuery { Conditions = { new AttributeCondition("street", AttributeOperator.StartsWith, "mill") } }.Select(parcels).Count);
		Assert.Equal(new long[] { 7, 14, 21, 28 }, new AttributeQuery { Conditions = { new AttributeCondition("year_built", AttributeOperator.IsEmpty) } }.Select(parcels).Select(f => f.Id));
		Assert.Equal(26, new AttributeQuery { Conditions = { new AttributeCondition("year_built", AttributeOperator.IsNotEmpty) } }.Select(parcels).Count);
		Assert.Equal(26, new AttributeQuery { Conditions = { new AttributeCondition("year_built", AttributeOperator.LessOrEqual, "1980") } }.Select(parcels).Count);
		Assert.Equal(29, new AttributeQuery { Conditions = { new AttributeCondition("year_built", AttributeOperator.NotEquals, "1951") } }.Select(parcels).Count);
		Assert.Equal(15, new AttributeQuery { Conditions = { new AttributeCondition("has_garage", AttributeOperator.Equals, "yes") } }.Select(parcels).Count);
		Assert.Equal(30, new AttributeQuery().Select(parcels).Count);
	}

	[Fact]
	public void SelectByAttribute_DatesAndTextNumbers()
	{
		FeatureLayer parcels = GeoPackageFormat.Read(Fixtures.Town("parcels-qgis.gpkg"));
		Assert.Equal(FieldType.Date, parcels.Fields["survey_date"].Type);
		int expected = parcels.Features.Count(f => (DateTime)f["survey_date"] >= new DateTime(2024, 3, 20));
		Assert.True(expected > 0);
		Assert.Equal(expected, new AttributeQuery { Conditions = { new AttributeCondition("survey_date", AttributeOperator.GreaterOrEqual, "2024-03-20") } }.Select(parcels).Count);

		FeatureLayer structures = CsvFormat.Read(Fixtures.Town("structures.csv"));
		Assert.Single(new AttributeQuery { Conditions = { new AttributeCondition("Tax code", AttributeOperator.Equals, "000121") } }.Select(structures));
		Assert.Empty(new AttributeQuery { Conditions = { new AttributeCondition("Tax code", AttributeOperator.Equals, "121") } }.Select(structures));
		Assert.Equal(2, new AttributeQuery { Conditions = { new AttributeCondition("Tax code", AttributeOperator.GreaterThan, "140") } }.Select(structures).Count);
	}

	[Fact]
	public void SelectByAttribute_BadQueriesSaySo()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		GisException missing = Assert.Throws<GisException>(() => new AttributeQuery { Conditions = { new AttributeCondition("zonecode", AttributeOperator.Equals, "R-1") } }.Select(parcels));
		Assert.Equal("This layer has no field called zonecode.", missing.Message);
		GisException number = Assert.Throws<GisException>(() => new AttributeQuery { Conditions = { new AttributeCondition("land_value", AttributeOperator.GreaterThan, "lots") } }.Select(parcels));
		Assert.Equal("land_value holds numbers, so \"lots\" can't be compared with it.", number.Message);
	}

	[Fact]
	public void AttributeQuery_IsJsonSerialisable()
	{
		AttributeQuery query = new AttributeQuery
		{
			Combine = AttributeCombine.Or,
			Conditions = { new AttributeCondition("zone_code", AttributeOperator.Equals, "C-1"), new AttributeCondition("year_built", AttributeOperator.IsEmpty) },
		};
		string json = JsonSerializer.Serialize(query);
		Assert.Contains("\"IsEmpty\"", json);
		AttributeQuery back = JsonSerializer.Deserialize<AttributeQuery>(json);
		Assert.Equal(query.ToString(), back.ToString());
		Assert.Equal("zone_code = C-1 OR year_built is empty", back.ToString());
	}

	[Fact]
	public void SpatialJoin_FindsEachStructuresParcel()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		FeatureLayer structures = CsvFormat.Read(Fixtures.Town("structures.csv"));
		List<PointJoinResult> results = SpatialJoin.PointsInPolygons(structures.Features, parcels.Features);
		Assert.Equal(structures.Features.Count, results.Count);

		foreach (PointJoinResult r in results.Where(r => ((string)r.Point["Structure ID"]).CompareTo("S-900") < 0))
		{
			Point p = (Point)r.Point.Geometry;
			int col = (int)Math.Floor((p.X - Lon0) / Dx);
			int row = (int)Math.Floor((p.Y - Lat0) / Dy);
			int parcel = row * 6 + col + 1;
			if (parcel == 8)
			{
				// This structure sits in parcel 8's courtyard, a hole in the parcel: it is in no parcel.
				Assert.Equal(JoinOutcome.None, r.Outcome);
				continue;
			}
			Assert.Equal(JoinOutcome.InOne, r.Outcome);
			Assert.Equal($"CF-{parcel:D4}", r.Polygon["parcel_id"]);
		}
		Assert.Single(results, r => r.Point.Geometry != null && ((Point)r.Point.Geometry).X > Lon0 + Dx && ((Point)r.Point.Geometry).X < Lon0 + 2 * Dx && r.Outcome == JoinOutcome.None);

		PointJoinResult onEdge = results.Single(r => (string)r.Point["Structure ID"] == "S-900");
		Assert.Equal(JoinOutcome.InSeveral, onEdge.Outcome);
		Assert.Equal(new[] { "CF-0001", "CF-0002" }, onEdge.Matches.Select(m => (string)m["parcel_id"]).OrderBy(s => s));

		PointJoinResult outside = results.Single(r => (string)r.Point["Structure ID"] == "S-901");
		Assert.Equal(JoinOutcome.None, outside.Outcome);
		Assert.Null(outside.Polygon);
	}

	[Fact]
	public void SpatialJoin_NearestWithinTolerance()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		FeatureLayer structures = CsvFormat.Read(Fixtures.Town("structures.csv"));
		Feature outside = structures.Features.Single(f => (string)f["Structure ID"] == "S-901");

		PointJoinResult near = SpatialJoin.PointsInPolygons(new[] { outside }, parcels.Features, new SpatialJoinOptions { NearestToleranceMetres = 110 }).Single();
		Assert.Equal(JoinOutcome.Nearest, near.Outcome);
		Assert.Equal("CF-0006", near.Polygon["parcel_id"]);
		Assert.Equal(Measure.Distance(Lon0 + 9 * Dx, Lat0 + 0.5 * Dy, Lon0 + 6 * Dx, Lat0 + 0.5 * Dy), near.DistanceMetres.Value, 0.05);
		Assert.Empty(near.Matches);

		PointJoinResult far = SpatialJoin.PointsInPolygons(new[] { outside }, parcels.Features, new SpatialJoinOptions { NearestToleranceMetres = 50 }).Single();
		Assert.Equal(JoinOutcome.None, far.Outcome);
	}

	[Fact]
	public void SpatialJoin_OverlappingPolygons_TakeTheSmallest()
	{
		FieldList fields = new FieldList();
		fields.Add(new LayerField("name", FieldType.Text));
		Feature county = new Feature(fields, Box(-84.1, 39.6, -84.0, 39.7), 1);
		county["name"] = "County";
		Feature town = new Feature(fields, Box(-84.06, 39.61, -84.04, 39.63), 2);
		town["name"] = "Town";
		Feature point = new Feature(new FieldList(), GisSetup.Factory.CreatePoint(new Coordinate(-84.05, 39.62)), 1);
		Feature nowhere = new Feature(new FieldList(), null, 2);
		List<PointJoinResult> r = SpatialJoin.PointsInPolygons(new[] { point, nowhere }, new[] { county, town });
		Assert.Equal(JoinOutcome.InSeveral, r[0].Outcome);
		Assert.Equal("Town", r[0].Polygon["name"]);
		Assert.Equal(2, r[0].Matches.Count);
		Assert.Equal(JoinOutcome.None, r[1].Outcome);
	}
}
