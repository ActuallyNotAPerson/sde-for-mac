using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Styles;
using Mapsui.Tiling.Fetcher;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using Sde.Gis.Features;
using Brush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using Pen = Mapsui.Styles.Pen;
using Geometry = NetTopologySuite.Geometries.Geometry;
using Point = Avalonia.Point;

namespace Sde.App.Maps;

/// <summary>
/// The Map page's map: tiles (a basemap and overlays such as FEMA's flood zones), layers of features, and a dot for
/// each structure on top. Drag to move, scroll or pinch to zoom, arrow keys and = / - from the keyboard. Command-drag
/// (or <see cref="Drag"/> set to Box) draws a selection box; <see cref="Sketch"/> turns clicks into the corners of a
/// line or polygon. It knows nothing about structures beyond the markers it is given, so the page decides what a
/// click means.
/// </summary>
/// <remarks>
/// This is the only class that holds the map engine's control (Mapsui's MapControl); with MapsuiLayers.cs,
/// MapLabels.cs and SdeTileSource.cs it is all the code that knows about Mapsui, so a new major version of the engine is
/// these files' problem. Clicks are handled here rather than by the engine (which waits 300 ms for a double click and
/// only hits a dot's own pixels): a click within 11 px of a dot hits it, found through an STRtree.
/// </remarks>
public sealed class MapCanvas : Panel, IDisposable
{
	public const double MinZoom = 2;
	public const double MaxZoom = 19;

	/// <summary>How close (in pixels) a click has to be to a dot to count as hitting it.</summary>
	private const double HitRadius = 11;

	/// <summary>How close (in pixels) a click has to be to a point or line of a layer.</summary>
	private const double FeatureHitRadius = 6;

	/// <summary>A structure dot's diameter in pixels.</summary>
	private const double DotDiameter = 13;

	/// <summary>A press that moves less than this (pixels) is a click, not a drag.</summary>
	private const double DragThreshold = 5;

	/// <summary>How far an arrow key moves the map, in pixels.</summary>
	private const double KeyPanPixels = 80;

	private readonly Canvas _overlay = new Canvas { IsHitTestVisible = false };
	private readonly Rectangle _box = new Rectangle { StrokeThickness = 1.5, IsVisible = false };
	private readonly Polyline _sketchLine = new Polyline { StrokeThickness = 2.5, IsVisible = false };
	private readonly Avalonia.Controls.Shapes.Polygon _sketchArea = new Avalonia.Controls.Shapes.Polygon { StrokeThickness = 2.5, IsVisible = false };
	private readonly List<LatLon> _sketch = new List<LatLon>();
	private readonly Dispatcher _ui;
	private bool _sketchFinished;
	private readonly Dictionary<string, BuiltFeatureLayer> _built = new Dictionary<string, BuiltFeatureLayer>();
	private readonly Dictionary<string, (TileLayer Layer, SdeTileSource Source)> _overlays = new Dictionary<string, (TileLayer, SdeTileSource)>();
	private MapControl _control;
	private MapTheme _theme = new MapTheme();
	private TileLayer _basemapLayer;
	private SdeTileSource _basemapSource;
	private MemoryLayer _structures;
	private MemoryLayer _rings;
	private MemoryLayer _structureLabels;
	private STRtree<int> _markerIndex = new STRtree<int>();
	private (double X, double Y)[] _markerMetres = Array.Empty<(double, double)>();
	private IReadOnlyList<MapMarker> _markers = Array.Empty<MapMarker>();
	private IReadOnlyList<object> _layers = Array.Empty<object>();
	private IReadOnlyCollection<Guid> _selectedMarkers = Array.Empty<Guid>();
	private Point? _pressedAt;
	private bool _boxing;
	private bool _moved;
	private bool _disposed;

	static MapCanvas()
	{
		MapLabelRenderer.Register();
		FocusableProperty.OverrideDefaultValue<MapCanvas>(true);
		ClipToBoundsProperty.OverrideDefaultValue<MapCanvas>(true);
		Mapsui.Logging.Logger.LogDelegate = (level, message, ex) =>
		{
			if (level <= Mapsui.Logging.LogLevel.Error && ex != null)
			{
				Services.AppLog.Write("Map: " + message, ex);
			}
		};
	}

	public MapCanvas()
	{
		_ui = Dispatcher.UIThread;
		_overlay.Children.Add(_sketchArea);
		_overlay.Children.Add(_sketchLine);
		_overlay.Children.Add(_box);
		CreateControl();
		AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
		AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
		AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
		ActualThemeVariantChanged += (_, _) => ApplyTheme();
	}

	// ───────────── Properties the page sets ─────────────

	/// <summary>Where tiles come from; null (or a basemap with no tiles) shows the plain background.</summary>
	public TileCache Tiles
	{
		get => _tiles;
		set
		{
			_tiles = value;
			ShowBasemap();
			ShowLayers(_layers);
		}
	}

	private TileCache _tiles;

	public Basemap Basemap
	{
		get => _basemap;
		set
		{
			_basemap = value ?? Basemaps.None;
			_tiles?.ResetFailures();
			ShowBasemap();
		}
	}

	private Basemap _basemap = Basemaps.None;

	public MapDrag Drag { get; set; } = MapDrag.Pan;

