using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.ViewModels;

/// <summary>The Map page's Keep offline…: the USGS map for the area on screen, kept on this Mac (C5).</summary>
public sealed partial class MapViewModel
{
	[RelayCommand]
	private async Task KeepOffline()
	{
		MapPrintScene scene = PrintScene?.Invoke();
		if (scene == null || !(scene.Resolution > 0))
		{
			return;
		}
		double halfW = scene.ScreenWidth / 2 * scene.Resolution;
		double halfH = scene.ScreenHeight / 2 * scene.Resolution;
		(double west, double north) = WebMercator.FromMetres(scene.CenterX - halfW, scene.CenterY + halfH);
		(double east, double south) = WebMercator.FromMetres(scene.CenterX + halfW, scene.CenterY - halfH);
		if (await _app.Sheets.ShowAsync(new OfflineAreaSheetViewModel(Tiles, west, south, east, north, Basemap, "The area shown on the map")) is string done)
		{
			Message = done;
		}
	}
}
