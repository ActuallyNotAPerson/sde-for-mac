using System.Security.Cryptography;
using System.Text.Json;
using NetTopologySuite.Geometries;
using Sde.Data;
using Sde.Gis.Features;
using Sde.Gis.Formats;

namespace Sde.Mcp.Tests;

/// <summary>list_layers and query_layer over the layers GeoPackage beside the database, which they never change.</summary>
public sealed class LayerToolTests
{
	private static readonly GeometryFactory Wgs84 = new GeometryFactory(new PrecisionModel(), 4326);

	/// <summary>Three made-up parcels in a row (west to east), two in zone AE, and a flood zone table not on the map.</summary>
	private static void AddLayers(TempDatabase db, Guid? stampedFor = null)
	{
		LayersStore layers = LayersStore.Open(db.Database.LayersPath, stampedFor ?? db.Database.DatabaseId);
		FeatureLayer parcels = new FeatureLayer("Parcels", GeometryKind.Polygon);
		parcels.AddField("PARCEL_ID", FieldType.Text);
		parcels.AddField("ZONE", FieldType.Text);
		parcels.AddField("ACRES", FieldType.Real);
		parcels.AddField("SURVEYED", FieldType.Date);
		string[] zones = { "AE", "X", "AE" };
		for (int i = 0; i < 3; i++)
		{
			double west = -84.30 + i * 0.01;
			Feature f = parcels.AddFeature(Square(west, 39.70, 0.005));
			f["PARCEL_ID"] = $"P-{i + 1:000}";
			f["ZONE"] = zones[i];
			f["ACRES"] = 0.25 * (i + 1);
			f["SURVEYED"] = new DateTime(2025, 5, i + 1);
		}
		layers.WriteTable("parcels", parcels);
		FeatureLayer zonesLayer = new FeatureLayer("Flood zones", GeometryKind.Polygon);
		zonesLayer.AddField("FLD_ZONE", FieldType.Text);
		// A detailed shape: a circle of 2,500 points.
		Coordinate[] ring = Enumerable.Range(0, 2500).Select(k => new Coordinate(-84.25 + 0.01 * Math.Cos(k * 2 * Math.PI / 2500), 39.70 + 0.01 * Math.Sin(k * 2 * Math.PI / 2500)))
			.Append(new Coordinate(-84.24, 39.70)).ToArray();
		zonesLayer.AddFeature(Wgs84.CreatePolygon(ring))["FLD_ZONE"] = "AE";
		layers.WriteTable("flood_zones", zonesLayer);

		db.Store.SaveMapLayer(new MapLayerRecord { Name = "Structures", SourceKind = MapLayerKinds.Structures, SortOrder = 10 });
		db.Store.SaveMapLayer(new MapLayerRecord { Name = "Parcels", SourceKind = MapLayerKinds.File, Source = "/Volumes/GIS/parcels.shp", TableName = "parcels", GeometryType = "Polygon", SortOrder = 5 });
		db.Store.SaveMapLayer(new MapLayerRecord { Name = "USGS Topo", SourceKind = MapLayerKinds.Basemap, Source = "usgs-topo", SortOrder = 1 });
		db.Store.SaveMapLayer(new MapLayerRecord { Name = "County parcels", SourceKind = MapLayerKinds.ArcGisRest, Source = "https://gis.example.org/arcgis/rest/services/Parcels/FeatureServer/0?token=SECRET123", SortOrder = 2 });
	}

	private static Polygon Square(double west, double south, double size) => Wgs84.CreatePolygon(new[]
	{
		new Coordinate(west, south), new Coordinate(west + size, south), new Coordinate(west + size, south + size), new Coordinate(west, south + size), new Coordinate(west, south),
	});

	private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

	[Fact]
	public async Task A_database_without_a_layers_file_lists_only_its_map_layers_and_makes_no_file()
	{
		await using McpSession s = await McpSession.StartAsync();
		s.Store.SaveMapLayer(new MapLayerRecord { Name = "Structures", SourceKind = MapLayerKinds.Structures });
		JsonElement list = await s.CallOkAsync("list_layers");
		Assert.Equal(s.Db.Database.FilePath, list.Str("database"));
		Assert.Null(list.Str("layersFile"));
		JsonElement structures = Assert.Single(list.List("layers"));
		Assert.False(structures.GetProperty("queryable").GetBoolean());
		Assert.Contains("find_structures", structures.Str("note"));
		Assert.Contains("find_structures", await s.CallErrorAsync("query_layer", new { layer = "Structures" }));
		Assert.Contains("list_layers", await s.CallErrorAsync("query_layer", new { layer = "Parcels" }));
		Assert.False(File.Exists(s.Db.Database.LayersPath));
	}

