using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Formats;
using Sde.Gis.Projections;

namespace Sde.Gis.Tests;

/// <summary>GeoJSON (RFC 7946 and GDAL's State Plane GeoJSON with a "crs" member) and CSV points and WKT.</summary>
public class GeoJsonAndCsvTests
{
	[Fact]
	public void GeoJson_PropertyTypesAreWorkedOut()
	{
		FeatureLayer layer = Fixtures.SourceParcels();
		Assert.Equal("parcels", layer.Name);
		Assert.Equal(30, layer.Features.Count);
		Assert.Equal(GeometryKind.Polygon, layer.GeometryKind);
		Assert.Equal(FieldType.Text, layer.Fields["parcel_id"].Type);
		Assert.Equal(FieldType.Integer, layer.Fields["house_no"].Type);
		Assert.Equal(FieldType.Real, layer.Fields["land_value"].Type);
		Assert.Equal(FieldType.Integer, layer.Fields["year_built"].Type);
		Assert.Equal(FieldType.Boolean, layer.Fields["has_garage"].Type);
		Assert.Equal(FieldType.Text, layer.Fields["survey_date"].Type);
		Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), layer.Features.Select(f => f.Id));
		Assert.Equal(1, ((Polygon)layer.Features[7].Geometry).NumInteriorRings);
		Assert.IsType<MultiPolygon>(layer.Features[29].Geometry);
		Assert.Null(layer.Features[6]["year_built"]);
		Assert.Equal("Rue Élan", layer.Features[6]["street"]);
	}

	[Fact]
	public void GeoJson_InStatePlaneWithACrsMember_IsConverted()
	{
		FeatureLayer layer = GeoJsonFormat.Read(Fixtures.Town("parcels-stateplane.geojson"));
		Assert.Equal(3735, layer.SourceCoordinateSystem.EpsgCode);
		// GDAL wrote the feet to 4 decimals (0.03 mm).
		Fixtures.AssertSameParcels(layer, 1e-8);
	}

	[Fact]
	public void GeoJson_WriteThenRead_KeepsShapesValuesAndIds()
	{
		FeatureLayer source = Fixtures.SourceParcels();
		string json = GeoJsonFormat.WriteString(source);
		FeatureLayer back = GeoJsonFormat.ReadString(json);
		Fixtures.AssertSameParcels(back, 1e-12);
		Assert.Equal(source.Features.Select(f => f.Id), back.Features.Select(f => f.Id));
		for (int i = 0; i < source.Features.Count; i++)
		{
			Assert.Equal(source.Features[i].GetValues(), back.Features[i].GetValues());
		}
	}

	[Fact]
	public void GeoJson_Write_WindsPolygonsCounterClockwise()
	{
		FeatureLayer layer = new FeatureLayer("cw");
		Polygon clockwise = GisSetup.Factory.CreatePolygon(new[] { new Coordinate(0, 0), new Coordinate(0, 1), new Coordinate(1, 1), new Coordinate(1, 0), new Coordinate(0, 0) });
		layer.AddFeature(clockwise);
		FeatureLayer back = GeoJsonFormat.ReadString(GeoJsonFormat.WriteString(layer));
		Assert.True(NetTopologySuite.Algorithm.Orientation.IsCCW(((Polygon)back.Features[0].Geometry).ExteriorRing.CoordinateSequence));
	}

	[Fact]
	public void GeoJson_UnknownCrs_IsRefused()
	{
		string json = "{\"type\":\"FeatureCollection\",\"crs\":{\"type\":\"name\",\"properties\":{\"name\":\"urn:ogc:def:crs:EPSG::27700\"}},\"features\":[]}";
		UnsupportedCoordinateSystemException ex = Assert.Throws<UnsupportedCoordinateSystemException>(() => GeoJsonFormat.ReadString(json));
		Assert.Equal("EPSG:27700", ex.SystemName);
	}

	[Fact]
	public void GeoJson_SingleFeatureAndBareGeometry_AreRead()
	{
		Assert.Single(GeoJsonFormat.ReadString("{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[-84.05,39.62]},\"properties\":{\"a\":1}}").Features);
		FeatureLayer bare = GeoJsonFormat.ReadString("{\"type\":\"LineString\",\"coordinates\":[[-84.05,39.62],[-84.04,39.63]]}");
		Assert.Equal(GeometryKind.Line, bare.GeometryKind);
		Assert.Throws<GisException>(() => GeoJsonFormat.ReadString("not json"));
		Assert.Throws<GisException>(() => GeoJsonFormat.ReadString("{\"type\":\"Topology\"}"));
	}

	[Fact]
	public void GeoJson_MixedPropertyTypes_BecomeText()
	{
		FeatureLayer layer = GeoJsonFormat.ReadString("{\"type\":\"FeatureCollection\",\"features\":[" +
			"{\"type\":\"Feature\",\"geometry\":null,\"properties\":{\"v\":1,\"w\":1}}," +
			"{\"type\":\"Feature\",\"geometry\":null,\"properties\":{\"v\":\"one\",\"w\":1.5}}]}");
		Assert.Equal(FieldType.Text, layer.Fields["v"].Type);
		Assert.Equal("1", layer.Features[0]["v"]);
		Assert.Equal(FieldType.Real, layer.Fields["w"].Type);
		Assert.Equal(1.0, layer.Features[0]["w"]);
	}

	[Fact]
	public void Csv_LatitudeLongitude_FoundByName()
	{
		FeatureLayer layer = CsvFormat.Read(Fixtures.Town("structures.csv"));
		Assert.Equal(GeometryKind.Point, layer.GeometryKind);
		Assert.Equal(22, layer.Features.Count);
		Assert.Equal(new[] { "1 row was left out because it had no usable location." }, layer.Warnings);
		Assert.Equal(FieldType.Text, layer.Fields["Tax code"].Type);
		Assert.Equal("000121", layer.Features[0]["Tax code"]);
		Assert.Equal(FieldType.Real, layer.Fields["First floor (ft)"].Type);
		Assert.Equal(FieldType.Real, layer.Fields["Latitude"].Type);
		Assert.Contains(layer.Features, f => (string)f["Occupancy"] == "Commercial, small");
		Point first = (Point)layer.Features[0].Geometry;
		Assert.Equal(-84.0498, first.X, 1e-9);
		Assert.Equal(39.62015, first.Y, 1e-9);
	}

	[Fact]
	public void Csv_StatePlaneXY_WithItsCoordinateSystem()
	{
		FeatureLayer lonLat = CsvFormat.Read(Fixtures.Town("structures.csv"));
		FeatureLayer xy = CsvFormat.Read(Fixtures.Town("structures-xy.csv"), new CsvOptions { CoordinateSystem = CoordinateSystem.FromEpsg(3735) });
		Assert.Equal(lonLat.Features.Count, xy.Features.Count);
		for (int i = 0; i < xy.Features.Count; i++)
		{
			Assert.Equal(lonLat.Features[i]["Structure ID"], xy.Features[i]["Structure ID"]);
			Assert.True(lonLat.Features[i].Geometry.Distance(xy.Features[i].Geometry) < 1e-8);
		}
	}

	[Fact]
	public void Csv_WktColumn_ReadsPolygons()
	{
		FeatureLayer layer = CsvFormat.Read(Fixtures.Town("parcels-wkt.csv"));
		Assert.Equal(GeometryKind.Polygon, layer.GeometryKind);
		Assert.False(layer.Fields.Contains("WKT"));
		Fixtures.AssertSameParcels(layer, 1e-9);
	}

	[Fact]
	public void Csv_ColumnsCanBeChosen_AndDelimiterIsDetected()
	{
		string text = "Name;N;E\nA;39.62;-84.05\nB;39.63;-84.04\n";
		Assert.Throws<GisException>(() => CsvFormat.ReadString(text));
		FeatureLayer layer = CsvFormat.ReadString(text, "pts", new CsvOptions { LatitudeColumn = "N", LongitudeColumn = "E" });
		Assert.Equal(2, layer.Features.Count);
		Assert.Equal(-84.04, ((Point)layer.Features[1].Geometry).X);
	}

	[Fact]
	public void Csv_NoLocationColumns_SaysWhatToDo()
	{
		GisException ex = Assert.Throws<GisException>(() => CsvFormat.ReadString("a,b\n1,2\n"));
		Assert.Equal("SDE couldn't find the location columns in this CSV. Name them Latitude and Longitude (or give a WKT column), or choose them.", ex.Message);
	}

	[Fact]
	public void Csv_Rfc4180_QuotesAndLineBreaks()
	{
		List<string[]> rows = CsvFormat.Parse("a,b\r\n\"x, y\",\"say \"\"hi\"\"\"\n\"two\nlines\",3", ',');
		Assert.Equal(3, rows.Count);
		Assert.Equal(new[] { "x, y", "say \"hi\"" }, rows[1]);
		Assert.Equal(new[] { "two\nlines", "3" }, rows[2]);
	}

	[Fact]
	public void Csv_Windows1252Text_IsDecoded()
	{
		byte[] bytes = { (byte)'L', (byte)'a', (byte)'t', (byte)',', (byte)'L', (byte)'o', (byte)'n', (byte)',', (byte)'S', (byte)'\n',
			(byte)'3', (byte)'9', (byte)',', (byte)'-', (byte)'8', (byte)'4', (byte)',', (byte)'R', (byte)'u', (byte)'e', (byte)' ', 0xC9, (byte)'l', (byte)'a', (byte)'n', (byte)'\n' };
		using TempFolder temp = new TempFolder();
		File.WriteAllBytes(temp.Path("w.csv"), bytes);
		Assert.Equal("Rue Élan", CsvFormat.Read(temp.Path("w.csv")).Features[0]["S"]);
	}
}
