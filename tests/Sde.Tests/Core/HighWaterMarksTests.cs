using Sde.Core.Assessments;
using Sde.Core.Depth;
using Sde.Core.Records;

namespace Sde.Tests.Core;

/// <summary>Flood depth from synthetic high-water marks: the estimate, the datum rules, the proposals and the file readers.</summary>
public class HighWaterMarksTests
{
	private const double Lat = 39.7;
	private const double Lon = -84.2;

	/// <summary>A mark <paramref name="northMetres"/> north and <paramref name="eastMetres"/> east of the structure.</summary>
	private static HighWaterMark Mark(double northMetres, decimal elevation, int? quality = 2, string datum = "NAVD88", double eastMetres = 0, string id = null) => new HighWaterMark
	{
		Id = id,
		Latitude = Lat + northMetres / 111195.0,
		Longitude = Lon + eastMetres / (111195.0 * Math.Cos(Lat * Math.PI / 180)),
		ElevationFt = elevation,
		DatumName = datum,
		Quality = quality,
	};

	[Fact]
	public void OneMark_AtTheStructure_IsTheEstimate()
	{
		WaterSurfaceEstimate e = HighWaterMarks.Estimate(new[] { Mark(0, 742.31m) }, Lat, Lon);
		Assert.Equal(742.31m, e.ElevationFt);
		Assert.Equal(EstimateConfidence.Low, e.Confidence);
		Assert.Equal("From 1 mark, 0 m away.", e.Note);
		Assert.Equal(1.0, Assert.Single(e.Marks).Weight, 6);
	}

	[Fact]
	public void MarksAreWeighted_ByInverseDistanceSquared_AndByQuality()
	{
		// Equal distances and quality: the plain average.
		Assert.Equal(743m, HighWaterMarks.Estimate(new[] { Mark(100, 742m), Mark(-100, 744m) }, Lat, Lon).ElevationFt);
		// Twice as far counts a quarter as much: (742 * 1 + 747 * 0.25) / 1.25 = 743.
		Assert.Equal(743m, HighWaterMarks.Estimate(new[] { Mark(100, 742m), Mark(-200, 747m) }, Lat, Lon).ElevationFt);
		// Excellent (1.0) against poor (0.4) at the same distance: (742 * 1 + 749 * 0.4) / 1.4 = 744.
		WaterSurfaceEstimate q = HighWaterMarks.Estimate(new[] { Mark(100, 742m, quality: 1), Mark(-100, 749m, quality: 4) }, Lat, Lon);
		Assert.Equal(744m, q.ElevationFt);
		Assert.Equal(new[] { 1 / 1.4, 0.4 / 1.4 }, q.Marks.Select(m => Math.Round(m.Weight, 6)), new RoundedComparer());
	}

	private sealed class RoundedComparer : IEqualityComparer<double>
	{
		public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-6;

		public int GetHashCode(double obj) => 0;
	}

	[Fact]
	public void OnlyTheNearestMarksWithinTheRadius_AndNotTheWorstRated_AreUsed()
	{
		HighWaterMark[] marks = { Mark(50, 742m), Mark(60, 742m), Mark(70, 742m), Mark(80, 760m), Mark(1500, 700m) };
		WaterSurfaceEstimate e = HighWaterMarks.Estimate(marks, Lat, Lon, new HighWaterMarkOptions { NearestCount = 3 });
		Assert.Equal(742m, e.ElevationFt);
		Assert.Equal(3, e.Marks.Count);
		Assert.Equal(EstimateConfidence.High, e.Confidence);

		WaterSurfaceEstimate none = HighWaterMarks.Estimate(new[] { Mark(1500, 700m) }, Lat, Lon);
		Assert.Null(none.ElevationFt);
		Assert.Equal(EstimateConfidence.None, none.Confidence);
		Assert.Equal("No high-water marks within 1 km.", none.Note);

		WaterSurfaceEstimate filtered = HighWaterMarks.Estimate(new[] { Mark(10, 750m, quality: 5), Mark(100, 742m, quality: 2) }, Lat, Lon, new HighWaterMarkOptions { WorstQuality = 4 });
		Assert.Equal(742m, filtered.ElevationFt);
	}

