using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sde.App.Maps;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>
/// The Map page. The <see cref="MapCanvas"/> draws and reports clicks; this class hands it the dots and layers, turns
/// its events into view-model calls, and keeps the two in step (basemap, mode, the structure picked in the list, the
/// area in view). When the page closes, the map engine is stopped.
/// </summary>
public partial class MapView : UserControl
{
	private MapViewModel _vm;
	private bool _fitted;

	public MapView()
	{
		InitializeComponent();
		Surface.MarkerClicked += (id, add) => _vm?.ClickStructure(id, add);
		Surface.FeatureClicked += (layer, feature, add) => _vm?.ClickFeature(layer, feature, add);
		Surface.MapClicked += (lat, lon) => _vm?.ClickMap(lat, lon);
		Surface.AreaSelected += (ids, add) => _vm?.SelectArea(ids.ToList(), add);
		Surface.FeaturesSelected += (layer, features, add) => _vm?.SelectFeatures(layer, features, add);
		Surface.SketchChanged += (points, finished) => _vm?.SketchChanged(points, finished);
		Surface.ViewChanged += OnViewChanged;
		Surface.SizeChanged += OnCanvasSizeChanged;
		ZoomInButton.Click += (_, _) => Surface.ZoomBy(1);
		ZoomOutButton.Click += (_, _) => Surface.ZoomBy(-1);
		ActualThemeVariantChanged += (_, _) => Rebuild();
		PointList.DoubleTapped += (_, _) => _vm?.OpenSelectedCommand.Execute(null);
		TableGrid.SelectionChanged += OnTableSelectionChanged;
		TableGrid.DoubleTapped += (_, _) => _vm?.ZoomToRowCommand.Execute(TableGrid.SelectedItem as AttributeRowViewModel);
	}

	private bool _fillingTable;

	/// <summary>Rows picked in the table are selected on the map (not while the table is being filled).</summary>
	private void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_vm != null && !_fillingTable && TableGrid.SelectedItems.Count > 0)
		{
			_vm.SelectTableRows(TableGrid.SelectedItems.OfType<AttributeRowViewModel>().ToList());
		}
	}

	/// <summary>Makes the table's columns for the layer it shows: one per field, sorting by the field's value.</summary>
	private void BuildTableColumns()
	{
		_fillingTable = true;
		TableGrid.Columns.Clear();
		foreach (AttributeColumn column in _vm?.TableColumns ?? Array.Empty<AttributeColumn>())
		{
			TableGrid.Columns.Add(new DataGridTextColumn
			{
				Header = column.Header,
				Binding = new Avalonia.Data.Binding($"[{column.Index}]"),
				CustomSortComparer = new AttributeComparer(column.Index),
				CanUserSort = true,
				MaxWidth = 320,
			});
		}
		_fillingTable = false;
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
		OnViewChanged();
	}

	private void OnViewChanged()
	{
		if (_vm != null && Surface.Bounds.Width > 0)
		{
			_vm.ViewExtent = Surface.Extent;
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
		_vm.LayersChanged += ShowLayers;
		_vm.FitRequested += Surface.FitToMarkers;
		_vm.CenterRequested += Center;
		_vm.ZoomToRequested += ZoomTo;
		_vm.RetryRequested += Surface.RetryTiles;
		_vm.SelectionChanged += ShowSelection;
		_vm.TableColumnsChanged += BuildTableColumns;
		_vm.PropertyChanged += VmChanged;
		Surface.Tiles = _vm.Tiles;
		Surface.Basemap = _vm.Basemap;
		ApplyMode();
		Rebuild();
		ShowLayers();
		BuildTableColumns();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		Detach();
		// The page closed: stop the map engine (its redraw loop, downloads and layers).
		Surface.Dispose();
	}

	private void Detach()
	{
		if (_vm == null)
		{
			return;
		}
		_vm.MarkersChanged -= Rebuild;
		_vm.LayersChanged -= ShowLayers;
		_vm.FitRequested -= Surface.FitToMarkers;
		_vm.CenterRequested -= Center;
		_vm.ZoomToRequested -= ZoomTo;
		_vm.RetryRequested -= Surface.RetryTiles;
		_vm.SelectionChanged -= ShowSelection;
		_vm.TableColumnsChanged -= BuildTableColumns;
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
				ApplyMode();
				break;
			case nameof(MapViewModel.Selected):
				Surface.Highlighted = _vm.Selected?.Id;
				break;
		}
	}

	/// <summary>What a click and a drag do on the map in the page's mode.</summary>
	private void ApplyMode()
	{
		Surface.Drag = _vm.IsSelect ? MapDrag.Box : MapDrag.Pan;
		Surface.Cursor = new Avalonia.Input.Cursor(_vm.IsSetLocation ? Avalonia.Input.StandardCursorType.Cross : Avalonia.Input.StandardCursorType.Arrow);
	}

	private void Center(MapPointViewModel p) => Surface.CenterOn(p.Latitude, p.Longitude);

	private void ZoomTo(MapExtent extent) => Surface.FitTo(extent);

	private void ShowLayers()
	{
		if (_vm != null)
		{
			Surface.ShowLayers(_vm.MapLayers());
		}
	}

	/// <summary>Rings the selected structures.</summary>
	private void ShowSelection()
	{
		if (_vm != null)
		{
			Surface.SelectedMarkers = _vm.SelectedStructures();
		}
	}

	/// <summary>
	/// Hands the visible structures to the map: each in the colour of its result class (the theme's), or in its
	/// category's colour when the structures are coloured by something else, with its label when labels are on.
	/// </summary>
	private void Rebuild()
	{
		if (_vm == null)
		{
			return;
		}
		Dictionary<string, IBrush> brushes = new();
		IReadOnlyDictionary<Guid, string> fills = _vm.ColoursByResult ? null : _vm.StructureFills();
		IReadOnlyDictionary<Guid, string> labels = _vm.StructureLabels();
		List<MapMarker> markers = new(_vm.Visible.Count);
		bool shown = _vm.StructuresLayer?.IsVisible ?? true;
		foreach (MapPointViewModel p in shown ? (IEnumerable<MapPointViewModel>)_vm.Visible : Array.Empty<MapPointViewModel>())
		{
			string key;
			if (fills == null)
			{
				key = "Status" + p.Class + "Fg";
			}
			else if (!fills.TryGetValue(p.Id, out key))
			{
				// Its category is unticked in the style.
				continue;
			}
			if (!brushes.TryGetValue(key, out IBrush fill))
			{
				fill = fills == null
					? this.TryFindResource(key, ActualThemeVariant, out object r) && r is IBrush b ? b : Brushes.Gray
					: LegendLineViewModel.Brush(key);
				brushes[key] = fill;
			}
			markers.Add(new MapMarker(p.Id, p.Latitude, p.Longitude, fill, labels != null && labels.TryGetValue(p.Id, out string text) ? text : null));
		}
		Surface.MarkerLabels = labels == null ? null : _vm.StructuresLayer.Labels;
		Surface.Markers = markers;
		Surface.Highlighted = _vm.Selected?.Id;
		ShowSelection();
	}
}
