using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.Reports;

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
		Surface.EditFeaturePicked += feature => _vm?.PickEditFeature(feature);
		Surface.EditShapeChanged += (shape, what) => _vm?.ChangeEditShape(shape, what);
		Surface.EditPointAdded += point => _vm?.AddEditPoint(point);
		Surface.EditDeleteRequested += () => _vm?.DeleteEditFeatureCommand.Execute(null);
		Surface.EditRefused += reason =>
		{
			if (_vm != null)
			{
				_vm.Message = reason;
			}
		};
		Surface.MarkerDragged += (id, to) => _ = _vm?.DragStructureAsync(id, to);
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
		bool editable = _vm?.TableEditable == true;
		foreach (AttributeColumn column in _vm?.TableColumns ?? Array.Empty<AttributeColumn>())
		{
			TableGrid.Columns.Add(new DataGridTextColumn
			{
				Header = column.Header,
				// While the layer is being edited, a typed value goes back to the row, which checks it.
				Binding = new Avalonia.Data.Binding($"[{column.Index}]") { Mode = editable ? Avalonia.Data.BindingMode.TwoWay : Avalonia.Data.BindingMode.OneWay },
				CustomSortComparer = new AttributeComparer(column.Index),
				CanUserSort = true,
				IsReadOnly = !editable,
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
		_vm.ClearSketchRequested += Surface.ClearSketch;
		_vm.ShowViewRequested += ShowView;
		_vm.MoveAnswered += Surface.ClearPendingMove;
		_vm.PropertyChanged += VmChanged;
		_vm.PrintScene = BuildPrintScene;
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
		_vm.ClearSketchRequested -= Surface.ClearSketch;
		_vm.ShowViewRequested -= ShowView;
		_vm.MoveAnswered -= Surface.ClearPendingMove;
		_vm.PropertyChanged -= VmChanged;
		if (_vm.PrintScene == BuildPrintScene)
		{
			_vm.PrintScene = null;
		}
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
			case nameof(MapViewModel.Sketch):
			case nameof(MapViewModel.SelectLayerKey):
			case nameof(MapViewModel.EditLayer):
			case nameof(MapViewModel.EditTool):
			case nameof(MapViewModel.SnapToShapes):
				ApplyMode();
				break;
			case nameof(MapViewModel.EditFeature):
				Surface.EditFeature = _vm.EditFeature;
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
		Surface.SelectLayer = _vm.IsSelect ? _vm.SelectLayerKey : null;
		Surface.EditLayer = _vm.IsEdit ? _vm.EditLayer?.Key : null;
		Surface.EditTool = _vm.EditTool;
		Surface.EditFeature = _vm.IsEdit ? _vm.EditFeature : null;
		Surface.Snap = _vm.SnapToShapes;
		Surface.MarkersDraggable = _vm.IsSetLocation;
		if (Surface.Sketch != _vm.Sketch)
		{
			Surface.Sketch = _vm.Sketch;
		}
		bool cross = _vm.IsSetLocation || _vm.Sketch != MapSketch.None || _vm.IsEdit && _vm.EditTool == MapEditTool.AddPoint;
		Surface.Cursor = new Avalonia.Input.Cursor(cross ? Avalonia.Input.StandardCursorType.Cross : Avalonia.Input.StandardCursorType.Arrow);
	}

	/// <summary>Shows exactly the area of a bookmark.</summary>
	private void ShowView(MapExtent extent) => Surface.FitTo(extent, MapCanvas.MaxZoom, margin: 0);

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
		(List<MapMarker> markers, IReadOnlyDictionary<Guid, string> labels) = BuildMarkers(ActualThemeVariant);
		Surface.MarkerLabels = labels == null ? null : _vm.StructuresLayer.Labels;
		Surface.Markers = markers;
		Surface.Highlighted = _vm.Selected?.Id;
		ShowSelection();
	}

	/// <summary>
	/// The structure dots to draw: each in the colour of its result class (the theme's), or in its category's colour
	/// when the structures are coloured by something else, with its label when labels are on.
	/// </summary>
	private (List<MapMarker> Markers, IReadOnlyDictionary<Guid, string> Labels) BuildMarkers(ThemeVariant theme)
	{
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
				fill = fills == null ? ThemeBrushFor(key, theme) : LegendLineViewModel.Brush(key);
				brushes[key] = fill;
			}
			markers.Add(new MapMarker(p.Id, p.Latitude, p.Longitude, fill, labels != null && labels.TryGetValue(p.Id, out string text) ? text : null));
		}
		return (markers, labels);
	}

	private IBrush ThemeBrushFor(string key, ThemeVariant theme) => this.TryFindResource(key, theme, out object r) && r is IBrush b ? b : Brushes.Gray;

	// ───────────── Printing ─────────────

	/// <summary>The map as it is on screen, in the light theme's colours (paper is white), with its legend.</summary>
	private MapPrintScene BuildPrintScene()
	{
		if (_vm == null)
		{
			return null;
		}
		(List<MapMarker> markers, IReadOnlyDictionary<Guid, string> labels) = BuildMarkers(ThemeVariant.Light);
		// The picture is drawn off the UI thread: brushes that belong to no thread.
		Dictionary<IBrush, IBrush> frozen = new Dictionary<IBrush, IBrush>();
		for (int i = 0; i < markers.Count; i++)
		{
			IBrush fill = markers[i].Fill ?? Brushes.Gray;
			if (!frozen.TryGetValue(fill, out IBrush still))
			{
				Color c = fill is ISolidColorBrush solid ? solid.Color : Colors.Gray;
				still = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c, fill.Opacity);
				frozen[fill] = still;
			}
			markers[i] = markers[i] with { Fill = still };
		}
		(double x, double y, double resolution) = Surface.ViewInMetres;
		IReadOnlyList<object> layers = _vm.MapLayers();
		return new MapPrintScene
		{
			CenterX = x,
			CenterY = y,
			Resolution = resolution,
			ScreenWidth = Surface.Bounds.Width,
			ScreenHeight = Surface.Bounds.Height,
			Basemap = _vm.Basemap,
			Overlays = layers.OfType<MapTileLayer>().Where(l => l.Visible).ToList(),
			Layers = layers.OfType<MapFeatureLayer>().Where(l => l.Visible).ToList(),
			Markers = markers,
			MarkerLabels = labels == null ? null : _vm.StructuresLayer.Labels,
			Legend = PrintLegend(),
		};
	}

	/// <summary>The printed legend: the structures (by result, or by the field they are coloured by), then each layer shown, top first.</summary>
	private List<MapLegendGroup> PrintLegend()
	{
		List<MapLegendGroup> groups = new List<MapLegendGroup>();
		if (_vm.StructuresLayer?.IsVisible ?? true)
		{
			List<MapLegendItem> items = _vm.ColoursByResult
				? _vm.Classes.Where(c => c.IsOn && c.Count > 0).Select(c => new MapLegendItem(c.Label, MapLegendShape.Dot, Hex(ThemeBrushFor(c.ColourKey, ThemeVariant.Light)), "#FFFFFF")).ToList()
				: _vm.StructureLegend.Select(l => new MapLegendItem(l.Label, MapLegendShape.Dot, Hex(l.Fill), "#FFFFFF")).ToList();
			string by = _vm.ColoursByResult ? "result" : (_vm.StructureLegendTitle ?? "").Replace("COLOURED BY ", "", StringComparison.Ordinal).ToLowerInvariant();
			groups.Add(new MapLegendGroup("Structures, by " + by, items));
		}
		foreach (MapLayerViewModel layer in _vm.Layers.Where(l => !l.IsStructures && l.IsVisible))
		{
			if (layer.IsTiles)
			{
				groups.Add(new MapLegendGroup(layer.Name, new[] { new MapLegendItem("Flood hazard zones from FEMA's National Flood Hazard Layer", MapLegendShape.Area, "#5B8FD9B3", "#3D6FB8") }));
			}
			else if (layer.Data != null)
			{
				MapLegendShape shape = layer.GeometryKind switch
				{
					Sde.Gis.Features.GeometryKind.Point => MapLegendShape.Dot,
					Sde.Gis.Features.GeometryKind.Line => MapLegendShape.Line,
					_ => MapLegendShape.Area,
				};
				groups.Add(new MapLegendGroup(layer.Name, layer.Legend.Select(l => new MapLegendItem(l.Label, shape, Hex(l.Fill), Hex(l.Stroke))).ToList()));
			}
		}
		return groups;
	}

	/// <summary>"#RRGGBBAA" for a solid brush (grey otherwise).</summary>
	private static string Hex(IBrush brush)
	{
		Color c = brush is ISolidColorBrush s ? s.Color : Colors.Gray;
		return $"#{c.R:X2}{c.G:X2}{c.B:X2}{(byte)Math.Round(c.A * (brush?.Opacity ?? 1)):X2}";
	}
}
