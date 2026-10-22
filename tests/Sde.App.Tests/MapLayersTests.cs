using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Sde.Data;
using Sde.Gis.Features;
using Sde.Gis.Formats;
using AColor = Avalonia.Media.Color;
using Point = Avalonia.Point;

namespace Sde.App.Tests;

/// <summary>A pretend ArcGIS server answering from the recorded answers in Fixtures/arcgis (made-up Cedar Ford parcels).</summary>
internal sealed class FakeArcGis : HttpMessageHandler
{
	public List<string> Urls { get; } = new();

	public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arcgis", name));

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		lock (Urls)
		{
			Urls.Add(request.RequestUri!.AbsoluteUri + (body == null ? "" : " " + body));
		}
		string query = Uri.UnescapeDataString(body ?? request.RequestUri.Query);
		string file = !request.RequestUri.AbsolutePath.EndsWith("/query", StringComparison.Ordinal) ? "parcels-layer.json"
			: query.Contains("resultOffset=0") ? "parcels-page1.geojson"
			: query.Contains("resultOffset=2") ? "parcels-page2.geojson"
			: "parcels-page3.geojson";
		return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture(file), Encoding.UTF8, "application/json") };
	}
}

/// <summary>
/// Layers on the Map page: files copied into the layers GeoPackage and listed in MapLayer, ArcGIS layers from recorded
/// answers, FEMA's flood zones as tiles, the basemap kept per database, order, opacity, removal, and the warning when
/// the layers file belongs to another database.
/// </summary>
public class MapLayersTests
{
	public static string Town(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "town", file);

	private static async Task<MapLayerViewModel> AddFile(UiSession ui, string file)
	{
		ui.Dialogs.Answers.Enqueue(new[] { Town(file) });
		await ui.Main.Map.AddFileLayerCommand.ExecuteAsync(null);
		ui.Pump();
		return ui.Main.Map.Layers.FirstOrDefault(l => l.IsFeatures && l.Record.Source == Town(file));
	}

	[AvaloniaFact]
	public async Task AFile_IsCopiedIntoTheLayersGeoPackage_ListedInMapLayer_AndDrawn()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapLayerViewModel parcels = await AddFile(ui, "parcels.geojson");

		Assert.NotNull(parcels);
		Assert.Equal("parcels", parcels.Name);
		Assert.Equal(30, parcels.Data.Features.Count);
		Assert.Equal("30 areas, from parcels.geojson", parcels.Detail);
		Assert.Equal(1, ui.Main.Map.PanelIndex);
		MapLayerRecord row = Assert.Single(ui.Store.GetMapLayers(), r => r.SourceKind == MapLayerKinds.File);
		Assert.Equal("Polygon", row.GeometryType);
		LayersStore store = LayersStore.Open(ui.Store.Database.LayersPath, ui.Store.Database.DatabaseId);
		Assert.Equal(30, store.ReadTable(row.TableName).Features.Count);
		Assert.Null(store.Warning);

