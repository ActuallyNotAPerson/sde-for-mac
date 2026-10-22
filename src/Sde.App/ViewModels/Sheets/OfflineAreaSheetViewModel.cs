using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.App.Services;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Keep a map area offline (C5): downloads the USGS background map's pieces for an area (the map's view, or a team's
/// structures) at zooms 12 to 17, so the map still shows it without a connection. Closes with a line saying what was
/// kept, or null when cancelled.
/// </summary>
public sealed partial class OfflineAreaSheetViewModel : SheetViewModel
{
	private readonly TileCache _cache;
	private readonly (double West, double South, double East, double North) _area;
	private CancellationTokenSource _cancel;

	public OfflineAreaSheetViewModel(TileCache cache, double west, double south, double east, double north, Basemap shown, string areaName)
		: base("Keep this area offline")
	{
		_cache = cache;
		_area = (west, south, east, north);
		AreaName = areaName;
		Basemap = OfflineArea.For(shown);
	}

	public override SheetSize Size => SheetSize.Small;

	public override bool CanCancel => !IsBusy;

	/// <summary>"The area shown on the map", "Team North's structures".</summary>
	public string AreaName { get; }

	public IReadOnlyList<Basemap> Choices => OfflineArea.Choices;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Summary), nameof(CanKeep))]
	public partial Basemap Basemap { get; set; }

	/// <summary>The closest zoom that fits under the limit, or null when the area is too large.</summary>
	public int? Zoom => OfflineArea.FittingZoom(_area.West, _area.South, _area.East, _area.North, Basemap);

	public long TileCount => Zoom is int z ? OfflineArea.Count(_area.West, _area.South, _area.East, _area.North, z) : 0;

	public bool CanKeep => Zoom != null && !IsBusy;

	/// <summary>"1,234 map pieces (about 25 MB), for zooms 12 to 17." or why it can't be kept.</summary>
	public string Summary => Zoom is int z
		? $"{TileCount.ToString("N0", CultureInfo.InvariantCulture)} map pieces (about {Math.Max(1, TileCount * 20 / 1024).ToString("N0", CultureInfo.InvariantCulture)} MB), for zooms {OfflineArea.MinZoom} to {z}."
			+ (z < Math.Min(OfflineArea.MaxZoom, Basemap.MaxZoom) ? " The closest zooms are left out to keep it under 5,000 pieces." : "")
		: "That area is too large to keep. Zoom in on the map, or choose a smaller area.";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanKeep), nameof(CanCancel))]
	public partial bool IsBusy { get; set; }

	/// <summary>0 to 100.</summary>
	[ObservableProperty]
	public partial double Progress { get; set; }

	[ObservableProperty]
	public partial string Status { get; set; }

	[RelayCommand]
	private async Task Keep()
	{
		if (Zoom is not int z || IsBusy)
		{
			return;
		}
		List<(int Z, int X, int Y)> tiles = OfflineArea.Tiles(_area.West, _area.South, _area.East, _area.North, z);
		_cancel = new CancellationTokenSource();
		IsBusy = true;
		Status = "Downloading…";
		Progress<int> progress = new Progress<int>(n => Progress = 100.0 * n / Math.Max(1, tiles.Count));
		OfflineAreaResult result;
		try
		{
			result = await Task.Run(() => OfflineArea.KeepAsync(_cache, Basemap, tiles, progress, _cancel.Token));
		}
		catch (OperationCanceledException)
		{
			Status = "Stopped. The pieces already downloaded are kept.";
			return;
		}
		finally
		{
			IsBusy = false;
			_cancel.Dispose();
			_cancel = null;
		}
		string kept = $"Kept {result.Kept.ToString("N0", CultureInfo.InvariantCulture)} map pieces of {Basemap.Name} for zooms {OfflineArea.MinZoom} to {result.MaxZoom}.";
		if (result.Failed > 0)
		{
			Status = kept + $" {result.Failed.ToString("N0", CultureInfo.InvariantCulture)} couldn't be downloaded" + (result.Problem == null ? "." : ": " + result.Problem);
			return;
		}
		Close(kept);
	}

	[RelayCommand]
	private void Stop() => _cancel?.Cancel();
}
