using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.Views;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>The Map page and the Event dashboard, through the real window with synthetic structures.</summary>
public class MapDashboardUiTests
{
	/// <summary>
	/// The three sample structures, two of them given coordinates (the ranch on River, the house on Mill Creek) and
	/// the office on Riverside left without.
	/// </summary>
	private static void SetUpPlaces(UiSession ui)
	{
		ScreenTourTests.AddSamples(ui.Store);
		foreach (PropertyRecord p in ui.Store.GetProperties())
		{
			if (p.StreetName == "River")
			{
				p.Latitude = 39.1031m;
				p.Longitude = -84.5120m;
				ui.Store.SaveProperty(p);
			}
			else if (p.StreetName == "Mill Creek")
			{
				p.Latitude = 39.1100m;
				p.Longitude = -84.5000m;
				ui.Store.SaveProperty(p);
			}
		}
		ui.Main.Structures.Reload();
		// The tests have no internet, and the page must work without a basemap.
		ui.Main.Map.Basemap = Basemaps.None;
	}

	private static MapView ShowMap(UiSession ui)
	{
		ui.Main.ShowCommand.Execute(Page.Map);
		ui.Pump();
		MapView view = ui.Window.GetVisualDescendants().OfType<MapView>().Single();
		return view;
	}

	[AvaloniaFact]
	public void Map_RendersWithNoBasemap_AndCountsPoints()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		MapView view = ShowMap(ui);
		MapViewModel map = ui.Main.Map;

		Assert.Equal(Page.Map, ui.Main.ActivePage);
		Assert.True(ui.Main.IsMapPage);
		Assert.Equal(2, map.Visible.Count);
		Assert.Equal(2, view.Map.Markers.Count);
		Assert.Equal(3, map.TotalCount);
		Assert.Equal(1, map.UnplacedCount);
		Assert.Equal("1 structure has no coordinates", map.UnplacedText);
		Assert.Equal(map.OnMapCount, map.Classes.Sum(c => c.Count));
		Assert.Equal("USGS topographic map", Basemaps.UsgsTopo.Name);