	[Fact]
	public async Task List_layers_names_each_layer_with_its_fields_and_leaves_the_file_alone()
	{
		await using McpSession s = await McpSession.StartAsync();
		AddLayers(s.Db);
		string before = Hash(s.Db.Database.LayersPath);

		JsonElement list = await s.CallOkAsync("list_layers");
		Assert.Equal(s.Db.Database.LayersPath, list.Str("layersFile"));
		Assert.Null(list.Str("warning"));
		Assert.Equal(new[] { "Structures", "Parcels", "County parcels", "USGS Topo", "Flood zones" }, list.List("layers").Select(l => l.Str("name")));
		JsonElement parcels = list.List("layers")[1];
		Assert.Equal("File", parcels.Str("kind"));
		Assert.Equal("parcels", parcels.Str("table"));
		Assert.Equal(3, parcels.Int("featureCount"));
		Assert.True(parcels.GetProperty("queryable").GetBoolean());
		Assert.Equal("/Volumes/GIS/parcels.shp", parcels.Str("source"));
		Assert.Equal(new[] { "PARCEL_ID", "ZONE", "ACRES", "SURVEYED" }, parcels.List("fields").Select(f => f.Str("name")));
		Assert.Equal("Real", parcels.List("fields")[2].Str("type"));
		Assert.Equal(4, parcels.List("extent").Count);
		// A web layer's address is given without its query, which can hold an access token.
		Assert.Equal("https://gis.example.org/arcgis/rest/services/Parcels/FeatureServer/0", list.List("layers")[2].Str("source"));
		Assert.Contains("aren't in the layers file", list.List("layers")[2].Str("note"));
		Assert.DoesNotContain("SECRET123", JsonSerializer.Serialize(list));
		Assert.Contains("no features", list.List("layers")[3].Str("note"));
		JsonElement zones = list.List("layers")[4];
		Assert.Equal("Table", zones.Str("kind"));
		Assert.Contains("not on the map", zones.Str("note"));

		await s.CallOkAsync("query_layer", new { layer = "Parcels", includeGeometry = true });
		Assert.Equal(before, Hash(s.Db.Database.LayersPath));
	}

	[Fact]
	public async Task Layers_saved_with_another_database_are_warned_about()
	{
		await using McpSession s = await McpSession.StartAsync();
		AddLayers(s.Db, stampedFor: Guid.NewGuid());
		Assert.Contains("different SDE database", (await s.CallOkAsync("list_layers")).Str("warning"));
	}

	[Fact]
	public async Task A_page_of_long_values_is_shortened_to_stay_under_the_result_limit()
	{
		await using McpSession s = await McpSession.StartAsync();
		FeatureLayer notes = new FeatureLayer("Notes", GeometryKind.Point);
		for (int c = 0; c < 20; c++)
		{
			notes.AddField("NOTE_" + c, FieldType.Text);
		}
		for (int i = 0; i < 100; i++)
		{
			Feature f = notes.AddFeature(Wgs84.CreatePoint(new Coordinate(-84.2 + i * 0.001, 39.7)));
			for (int c = 0; c < 20; c++)
			{
				f["NOTE_" + c] = new string((char)('a' + c % 26), 500);
			}
		}
		LayersStore.Open(s.Db.Database.LayersPath, s.Db.Database.DatabaseId).WriteTable("notes", notes);

		CallToolResultText result = new CallToolResultText(await s.CallAsync("query_layer", new { layer = "notes", pageSize = 100 }));
		Assert.True(result.Text.Length < 150_000, $"The result is {result.Text.Length:N0} characters.");
		JsonElement page = result.Json;
		Assert.Equal(100, page.List("features").Count);
		Assert.Contains("were cut to", page.Str("note"));
		Assert.All(page.List("features")[0].GetProperty("attributes").EnumerateObject(), v => Assert.True(v.Value.GetString().Length < 60));
	}

	/// <summary>A tool result's text and structured content.</summary>
	private sealed class CallToolResultText(ModelContextProtocol.Protocol.CallToolResult result)
	{
		public string Text { get; } = McpSession.Text(result);

