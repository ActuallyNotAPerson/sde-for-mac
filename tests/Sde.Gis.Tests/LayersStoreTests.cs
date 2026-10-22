using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Formats;

namespace Sde.Gis.Tests;

/// <summary>The layers GeoPackage beside an SDE database: DatabaseId, tables, reading by box, and transactional edits.</summary>
public class LayersStoreTests
{
	private static Polygon Square(double x, double y, double size) => GisSetup.Factory.CreatePolygon(new[]
	{
		new Coordinate(x, y), new Coordinate(x + size, y), new Coordinate(x + size, y + size), new Coordinate(x, y + size), new Coordinate(x, y),
	});

	[Fact]
	public void OpensBesideTheSdeDatabase_WithItsDatabaseId()
	{
		using TempDatabase db = new TempDatabase();
		LayersStore store = LayersStore.Open(db.Database.LayersPath, db.Database.DatabaseId);
		Assert.True(File.Exists(db.Database.LayersPath));
		Assert.EndsWith(".layers.gpkg", store.Path);
		Assert.False(store.BelongsToAnotherDatabase);
		Assert.Null(store.Warning);
		Assert.Equal(db.Database.DatabaseId.ToString("D"), store.GetMetadata(LayersStore.DatabaseIdKey));
		Assert.Empty(store.Tables());
	}

	[Fact]
	public void AnotherDatabasesLayers_GiveAWarning_UntilClaimed()
	{
		using TempFolder temp = new TempFolder();
		Guid first = Guid.NewGuid();
		Guid second = Guid.NewGuid();
		LayersStore.Open(temp.Path("a.layers.gpkg"), first);
		LayersStore other = LayersStore.Open(temp.Path("a.layers.gpkg"), second);
		Assert.True(other.BelongsToAnotherDatabase);
		Assert.Equal(first, other.StoredDatabaseId);
		Assert.Equal("These map layers were saved with a different SDE database, so they may not match this database's structures. Check them before you edit.", other.Warning);
		other.ClaimForDatabase();
		Assert.False(LayersStore.Open(temp.Path("a.layers.gpkg"), second).BelongsToAnotherDatabase);
	}