	[Fact]
	public void MarksInAnotherDatum_OrWithNone_AreLeftOut()
	{
		WaterSurfaceEstimate e = HighWaterMarks.Estimate(new[]
		{
			Mark(20, 742.0m, datum: null), Mark(50, 742.4m), Mark(60, 741.8m, datum: "NGVD 1929"), Mark(70, 742.6m, datum: "NAVD 1988"),
		}, Lat, Lon);
		Assert.Equal("NAVD88", e.DatumName);
		Assert.Equal(VerticalDatum.Navd88, e.Datum);
		Assert.Equal(2, e.Marks.Count);
		Assert.Contains("1 mark in another datum left out.", e.Note);
		Assert.Contains("1 mark with no datum left out.", e.Note);

		WaterSurfaceEstimate unknown = HighWaterMarks.Estimate(new[] { Mark(20, 742m, datum: ""), Mark(30, 742m, datum: null), Mark(40, 742m, datum: " ") }, Lat, Lon);
		Assert.Equal(742m, unknown.ElevationFt);
		Assert.Equal(EstimateConfidence.Low, unknown.Confidence);
		Assert.EndsWith("The marks don't say which datum they use.", unknown.Note);
	}

	[Theory]
	[InlineData(150, 0.8, EstimateConfidence.High)]
	[InlineData(250, 0.8, EstimateConfidence.Medium)]
	[InlineData(150, 1.5, EstimateConfidence.Medium)]
	[InlineData(150, 2.5, EstimateConfidence.Low)]
	[InlineData(600, 0.5, EstimateConfidence.Low)]
	public void Confidence_DependsOnDistanceAndAgreement(double nearest, double spread, EstimateConfidence expected)
	{
		HighWaterMark[] marks = { Mark(nearest, 742m), Mark(nearest + 10, 742m + (decimal)spread), Mark(nearest + 20, 742m) };
		Assert.Equal(expected, HighWaterMarks.Estimate(marks, Lat, Lon).Confidence);
	}

	[Fact]
	public void Datums_AreReadHoweverTheyAreWritten()
	{
		Assert.Equal(VerticalDatum.Navd88, VerticalDatums.Parse("North American Vertical Datum of 1988"));
		Assert.Equal(VerticalDatum.Navd88, VerticalDatums.Parse("navd 88"));
		Assert.Equal(VerticalDatum.Ngvd29, VerticalDatums.Parse("NGVD 1929"));
		Assert.Equal(VerticalDatum.Ngvd29, VerticalDatums.Parse("Sea Level Datum of 1929"));
		Assert.Equal(VerticalDatum.Other, VerticalDatums.Parse("Local datum"));
		Assert.Equal(VerticalDatum.Unknown, VerticalDatums.Parse(" "));
		Assert.False(VerticalDatums.Differ("NAVD88", "NAVD 1988"));
		Assert.True(VerticalDatums.Differ("NAVD88", "NGVD29"));
		Assert.False(VerticalDatums.Differ("NAVD88", null));
		Assert.True(VerticalDatums.Differ("Local A", "Local B"));
	}

	private static PropertyRecord Structure(string datum = "NAVD88", decimal? ffe = 739.8m) => new PropertyRecord { Datum = datum, FirstFloorElevation = ffe };

	private static WaterSurfaceEstimate EstimateOf(decimal elevation, string datum = "NAVD88") => new WaterSurfaceEstimate
	{
		ElevationFt = elevation, DatumName = datum, Confidence = EstimateConfidence.High, Marks = Array.Empty<UsedMark>(),
	};

	[Fact]
	public void Proposal_GivesTheFloodElevationAndTheDepthAboveTheLowestFloor()
	{
		DepthProposal p = HighWaterMarks.Propose(EstimateOf(742.55m), Structure(), groundElevationFt: 738.4m);
		Assert.True(p.CanApply);
		Assert.Equal(742.6m, p.FloodElevationFt);
		Assert.Equal(2.8m, p.DepthAboveLowestFloorFt);
		Assert.Equal(4.2m, p.DepthAboveGroundFt);
		Assert.Empty(p.Notes);
		Assert.Equal(new[] { new FieldInputChange("Assessment.DepthOfFlood", "742.6"), new FieldInputChange("Assessment.DepthOfFloodAboveFFE", "2.8") }, p.Changes);
	}