	/// <summary>What clicks draw; changing it starts a new shape.</summary>
	public MapSketch Sketch
	{
		get => _sketchKind;
		set
		{
			_sketchKind = value;
			ClearSketch();
		}
	}

	private MapSketch _sketchKind;

	/// <summary>The structure to ring (the one picked in the list beside the map), if any.</summary>
	public Guid? Highlighted
	{
		get => _highlighted;
		set
		{
			_highlighted = value;
			ShowRings();
		}
	}

	private Guid? _highlighted;

	/// <summary>The structures that are selected (ringed in the accent colour).</summary>
	public IReadOnlyCollection<Guid> SelectedMarkers
	{
		get => _selectedMarkers;
		set
		{
			_selectedMarkers = value ?? Array.Empty<Guid>();
			ShowRings();
		}
	}

	/// <summary>The structure dots, drawn in this order (the last on top).</summary>
	public IReadOnlyList<MapMarker> Markers
	{
		get => _markers;
		set
		{
			_markers = value ?? Array.Empty<MapMarker>();
			ShowMarkers();
		}
	}

	/// <summary>How structure labels look (their text is each marker's Label); null for no labels.</summary>
	public Sde.Gis.Styles.LabelStyle MarkerLabels
	{
		get => _markerLabels;
		set
		{
			_markerLabels = value;
			ShowMarkers();
		}
	}

	private Sde.Gis.Styles.LabelStyle _markerLabels;

	/// <summary>
	/// The key of the layer that clicks and boxes select in (in Select mode); null selects structures. In other modes a
	/// click on a layer's feature reports it with <see cref="FeatureClicked"/>.
	/// </summary>
	public string SelectLayer { get; set; }

	/// <summary>The screen's pixel density (2 on a Retina screen), for sharper tiles.</summary>
	public double PixelDensity => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

	// ───────────── Where the map is ─────────────

	public double Zoom => WebMercator.ZoomOf(Viewport.Resolution);

	public double CenterLatitude => WebMercator.FromMetres(Viewport.CenterX, Viewport.CenterY).Latitude;

	public double CenterLongitude => WebMercator.FromMetres(Viewport.CenterX, Viewport.CenterY).Longitude;

	/// <summary>The map scale denominator at the centre (24000 for 1:24,000).</summary>
	public double Scale => WebMercator.Scale(Viewport.Resolution, CenterLatitude);

	/// <summary>The area in view, in longitude and latitude.</summary>
	public MapExtent Extent
	{
		get
		{
			(double lat0, double lon0) = ToLatLon(new Point(0, Bounds.Height));
			(double lat1, double lon1) = ToLatLon(new Point(Bounds.Width, 0));
			return new MapExtent(lon0, lat0, lon1, lat1);
		}
	}

	/// <summary>Where the map is: the engine's view, or the one kept while the page is closed.</summary>
	private Viewport Viewport => _control?.Map.Navigator.Viewport ?? _parked;

	private Viewport _parked = new Viewport(WebMercator.ToMetres(-96.0, 38.5).X, WebMercator.ToMetres(-96.0, 38.5).Y, WebMercator.Resolution(4), 0, 0, 0);

	/// <summary>The corners of the line or polygon being drawn.</summary>
	public IReadOnlyList<LatLon> SketchPoints => _sketch;

	// ───────────── Events ─────────────

	/// <summary>A dot was clicked. True when Command was held (adds to a selection).</summary>
	public event Action<Guid, bool> MarkerClicked;

	/// <summary>A feature of a layer was clicked: the layer's key, the feature, and whether Command was held.</summary>
	public event Action<string, Feature, bool> FeatureClicked;

	/// <summary>Empty map was clicked (not a drag): latitude and longitude of the spot.</summary>
	public event Action<double, double> MapClicked;

	/// <summary>A box was drawn: the dots inside it (maybe none). True when Command was held to draw it, which adds to the selection.</summary>
	public event Action<IReadOnlyList<Guid>, bool> AreaSelected;

	/// <summary>A box was drawn while <see cref="SelectLayer"/> names a layer: that layer's key, the features in it, and Command.</summary>
	public event Action<string, IReadOnlyList<Feature>, bool> FeaturesSelected;

	/// <summary>A corner was added to (or taken from) the shape being drawn; true when the shape was finished.</summary>
	public event Action<IReadOnlyList<LatLon>, bool> SketchChanged;

	/// <summary>The map moved or zoomed.</summary>
	public event Action ViewChanged;

	// ───────────── View ─────────────

	public void SetView(double latitude, double longitude, double zoom)
	{
		(double x, double y) = WebMercator.ToMetres(longitude, latitude);
		Navigate(x, y, WebMercator.Resolution(Math.Clamp(zoom, MinZoom, MaxZoom)));
	}

	public void ZoomBy(double steps)
	{
		Viewport v = Viewport;
		Navigate(v.CenterX, v.CenterY, WebMercator.Resolution(Math.Clamp(Zoom + steps, MinZoom, MaxZoom)));
	}

