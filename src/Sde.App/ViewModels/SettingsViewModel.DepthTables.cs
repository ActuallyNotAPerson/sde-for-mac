namespace Sde.App.ViewModels;

/// <summary>Settings, then Depth tables (see <see cref="DepthTablesViewModel"/>).</summary>
public sealed partial class SettingsViewModel
{
	private DepthTablesViewModel _depthTables;

	/// <summary>The community's depth-damage tables, made when the Settings page first shows them.</summary>
	public DepthTablesViewModel DepthTables => _depthTables ??= new DepthTablesViewModel(_app);
}