		public JsonElement Json { get; } = result.StructuredContent.Value;
	}

	[Fact]
	public async Task Query_layer_finds_features_by_value_and_area_a_page_at_a_time()
	{
		await using McpSession s = await McpSession.StartAsync();
		AddLayers(s.Db);

		JsonElement all = await s.CallOkAsync("query_layer", new { layer = "parcels" });
		Assert.Equal("Parcels", all.Str("layer"));
		Assert.Equal("parcels", all.Str("table"));
		Assert.Equal(3, all.Int("total"));
		JsonElement first = all.List("features")[0];
		Assert.Equal("P-001", first.GetProperty("attributes").Str("PARCEL_ID"));
		Assert.Equal(0.25m, first.GetProperty("attributes").Dec("ACRES"));
		Assert.Equal("2025-05-01", first.GetProperty("attributes").Str("SURVEYED"));
		Assert.Equal(4, first.List("bbox").Count);
		Assert.False(first.TryGetProperty("geometry", out _));

		JsonElement ae = await s.CallOkAsync("query_layer", new { layer = "Parcels", where = new[] { new { field = "ZONE", @operator = "=", value = "AE" } } });
		Assert.Equal(new[] { "P-001", "P-003" }, ae.List("features").Select(f => f.GetProperty("attributes").Str("PARCEL_ID")));
		JsonElement either = await s.CallOkAsync("query_layer", new
		{
			layer = "Parcels", match = "any",
			where = new[] { new { field = "ACRES", @operator = ">=", value = "0.75" }, new { field = "PARCEL_ID", @operator = "starts with", value = "p-001" } },
		});
		Assert.Equal(2, either.Int("total"));

		// Only the middle parcel is in this area.
		JsonElement area = await s.CallOkAsync("query_layer", new { layer = "Parcels", bbox = new[] { -84.2905, 39.69, -84.2855, 39.71 }, fields = new[] { "zone" } });
		JsonElement middle = Assert.Single(area.List("features"));
		Assert.Equal("X", middle.GetProperty("attributes").Str("ZONE"));
		Assert.False(middle.GetProperty("attributes").TryGetProperty("PARCEL_ID", out _));

		JsonElement shaped = await s.CallOkAsync("query_layer", new { layer = "Parcels", includeGeometry = true, pageSize = 2 });
		Assert.Equal(2, shaped.List("features").Count);
		Assert.Contains("page = 2", shaped.Str("next"));
		JsonElement geometry = shaped.List("features")[0].GetProperty("geometry");
		Assert.Equal("Polygon", geometry.Str("type"));
		Assert.Equal(5, geometry.GetProperty("coordinates")[0].GetArrayLength());
		Assert.Equal(-84.3, geometry.GetProperty("coordinates")[0][0][0].GetDouble(), 6);

		// A very detailed shape is left out, with its bounding box kept.
		JsonElement circle = Assert.Single((await s.CallOkAsync("query_layer", new { layer = "flood_zones", includeGeometry = true })).List("features"));
		Assert.False(circle.TryGetProperty("geometry", out _));
		Assert.Contains("2,501 points", circle.Str("geometryNote"));
		Assert.Equal(4, circle.List("bbox").Count);

		Assert.Contains("PARCEL_ID, ZONE, ACRES, SURVEYED", await s.CallErrorAsync("query_layer", new { layer = "Parcels", fields = new[] { "OWNER" } }));
		Assert.Contains("no field called OWNER", await s.CallErrorAsync("query_layer", new { layer = "Parcels", where = new[] { new { field = "OWNER", @operator = "=", value = "x" } } }));
		Assert.Contains("isn't an operator", await s.CallErrorAsync("query_layer", new { layer = "Parcels", where = new[] { new { field = "ZONE", @operator = "like", value = "A%" } } }));
		Assert.Contains("can't be compared", await s.CallErrorAsync("query_layer", new { layer = "Parcels", where = new[] { new { field = "ACRES", @operator = ">", value = "big" } } }));
		Assert.Contains("west, south, east, north", await s.CallErrorAsync("query_layer", new { layer = "Parcels", bbox = new[] { -84.0, 39.0, -85.0, 40.0 } }));
		Assert.Contains("tiles", await s.CallErrorAsync("query_layer", new { layer = "USGS Topo" }));
	}
}