	/// <summary>Moves the map at once (no animation). While the page is closed, the view is kept for when it opens again.</summary>
	private void Navigate(double x, double y, double resolution)
	{
		if (_control == null)
		{
			_parked = _parked with { CenterX = x, CenterY = y, Resolution = resolution };
			return;
		}
		_control.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), resolution, 0);
		Refresh();
	}

	/// <summary>Shows every dot (or the whole country when there are none).</summary>
	public void FitToMarkers()
	{
		if (_markers.Count == 0 || Bounds.Width < 10 || Bounds.Height < 10)
		{
			SetView(38.5, -96.0, 4);
			return;
		}
		FitTo(new MapExtent(_markers.Min(m => m.Longitude), _markers.Min(m => m.Latitude), _markers.Max(m => m.Longitude), _markers.Max(m => m.Latitude)), 17);
	}

	/// <summary>Shows an area (with a margin of <paramref name="margin"/> pixels in all), no closer than <paramref name="maxZoom"/>.</summary>
	public void FitTo(MapExtent extent, double maxZoom = 18, double margin = 80)
	{
		if (Bounds.Width < 10 || Bounds.Height < 10)
		{
			return;
		}
		double minU = WebMercator.U(extent.MinLongitude);
		double maxU = WebMercator.U(extent.MaxLongitude);
		double minV = WebMercator.V(extent.MaxLatitude);
		double maxV = WebMercator.V(extent.MinLatitude);
		double spanU = Math.Max(maxU - minU, 1e-7);
		double spanV = Math.Max(maxV - minV, 1e-7);
		// Leave a margin so the outer dots aren't on the edge.
		double zoomU = Math.Log2(Math.Max(Bounds.Width - margin, 40) / (WebMercator.TileSize * spanU));
		double zoomV = Math.Log2(Math.Max(Bounds.Height - margin, 40) / (WebMercator.TileSize * spanV));
		double zoom = Math.Clamp(Math.Min(Math.Min(zoomU, zoomV), maxZoom), MinZoom, MaxZoom);
		SetView(WebMercator.Latitude((minV + maxV) / 2), WebMercator.Longitude((minU + maxU) / 2), zoom);
	}

	/// <summary>Moves the map so the point is in view, without changing the zoom unless it is far out.</summary>
	public void CenterOn(double latitude, double longitude, double atLeastZoom = 15) =>
		SetView(latitude, longitude, Math.Max(Zoom, atLeastZoom));

	/// <summary>Where a latitude and longitude is on this control.</summary>
	public Point ToScreen(double latitude, double longitude)
	{
		(double x, double y) = WebMercator.ToMetres(longitude, latitude);
		(double sx, double sy) = Viewport.WorldToScreenXY(x, y);
		return new Point(sx, sy);
	}

	/// <summary>The latitude and longitude under a point on this control.</summary>
	public (double Latitude, double Longitude) ToLatLon(Point p)
	{
		(double x, double y) = Viewport.ScreenToWorldXY(p.X, p.Y);
		x = Math.Clamp(x, -WebMercator.HalfWorld, WebMercator.HalfWorld);
		y = Math.Clamp(y, -WebMercator.HalfWorld, WebMercator.HalfWorld);
		(double lon, double lat) = WebMercator.FromMetres(x, y);
		return (lat, lon);
	}

	/// <summary>The nearest dot within reach of a point (the topmost when two are as close), or null.</summary>
	public Guid? MarkerAt(Point p)
	{
		double resolution = Viewport.Resolution;
		if (_markers.Count == 0 || !(resolution > 0))
		{
			return null;
		}
		(double x, double y) = Viewport.ScreenToWorldXY(p.X, p.Y);
		double reach = HitRadius * resolution;
		int best = -1;
		double bestDistance = double.MaxValue;
		foreach (int i in _markerIndex.Query(new Envelope(x - reach, x + reach, y - reach, y + reach)))
		{
			double dx = _markerMetres[i].X - x;
			double dy = _markerMetres[i].Y - y;
			double d = Math.Sqrt(dx * dx + dy * dy);
			if (d <= reach && (d < bestDistance || d == bestDistance && i > best))
			{
				best = i;
				bestDistance = d;
			}
		}
		return best < 0 ? null : _markers[best].Id;
	}

	/// <summary>The dots inside a rectangle of the control.</summary>
	public IReadOnlyList<Guid> MarkersIn(Rect area)
	{
		Envelope box = WorldBox(area);
		return _markerIndex.Query(box).OrderBy(i => i).Where(i => box.Contains(_markerMetres[i].X, _markerMetres[i].Y)).Select(i => _markers[i].Id).ToList();
	}

	/// <summary>The topmost feature of a visible layer under a point, with its layer's key, or null.</summary>
	public (string Layer, Feature Feature)? FeatureAt(Point p, string onlyLayer = null)
	{
		(double x, double y) = Viewport.ScreenToWorldXY(p.X, p.Y);
		double reach = FeatureHitRadius * Viewport.Resolution;
		foreach (object layer in _layers.Reverse())
		{
			if (layer is MapFeatureLayer spec && spec.Visible && (onlyLayer == null || spec.Key == onlyLayer)
				&& _built.TryGetValue(spec.Key, out BuiltFeatureLayer built) && built.Layer.Enabled && built.FeatureAt(x, y, reach) is Feature f)
			{
				return (spec.Key, f);
			}
		}
		return null;
	}

	/// <summary>The features of a layer that touch a rectangle of the control.</summary>
	public IReadOnlyList<Feature> FeaturesIn(string layer, Rect area)
	{
		if (layer == null || !_built.TryGetValue(layer, out BuiltFeatureLayer built))
		{
			return Array.Empty<Feature>();
		}
		Envelope box = WorldBox(area);
		Geometry boxShape = new GeometryFactory().ToGeometry(box);
		return built.Query(box).OrderBy(i => i).Where(i => built.Shapes[i].Intersects(boxShape)).Select(i => built.Features[i]).ToList();
	}

	private Envelope WorldBox(Rect area)
	{
		(double x0, double y0) = Viewport.ScreenToWorldXY(area.Left, area.Top);
		(double x1, double y1) = Viewport.ScreenToWorldXY(area.Right, area.Bottom);
		return new Envelope(x0, x1, y0, y1);
	}

	// ───────────── Layers ─────────────

	/// <summary>
	/// The layers to draw over the basemap, bottom first: <see cref="MapFeatureLayer"/> and <see cref="MapTileLayer"/>.
	/// Layers already drawn are kept when their revision hasn't changed.
	/// </summary>
	public void ShowLayers(IReadOnlyList<object> layers)
	{
		_layers = layers ?? Array.Empty<object>();
		if (_control == null)
		{
			return;
		}
		HashSet<string> keys = new HashSet<string>();
		foreach (object layer in _layers)
		{
			switch (layer)
			{
				case MapFeatureLayer spec:
					keys.Add(spec.Key);
					if (!_built.TryGetValue(spec.Key, out BuiltFeatureLayer built) || built.Spec.Revision != spec.Revision || !ReferenceEquals(built.Spec.Data, spec.Data))
					{
						built?.Dispose();
						built = BuiltFeatureLayer.Build(spec, _theme, (float)PixelDensity);
						_built[spec.Key] = built;
					}
					else if (built.Spec.SelectionRevision != spec.SelectionRevision || built.Spec.Visible != spec.Visible || !ReferenceEquals(built.Spec.Display, spec.Display))
					{
						built.ShowSelection(spec, _theme);
					}
					built.Layer.Opacity = spec.Display?.ClampedOpacity ?? 1;
					break;
				case MapTileLayer tiles:
					keys.Add(tiles.Key);
					if (_tiles != null && (!_overlays.TryGetValue(tiles.Key, out (TileLayer Layer, SdeTileSource Source) o) || o.Source.Basemap != tiles.Source))
					{
						if (_overlays.TryGetValue(tiles.Key, out o))
						{
							o.Layer.Dispose();
							o.Source.Dispose();
						}
						SdeTileSource source = new SdeTileSource(tiles.Source, _tiles, PixelDensity > 1.5 ? 2 : 1);
						// Only the tiles of the zoom level shown: FEMA's map service draws each one on request.
						TileLayer layerOfTiles = new TileLayer(source, 50, 120, new MinimalDataFetchStrategy()) { Name = tiles.Source.Name };
						layerOfTiles.Attribution.Enabled = false;
						_overlays[tiles.Key] = (layerOfTiles, source);
					}
					if (_overlays.TryGetValue(tiles.Key, out o))
					{
						o.Layer.Opacity = tiles.Display?.ClampedOpacity ?? 1;
					}
					break;
			}
		}
		foreach (string gone in _built.Keys.Where(k => !keys.Contains(k)).ToList())
		{
			_built[gone].Dispose();
			_built.Remove(gone);
		}
		foreach (string gone in _overlays.Keys.Where(k => !keys.Contains(k)).ToList())
		{
			_overlays[gone].Layer.Dispose();
			_overlays[gone].Source.Dispose();
			_overlays.Remove(gone);
		}
		Arrange();
	}

	/// <summary>Puts the engine's layers in order: basemap, the page's layers, selection highlights, labels, structures.</summary>
	private void Arrange()
	{
		if (_control == null)
		{
			return;
		}
		List<ILayer> order = new List<ILayer>();
		if (_basemapLayer != null)
		{
			order.Add(_basemapLayer);
		}
		foreach (object layer in _layers)
		{
			if (layer is MapFeatureLayer spec && _built.TryGetValue(spec.Key, out BuiltFeatureLayer built))
			{
				order.Add(built.Layer);
			}
			else if (layer is MapTileLayer tiles && _overlays.TryGetValue(tiles.Key, out (TileLayer Layer, SdeTileSource Source) o))
			{
				order.Add(o.Layer);
			}
		}
		foreach (object layer in _layers)
		{
			if (layer is MapFeatureLayer spec && _built.TryGetValue(spec.Key, out BuiltFeatureLayer built))
			{
				order.Add(built.Selection);
			}
		}
		foreach (object layer in _layers)
		{
			if (layer is MapFeatureLayer spec && _built.TryGetValue(spec.Key, out BuiltFeatureLayer built) && built.Labels != null)
			{
				order.Add(built.Labels);
			}
		}
		order.Add(_structures);
		order.Add(_rings);
		if (_structureLabels != null)
		{
			order.Add(_structureLabels);
		}
		LayerCollection layers = _control.Map.Layers;
		if (!layers.SequenceEqual(order))
		{
			layers.ClearAllGroups();
			layers.Add(order.ToArray());
		}
		UpdateScaleVisibility();
		Refresh();
	}

	/// <summary>Turns each layer and its labels on or off for the current scale, and by their Visible setting.</summary>
	private void UpdateScaleVisibility()
	{
		if (_control == null)
		{
			return;
		}
		double scale = Scale;
		foreach (object layer in _layers)
		{
			if (layer is MapFeatureLayer spec && _built.TryGetValue(spec.Key, out BuiltFeatureLayer built))
			{
				bool on = spec.Visible && (spec.Display?.IsVisibleAt(scale) ?? true);
				built.Layer.Enabled = on;
				built.Selection.Enabled = on;
				if (built.Labels != null)
				{
					built.Labels.Enabled = on && spec.Labels.IsVisibleAt(scale);
				}
			}
			else if (layer is MapTileLayer tiles && _overlays.TryGetValue(tiles.Key, out (TileLayer Layer, SdeTileSource Source) o))
			{
				o.Layer.Enabled = tiles.Visible && (tiles.Display?.IsVisibleAt(scale) ?? true);
			}
		}
		if (_structureLabels != null)
		{
			_structureLabels.Enabled = _markerLabels?.IsVisibleAt(scale) ?? false;
		}
	}

	private void ShowBasemap()
	{
		if (_control == null)
		{
			return;
		}
		if (_basemapLayer != null && _basemapSource?.Basemap == _basemap && _tiles != null)
		{
			return;
		}
		_basemapLayer?.Dispose();
		_basemapSource?.Dispose();
		_basemapLayer = null;
		_basemapSource = null;
		if (_tiles != null && _basemap.HasTiles)
		{
			_basemapSource = new SdeTileSource(_basemap, _tiles, PixelDensity > 1.5 ? 2 : 1);
			// The tiles of the zoom level shown and of one level up (shown while the sharp ones load), no more.
			_basemapLayer = new TileLayer(_basemapSource, 100, 200, new DataFetchStrategy(1)) { Name = _basemap.Name };
			_basemapLayer.Attribution.Enabled = false;
		}
		Arrange();
		_control.Map.RefreshData();
	}

	/// <summary>Throws away the tiles that couldn't be had and asks for them again.</summary>
	public void RetryTiles()
	{
		_basemapLayer?.ClearCache();
		foreach ((TileLayer layer, SdeTileSource _) in _overlays.Values)
		{
			layer.ClearCache();
		}
		_control?.Map.RefreshData();
		Refresh();
	}

	private void ShowMarkers()
	{
		if (_control == null)
		{
			return;
		}
		Dictionary<MColor, IStyle> dots = new Dictionary<MColor, IStyle>();
		List<IFeature> features = new List<IFeature>(_markers.Count);
		List<IFeature> labels = new List<IFeature>();
		STRtree<int> index = new STRtree<int>();
		(double X, double Y)[] metres = new (double, double)[_markers.Count];
		for (int i = 0; i < _markers.Count; i++)
		{
			MapMarker m = _markers[i];
			(double x, double y) = WebMercator.ToMetres(m.Longitude, m.Latitude);
			metres[i] = (x, y);
			index.Insert(new Envelope(x, x, y, y), i);
			MColor fill = ToEngine(m.Fill, new MColor(128, 128, 128));
			if (!dots.TryGetValue(fill, out IStyle dot))
			{
				dot = new SymbolStyle
				{
					SymbolType = SymbolType.Ellipse,
					SymbolScale = DotDiameter / SymbolStyle.DefaultWidth,
					Fill = new Brush(fill),
					Outline = new Pen(_theme.DotOutline, 1.8),
				};
				dots[fill] = dot;
			}
			PointFeature f = new PointFeature(x, y);
			f.Styles.Add(dot);
			features.Add(f);
			if (_markerLabels != null && !string.IsNullOrWhiteSpace(m.Label))
			{
				PointFeature label = new PointFeature(x, y);
				label["text"] = m.Label;
				labels.Add(label);
			}
		}
		index.Build();
		_markerIndex = index;
		_markerMetres = metres;
		_structures ??= new MemoryLayer("Structures") { Style = null };
		_structures.Attribution.Enabled = false;
		_structures.Features = features;
		_structures.DataHasChanged();
		_structureLabels?.Dispose();
		_structureLabels = null;
		if (_markerLabels != null)
		{
			_structureLabels = new MemoryLayer("Structure labels")
			{
				Features = labels,
				Style = new MapLabelStyle
				{
					Text = f => f["text"] as string,
					Size = _markerLabels.Size > 0 ? _markerLabels.Size : 11,
					Color = _theme.Text,
					Halo = _markerLabels.Halo ? _theme.Halo : null,
					HaloWidth = _markerLabels.HaloWidth,
					Rise = DotDiameter / 2 + 3,
				},
			};
			_structureLabels.Attribution.Enabled = false;
		}
		ShowRings();
		Arrange();
	}

	private void ShowRings()
	{
		if (_control == null)
		{
			return;
		}
		SymbolStyle selectedRing = new SymbolStyle
		{
			SymbolType = SymbolType.Ellipse,
			SymbolScale = (DotDiameter + 7) / SymbolStyle.DefaultWidth,
			Fill = null,
			Outline = new Pen(_theme.Accent, 2.5),
		};
		SymbolStyle highlightRing = new SymbolStyle
		{
			SymbolType = SymbolType.Ellipse,
			SymbolScale = (DotDiameter + 12) / SymbolStyle.DefaultWidth,
			Fill = null,
			Outline = new Pen(_theme.Accent, 3),
		};
		HashSet<Guid> selected = _selectedMarkers as HashSet<Guid> ?? new HashSet<Guid>(_selectedMarkers);
		List<IFeature> rings = new List<IFeature>();
		for (int i = 0; i < _markers.Count; i++)
		{
			bool isSelected = selected.Contains(_markers[i].Id);
			bool isHighlighted = _highlighted == _markers[i].Id;
			if (!isSelected && !isHighlighted)
			{
				continue;
			}
			PointFeature f = new PointFeature(_markerMetres[i].X, _markerMetres[i].Y);
			f.Styles.Add(isHighlighted ? highlightRing : selectedRing);
			rings.Add(f);
		}
		_rings ??= new MemoryLayer("Selected structures") { Style = null };
		_rings.Attribution.Enabled = false;
		_rings.Features = rings;
		_rings.DataHasChanged();
		Refresh();
	}

	// ───────────── Theme ─────────────

	private void ApplyTheme()
	{
		_theme = new MapTheme
		{
			Background = ToEngine(Background ?? Resource("SubtleBg"), new MColor(242, 244, 246)),
			DotOutline = ToEngine(Resource("CardBg"), MColor.White),
			Accent = ToEngine(Resource("Accent"), new MColor(30, 90, 168)),
			Text = ToEngine(Resource("TextPrimary"), new MColor(22, 25, 29)),
			Halo = ToEngine(Resource("CardBg"), MColor.White),
		};
		IBrush accent = Resource("Accent") ?? Brushes.DodgerBlue;
		Avalonia.Media.Color a = accent is ISolidColorBrush s ? s.Color : Colors.DodgerBlue;
		_box.Stroke = accent;
		_box.Fill = new SolidColorBrush(Avalonia.Media.Color.FromArgb(50, a.R, a.G, a.B));
		_sketchLine.Stroke = accent;
		_sketchArea.Stroke = accent;
		_sketchArea.Fill = new SolidColorBrush(Avalonia.Media.Color.FromArgb(45, a.R, a.G, a.B));
		if (_control == null)
		{
			return;
		}
		_control.Map.BackColor = _theme.Background;
		// Every style that came from the theme is made again in the new colours.
		foreach (BuiltFeatureLayer built in _built.Values)
		{
			built.ShowSelection(built.Spec, _theme);
		}
		ShowMarkers();
	}

	private IBrush Resource(string key) =>
		this.TryFindResource(key, ActualThemeVariant, out object r) && r is IBrush b ? b : null;

	private static MColor ToEngine(IBrush brush, MColor fallback) =>
		brush is ISolidColorBrush s ? new MColor(s.Color.R, s.Color.G, s.Color.B, (int)Math.Round(s.Color.A * s.Opacity)) : fallback;

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == Panel.BackgroundProperty)
		{
			ApplyTheme();
		}
	}

	// ───────────── The engine's control ─────────────

	/// <summary>Makes the engine's control (again, if the page closed and came back).</summary>
	private void CreateControl()
	{
		_control = new MapControl { Focusable = false, UseFling = false };
		AutomationProperties.SetAccessibilityView(_control, AccessibilityView.Raw);
		Map map = _control.Map;
		map.CRS = "EPSG:3857";
		// No debug overlays (the engine's logging and performance widgets), no per-layer attribution widgets.
		map.Widgets.Clear();
		map.Navigator.RotationLock = true;
		map.Navigator.OverrideZoomBounds = new MMinMax(WebMercator.Resolution(MaxZoom), WebMercator.Resolution(MinZoom));
		map.Navigator.OverridePanBounds = new MRect(-WebMercator.HalfWorld, -WebMercator.HalfWorld, WebMercator.HalfWorld, WebMercator.HalfWorld);
		map.Navigator.MouseWheelAnimation.UseContinuousMouseWheelZoom = true;
		map.Navigator.MouseWheelAnimation.ContinuousMouseWheelZoomStepSize = 0.35;
		map.Navigator.ViewportChanged += OnViewportChanged;
		map.Navigator.CenterOnAndZoomTo(new MPoint(_parked.CenterX, _parked.CenterY), _parked.Resolution, 0);
		Children.Insert(0, _control);
		if (!Children.Contains(_overlay))
		{
			Children.Add(_overlay);
		}
		_disposed = false;
		_basemapLayer = null;
		_basemapSource = null;
		_structures = null;
		_rings = null;
		_structureLabels = null;
		ApplyTheme();
		ShowBasemap();
		ShowLayers(_layers);
	}

	private void OnViewportChanged(object sender, EventArgs e)
	{
		if (!_ui.CheckAccess())
		{
			// The dispatcher this control was made on: a pool thread must not ask for "the" UI dispatcher (see UnhookRedrawLoop).
			_ui.Post(() => OnViewportChanged(sender, e));
			return;
		}
		UpdateScaleVisibility();
		DrawSketch();
		ViewChanged?.Invoke();
	}

	/// <summary>Redraws now (the engine also redraws by itself when tiles arrive).</summary>
	private void Refresh()
	{
		_control?.RefreshGraphics();
		_control?.InvalidateVisual();
	}

	/// <summary>Fetches what the current view needs (tiles, rasterized layers) and waits for it, for tests and printing.</summary>
	public async Task LoadAsync()
	{
		if (_control != null)
		{
			await _control.Map.RefreshDataAsync();
			Refresh();
		}
	}

	/// <summary>The engine's own tile counts, for tests: the basemap source (null without one).</summary>
	internal SdeTileSource BasemapSource => _basemapSource;

	/// <summary>An overlay's tile source by layer key (for tests).</summary>
	internal SdeTileSource OverlaySource(string key) => _overlays.TryGetValue(key, out (TileLayer Layer, SdeTileSource Source) o) ? o.Source : null;

	/// <summary>True when a feature layer is drawn as raster tiles (for tests).</summary>
	internal bool IsRasterized(string key) => _built.TryGetValue(key, out BuiltFeatureLayer b) && b.IsRasterized;

	/// <summary>The engine's map, for the performance test.</summary>
	internal Map EngineMap => _control?.Map;

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (_disposed)
		{
			CreateControl();
		}
		ApplyTheme();
	}

	/// <summary>Stops the engine (its redraw loop, tile downloads and layers). The page calls this when it closes.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (_control != null)
		{
			_parked = _control.Map.Navigator.Viewport;
			_control.Map.Navigator.ViewportChanged -= OnViewportChanged;
			Children.Remove(_control);
			RedrawLoopUnhooked = UnhookRedrawLoop(_control);
			_control.Dispose();
			_control = null;
		}
		foreach (BuiltFeatureLayer built in _built.Values)
		{
			built.Dispose();
		}
		_built.Clear();
		foreach ((TileLayer layer, SdeTileSource source) in _overlays.Values)
		{
			layer.Dispose();
			source.Dispose();
		}
		_overlays.Clear();
		_basemapSource?.Dispose();
		_basemapLayer?.Dispose();
		_basemapLayer = null;
		_basemapSource = null;
	}

	/// <summary>True once <see cref="Dispose"/> has run (and until the page shows the map again).</summary>
	public bool IsDisposed => _disposed;

	/// <summary>True when the last <see cref="Dispose"/> could unhook the engine's redraw loop (see <see cref="UnhookRedrawLoop"/>).</summary>
	internal bool RedrawLoopUnhooked { get; private set; }

	/// <summary>
	/// Mapsui 5.1's redraw loop (its RenderController) asks the control for one more redraw after the control is
	/// disposed, from a pool thread, and that reaches for Avalonia's UI dispatcher after the page has gone. In the app
	/// this is harmless. In the headless tests it can land after a test has ended and claim the next test's dispatcher
	/// for the wrong thread. So the loop's redraw callback is unhooked before the control is disposed. Returns false if a
	/// later Mapsui renames the fields; MapEngineTests then fails, so an upgrade can't drop this quietly.
	/// </summary>
	internal static bool UnhookRedrawLoop(MapControl control)
	{
		FieldInfo controllerField = typeof(MapControl).GetField("_renderController", BindingFlags.Instance | BindingFlags.NonPublic);
		object controller = controllerField?.GetValue(control);
		FieldInfo callback = controller?.GetType().GetField("_invalidateCanvas", BindingFlags.Instance | BindingFlags.NonPublic);
		if (callback == null || callback.FieldType != typeof(Action))
		{
			return false;
		}
		callback.SetValue(controller, null);
		return true;
	}

	// ───────────── Mouse ─────────────

	private void OnPressed(object sender, PointerPressedEventArgs e)
	{
		Focus();
		PointerPoint point = e.GetCurrentPoint(this);
		if (!point.Properties.IsLeftButtonPressed)
		{
			return;
		}
		_pressedAt = point.Position;
		_moved = false;
		if (_sketchKind != MapSketch.None && e.ClickCount >= 2)
		{
			// A double click finishes the line or polygon (its first click already added the last corner).
			_pressedAt = null;
			FinishSketch();
			e.Handled = true;
			return;
		}
		_boxing = _sketchKind == MapSketch.None && (Drag == MapDrag.Box || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
		if (_boxing)
		{
			// The engine never sees a box drag, so the map doesn't move under it.
			e.Pointer.Capture(this);
			e.Handled = true;
		}
	}

	private void OnMoved(object sender, PointerEventArgs e)
	{
		if (_pressedAt is not Point start)
		{
			return;
		}
		Point now = e.GetPosition(this);
		if (!_moved && Math.Abs(now.X - start.X) < DragThreshold && Math.Abs(now.Y - start.Y) < DragThreshold)
		{
			if (_boxing)
			{
				e.Handled = true;
			}
			return;
		}
		_moved = true;
		if (!_boxing)
		{
			return;
		}
		Rect r = new Rect(Math.Min(start.X, now.X), Math.Min(start.Y, now.Y), Math.Abs(now.X - start.X), Math.Abs(now.Y - start.Y));
		Canvas.SetLeft(_box, r.X);
		Canvas.SetTop(_box, r.Y);
		_box.Width = r.Width;
		_box.Height = r.Height;
		_box.IsVisible = true;
		e.Handled = true;
	}

	private void OnReleased(object sender, PointerReleasedEventArgs e)
	{
		if (_pressedAt is not Point start)
		{
			return;
		}
		_pressedAt = null;
		bool add = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
		Point end = e.GetPosition(this);
		if (_boxing)
		{
			_boxing = false;
			_box.IsVisible = false;
			e.Pointer.Capture(null);
			e.Handled = true;
			if (_moved)
			{
				SelectBox(new Rect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)), add);
				return;
			}
		}
		if (!_moved)
		{
			// Our own click: immediate (no waiting for a double click) and with a generous reach around dots.
			Click(start, add);
		}
	}

	/// <summary>The structures, or the features of <see cref="SelectLayer"/>, inside a box of the control.</summary>
	public void SelectBox(Rect area, bool add)
	{
		if (SelectLayer == null)
		{
			AreaSelected?.Invoke(MarkersIn(area), add);
		}
		else
		{
			FeaturesSelected?.Invoke(SelectLayer, FeaturesIn(SelectLayer, area), add);
		}
	}

	/// <summary>
	/// A click at a point of the control: adds a corner to the shape being drawn; otherwise opens (or selects) the dot
	/// there, or a layer's feature, or reports the empty spot.
	/// </summary>
	public void Click(Point at, bool add = false)
	{
		if (_sketchKind != MapSketch.None)
		{
			if (_sketchFinished)
			{
				// The last shape was finished: this click starts a new one.
				_sketch.Clear();
				_sketchFinished = false;
			}
			(double lat, double lon) = ToLatLon(at);
			_sketch.Add(new LatLon(lat, lon));
			DrawSketch();
			SketchChanged?.Invoke(_sketch.ToList(), false);
			return;
		}
		if (Drag == MapDrag.Box && SelectLayer != null && FeatureAt(at, SelectLayer) is (string target, Feature picked))
		{
			// Selecting in a layer: its features come before the dots over them.
			FeatureClicked?.Invoke(target, picked, add);
			return;
		}
		if (MarkerAt(at) is Guid id)
		{
			MarkerClicked?.Invoke(id, add);
			return;
		}
		if (FeatureAt(at) is (string layer, Feature feature))
		{
			FeatureClicked?.Invoke(layer, feature, add);
			return;
		}
		(double latitude, double longitude) = ToLatLon(at);
		MapClicked?.Invoke(latitude, longitude);
	}

	// ───────────── Drawing a line or polygon ─────────────

	/// <summary>Finishes the shape being drawn (Return or a double click).</summary>
	public void FinishSketch()
	{
		if (_sketchKind == MapSketch.None || _sketch.Count == 0)
		{
			return;
		}
		List<LatLon> points = _sketch.ToList();
		_sketchFinished = true;
		SketchChanged?.Invoke(points, true);
	}

	/// <summary>Forgets the shape being drawn.</summary>
	public void ClearSketch()
	{
		_sketch.Clear();
		_sketchFinished = false;
		DrawSketch();
	}

	/// <summary>Removes the last corner (Delete).</summary>
	public void UndoSketchPoint()
	{
		if (_sketch.Count == 0)
		{
			return;
		}
		_sketch.RemoveAt(_sketch.Count - 1);
		_sketchFinished = false;
		DrawSketch();
		SketchChanged?.Invoke(_sketch.ToList(), false);
	}

	private void DrawSketch()
	{
		Points points = new Points(_sketch.Select(p => ToScreen(p.Latitude, p.Longitude)));
		bool area = _sketchKind == MapSketch.Polygon && _sketch.Count >= 3;
		_sketchArea.Points = area ? points : new Points();
		_sketchArea.IsVisible = area;
		_sketchLine.Points = points;
		_sketchLine.IsVisible = !area && _sketch.Count >= 2;
	}

	// ───────────── Keyboard ─────────────

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (_control == null)
		{
			return;
		}
		Viewport v = Viewport;
		double step = KeyPanPixels * v.Resolution;
		switch (e.Key)
		{
			case Key.Left:
				Pan(-step, 0);
				break;
			case Key.Right:
				Pan(step, 0);
				break;
			case Key.Up:
				Pan(0, step);
				break;
			case Key.Down:
				Pan(0, -step);
				break;
			case Key.OemPlus or Key.Add:
				ZoomBy(1);
				break;
			case Key.OemMinus or Key.Subtract:
				ZoomBy(-1);
				break;
			case Key.D0 or Key.NumPad0:
				FitToMarkers();
				break;
			case Key.Enter when _sketchKind != MapSketch.None:
				FinishSketch();
				break;
			case Key.Back or Key.Delete when _sketchKind != MapSketch.None:
				UndoSketchPoint();
				break;
			case Key.Escape when _sketch.Count > 0 || _boxing:
				_boxing = false;
				_box.IsVisible = false;
				ClearSketch();
				SketchChanged?.Invoke(Array.Empty<LatLon>(), false);
				break;
			default:
				return;
		}
		e.Handled = true;
	}

	private void Pan(double dx, double dy)
	{
		Viewport v = Viewport;
		Navigate(v.CenterX + dx, v.CenterY + dy, v.Resolution);
	}
}
