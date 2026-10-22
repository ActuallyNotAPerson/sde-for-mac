using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Formats;

namespace Sde.Gis.Tests;

/// <summary>GeoPackage geometry blobs, a GeoPackage made by QGIS, and GeoPackages SDE writes (checked with GDAL when it's installed).</summary>
public class GeoPackageTests
{
	[Fact]
	public void Blob_HasTheGeoPackageHeaderAndEnvelope()
	{
		Polygon square = GisSetup.Factory.CreatePolygon(new[] { new Coordinate(-84, 39), new Coordinate(-83, 39), new Coordinate(-83, 40), new Coordinate(-84, 40), new Coordinate(-84, 39) });
		byte[] blob = GeoPackageGeometry.Write(square);
		Assert.Equal((byte)'G', blob[0]);
		Assert.Equal((byte)'P', blob[1]);
		Assert.Equal(0, blob[2]);
		Assert.Equal(0x03, blob[3]);
		Assert.Equal(4326, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4)));
		Assert.Equal(-84.0, BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(8)));
		Assert.Equal(-83.0, BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(16)));
		Assert.Equal(39.0, BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(24)));
		Assert.Equal(40.0, BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(32)));
		Assert.Equal(1, blob[40]);
		Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(41)));
		Assert.True(square.EqualsExact(GeoPackageGeometry.Read(blob, out int srs)));
		Assert.Equal(4326, srs);
		Assert.True(GeoPackageGeometry.TryGetEnvelope(blob, out Envelope e));
		Assert.Equal(square.EnvelopeInternal, e);
	}

	[Fact]
	public void Blob_EmptyAndBigEndianAndNoEnvelope_AreRead()
	{
		byte[] empty = GeoPackageGeometry.Write(GisSetup.Factory.CreatePoint());
		Assert.Equal(0x11, empty[3]);
		Assert.True(GeoPackageGeometry.IsEmpty(empty));
		Assert.False(GeoPackageGeometry.TryGetEnvelope(empty, out _));

		// A big-endian blob with no envelope, as some writers make: GP, version 0, flags 0, srs 4326, then big-endian WKB.
		byte[] blob = new byte[8 + 21];
		blob[0] = (byte)'G';
		blob[1] = (byte)'P';
		BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(4), 4326);
		blob[8] = 0;
		BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(9), 1);
		BinaryPrimitives.WriteDoubleBigEndian(blob.AsSpan(13), -84.05);
		BinaryPrimitives.WriteDoubleBigEndian(blob.AsSpan(21), 39.62);
		Point p = (Point)GeoPackageGeometry.Read(blob, out int srs);
		Assert.Equal(4326, srs);
		Assert.Equal(-84.05, p.X);
		Assert.Equal(39.62, p.Y);
		Assert.True(GeoPackageGeometry.TryGetEnvelope(blob, out Envelope e));
		Assert.Equal(-84.05, e.MinX);
		Assert.Throws<FormatException>(() => GeoPackageGeometry.Read(new byte[] { 1, 2, 3 }));
	}

	[Fact]
	public void QgisGeoPackage_InStatePlane_IsReadAndConverted()
	{
		IReadOnlyList<GeoPackageTableInfo> tables = GeoPackageFormat.ListLayers(Fixtures.Town("parcels-qgis.gpkg"));
		GeoPackageTableInfo info = Assert.Single(tables);
		Assert.Equal("parcels-qgis", info.Name);
		Assert.Equal(3735, info.SrsId);
		Assert.Equal(30, info.FeatureCount);
		Assert.Equal(GeometryKind.Polygon, info.GeometryKind);

		FeatureLayer layer = GeoPackageFormat.Read(Fixtures.Town("parcels-qgis.gpkg"));
		Assert.Equal(3735, layer.SourceCoordinateSystem.EpsgCode);
		Fixtures.AssertSameParcels(layer, 1e-8);
		Assert.Equal(FieldType.Integer, layer.Fields["house_no"].Type);
		Assert.Equal(FieldType.Boolean, layer.Fields["has_garage"].Type);
		Assert.Equal(FieldType.Date, layer.Fields["survey_date"].Type);
		Feature first = layer.Features.Single(f => (string)f["parcel_id"] == "CF-0001");
		Assert.Equal(new DateTime(2024, 3, 2), first["survey_date"]);
		Assert.Equal(false, first["has_garage"]);
		Assert.Contains(layer.Features, f => (string)f["street"] == "Rue Élan");
	}

	[Fact]
	public void Write_ThenRead_GivesTheSameLayer()
	{
		using TempFolder temp = new TempFolder();
		FeatureLayer source = Fixtures.SourceParcels();
		string path = temp.Path("parcels.gpkg");
		GeoPackageFormat.Write(path, source, "parcels", "Synthetic parcels");
		Assert.False(File.Exists(path + ".tmp"));
		GeoPackageTableInfo info = Assert.Single(GeoPackageFormat.ListLayers(path));
		Assert.Equal("MULTIPOLYGON", info.GeometryType);
		Assert.Equal(4326, info.SrsId);
		// gpkg_contents' extent comes from the R-tree, whose 32-bit floats are rounded outward.
		Assert.True(info.Extent.Contains(source.GetExtent()));
		Assert.Equal(source.GetExtent().MinX, info.Extent.MinX, 1e-5);
		Assert.Equal(source.GetExtent().MaxY, info.Extent.MaxY, 1e-5);
		FeatureLayer back = GeoPackageFormat.Read(path);
		Fixtures.AssertSameParcels(back, 1e-12);
		Assert.Equal(source.Features.Select(f => f.Id), back.Features.Select(f => f.Id));
		Assert.Equal(source.Features[0]["land_value"], back.Features[0]["land_value"]);
		Assert.Equal(true, back.Features[1]["has_garage"]);

		using SqliteConnection c = new SqliteConnection($"Data Source={path};Pooling=False");
		c.Open();
		using SqliteCommand cmd = c.CreateCommand();
		cmd.CommandText = "PRAGMA application_id";
		Assert.Equal(0x47504B47L, (long)cmd.ExecuteScalar());
		cmd.CommandText = "SELECT COUNT(*) FROM rtree_parcels_geom";
		Assert.Equal(30L, (long)cmd.ExecuteScalar());
		cmd.CommandText = "SELECT extension_name FROM gpkg_extensions WHERE table_name = 'parcels'";
		Assert.Equal("gpkg_rtree_index", cmd.ExecuteScalar());
	}

	[Fact]
	public void Write_FieldNamedFid_IsRenamed()
	{
		using TempFolder temp = new TempFolder();
		FeatureLayer layer = new FeatureLayer("pts");
		layer.AddField("FID", FieldType.Integer);
		layer.AddField("Geom", FieldType.Text);
		Feature f = layer.AddFeature(GisSetup.Factory.CreatePoint(new Coordinate(-84, 39)));
		f["FID"] = 7;
		f["Geom"] = "x";
		GeoPackageFormat.Write(temp.Path("p.gpkg"), layer);
		FeatureLayer back = GeoPackageFormat.Read(temp.Path("p.gpkg"));
		Assert.Equal(7L, back.Features[0]["FID_2"]);
		Assert.Equal("x", back.Features[0]["Geom_2"]);
	}

	[Fact]
	public void SdeGeoPackage_OpensInGdal()
	{
		Assert.SkipWhen(Gdal.OgrInfo == null, "GDAL (ogrinfo) isn't installed here.");
		using TempFolder temp = new TempFolder();
		string path = temp.Path("parcels.gpkg");
		GeoPackageFormat.Write(path, Fixtures.SourceParcels(), "parcels");
		(int code, string output) = Gdal.RunOgrInfo("-so", "-al", path);
		Assert.True(code == 0, output);
		Assert.Contains("Feature Count: 30", output);
		Assert.Contains("Geometry: Multi Polygon", output);
		Assert.Contains("4326", output);
		Assert.DoesNotContain("ERROR", output);
		// GDAL's spatial filter goes through the R-tree index.
		(code, output) = Gdal.RunOgrInfo("-so", "-spat", "-84.0499", "39.6201", "-84.0497", "39.6202", path, "parcels");
		Assert.True(code == 0, output);
		Assert.Contains("Feature Count: 1", output);
	}

	[Fact]
	public void SdeGeoPackage_PassesGdalsValidator()
	{
		using TempFolder temp = new TempFolder();
		string path = temp.Path("parcels.gpkg");
		GeoPackageFormat.Write(path, Fixtures.SourceParcels(), "parcels");
		(int ExitCode, string Output)? result = Gdal.ValidateGeoPackage(path);
		Assert.SkipWhen(result == null, "QGIS (with GDAL's validate_gpkg.py) isn't installed here.");
		Assert.True(result.Value.ExitCode == 0, result.Value.Output);
	}

	[Fact]
	public void NotAGeoPackage_SaysSo()
	{
		using TempFolder temp = new TempFolder();
		File.WriteAllText(temp.Path("x.gpkg"), "hello");
		Assert.Throws<GisException>(() => GeoPackageFormat.Read(temp.Path("x.gpkg")));
		Assert.Throws<GisException>(() => GeoPackageFormat.Read(temp.Path("missing.gpkg")));
		GeoPackageFormat.Write(temp.Path("one.gpkg"), Fixtures.SourceParcels(), "parcels");
		GisException ex = Assert.Throws<GisException>(() => GeoPackageFormat.Read(temp.Path("one.gpkg"), "roads"));
		Assert.Equal("This GeoPackage has no layer called roads.", ex.Message);
	}
}
