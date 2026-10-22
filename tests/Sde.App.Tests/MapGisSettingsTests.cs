using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.Views;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>Settings › Map &amp; GIS: the background map, flood zones at start, the saved map pieces, and owner names in GIS files.</summary>
public class MapGisSettingsTests
{
	/// <summary>Pumps the window until <paramref name="done"/> (or ten seconds).</summary>
	private static void Until(UiSession ui, Func<bool> done)
	{
		Stopwatch clock = Stopwatch.StartNew();
		while (!done() && clock.ElapsedMilliseconds < 10000)
		{
			ui.Pump();
			Thread.Sleep(5);
		}
		ui.Pump();
	}

	private static MapGisSettingsViewModel ShowPanel(UiSession ui)
	{
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Pump();
		ui.Main.Settings.Show(SettingsCategory.MapAndGis);
		MapGisSettingsViewModel vm = ui.Main.Settings.MapGis;
		Until(ui, () => vm.TileCacheText != "Counting…");
		return vm;
	}

	/// <summary>Three made-up map pieces of 2 KB in the tile folder, and a file that isn't a map piece.</summary>
	private static string SomeTiles(UiSession ui)
	{
		string folder = MapViewModel.TileFolder(ui.Services);
		string dir = Path.Combine(folder, "usgs-topo", "15", "8800");
		Directory.CreateDirectory(dir);
		for (int i = 0; i < 3; i++)
		{
			File.WriteAllBytes(Path.Combine(dir, 12700 + i + ".tile"), new byte[2048]);
		}
		File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a tile");
		return folder;
	}

	[AvaloniaFact]
	public void ThePanel_StartsWithTheDefaults_AndSavesEachChoiceInTheDatabase()
	{
		using UiSession ui = new UiSession();
		MapGisSettingsViewModel vm = ShowPanel(ui);
		MapGisPanel panel = ui.Window.GetVisualDescendants().OfType<MapGisPanel>().Single();
		Assert.True(panel.IsEffectivelyVisible);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		Assert.Equal(Basemaps.UsgsTopo, vm.Basemap);
		Assert.False(vm.FloodZonesAtStart);
		Assert.True(vm.LeaveOutOwnerNames);
		Assert.Equal("None saved", vm.TileCacheText);
		Assert.False(vm.ClearTilesCommand.CanExecute(null));

		vm.Basemap = Basemaps.UsgsImagery;
		vm.FloodZonesAtStart = true;
		vm.LeaveOutOwnerNames = false;
		Assert.Equal("usgs-imagery", ui.Store.GetMapBasemapId());
		Assert.True(ui.Store.GetMapFloodZonesAtStart());
		Assert.False(ui.Store.GetGisLeaveOutOwnerNames());
		// One basemap row, however often it changes.
		vm.Basemap = Basemaps.OpenStreetMap;
		Assert.Single(ui.Store.GetMapLayers(), r => r.SourceKind == Sde.Data.MapLayerKinds.Basemap);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public void TheBackground_IsTheSameChoiceAsOnTheMapPage()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui, Basemaps.UsgsTopo);
		MapGisSettingsViewModel vm = ShowPanel(ui);
		vm.Basemap = Basemaps.OpenStreetMap;

		MapView view = MapTestSupport.ShowMap(ui);
		Assert.Equal(Basemaps.OpenStreetMap, ui.Main.Map.Basemap);
		Assert.Equal(Basemaps.OpenStreetMap, view.Map.Basemap);

