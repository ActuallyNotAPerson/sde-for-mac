using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// Keeping a map area offline and worksheet maps (v2.5, C5): which pieces cover an area, the 5,000-piece limit, the
/// download into the map's tile cache (fake tiles: nothing goes online), and the Map and Assignments pages' Offline….
/// </summary>
public class OfflineAreaTests
{
	// A few streets of a made-up town: about 1.3 km by 1 km.
	private const double West = -84.520;
	private const double South = 39.100;
	private const double East = -84.505;
	private const double North = 39.109;

	[Fact]
	public void Tiles_CoverTheAreaAtZooms12To17_AndLargeAreasDropTheClosestZooms()
	{
		List<(int Z, int X, int Y)> tiles = OfflineArea.Tiles(West, South, East, North);
		Assert.Equal(Enumerable.Range(12, 6), tiles.Select(t => t.Z).Distinct().OrderBy(z => z));
		Assert.Equal(tiles.Count, OfflineArea.Count(West, South, East, North, 17));
		Assert.InRange(tiles.Count, 20, 200);
		// USGS's maps stop at zoom 16; a map that goes closer is kept to 17.
		Assert.Equal(16, OfflineArea.FittingZoom(West, South, East, North, Basemaps.UsgsTopo));
		Assert.Equal(17, OfflineArea.FittingZoom(West, South, East, North, Basemaps.UsgsTopo with { MaxZoom = 19 }));

		// A county is too many pieces at 17 but fits closer to 12; a state doesn't fit at all.
		int? county = OfflineArea.FittingZoom(-84.8, 39.0, -84.2, 39.3, Basemaps.UsgsTopo);
		Assert.NotNull(county);
		Assert.InRange(county!.Value, 12, 16);
		Assert.True(OfflineArea.Count(-84.8, 39.0, -84.2, 39.3, county.Value) <= OfflineArea.MaxTiles);
		Assert.Null(OfflineArea.FittingZoom(-89.0, 36.0, -80.0, 42.0, Basemaps.UsgsTopo));

		// Only USGS maps are kept; OpenStreetMap falls back to the topographic map.
		Assert.Equal(Basemaps.UsgsImagery, OfflineArea.For(Basemaps.UsgsImagery));
		Assert.Equal(Basemaps.UsgsTopo, OfflineArea.For(Basemaps.OpenStreetMap));
	}

	[Fact]
	public async Task Keep_DownloadsEachPieceOnce_AndCountsFailures()
	{
		string folder = Path.Combine(Path.GetTempPath(), "sde-offline-" + Guid.NewGuid().ToString("N"));
		try
		{
			int asked = 0;
			TileCache cache = new TileCache(folder, (url, ct) =>
			{
				Interlocked.Increment(ref asked);
				return url.Contains("/17/", StringComparison.Ordinal) ? throw new HttpRequestException("Not here", null, System.Net.HttpStatusCode.NotFound) : Task.FromResult(new byte[] { 1, 2, 3 });
			});
			List<(int Z, int X, int Y)> tiles = OfflineArea.Tiles(West, South, East, North);
			int seventeen = tiles.Count(t => t.Z == 17);
			OfflineAreaResult first = await OfflineArea.KeepAsync(cache, Basemaps.UsgsTopo, tiles, null, CancellationToken.None);
			Assert.Equal(tiles.Count - seventeen, first.Kept);
			Assert.Equal(seventeen, first.Failed);
			Assert.NotNull(first.Problem);

			// Kept pieces come from disk the second time.
			int before = asked;
			OfflineAreaResult again = await OfflineArea.KeepAsync(cache, Basemaps.UsgsTopo, tiles.Where(t => t.Z < 17).ToList(), null, CancellationToken.None);
			Assert.Equal(0, again.Failed);
			Assert.Equal(before, asked);
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	private static async Task Finish(UiSession ui, Task task)
	{
		Stopwatch clock = Stopwatch.StartNew();
		while (!task.IsCompleted && clock.ElapsedMilliseconds < 30000)
		{
			ui.Pump();
			Thread.Sleep(10);
		}
		await task;
	}

	[AvaloniaFact]
	public async Task MapPage_KeepsTheAreaOnScreen()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui, Basemaps.UsgsTopo);
		MapView view = MapTestSupport.ShowMap(ui);
		MapTestSupport.Settle(ui, view);
		MapViewModel map = ui.Main.Map;

		Task keeping = map.KeepOfflineCommand.ExecuteAsync(null);
		ui.Pump();
		OfflineAreaSheetViewModel sheet = Assert.IsType<OfflineAreaSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(Basemaps.UsgsTopo, sheet.Basemap);
		Assert.True(sheet.CanKeep, sheet.Summary);
		Assert.Contains("map pieces", sheet.Summary);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("56k-map-offline");
		Task kept = sheet.KeepCommand.ExecuteAsync(null);
		await Finish(ui, kept);
		await Finish(ui, keeping);
		Assert.StartsWith("Kept ", map.Message);
		Assert.Contains("USGS topographic map", map.Message);
	}

	[AvaloniaFact]
	public async Task Assignments_KeepTheTeamsArea_AndWorksheetsGetMaps()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		ui.Pump();
		TeamRecordFor(ui);
		ui.Main.ShowCommand.Execute(Page.Assignments);
		ui.Pump();
		AssignmentsViewModel page = ui.Main.Assignments;
		Assert.Equal(3, page.Stops.Count);

		// Worksheets without a background map, then with one: the map adds a picture to each placed structure's page.
		AssignmentsViewModel.WorksheetMapWait = TimeSpan.FromSeconds(10);
		string plain = Path.Combine(ui.Folder, "plain.pdf");
		ui.Dialogs.Answers.Enqueue(plain);
		await Finish(ui, page.WorksheetsCommand.ExecuteAsync(null));
		ui.Main.Map.Basemap = Basemaps.UsgsTopo;
		string withMaps = Path.Combine(ui.Folder, "maps.pdf");
		ui.Dialogs.Answers.Enqueue(withMaps);
		await Finish(ui, page.WorksheetsCommand.ExecuteAsync(null));
		Assert.True(new FileInfo(withMaps).Length > new FileInfo(plain).Length + 20000, $"{new FileInfo(plain).Length} vs {new FileInfo(withMaps).Length}");

		// Offline… for the team's structures.
		Task keeping = page.KeepOfflineCommand.ExecuteAsync(null);
		ui.Pump();
		OfflineAreaSheetViewModel sheet = Assert.IsType<OfflineAreaSheetViewModel>(ui.Main.Sheets.Current);
		Assert.StartsWith("Team North's structures", sheet.AreaName);
		Assert.Equal(16, sheet.Zoom);
		Assert.DoesNotContain("left out", sheet.Summary);
		await Finish(ui, sheet.KeepCommand.ExecuteAsync(null));
		await Finish(ui, keeping);
		Assert.StartsWith("Kept ", page.Message);
	}

	/// <summary>Team North with every structure assigned.</summary>
	private static void TeamRecordFor(UiSession ui)
	{
		Sde.Data.TeamRecord team = ui.Store.SaveTeam(new Sde.Data.TeamRecord { Name = "Team North" });
		ui.Store.AssignToTeam(ui.Store.GetProperties().Select(p => p.PropertyID).ToList(), team.TeamID);
	}
}