		// The map zoomed to the new layer and draws it: a parcel's middle is tinted by its fill.
		Assert.InRange(view.Map.CenterLatitude, 39.61, 39.64);
		Envelope first = parcels.Data.Features[0].Geometry.EnvelopeInternal;
		WriteableBitmap frame = MapTestSupport.Capture(ui);
		Point middle = MapTestSupport.InWindow(ui, view, view.Map.ToScreen(first.Centre.Y, first.Centre.X));
		Assert.False(MapTestSupport.Near(MapTestSupport.At(frame, middle.X, middle.Y), MapTestSupport.ThemeColour("SubtleBg"), 3), "the parcel isn't drawn");
		Assert.Equal(("" + parcels.Key, parcels.Data.Features[0].Id), (view.Map.FeatureAt(view.Map.ToScreen(first.Centre.Y, first.Centre.X))?.Layer, view.Map.FeatureAt(view.Map.ToScreen(first.Centre.Y, first.Centre.X))?.Feature.Id));
	}

	[AvaloniaTheory]
	[InlineData("parcels-stateplane.shp", 30, "Polygon")]
	[InlineData("parcels-qgis.gpkg", 30, "Polygon")]
	[InlineData("structures.csv", 22, "Point")]
	[InlineData("parcels-wkt.csv", 30, "Polygon")]
	public async Task EveryKindOfFile_IsReadInLongitudeAndLatitude(string file, int count, string kind)
	{
		using UiSession ui = new UiSession();
		MapTestSupport.ShowMap(ui);
		MapLayerViewModel layer = await AddFile(ui, file);
		Assert.NotNull(layer);
		Assert.Equal(count, layer.Data.Features.Count);
		Assert.Equal(kind, layer.Record.GeometryType);
		Envelope e = layer.Data.GetExtent();
		Assert.InRange(e.MinX, -84.06, -84.03);
		Assert.InRange(e.MinY, 39.61, 39.64);
	}

	[AvaloniaFact]
	public async Task Layers_AreKeptWithTheDatabase_InOrder_WithOpacityVisibilityAndBasemap()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		MapLayerViewModel parcels = await AddFile(ui, "parcels.geojson");
		MapLayerViewModel points = await AddFile(ui, "structures.csv");
		map.AddFloodZonesCommand.Execute(null);
		Assert.Equal(new[] { "Structures", "FEMA flood zones", "structures", "parcels" }, map.Layers.Select(l => l.Name));

		points.OpacityPercent = 40;
		parcels.IsVisible = false;
		map.MoveLayerUpCommand.Execute(parcels);
		map.Basemap = Basemaps.UsgsImagery;
		Assert.Equal(new[] { "Structures", "FEMA flood zones", "parcels", "structures" }, map.Layers.Select(l => l.Name));

		// The same database opened again: the same layers, order, settings and basemap, read back from the database
		// and the layers GeoPackage.
		ui.Services.Open(ui.Store.Database.FilePath);
		ui.Main.ShowCommand.Execute(Page.Map);
		ui.Pump();
		await ui.Main.Map.LayersLoaded;
		map = ui.Main.Map;
		Assert.Equal(new[] { "Structures", "FEMA flood zones", "parcels", "structures" }, map.Layers.Select(l => l.Name));
		Assert.Equal(40, map.Layers.Single(l => l.Name == "structures").OpacityPercent);
		Assert.False(map.Layers.Single(l => l.Name == "parcels").IsVisible);
		Assert.Equal(30, map.Layers.Single(l => l.Name == "parcels").Data.Features.Count);
		Assert.Same(Basemaps.UsgsImagery, map.Basemap);
		Assert.Equal(1, ui.Store.GetMapLayers().Count(r => r.SourceKind == MapLayerKinds.Basemap));
	}

	[AvaloniaFact]
	public async Task ALayer_IsRemovedWithItsTable_AfterAsking()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.ShowMap(ui);
		MapLayerViewModel parcels = await AddFile(ui, "parcels.geojson");
		string table = parcels.Record.TableName;

		ui.Dialogs.Answers.Enqueue(false);
		await ui.Main.Map.RemoveLayerCommand.ExecuteAsync(parcels);
		Assert.Contains(parcels, ui.Main.Map.Layers);

		ui.Dialogs.Answers.Enqueue(true);
		await ui.Main.Map.RemoveLayerCommand.ExecuteAsync(parcels);
		Assert.DoesNotContain(parcels, ui.Main.Map.Layers);
		Assert.DoesNotContain(ui.Store.GetMapLayers(), r => r.LayerID == parcels.Id);
		Assert.False(LayersStore.Open(ui.Store.Database.LayersPath, ui.Store.Database.DatabaseId).Exists(table));
		// Structures can't be removed.
		Assert.False(ui.Main.Map.StructuresLayer.CanRemove);
	}

	[AvaloniaFact]
	public async Task AnArcGisLayer_IsDownloadedForTheAreaInView_WithItsCreditInTheAttributionLine()
	{
		using UiSession ui = new UiSession();
		FakeArcGis server = new FakeArcGis();
		ui.Services.HttpClientFactory = () => new HttpClient(server);
		MapView view = MapTestSupport.ShowMap(ui);
		view.Map.SetView(39.6250, -84.0480, 15);
		ui.Pump();
		MapViewModel map = ui.Main.Map;

		Task adding = map.AddOnlineLayerCommand.ExecuteAsync(null);
		OnlineLayerSheetViewModel sheet = Assert.IsType<OnlineLayerSheetViewModel>(ui.Main.Sheets.Current);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.Url = "not an address";
		await sheet.CheckCommand.ExecuteAsync(null);
		Assert.Contains("isn't an ArcGIS layer address", sheet.Problem);

		sheet.Url = "https://gis.cedarford.example/arcgis/rest/services/Parcels/FeatureServer/0/query?where=1%3D1";
		await sheet.CheckCommand.ExecuteAsync(null);
		Assert.Equal("Cedar Ford Parcels: areas, 7 fields.", sheet.InfoText);
		Assert.Equal("Credit: Cedar Ford County GIS (synthetic)", sheet.CopyrightText);
		await sheet.AddCommand.ExecuteAsync(null);
		await adding;
		Assert.Null(ui.Main.Sheets.Current);

		MapLayerViewModel layer = Assert.Single(map.Layers, l => l.Kind == MapLayerKinds.ArcGisRest);
		Assert.Equal("Cedar Ford Parcels", layer.Name);
		Assert.Equal(5, layer.Data.Features.Count);
		Assert.Equal("https://gis.cedarford.example/arcgis/rest/services/Parcels/FeatureServer/0", layer.Record.Source);
		// Only the area in view was asked for, in longitude and latitude.
		Assert.Contains(server.Urls, u => u.Contains("/query") && u.Contains("geometry=") && u.Contains("inSR=4326"));
		Assert.Contains("Cedar Ford Parcels: Cedar Ford County GIS (synthetic)", map.Attribution);
		Assert.StartsWith("Map data: U.S. Geological Survey", map.Attribution);
		layer.IsVisible = false;
		Assert.DoesNotContain("Cedar Ford", map.Attribution);
	}

	[AvaloniaFact]
	public void FemaFloodZones_AreTilesFromFemasMapService_DrawnOverTheBasemap()
	{
		FakeTiles.Requested.Clear();
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		const int Z = 15;
		view.Map.SetView(39.1031, -84.5120, Z);
		ui.Main.Map.AddFloodZonesCommand.Execute(null);
		MapTestSupport.Settle(ui, view);

		List<string> asked;
		lock (FakeTiles.Requested)
		{
			// (Only this map's: another test's map may still be finishing its downloads.)
			asked = FakeTiles.Requested.Where(u => u.Contains("NFHL") && Near(u, 39.1031, -84.5120)).ToList();
		}
		Assert.NotEmpty(asked);
		Assert.All(asked, u => Assert.Contains("&bboxSR=3857&imageSR=3857&size=256,256&dpi=96&format=png32&transparent=true&f=image&layers=show:28", u));
		// Only the tiles of the zoom level shown: FEMA's server draws each one on request.
		int n = 1 << Z;
		int tx = (int)Math.Floor(WebMercator.U(-84.5120) * n);
		int ty = (int)Math.Floor(WebMercator.V(39.1031) * n);
		Assert.Contains(asked, u => u.Contains("bbox=" + Basemap.Bbox(Z, tx, ty) + "&"));
		Assert.True(asked.All(u => Math.Abs(2 * WebMercator.HalfWorld / n - Size(u)) < 0.01), string.Join("\n", asked.Select(u => Math.Log2(2 * WebMercator.HalfWorld / Size(u)).ToString("0.###"))));
		Assert.Contains("Flood hazard zones: FEMA National Flood Hazard Layer", ui.Main.Map.Attribution);

		// The blue band of the fake tile is drawn (at 70% opacity) where the tile is.
		WriteableBitmap frame = MapTestSupport.Capture(ui);
		Point band = MapTestSupport.InWindow(ui, view, view.Map.ToScreen(WebMercator.Latitude((ty + 128.0 / 256) / n), WebMercator.Longitude((tx + 0.5) / n)));
		AColor seen = MapTestSupport.At(frame, band.X, band.Y);
		Assert.True(seen.B > seen.R + 40, $"no flood zone drawn: {seen}");
	}

	/// <summary>True when an export tile's box is within about 10 km of a place.</summary>
	private static bool Near(string url, double latitude, double longitude)
	{
		double[] v = Box(url);
		(double x, double y) = WebMercator.ToMetres(longitude, latitude);
		return Math.Abs((v[0] + v[2]) / 2 - x) < 15000 && Math.Abs((v[1] + v[3]) / 2 - y) < 15000;
	}

	private static double[] Box(string url)
	{
		string box = url[(url.IndexOf("bbox=", StringComparison.Ordinal) + 5)..];
		return box[..box.IndexOf('&')].Split(',').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
	}

	private static double Size(string url)
	{
		double[] v = Box(url);
		return v[2] - v[0];
	}

	[AvaloniaFact]
	public async Task RetinaFloodZoneTiles_AreAskedForAtDoubleSize_AndKeptApart()
	{
		using UiSession ui = new UiSession();
		List<string> urls = new List<string>();
		TileCache cache = new TileCache(Path.Combine(ui.Folder, "tiles"), (url, ct) =>
		{
			urls.Add(url);
			return Task.FromResult(FakeTiles.FloodTile(url));
		});
		using SdeTileSource retina = new SdeTileSource(TileOverlays.FloodZones, cache, scale: 2);
		byte[] tile = await retina.GetTileAsync(new BruTile.TileInfo { Index = new BruTile.TileIndex(1, 1, 1) });
		using SdeTileSource plain = new SdeTileSource(TileOverlays.FloodZones, cache, scale: 1);
		await plain.GetTileAsync(new BruTile.TileInfo { Index = new BruTile.TileIndex(1, 1, 1) });

		Assert.Equal(2, urls.Count);
		Assert.Contains("size=512,512&dpi=192", urls[0]);
		Assert.Contains("size=256,256&dpi=96", urls[1]);
		using SkiaSharp.SKBitmap decoded = SkiaSharp.SKBitmap.Decode(tile);
		Assert.Equal(512, decoded.Width);
		Assert.True(File.Exists(Path.Combine(ui.Folder, "tiles", "fema-nfhl-28", "1", "1", "1@2x.tile")));
		Assert.True(File.Exists(Path.Combine(ui.Folder, "tiles", "fema-nfhl-28", "1", "1", "1.tile")));
	}

	[AvaloniaFact]
	public void ALayersFileFromAnotherDatabase_IsWarnedAbout_UntilThePersonKeepsIt()
	{
		using UiSession ui = new UiSession();
		LayersStore.Open(ui.Store.Database.LayersPath, Guid.NewGuid());
		MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		Assert.True(map.HasLayersWarning);
		Assert.Contains("different SDE database", map.LayersWarning);
		map.PanelIndex = 1;
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		map.KeepLayersCommand.Execute(null);
		Assert.False(map.HasLayersWarning);
		Assert.Null(LayersStore.Open(ui.Store.Database.LayersPath, ui.Store.Database.DatabaseId).Warning);
	}

	[AvaloniaFact]
	public async Task ALayerWithManyFeatures_IsDrawnAsRasterTiles()
	{
		using UiSession ui = new UiSession();
		MapView view = MapTestSupport.ShowMap(ui);
		FeatureLayer many = new FeatureLayer("Many parcels", GeometryKind.Polygon);
		many.AddField("n", FieldType.Integer);
		GeometryFactory gf = Sde.Gis.GisSetup.Factory;
		for (int i = 0; i < 2000; i++)
		{
			double x = -84.10 + i % 50 * 0.0004;
			double y = 39.60 + i / 50 * 0.0004;
			many.AddFeature(gf.CreatePolygon(new[] { new Coordinate(x, y), new Coordinate(x + 0.0003, y), new Coordinate(x + 0.0003, y + 0.0003), new Coordinate(x, y + 0.0003), new Coordinate(x, y) }))["n"] = i;
		}
		string path = Path.Combine(ui.Folder, "many.geojson");
		GeoJsonFormat.Write(path, many);
		ui.Dialogs.Answers.Enqueue(new[] { path });
		await ui.Main.Map.AddFileLayerCommand.ExecuteAsync(null);
		ui.Pump();
		MapLayerViewModel layer = Assert.Single(ui.Main.Map.Layers, l => l.IsFeatures);
		Assert.True(view.Map.IsRasterized(layer.Key));
		// Clicks still find features through the index, rasterized or not.
		Point p = view.Map.ToScreen(39.60 + 0.00015, -84.10 + 0.00015);
		Assert.Equal(0L, view.Map.FeatureAt(p)?.Feature["n"]);
	}
}
