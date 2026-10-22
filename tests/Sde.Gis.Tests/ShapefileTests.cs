using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Formats;
using Sde.Gis.Projections;

namespace Sde.Gis.Tests;

/// <summary>Shapefiles written by GDAL (State Plane feet with an ESRI .prj; a Windows-1252 .dbf) and by SDE itself.</summary>
public class ShapefileTests
{
	[Fact]
	public void StatePlaneShapefile_IsConvertedToWgs84()
	{
		FeatureLayer layer = ShapefileFormat.Read(Fixtures.Town("parcels-stateplane.shp"));
		Assert.Equal(GeometryKind.Polygon, layer.GeometryKind);
		Assert.Equal("NAD_1983_StatePlane_Ohio_South_FIPS_3402_Feet", layer.SourceCoordinateSystem.Name);
		Assert.Same(LinearUnit.UsSurveyFoot, layer.SourceCoordinateSystem.Unit);
		// 1e-7 degrees is about 1 cm.
		Fixtures.AssertSameParcels(layer, 1e-7);
		Assert.Empty(layer.Warnings);
	}

	[Fact]
	public void Attributes_KeepTheirTypesAndText()
	{
		FeatureLayer layer = ShapefileFormat.Read(Fixtures.Town("parcels-stateplane.shp"));
		Assert.Equal(FieldType.Text, layer.Fields["parcel_id"].Type);
		Assert.Equal(FieldType.Integer, layer.Fields["house_no"].Type);
		Assert.Equal(FieldType.Real, layer.Fields["land_value"].Type);
		Assert.Equal(FieldType.Date, layer.Fields["survey_dat"].Type);
		Feature first = layer.Features.Single(f => (string)f["parcel_id"] == "CF-0001");
		Assert.Equal(100L, first["house_no"]);
		Assert.Equal(21500.25, first["land_value"]);
		Assert.Equal(new DateTime(2024, 3, 2), first["survey_dat"]);
		Assert.Null(layer.Features.Single(f => (string)f["parcel_id"] == "CF-0007")["year_built"]);
		Assert.Contains(layer.Features, f => (string)f["street"] == "Rue Élan");
	}

	[Fact]
	public void Cp1252Dbf_IsDecodedFromTheCpgFile()
	{
		Assert.Equal(1252, ShapefileFormat.EncodingFromCpg(Fixtures.Town("parcels-cp1252.cpg")).CodePage);
		FeatureLayer layer = ShapefileFormat.Read(Fixtures.Town("parcels-cp1252.shp"));
		Assert.Contains(layer.Features, f => (string)f["street"] == "Rue Élan");
		Assert.Null(layer.SourceCoordinateSystem);
		Fixtures.AssertSameParcels(layer, 1e-9);
	}

	[Theory]
	[InlineData("UTF-8", 65001)]
	[InlineData("utf8", 65001)]
	[InlineData("1252", 1252)]
	[InlineData("ANSI 1252", 1252)]
	[InlineData("CP1252", 1252)]
	[InlineData("ISO 88591", 28591)]
	[InlineData("ISO-8859-1", 28591)]
	[InlineData("437", 437)]
	public void CpgNames_AreUnderstood(string text, int codePage)
	{
		using TempFolder temp = new TempFolder();
		File.WriteAllText(temp.Path("x.cpg"), text);
		Assert.Equal(codePage, ShapefileFormat.EncodingFromCpg(temp.Path("x.cpg")).CodePage);
	}

	[Fact]
	public void MissingPrj_StatePlaneCoordinates_AreRefused()
	{
		using TempFolder temp = new TempFolder();
		temp.CopyTown("parcels-stateplane", ".shp", ".shx", ".dbf");
		GisException ex = Assert.Throws<GisException>(() => ShapefileFormat.Read(temp.Path("parcels-stateplane.shp")));
		Assert.Equal("This Shapefile has no .prj file, so SDE can't tell where it is. Copy its .prj file next to it, or set its coordinate system in QGIS first.", ex.Message);
	}

