using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Sde.App.Maps;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>
/// The Map page. The <see cref="MapCanvas"/> draws and reports clicks; this class hands the dots to it, turns its
/// events into view-model calls, and keeps the two in step (basemap, mode, the structure picked in the list).
/// </summary>
public partial class MapView : UserControl
{
	private MapViewModel _vm;
	private bool _fitted;

	public MapView()
	{
		InitializeComponent();
		Surface.MarkerClicked += id => _vm?.ClickStructure(id);
		Surface.MapClicked += (lat, lon) => _vm?.ClickMap(lat, lon);
		Surface.AreaSelected += (ids, add) => _vm?.SelectArea(ids.ToList(), add);
		Surface.SizeChanged += OnCanvasSizeChanged;
		ZoomInButton.Click += (_, _) => Surface.ZoomBy(1);
		ZoomOutButton.Click += (_, _) => Surface.ZoomBy(-1);
		ActualThemeVariantChanged += (_, _) => Rebuild();
		PointList.DoubleTapped += (_, _) => _vm?.OpenSelectedCommand.Execute(null);
	}

	/// <summary>The map control, for tests that need its screen positions.</summary>
	public MapCanvas Map => Surface;

	private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (!_fitted && e.NewSize.Width > 20 && e.NewSize.Height > 20)
		{
			_fitted = true;
			Surface.FitToMarkers();
		}
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		Detach();
		_vm = DataContext as MapViewModel;
		if (_vm == null)
		{
			return;
		}
		_vm.MarkersChanged += Rebuild;
		_vm.FitRequested += Surface.FitToMarkers;
		_vm.CenterRequested += Center;
		_vm.PropertyChanged += VmChanged;
		Surface.Tiles = _vm.Tiles;
		Surface.Basemap = _vm.Basemap;
		Surface.Drag = _vm.IsSelect ? MapDrag.Box : MapDrag.Pan;
		Surface.Cursor = new Avalonia.Input.Cursor(_vm.IsSetLocation ? Avalonia.Input.StandardCursorType.Cross : Avalonia.Input.StandardCursorType.Arrow);
		Rebuild();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		Detach();
	}

	private void Detach()
	{
		if (_vm == null)
		{
			return;
		}
		_vm.MarkersChanged -= Rebuild;
		_vm.FitRequested -= Surface.FitToMarkers;
		_vm.CenterRequested -= Center;
		_vm.PropertyChanged -= VmChanged;
		Surface.Tiles = null;
		_vm = null;
	}

	private void VmChanged(object sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(MapViewModel.Basemap):
				Surface.Basemap = _vm.Basemap;
				break;
			case nameof(MapViewModel.Mode):
				Surface.Drag = _vm.IsSelect ? MapDrag.Box : MapDrag.Pan;
				Surface.Cursor = new Avalonia.Input.Cursor(_vm.IsSetLocation ? Avalonia.Input.StandardCursorType.Cross : Avalonia.Input.StandardCursorType.Arrow);
				break;
			case nameof(MapViewModel.Selected):
				Surface.Highlighted = _vm.Selected?.Id;
				break;
		}
	}

	private void Center(MapPointViewModel p) => Surface.CenterOn(p.Latitude, p.Longitude);

	/// <summary>Hands the visible structures to the map, each in the colour of its result class.</summary>
	private void Rebuild()
	{
		if (_vm == null)
		{
			return;
		}
		Dictionary<Sde.Core.Assessments.DeterminationClass, IBrush> brushes = new();
		List<MapMarker> markers = new(_vm.Visible.Count);
		foreach (MapPointViewModel p in _vm.Visible)
		{
			if (!brushes.TryGetValue(p.Class, out IBrush fill))
			{
				fill = this.TryFindResource("Status" + p.Class + "Fg", ActualThemeVariant, out object r) && r is IBrush b ? b : Brushes.Gray;
				brushes[p.Class] = fill;
			}
			markers.Add(new MapMarker(p.Id, p.Latitude, p.Longitude, fill));
		}
		Surface.Markers = markers;
	}
}