	[Fact]
	public void Proposal_RefusesMixedDatums_UnlessTheEventHasAnOffset()
	{
		DepthProposal mixed = HighWaterMarks.Propose(EstimateOf(742.0m, "NAVD88"), Structure("NGVD 1929"));
		Assert.True(mixed.DatumMismatch);
		Assert.False(mixed.CanApply);
		Assert.Empty(mixed.Changes);
		Assert.Equal("The marks are in NAVD88 but this structure's elevations are in NGVD29. Enter the event's datum offset, then try again.", Assert.Single(mixed.Notes));

		DepthProposal converted = HighWaterMarks.Propose(EstimateOf(742.0m, "NAVD88"), Structure("NGVD 1929", 741.0m), datumOffsetFt: 0.62m);
		Assert.Equal(742.6m, converted.FloodElevationFt);
		Assert.Equal(1.6m, converted.DepthAboveLowestFloorFt);
		Assert.Equal(0.62m, converted.OffsetAppliedFt);
		Assert.Contains("Converted from NAVD88 to NGVD29 with the event's offset of +0.62 ft.", converted.Notes);

		// The offset is only for converting: with matching datums it isn't added.
		Assert.Equal(742.0m, HighWaterMarks.Propose(EstimateOf(742.0m), Structure(), datumOffsetFt: 0.62m).FloodElevationFt);
	}

	[Fact]
	public void Proposal_WithoutALowestFloor_OrADatum_SaysSo()
	{
		DepthProposal noFloor = HighWaterMarks.Propose(EstimateOf(742.0m), Structure(ffe: 0m));
		Assert.Null(noFloor.DepthAboveLowestFloorFt);
		Assert.Single(noFloor.Changes);
		Assert.Contains("The elevation of the lowest floor isn't entered, so there's no depth above it.", noFloor.Notes);

		DepthProposal below = HighWaterMarks.Propose(EstimateOf(739.0m), Structure());
		Assert.Equal(-0.8m, below.DepthAboveLowestFloorFt);
		Assert.Contains("The water stayed below the lowest floor.", below.Notes);

		Assert.Contains("This structure has no datum. The marks are in NAVD88.", HighWaterMarks.Propose(EstimateOf(742m), Structure(datum: null)).Notes);
		Assert.Contains("The marks don't say which datum they use. Check they are in NAVD88.", HighWaterMarks.Propose(EstimateOf(742m, null), Structure()).Notes);
		Assert.False(HighWaterMarks.Propose(new WaterSurfaceEstimate { Note = "No high-water marks within 1 km." }, Structure()).CanApply);
	}