	[Fact]
	public void MissingPrj_LongitudeLatitude_IsReadAsWgs84WithAWarning()
	{
		using TempFolder temp = new TempFolder();
		temp.CopyTown("parcels-cp1252", ".shp", ".shx", ".dbf", ".cpg");
		FeatureLayer layer = ShapefileFormat.Read(temp.Path("parcels-cp1252.shp"));
		Assert.Equal(30, layer.Features.Count);
		Assert.Contains(layer.Warnings, w => w.Contains("no .prj file", StringComparison.Ordinal));
	}

	[Fact]
	public void Nad27Prj_IsRefused()
	{
		using TempFolder temp = new TempFolder();
		temp.CopyTown("parcels-stateplane", ".shp", ".shx", ".dbf");
		File.Copy(Fixtures.Path("prj", "ohio-south-ftus-nad27.esri.prj"), temp.Path("parcels-stateplane.prj"));
		UnsupportedCoordinateSystemException ex = Assert.Throws<UnsupportedCoordinateSystemException>(() => ShapefileFormat.Read(temp.Path("parcels-stateplane.shp")));
		Assert.Equal("This file's coordinate system (NAD27) isn't supported. Reproject it to NAD83 or WGS 84 in QGIS first.", ex.Message);
	}

	[Fact]
	public void MissingDbf_SaysWhatToDo()
	{
		using TempFolder temp = new TempFolder();
		temp.CopyTown("parcels-stateplane", ".shp", ".shx", ".prj");
		GisException ex = Assert.Throws<GisException>(() => ShapefileFormat.Read(temp.Path("parcels-stateplane.shp")));
		Assert.Contains(".dbf", ex.Message);
	}

	[Fact]
	public void UpperCaseParts_AreFound()
	{
		using TempFolder temp = new TempFolder();
		foreach (string ext in new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" })
		{
			File.Copy(Fixtures.Town("parcels-stateplane" + ext), temp.Path("PARCELS" + ext.ToUpperInvariant()));
		}
		Assert.Equal(30, GisFile.Read(temp.Path("PARCELS.SHP")).Features.Count);
	}

	[Theory]
	[InlineData(3735)]
	[InlineData(2223)]
	[InlineData(4326)]
	public void Write_ThenRead_GivesTheSameParcels(int epsg)
	{
		using TempFolder temp = new TempFolder();
		FeatureLayer source = Fixtures.SourceParcels();
		CoordinateSystem target = CoordinateSystem.FromEpsg(epsg);
		ShapefileFormat.Write(temp.Path("out.shp"), source, target);
		Assert.Equal("UTF-8", File.ReadAllText(temp.Path("out.cpg")));
		Assert.Equal(target.IsGeographic, CoordinateSystem.FromWkt(File.ReadAllText(temp.Path("out.prj"))).IsGeographic);
		FeatureLayer back = ShapefileFormat.Read(temp.Path("out.shp"));
		Fixtures.AssertSameParcels(back, 1e-9);
		Feature élan = back.Features.First(f => (string)f["street"] == "Rue Élan");
		Assert.NotNull(élan);
		Assert.Equal(FieldType.Boolean, back.Fields["has_garage"].Type);
		Assert.Equal(FieldType.Integer, back.Fields["year_built"].Type);
		if (Gdal.OgrInfo != null)
		{
			(int code, string output) = Gdal.RunOgrInfo("-so", "-al", temp.Path("out.shp"));
			Assert.True(code == 0, output);
			Assert.Contains("Feature Count: 30", output);
		}
	}

	[Fact]
	public void Write_RefusesAMixOfKinds()
	{
		using TempFolder temp = new TempFolder();
		FeatureLayer mixed = new FeatureLayer("mixed");
		mixed.AddFeature(GisSetup.Factory.CreatePoint(new Coordinate(-84, 39)));
		mixed.AddFeature(GisSetup.Factory.CreateLineString(new[] { new Coordinate(-84, 39), new Coordinate(-84.1, 39.1) }));
		Assert.Throws<GisException>(() => ShapefileFormat.Write(temp.Path("mixed.shp"), mixed));
	}
}
