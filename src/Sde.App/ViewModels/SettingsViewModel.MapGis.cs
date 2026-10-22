namespace Sde.App.ViewModels;

/// <summary>Settings, then Map and GIS (see <see cref="MapGisSettingsViewModel"/>).</summary>
public sealed partial class SettingsViewModel
{
	private MapGisSettingsViewModel _mapGis;

	/// <summary>The map's background, flood zones at start, the map pieces kept on this Mac, and owner names in GIS files.</summary>
	public MapGisSettingsViewModel MapGis => _mapGis ??= new MapGisSettingsViewModel(_app);
}
