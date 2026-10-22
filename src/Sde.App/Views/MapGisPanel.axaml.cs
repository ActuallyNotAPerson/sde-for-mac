using Avalonia;
using Avalonia.Controls;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>Settings › Map &amp; GIS: the background map, flood zones at start, the saved map pieces, and owner names in GIS files.</summary>
public partial class MapGisPanel : UserControl
{
	public MapGisPanel() => InitializeComponent();

	/// <summary>Settings opened: the Map page may have changed the background since, or saved more map pieces.</summary>
	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (DataContext is MapGisSettingsViewModel vm)
		{
			_ = vm.Refresh();
		}
	}
}