	[Fact]
	public void TableNames_AreSafeAndUnique()
	{
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("x.layers.gpkg"), Guid.NewGuid());
		Assert.Equal("parcels_2024_county", LayersStore.SafeTableName("Parcels 2024 (County)"));
		Assert.Equal("layer_2024", LayersStore.SafeTableName("2024"));
		Assert.Equal("layer_gpkg_contents", LayersStore.SafeTableName("gpkg_contents"));
		Assert.Equal("layer_rtree_x", LayersStore.SafeTableName("rtree x"));
		Assert.Equal("drop_table_x", LayersStore.SafeTableName("\"; DROP TABLE x; --"));
		store.WriteTable("parcels", Fixtures.SourceParcels());
		Assert.Equal("parcels_2", store.NewTableName("Parcels"));
		Assert.Throws<ArgumentException>(() => store.WriteTable("Bad Name", Fixtures.SourceParcels()));
	}

	[Fact]
	public void WriteReplaceAndDeleteTables()
	{
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("x.layers.gpkg"), Guid.NewGuid());
		GeoPackageTableInfo info = store.WriteTable("parcels", Fixtures.SourceParcels(), description: "From the county");
		Assert.Equal(30, info.FeatureCount);
		Assert.Equal("parcels", info.Title);
		Assert.Equal("From the county", info.Description);
		Assert.Equal(GeometryKind.Polygon, info.GeometryKind);
		Assert.Throws<GisException>(() => store.WriteTable("parcels", Fixtures.SourceParcels()));

		FeatureLayer fewer = Fixtures.SourceParcels();
		fewer.Features.RemoveRange(10, 20);
		Assert.Equal(10, store.WriteTable("parcels", fewer, replace: true).FeatureCount);
		Fixtures.AssertSameParcels(Fixtures.SourceParcels(), 0); // the source itself is unchanged
		Assert.Equal(10, store.ReadTable("parcels").Features.Count);

		store.WriteTable("structures", CsvFormat.Read(Fixtures.Town("structures.csv")));
		Assert.Equal(new[] { "parcels", "structures" }, store.Tables().Select(t => t.Name));
		Assert.True(store.DeleteTable("parcels"));
		Assert.False(store.DeleteTable("parcels"));
		Assert.Equal(new[] { "structures" }, store.Tables().Select(t => t.Name));
		using SqliteConnection c = new SqliteConnection($"Data Source={store.Path};Pooling=False");
		c.Open();
		using SqliteCommand cmd = c.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%parcels%'";
		Assert.Equal(0L, (long)cmd.ExecuteScalar());
	}

	[Fact]
	public void ReadFeatures_ByBox_MatchesAFullScan()
	{
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("x.layers.gpkg"), Guid.NewGuid());
		store.WriteTable("parcels", Fixtures.SourceParcels());
		Envelope box = new Envelope(-84.0495, -84.0483, 39.6204, 39.6209);
		FeatureLayer found = store.ReadFeatures("parcels", box);
		List<long> expected = Fixtures.SourceParcels().Features.Where(f => f.Geometry.EnvelopeInternal.Intersects(box)).Select(f => f.Id).ToList();
		Assert.NotEmpty(expected);
		Assert.Equal(expected, found.Features.Select(f => f.Id).OrderBy(i => i));
		Assert.Empty(store.ReadFeatures("parcels", new Envelope(-80, -79, 30, 31)).Features);
	}

	[Fact]
	public void ApplyEdits_InsertUpdateDelete_AndTheIndexFollows()
	{
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("x.layers.gpkg"), Guid.NewGuid());
		store.WriteTable("parcels", Fixtures.SourceParcels());
		FeatureLayer layer = store.ReadTable("parcels");

		Feature moved = layer.Features.Single(f => f.Id == 1);
		Envelope oldBox = moved.Geometry.EnvelopeInternal;
		moved.Geometry = Square(-84.0300, 39.6300, 0.0002);
		moved["zone_code"] = "C-2";
		moved["land_value"] = "99,500.5";

		Feature added = layer.NewFeature(Square(-84.0400, 39.6400, 0.0002));
		added["parcel_id"] = "CF-0100";
		added["has_garage"] = true;

		FeatureEdits edits = new FeatureEdits();
		edits.Updates.Add(moved);
		edits.Inserts.Add(added);
		edits.Deletes.Add(2);
		FeatureEditResult result = store.ApplyEdits("parcels", edits);
		Assert.Equal(1, result.Updated);
		Assert.Equal(1, result.Deleted);
		long newId = Assert.Single(result.InsertedIds);
		Assert.Equal(newId, added.Id);
		Assert.True(newId > 30);

		Assert.DoesNotContain(store.ReadFeatures("parcels", oldBox).Features, f => f.Id == 1);
		Feature reread = Assert.Single(store.ReadFeatures("parcels", new Envelope(-84.0301, -84.0299, 39.6299, 39.6301)).Features);
		Assert.Equal(1, reread.Id);
		Assert.Equal("C-2", reread["zone_code"]);
		Assert.Equal(99500.5, reread["land_value"]);
		Assert.Equal("CF-0100", Assert.Single(store.ReadFeatures("parcels", new Envelope(-84.0401, -84.0399, 39.6399, 39.6401)).Features)["parcel_id"]);
		Assert.Null(store.ReadFeature("parcels", 2));
		Assert.Equal(30, store.Table("parcels").FeatureCount);
		Assert.True(store.Table("parcels").Extent.Contains(-84.0300, 39.6302));
	}

	[Fact]
	public void ApplyEdits_IsAllOrNothing()
	{
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("x.layers.gpkg"), Guid.NewGuid());
		store.WriteTable("parcels", Fixtures.SourceParcels());
		FeatureLayer layer = store.ReadTable("parcels");
		FeatureEdits edits = new FeatureEdits();
		Feature added = layer.NewFeature(Square(-84.0400, 39.6400, 0.0002));
		added["parcel_id"] = "CF-0100";
		edits.Inserts.Add(added);
		edits.Deletes.Add(3);
		Feature gone = layer.NewFeature(Square(-84.0400, 39.6400, 0.0002), 9999);
		edits.Updates.Add(gone);
		GisException ex = Assert.Throws<GisException>(() => store.ApplyEdits("parcels", edits));
		Assert.Equal("A feature to change is no longer in the layer. Reload the layer and try again.", ex.Message);
		Assert.Equal(30, store.Table("parcels").FeatureCount);
		Assert.NotNull(store.ReadFeature("parcels", 3));
		Assert.DoesNotContain(store.ReadTable("parcels").Features, f => (string)f["parcel_id"] == "CF-0100");
	}

	[Fact]
	public void ApplyEdits_FitsGeometriesToTheTableType()
	{
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("x.layers.gpkg"), Guid.NewGuid());
		store.WriteTable("parcels", Fixtures.SourceParcels());
		Assert.Equal("MULTIPOLYGON", store.Table("parcels").GeometryType);
		FeatureLayer layer = store.ReadTable("parcels");

		FeatureEdits ok = new FeatureEdits();
		ok.Inserts.Add(layer.NewFeature(Square(-84.0400, 39.6400, 0.0002)));
		store.ApplyEdits("parcels", ok);
		Assert.IsType<MultiPolygon>(store.ReadFeature("parcels", ok.Inserts[0].Id).Geometry);

		FeatureEdits bad = new FeatureEdits();
		bad.Inserts.Add(layer.NewFeature(GisSetup.Factory.CreatePoint(new Coordinate(-84.04, 39.64))));
		GisException ex = Assert.Throws<GisException>(() => store.ApplyEdits("parcels", bad));
		Assert.Equal("This layer holds polygons, so a point can't be saved in it.", ex.Message);

		FeatureLayer points = CsvFormat.Read(Fixtures.Town("structures.csv"));
		store.WriteTable("structures", points);
		Assert.Equal("POINT", store.Table("structures").GeometryType);
		FeatureEdits two = new FeatureEdits();
		two.Inserts.Add(points.NewFeature(GisSetup.Factory.CreateMultiPoint(new[] { GisSetup.Factory.CreatePoint(new Coordinate(-84.04, 39.64)), GisSetup.Factory.CreatePoint(new Coordinate(-84.03, 39.64)) })));
		Assert.Equal("This layer holds single points, so a feature with several parts can't be saved in it.", Assert.Throws<GisException>(() => store.ApplyEdits("structures", two)).Message);
	}

	[Fact]
	public void BadValues_AreRefusedInPlainEnglish()
	{
		FeatureLayer layer = Fixtures.SourceParcels();
		Feature f = layer.Features[0];
		FormatException ex = Assert.Throws<FormatException>(() => f["house_no"] = "twelve");
		Assert.Equal("house_no needs a whole number, not \"twelve\".", ex.Message);
		f["house_no"] = "1,204";
		Assert.Equal(1204L, f["house_no"]);
		f["has_garage"] = "yes";
		Assert.Equal(true, f["has_garage"]);
	}

	[Fact]
	public void EditedStore_StillOpensInGdal()
	{
		Assert.SkipWhen(Gdal.OgrInfo == null, "GDAL (ogrinfo) isn't installed here.");
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("x.layers.gpkg"), Guid.NewGuid());
		store.WriteTable("parcels", Fixtures.SourceParcels());
		FeatureLayer layer = store.ReadTable("parcels");
		FeatureEdits edits = new FeatureEdits();
		layer.Features[0].Geometry = Square(-84.0300, 39.6300, 0.0002);
		edits.Updates.Add(layer.Features[0]);
		edits.Deletes.Add(5);
		store.ApplyEdits("parcels", edits);
		(int code, string output) = Gdal.RunOgrInfo("-so", "-spat", "-84.0301", "39.6299", "-84.0299", "39.6301", store.Path, "parcels");
		Assert.True(code == 0, output);
		Assert.Contains("Feature Count: 1", output);
		Assert.DoesNotContain("gpkg_metadata", Gdal.RunOgrInfo(store.Path).Output);
		Assert.Contains(store.DatabaseId.ToString("D"), Gdal.RunOgrInfo("-so", "-al", store.Path).Output + Gdal.RunOgrInfo("-mdd", "all", "-so", store.Path, "parcels").Output + Gdal.RunOgrInfo("-so", store.Path).Output);
		(int ExitCode, string Output)? valid = Gdal.ValidateGeoPackage(store.Path);
		if (valid != null)
		{
			Assert.True(valid.Value.ExitCode == 0, valid.Value.Output);
		}
	}

	[Fact]
	public void FiftyThousandPolygons_ReadByBoxQuickly()
	{
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("big.layers.gpkg"), Guid.NewGuid());
		FeatureLayer layer = new FeatureLayer("big parcels");
		layer.AddField("parcel_id", FieldType.Text);
		layer.AddField("value", FieldType.Real);
		const int columns = 250;
		const int rows = 200;
		const double size = 0.0003;
		for (int r = 0; r < rows; r++)
		{
			for (int c = 0; c < columns; c++)
			{
				double x = -84.10 + c * size;
				double y = 39.55 + r * size;
				Polygon p = GisSetup.Factory.CreatePolygon(new[]
				{
					new Coordinate(x, y), new Coordinate(x + size * 0.9, y), new Coordinate(x + size * 0.95, y + size * 0.5),
					new Coordinate(x + size * 0.9, y + size * 0.9), new Coordinate(x, y + size * 0.9), new Coordinate(x, y),
				});
				Feature f = layer.AddFeature(p);
				f["parcel_id"] = $"P-{r:D3}-{c:D3}";
				f["value"] = 1000.0 + r * columns + c;
			}
		}
		Assert.Equal(50000, layer.Features.Count);

		Stopwatch watch = Stopwatch.StartNew();
		store.WriteTable("big", layer);
		long write = watch.ElapsedMilliseconds;

		// A city-block-sized view (about 1% of the layer), then a quarter, then everything.
		Envelope small = new Envelope(-84.08, -84.077, 39.56, 39.563);
		Envelope quarter = new Envelope(-84.10, -84.10 + columns * size / 2, 39.55, 39.55 + rows * size / 2);
		store.ReadFeatures("big", small);
		watch.Restart();
		int smallCount = store.ReadFeatures("big", small).Features.Count;
		long smallMs = watch.ElapsedMilliseconds;
		watch.Restart();
		int quarterCount = store.ReadFeatures("big", quarter).Features.Count;
		long quarterMs = watch.ElapsedMilliseconds;
		watch.Restart();
		int all = store.ReadTable("big").Features.Count;
		long allMs = watch.ElapsedMilliseconds;

		TestContext.Current.TestOutputHelper?.WriteLine(
			$"50,000 polygons: write {write} ms; read by box: {smallCount} features in {smallMs} ms, {quarterCount} in {quarterMs} ms; read all {all} in {allMs} ms.");
		Assert.InRange(smallCount, 100, 200);
		Assert.InRange(quarterCount, 12000, 13000);
		Assert.Equal(50000, all);
		// Generous limits so slow CI machines pass; the timings above are what to look at.
		Assert.True(smallMs < 500, $"Reading a small box took {smallMs} ms.");
		Assert.True(allMs < 10000, $"Reading everything took {allMs} ms.");
	}
}