		// Changed on the Map page, Settings shows it next time.
		ui.Main.Map.Basemap = Basemaps.UsgsImagery;
		ShowPanel(ui);
		Assert.Equal(Basemaps.UsgsImagery, ui.Main.Settings.MapGis.Basemap);
		Assert.Single(ui.Store.GetMapLayers(), r => r.SourceKind == Sde.Data.MapLayerKinds.Basemap);
	}

	[AvaloniaFact]
	public void FloodZonesAtStart_TurnsThemOnWhenTheMapOpens_ButLeavesThemToYouAfterThat()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		ShowPanel(ui).FloodZonesAtStart = true;

		MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		MapLayerViewModel zones = Assert.Single(map.Layers, l => l.IsTiles);
		Assert.True(zones.IsVisible);
		Assert.Equal(0, map.PanelIndex);

		// Turned off on the map, they stay off while SDE runs.
		zones.IsVisible = false;
		ui.Main.ShowCommand.Execute(Page.Structures);
		MapTestSupport.ShowMap(ui);
		Assert.False(Assert.Single(map.Layers, l => l.IsTiles).IsVisible);

		// Switched off and on again in Settings: on again when the map next opens.
		MapGisSettingsViewModel vm = ShowPanel(ui);
		vm.FloodZonesAtStart = false;
		MapTestSupport.ShowMap(ui);
		Assert.False(Assert.Single(map.Layers, l => l.IsTiles).IsVisible);
		ShowPanel(ui).FloodZonesAtStart = true;
		MapTestSupport.ShowMap(ui);
		Assert.True(Assert.Single(map.Layers, l => l.IsTiles).IsVisible);
	}

	[AvaloniaFact]
	public async Task SavedMapPieces_AreCounted_AndClearLeavesEverythingElse()
	{
		using UiSession ui = new UiSession();
		string folder = SomeTiles(ui);
		MapGisSettingsViewModel vm = ShowPanel(ui);
		Assert.Equal("6 KB, 3 pieces", vm.TileCacheText);
		Assert.True(vm.ClearTilesCommand.CanExecute(null));

		await vm.ClearTilesCommand.ExecuteAsync(null);
		Assert.Empty(Directory.GetFiles(folder, "*.tile", SearchOption.AllDirectories));
		Assert.Empty(Directory.GetDirectories(folder));
		Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
		Assert.Equal("None saved", vm.TileCacheText);
		Assert.Equal("Cleared 6 KB. The map downloads pieces again as it needs them.", vm.TileMessage);
		Assert.False(vm.ClearTilesCommand.CanExecute(null));
	}

	[Fact]
	public void Sizes_ReadLikeFinder()
	{
		Assert.Equal("1 KB", MapGisSettingsViewModel.Size(10));
		Assert.Equal("640 KB", MapGisSettingsViewModel.Size(640 * 1024));
		Assert.Equal("24.6 MB", MapGisSettingsViewModel.Size((long)(24.6 * 1024 * 1024)));
		Assert.Equal("1.5 GB", MapGisSettingsViewModel.Size(3L * 512 * 1024 * 1024));
	}

	[AvaloniaFact]
	public void OwnerNames_OnTheDashboard_StartAsTheSettingSays_EachTimeItOpens()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		Assert.False(ui.Main.Dashboard.IncludeOwnerNames);
		ui.Main.Dashboard.IncludeOwnerNames = true;
		ui.Main.ShowCommand.Execute(Page.Structures);
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		Assert.False(ui.Main.Dashboard.IncludeOwnerNames);

		ShowPanel(ui).LeaveOutOwnerNames = false;
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		Assert.True(ui.Main.Dashboard.IncludeOwnerNames);
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public void Screenshot(string theme)
	{
		using UiSession ui = new UiSession(theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
		MapTestSupport.SetUpPlaces(ui);
		SomeTiles(ui);
		File.WriteAllBytes(Path.Combine(MapViewModel.TileFolder(ui.Services), "usgs-topo", "15", "8800", "12800.tile"), new byte[18 * 1024 * 1024]);
		ui.Store.SetMapFloodZonesAtStart(true);
		MapGisSettingsViewModel vm = ShowPanel(ui);
		Assert.True(vm.FloodZonesAtStart);
		Assert.Equal("18.0 MB, 4 pieces", vm.TileCacheText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot(theme == "Dark" ? "45c-dark-settings-map-gis" : "31g-settings-map-gis");
	}
}
