using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.App.Services;

namespace Sde.App.ViewModels;

/// <summary>
/// Settings, then Map and GIS: the map's background, whether FEMA's flood zones show when the map opens, the map
/// pieces kept on this Mac (their size, and Clear), and whether GIS files start without owners' names. The first,
/// second and last are this database's (see SdeStore.MapSettings); the map pieces are shared by every database.
/// </summary>
public sealed partial class MapGisSettingsViewModel : ObservableObject
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private readonly AppServices _app;
	private readonly string _tileFolder;
	private bool _loading;

	public MapGisSettingsViewModel(AppServices app)
	{
		_app = app;
		_tileFolder = MapViewModel.TileFolder(app);
		TileCacheText = "Counting…";
		ReadSettings();
	}

	/// <summary>Reads the settings again (the Map page may have changed the background) and counts the map pieces.</summary>
	public Task Refresh()
	{
		ReadSettings();
		return MeasureTilesAsync();
	}

	private void ReadSettings()
	{
		_loading = true;
		try
		{
			Basemap = Basemaps.WithId(_app.Store.GetMapBasemapId());
			FloodZonesAtStart = _app.Store.GetMapFloodZonesAtStart();
			LeaveOutOwnerNames = _app.Store.GetGisLeaveOutOwnerNames();
		}
		finally
		{
			_loading = false;
		}
	}

	public IReadOnlyList<Basemap> BasemapChoices => Basemaps.All;

	/// <summary>The map's background (the same choice as on the Map page).</summary>
	[ObservableProperty]
	public partial Basemap Basemap { get; set; }

	partial void OnBasemapChanged(Basemap value)
	{
		if (!_loading && value != null)
		{
			Save(() => _app.Store.SetMapBasemapId(value.Id));
		}
	}

	/// <summary>On: each time the map opens, FEMA's flood zones show over the background.</summary>
	[ObservableProperty]
	public partial bool FloodZonesAtStart { get; set; }

	partial void OnFloodZonesAtStartChanged(bool value)
	{
		if (!_loading)
		{
			Save(() => _app.Store.SetMapFloodZonesAtStart(value));
		}
	}

	/// <summary>On (the default): the Dashboard's GIS files start without owners' names.</summary>
	[ObservableProperty]
	public partial bool LeaveOutOwnerNames { get; set; }

	partial void OnLeaveOutOwnerNamesChanged(bool value)
	{
		if (!_loading)
		{
			Save(() => _app.Store.SetGisLeaveOutOwnerNames(value));
		}
	}

	/// <summary>"24.6 MB, 1,210 pieces" or "None saved".</summary>
	[ObservableProperty]
	public partial string TileCacheText { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(ClearTilesCommand))]
	public partial bool HasTiles { get; set; }

	/// <summary>What the last Clear did, or null.</summary>
	[ObservableProperty]
	public partial string TileMessage { get; set; }

	/// <summary>Counts the map pieces kept on this Mac (off the screen's thread: there can be thousands).</summary>
	public async Task MeasureTilesAsync()
	{
		string folder = _tileFolder;
		(long bytes, int count) = await Task.Run(() => TileCache.SizeOf(folder));
		HasTiles = count > 0;
		TileCacheText = count == 0 ? "None saved" : $"{Size(bytes)}, {count.ToString("N0", Us)} {(count == 1 ? "piece" : "pieces")}";
	}

	/// <summary>Deletes the map pieces kept on this Mac (the map downloads them again as it needs them).</summary>
	[RelayCommand(CanExecute = nameof(HasTiles))]
	private async Task ClearTiles()
	{
		string folder = _tileFolder;
		long freed = await Task.Run(() => TileCache.ClearFolder(folder));
		await MeasureTilesAsync();
		TileMessage = HasTiles
			? $"Cleared {Size(freed)}. Some pieces were in use and stay until next time."
			: $"Cleared {Size(freed)}. The map downloads pieces again as it needs them.";
	}

	/// <summary>"640 KB", "24.6 MB", "1.2 GB".</summary>
	public static string Size(long bytes)
	{
		const double Kb = 1024;
		if (bytes < Kb * Kb)
		{
			return Math.Max(1, (long)Math.Round(bytes / Kb)).ToString("N0", Us) + " KB";
		}
		if (bytes < Kb * Kb * Kb)
		{
			return (bytes / (Kb * Kb)).ToString("0.0", Us) + " MB";
		}
		return (bytes / (Kb * Kb * Kb)).ToString("0.0", Us) + " GB";
	}

	private void Save(Action save)
	{
		try
		{
			save();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the setting", ex);
		}
	}
}