	[Fact]
	public void ProposedChanges_ApplyThroughFieldInput()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		f.Property.Datum = "NAVD88";
		f.Property.FirstFloorElevation = 739.8m;
		DepthProposal p = HighWaterMarks.Propose(HighWaterMarks.Estimate(new[] { Mark(30, 742.5m), Mark(-30, 742.7m), Mark(0, 742.6m, eastMetres: 40) }, Lat, Lon), f.Property);
		FieldInput.Apply(f, p.Changes, t.Store.Reference, t.Store.Calculator);
		Assert.Equal(742.6m, f.Assessment.DepthOfFlood);
		Assert.Equal(2.8m, f.Assessment.DepthOfFloodAboveFFE);
	}

	[Fact]
	public void Csv_ReadsUsgsColumns_QuotedCells_AndReportsBadRows()
	{
		string csv = "hwm_id,latitude_dd,longitude_dd,elev_ft,vertical_datum_name,hwm_quality_name,hwm_type_name,notes\r\n"
			+ "101,39.7001,-84.2001,742.31,NAVD88,\"Good: +/- 0.10 ft\",Debris,\"seed line, east wall\"\r\n"
			+ "102,39.7002,-84.2002,742.5,,Excellent: +/- 0.05 ft,Mud,\r\n"
			+ "103,39.7003,-84.2003,,NAVD88,2,Mud,\r\n"
			+ "104,0,0,740,NAVD88,2,Mud,\r\n"
			+ ",,,,,,,\r\n";
		HighWaterMarkFile file = HighWaterMarkFiles.ReadCsv(new StringReader(csv), defaultDatum: "NAVD 1988");
		Assert.Equal(new[] { "101", "102" }, file.Marks.Select(m => m.Id));
		Assert.Equal((39.7001, -84.2001, 742.31m, "NAVD88", 2, "Debris"), (file.Marks[0].Latitude, file.Marks[0].Longitude, file.Marks[0].ElevationFt, file.Marks[0].DatumName, file.Marks[0].Quality.Value, file.Marks[0].Kind));
		Assert.Equal(("NAVD 1988", 1), (file.Marks[1].DatumName, file.Marks[1].Quality.Value));
		Assert.Equal(2, file.Skipped);
		Assert.Equal(new[] { "Row 4: no elevation.", "Row 5: the latitude and longitude aren't a place on the map." }, file.Problems);
	}

	[Fact]
	public void Csv_FindsColumnsByOtherCommonNames_OrSaysWhatIsMissing()
	{
		HighWaterMarkFile semi = HighWaterMarkFiles.ReadCsv(new StringReader("Lat;Lon;Elevation (ft);Datum;Quality\n39.7;-84.2;742;NGVD29;fair\n"));
		HighWaterMark m = Assert.Single(semi.Marks);
		Assert.Equal((742m, "NGVD29", 3, "2"), (m.ElevationFt, m.DatumName, m.Quality.Value, m.Id));

		HighWaterMarkFile missing = HighWaterMarkFiles.ReadCsv(new StringReader("x,y,height\n1,2,3\n"));
		Assert.Empty(missing.Marks);
		Assert.Equal("Couldn't find a column for elevation in feet (elev_ft or elevation).", Assert.Single(missing.Problems));
		Assert.Equal("The file is empty.", Assert.Single(HighWaterMarkFiles.ReadCsv(new StringReader("")).Problems));
	}

	[Fact]
	public void GeoJson_ReadsPoints_SkipsOtherShapes_AndRefusesOtherCoordinateSystems()
	{
		string json = """
			{ "type": "FeatureCollection", "features": [
			  { "type": "Feature", "id": 7, "geometry": { "type": "Point", "coordinates": [-84.2, 39.7, 0] },
			    "properties": { "elev_ft": 742.4, "vertical_datum_name": "NAVD88", "hwm_quality_id": "1", "hwm_label": "HWM 7" } },
			  { "type": "Feature", "geometry": { "type": "LineString", "coordinates": [[-84.2, 39.7], [-84.1, 39.7]] }, "properties": {} },
			  { "type": "Feature", "geometry": null, "properties": { "elev_ft": 1 } },
			  { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-84.21, 39.71] }, "properties": { "Elevation": "741.9" } }
			] }
			""";
		HighWaterMarkFile file = HighWaterMarkFiles.ReadGeoJson(json, "NAVD88");
		Assert.Equal(2, file.Marks.Count);
		Assert.Equal(("7", "HWM 7", 39.7, -84.2, 742.4m, 1), (file.Marks[0].Id, file.Marks[0].Label, file.Marks[0].Latitude, file.Marks[0].Longitude, file.Marks[0].ElevationFt, file.Marks[0].Quality.Value));
		Assert.Equal((741.9m, "NAVD88", (int?)null), (file.Marks[1].ElevationFt, file.Marks[1].DatumName, file.Marks[1].Quality));
		Assert.Equal(new[] { "Feature 2: not a point.", "Feature 3: not a point." }, file.Problems);

		string projected = """{ "type": "FeatureCollection", "crs": { "type": "name", "properties": { "name": "urn:ogc:def:crs:EPSG::3735" } }, "features": [] }""";
		Assert.StartsWith("The file uses another coordinate system.", Assert.Single(HighWaterMarkFiles.ReadGeoJson(projected).Problems));
		Assert.Equal("The file isn't valid GeoJSON.", Assert.Single(HighWaterMarkFiles.ReadGeoJson("{ not json").Problems));
	}

	[Fact]
	public void Read_ChoosesTheReaderByExtension()
	{
		string folder = Path.Combine(Path.GetTempPath(), "sde-hwm-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			string csv = Path.Combine(folder, "marks.csv");
			File.WriteAllText(csv, "﻿latitude,longitude,elev_ft\n39.7,-84.2,742\n");
			string geojson = Path.Combine(folder, "marks.geojson");
			File.WriteAllText(geojson, """{"type":"Feature","geometry":{"type":"Point","coordinates":[-84.2,39.7]},"properties":{"elev_ft":742}}""");
			Assert.Single(HighWaterMarkFiles.Read(csv).Marks);
			Assert.Single(HighWaterMarkFiles.Read(geojson).Marks);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