		// The map fitted itself around both dots: both are inside the control.
		foreach (MapMarker m in view.Map.Markers)
		{
			Point p = view.Map.ToScreen(m.Latitude, m.Longitude);
			Assert.True(new Rect(view.Map.Bounds.Size).Contains(p), $"{p} outside {view.Map.Bounds}");
		}
		ui.Snapshot("56-map");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public void Map_Filter_HidesAClass()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		MapView view = ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		MapClassViewModel first = map.Classes.First(c => c.Count > 0);
		first.IsOn = false;
		ui.Pump();
		Assert.Equal(map.OnMapCount - first.Count, map.Visible.Count);
		Assert.Equal(map.Visible.Count, view.Map.Markers.Count);
		Assert.DoesNotContain(map.Visible, p => p.Class == first.Class);
		map.ShowAllCommand.Execute(null);
		Assert.Equal(map.OnMapCount, map.Visible.Count);
	}

	[AvaloniaFact]
	public void Map_Colours_MatchTheStructuresResult()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		ShowMap(ui);
		foreach (MapPointViewModel p in ui.Main.Map.Visible)
		{
			StructureRowViewModel row = ui.Main.Structures.Rows.First(r => r.Summary.PropertyID == p.Id);
			Assert.Equal(row.State.ToString(), p.Class.ToString());
		}
	}

	[AvaloniaFact]
	public void Map_ClickingAPoint_OpensTheAssessment()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		MapView view = ShowMap(ui);
		MapPointViewModel target = ui.Main.Map.Visible.First(p => p.AssessmentID != null);
		Point at = view.Map.ToScreen(target.Latitude, target.Longitude);
		Assert.Equal(target.Id, view.Map.MarkerAt(at));
		view.Map.Click(at);
		ui.Pump();
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.Equal(target.AssessmentID, ui.Main.Editor.Assessment.AssessmentID);
	}

	[AvaloniaFact]
	public void Map_SetLocation_SavesCoordinatesAndWritesHistory()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		MapView view = ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		map.SetModeCommand.Execute(MapMode.SetLocation);
		ui.Pump();
		Assert.True(map.IsSetLocation);
		MapPointViewModel office = Assert.Single(map.Unplaced);
		Assert.Same(office, map.PlaceTarget);

		// A click on empty map, a little away from both dots.
		Point spot = view.Map.ToScreen(39.1066, -84.5060) + new Point(0, 60);
		(double lat, double lon) = view.Map.ToLatLon(spot);
		view.Map.Click(spot);
		ui.Pump();

		PropertyRecord saved = ui.Store.GetProperty(office.Id);
		Assert.Equal(Math.Round((decimal)lat, 6), saved.Latitude);
		Assert.Equal(Math.Round((decimal)lon, 6), saved.Longitude);
		Assert.Contains(ui.Store.GetPropertyHistory(office.Id), h => h.Field != null && h.Field.EndsWith("Latitude") && h.Via == "Map");
		Assert.Equal(0, map.UnplacedCount);
		Assert.Equal(3, map.Visible.Count);
		Assert.Null(map.PlaceTarget);
		Assert.Contains("Saved the location", map.Message);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
	}

	[AvaloniaFact]
	public void Map_SelectingABox_ChecksThoseStructuresInTheList()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		MapView view = ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		IReadOnlyList<Guid> inside = view.Map.MarkersIn(new Rect(view.Map.Bounds.Size));
		Assert.Equal(2, inside.Count);
		map.SetModeCommand.Execute(MapMode.Select);
		Assert.Equal(MapDrag.Box, view.Map.Drag);
		map.SelectArea(inside.ToList(), add: false);
		Assert.Equal(2, ui.Main.Structures.CheckedCount);
		Assert.Contains("2 structures are checked", map.Message);
		map.SelectArea(new List<Guid>(), add: false);
		Assert.Equal(0, ui.Main.Structures.CheckedCount);
	}

	[AvaloniaFact]
	public void Map_LinkGoesToTheCoordinateTools_AndZoomKeepsThePointUnderTheMouse()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		MapView view = ShowMap(ui);
		ui.Main.Map.ShowCoordinateToolsCommand.Execute(null);
		Assert.Equal(Page.ImportExport, ui.Main.ActivePage);

		view.Map.SetView(39.1, -84.5, 10);
		double before = view.Map.Zoom;
		view.Map.ZoomBy(2);
		Assert.Equal(before + 2, view.Map.Zoom, 6);
		Assert.Equal(39.1, view.Map.CenterLatitude, 6);
	}

	[AvaloniaFact]
	public void Map_Accessibility_EveryControlIsNamed_InEveryMode()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		ShowMap(ui);
		List<string> problems = new();
		foreach (MapMode mode in Enum.GetValues<MapMode>())
		{
			ui.Main.Map.SetModeCommand.Execute(mode);
			ui.Pump();
			problems.AddRange(AccessibilityTests.Unnamed(ui.Window).Select(p => mode + ": " + p));
		}
		Assert.Empty(problems);
		ui.Main.Map.Basemap = Basemaps.OpenStreetMap;
		Assert.Equal("© OpenStreetMap contributors", ui.Main.Map.Attribution);
		Assert.True(ui.Main.Map.UsesInternet);
	}

	[AvaloniaFact]
	public void Map_EmptyDatabase_ShowsAHelpfulMessage()
	{
		using UiSession ui = new UiSession();
		ui.Main.Map.Basemap = Basemaps.None;
		ShowMap(ui);
		Assert.Empty(ui.Main.Map.Visible);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
	}

	[Fact]
	public void WebMercator_RoundTrips()
	{
		foreach ((double lat, double lon) in new[] { (39.1, -84.5), (-33.9, 151.2), (0.0, 0.0), (64.1, -21.9) })
		{
			Assert.Equal(lat, WebMercator.Latitude(WebMercator.V(lat)), 7);
			Assert.Equal(lon, WebMercator.Longitude(WebMercator.U(lon)), 7);
		}
		Assert.Equal(0.5, WebMercator.U(0), 9);
		Assert.Equal(0.5, WebMercator.V(0), 9);
	}

	[AvaloniaFact]
	public async Task TileCache_DownloadsOnce_ThenReadsFromDisk()
	{
		using UiSession ui = new UiSession();
		string folder = Path.Combine(ui.Folder, "tiles");
		byte[] png;
		using (SkiaSharp.SKBitmap bmp = new SkiaSharp.SKBitmap(8, 8))
		{
			bmp.Erase(SkiaSharp.SKColors.SteelBlue);
			using SkiaSharp.SKData data = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
			png = data.ToArray();
		}
		Basemap basemap = new Basemap("test", "Test", "https://tiles.example/{z}/{x}/{y}.png", "Test", 5);
		int downloads = 0;
		TileCache first = new TileCache(folder, (url, ct) =>
		{
			downloads++;
			Assert.Equal("https://tiles.example/3/2/1.png", url);
			return Task.FromResult(png);
		});
		Assert.Null(first.TryGet(basemap, 3, 2, 1));
		Assert.NotNull(await Wait(ui, () => first.TryGet(basemap, 3, 2, 1)));
		Assert.Equal(1, downloads);

		// A new cache (as after restarting the app) finds the tile on disk and never calls the downloader.
		TileCache second = new TileCache(folder, (url, ct) => throw new HttpRequestException("offline"));
		Assert.Null(second.TryGet(basemap, 3, 2, 1));
		Assert.NotNull(await Wait(ui, () => second.TryGet(basemap, 3, 2, 1)));
		Assert.Equal(0, second.FailureCount);

		// A tile that was never saved fails quietly and says so once.
		List<string> reasons = new();
		second.DownloadFailed += reasons.Add;
		second.TryGet(basemap, 3, 0, 0);
		await Wait(ui, () => reasons.Count > 0 ? new object() : null);
		Assert.Single(reasons);
		Assert.Equal(1, second.FailureCount);
	}

	/// <summary>A tile server that answers every request with one status code and remembers the User-Agent sent.</summary>
	private sealed class TileServer(HttpStatusCode status) : HttpMessageHandler
	{
		public List<string> UserAgents { get; } = new();

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (UserAgents)
			{
				UserAgents.Add(request.Headers.UserAgent.ToString());
			}
			return Task.FromResult(new HttpResponseMessage(status));
		}
	}

	[AvaloniaTheory]
	[InlineData(HttpStatusCode.Forbidden, "The map service refused the request (HTTP 403).")]
	[InlineData(HttpStatusCode.NotFound, "The map service couldn't find this part of the map (HTTP 404).")]
	public async Task TileCache_ARefusedOrMissingTile_IsNotCalledAConnectionProblem(HttpStatusCode status, string expected)
	{
		using UiSession ui = new UiSession();
		Basemap basemap = new Basemap("test", "Test", "https://tiles.example/{z}/{x}/{y}.png", "Test", 5);
		TileServer server = new TileServer(status);
		using HttpClient client = TileCache.CreateClient(server);
		TileCache cache = new TileCache(Path.Combine(ui.Folder, "tiles"), (url, ct) => TileCache.DownloadAsync(client, url, ct));
		List<string> reasons = new();
		cache.DownloadFailed += reasons.Add;
		cache.TryGet(basemap, 3, 2, 1);
		await Wait(ui, () => reasons.Count > 0 ? new object() : null);
		Assert.Equal(expected, Assert.Single(reasons));

		// The tile server is told which version of SDE is asking.
		string version = typeof(TileCache).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
		Assert.Equal($"SDE-for-Mac/{version} (Substantial Damage Estimator)", Assert.Single(server.UserAgents));
		Assert.Equal(TileCache.UserAgent, server.UserAgents[0]);

		// No answer at all is still a connection problem.
		TileCache offline = new TileCache(Path.Combine(ui.Folder, "offline"), (url, ct) => throw new HttpRequestException("No route to host"));
		List<string> offlineReasons = new();
		offline.DownloadFailed += offlineReasons.Add;
		offline.TryGet(basemap, 3, 2, 1);
		await Wait(ui, () => offlineReasons.Count > 0 ? new object() : null);
		Assert.Equal("The map service didn't answer. Check the internet connection.", Assert.Single(offlineReasons));
	}

	private static async Task<T> Wait<T>(UiSession ui, Func<T> get) where T : class
	{
		for (int i = 0; i < 100; i++)
		{
			ui.Pump();
			T value = get();
			if (value != null)
			{
				return value;
			}
			await Task.Delay(50);
		}
		return null;
	}

	// ───────────── Dashboard ─────────────

	[AvaloniaFact]
	public void Dashboard_NumbersMatchTheEventSummary()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		DashboardViewModel d = ui.Main.Dashboard;

		EventSummary expected = EventSummary.Build(
			ui.Store.LoadAssessments(ui.Store.ListMapPoints().Where(r => r.AssessmentID != null).Select(r => r.AssessmentID!.Value)),
			ui.Store.GetProperties(), null, ui.Store.Reference);
		Assert.Equal(Page.Dashboard, ui.Main.ActivePage);
		Assert.Equal(expected.TotalStructures.ToString(), d.Structures);
		Assert.Equal(expected.Count(DeterminationClass.Substantial).ToString(), d.SubstantialText);
		Assert.Equal(expected.TotalStructures, d.ByResult.Sum(r => r.Count) + expected.NotYetAssessed);
		Assert.Equal(expected.AssessedStructures, d.Progress[0].Count);
		Assert.Equal(expected.NotYetAssessed, d.Progress[1].Count);
		Assert.Equal(expected.ByStreet.Count, d.ByStreet.Count);
		Assert.Equal(expected.ByStreet[0].Label, d.ByStreet[0].Label);
		Assert.Equal(expected.ByInspector.Sum(g => g.Count), d.ByInspector.Sum(g => g.Count));
		Assert.NotNull(d.Dates);
		Assert.Contains("Sep 14, 2026", d.Dates);
		Assert.NotEmpty(d.ByCommunity);

		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("57-dashboard");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task Dashboard_SummaryPdf_WritesAFileAndOpensIt()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		string path = Path.Combine(ui.Folder, "summary.pdf");
		ui.Dialogs.Answers.Enqueue(path);
		await ui.Main.Dashboard.SummaryPdfCommand.ExecuteAsync(null);
		Assert.True(File.Exists(path));
		Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
		Assert.Contains(path, ui.Dialogs.Opened);
		Assert.Contains("summary.pdf", ui.Main.Dashboard.LastResult);
	}

	[AvaloniaFact]
	public async Task Dashboard_Cancelling_WritesNothing()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		await ui.Main.Dashboard.GeoJsonCommand.ExecuteAsync(null);
		Assert.Null(ui.Main.Dashboard.LastResult);
		Assert.Empty(ui.Dialogs.Opened);
	}

	[AvaloniaFact]
	public async Task Dashboard_GeoPackage_WritesAGeoPackageAndStyle_WithoutOwnerNamesByDefault()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		DashboardViewModel d = ui.Main.Dashboard;
		Assert.False(d.IncludeOwnerNames);
		string path = Path.Combine(ui.Folder, "event.gpkg");
		ui.Dialogs.Answers.Enqueue(path);
		await d.GeoPackageCommand.ExecuteAsync(null);

		Assert.True(File.Exists(path));
		Assert.True(File.Exists(Path.Combine(ui.Folder, "event.qml")));
		Assert.Contains("1 had no coordinates", d.LastResult);
		using (SqliteConnection c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
		{
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "PRAGMA application_id";
			Assert.Equal((long)GisApplicationId, Convert.ToInt64(cmd.ExecuteScalar()));
			cmd.CommandText = "SELECT COUNT(*) FROM structures";
			Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar()));
			cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('structures') WHERE name = 'owner_name'";
			Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
		}
		SqliteConnection.ClearAllPools();
	}

	private const int GisApplicationId = Sde.Interop.GisExport.GeoPackageApplicationId;

	[AvaloniaFact]
	public async Task Dashboard_GeoJson_OwnerNamesOnlyWhenTicked()
	{
		using UiSession ui = new UiSession();
		SetUpPlaces(ui);
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		DashboardViewModel d = ui.Main.Dashboard;

		string plain = Path.Combine(ui.Folder, "plain.geojson");
		ui.Dialogs.Answers.Enqueue(plain);
		await d.GeoJsonCommand.ExecuteAsync(null);
		string text = File.ReadAllText(plain);
		Assert.Equal(2, JsonDocument.Parse(text).RootElement.GetProperty("features").GetArrayLength());
		Assert.DoesNotContain("Sample", text);
		Assert.DoesNotContain("Example", text);
		Assert.True(File.Exists(Path.Combine(ui.Folder, "plain.qml")));

		d.IncludeOwnerNames = true;
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		string named = Path.Combine(ui.Folder, "named.geojson");
		ui.Dialogs.Answers.Enqueue(named);
		await d.GeoJsonCommand.ExecuteAsync(null);
		Assert.Contains("Sample", File.ReadAllText(named));
	}

	[AvaloniaFact]
	public void Dashboard_Empty_ShowsAMessage_AndKeepsExportsOff()
	{
		using UiSession ui = new UiSession();
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		Assert.False(ui.Main.Dashboard.HasAnything);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
	}
}
