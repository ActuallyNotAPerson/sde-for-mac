using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Sde.App.Maps;

/// <summary>A dot on the map: which structure, where, and what colour.</summary>
public readonly record struct MapMarker(Guid Id, double Latitude, double Longitude, IBrush Fill);

/// <summary>What dragging does on the map.</summary>
public enum MapDrag
{
	/// <summary>Dragging moves the map. Holding Command and dragging draws a box instead.</summary>
	Pan,

	/// <summary>Dragging draws a box around the structures to select.</summary>
	Box,
}

/// <summary>
/// A small slippy map: Web Mercator tiles from a <see cref="TileCache"/> with a dot for each structure on top. Drag to
/// move, scroll or pinch to zoom, arrow keys and + / - from the keyboard. Command-drag (or <see cref="Drag"/> set to
/// Box) draws a selection box. It knows nothing about structures beyond the markers it is given, so the page decides
/// what a click means.
/// </summary>
public sealed class MapCanvas : Control
{
	public const double MinZoom = 2;
	public const double MaxZoom = 19;

	/// <summary>How close (in pixels) a click has to be to a dot to count as hitting it.</summary>
	private const double HitRadius = 11;

	private const double DotRadius = 6.5;

	public static readonly StyledProperty<IBrush> BackgroundProperty = AvaloniaProperty.Register<MapCanvas, IBrush>(nameof(Background));

	private IReadOnlyList<MapMarker> _markers = Array.Empty<MapMarker>();
	private double _centerU = WebMercator.U(-96.0);
	private double _centerV = WebMercator.V(38.5);
	private double _zoom = 4;
	private Point? _pressedAt;
	private Point _lastPoint;
	private bool _moved;
	private bool _boxing;
	private Rect _box;

	static MapCanvas()
	{
		AffectsRender<MapCanvas>(BackgroundProperty);
		FocusableProperty.OverrideDefaultValue<MapCanvas>(true);
		ClipToBoundsProperty.OverrideDefaultValue<MapCanvas>(true);
	}

	public IBrush Background
	{
		get => GetValue(BackgroundProperty);
		set => SetValue(BackgroundProperty, value);
	}

	/// <summary>Where tiles come from; null (or a basemap with no tiles) shows the plain background.</summary>
	public TileCache Tiles
	{
		get => _tiles;
		set
		{
			if (_tiles != null)
			{
				_tiles.Changed -= InvalidateVisual;
			}
			_tiles = value;
			if (_tiles != null)
			{
				_tiles.Changed += InvalidateVisual;
			}
			InvalidateVisual();
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
			InvalidateVisual();
		}
	}

	private Basemap _basemap = Basemaps.None;

	public MapDrag Drag { get; set; } = MapDrag.Pan;

	/// <summary>The structure to ring (the one picked in the list beside the map), if any.</summary>
	public Guid? Highlighted
	{
		get => _highlighted;
		set
		{
			_highlighted = value;
			InvalidateVisual();
		}
	}

	private Guid? _highlighted;

	public IReadOnlyList<MapMarker> Markers
	{
		get => _markers;
		set
		{
			_markers = value ?? Array.Empty<MapMarker>();
			InvalidateVisual();
		}
	}

	public double Zoom => _zoom;

	public double CenterLatitude => WebMercator.Latitude(_centerV);

	public double CenterLongitude => WebMercator.Longitude(_centerU);

	/// <summary>A dot was clicked.</summary>
	public event Action<Guid> MarkerClicked;

	/// <summary>Empty map was clicked (not a drag): latitude and longitude of the spot.</summary>
	public event Action<double, double> MapClicked;

	/// <summary>A box was drawn: the dots inside it (maybe none). True when Command was held to draw it, which adds to the selection.</summary>
	public event Action<IReadOnlyList<Guid>, bool> AreaSelected;

	// ───────────── View ─────────────

	public void SetView(double latitude, double longitude, double zoom)
	{
		_zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
		_centerU = WebMercator.U(longitude);
		_centerV = WebMercator.V(latitude);
		ClampCenter();
		InvalidateVisual();
	}

	public void ZoomBy(double steps) => ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), steps);

	/// <summary>Shows every dot (or the whole country when there are none).</summary>
	public void FitToMarkers()
	{
		if (_markers.Count == 0 || Bounds.Width < 10 || Bounds.Height < 10)
		{
			SetView(38.5, -96.0, 4);
			return;
		}
		double minU = _markers.Min(m => WebMercator.U(m.Longitude));
		double maxU = _markers.Max(m => WebMercator.U(m.Longitude));
		double minV = _markers.Min(m => WebMercator.V(m.Latitude));
		double maxV = _markers.Max(m => WebMercator.V(m.Latitude));
		double spanU = Math.Max(maxU - minU, 1e-7);
		double spanV = Math.Max(maxV - minV, 1e-7);
		// Leave a margin so the outer dots aren't on the edge, and don't zoom closer than a street.
		double zoomU = Math.Log2(Math.Max(Bounds.Width - 80, 40) / (WebMercator.TileSize * spanU));
		double zoomV = Math.Log2(Math.Max(Bounds.Height - 80, 40) / (WebMercator.TileSize * spanV));
		_zoom = Math.Clamp(Math.Min(Math.Min(zoomU, zoomV), 17), MinZoom, MaxZoom);
		_centerU = (minU + maxU) / 2;
		_centerV = (minV + maxV) / 2;
		InvalidateVisual();
	}

	/// <summary>Moves the map so the point is in view, without changing the zoom unless it is far out.</summary>
	public void CenterOn(double latitude, double longitude, double atLeastZoom = 15)
	{
		_zoom = Math.Clamp(Math.Max(_zoom, atLeastZoom), MinZoom, MaxZoom);
		_centerU = WebMercator.U(longitude);
		_centerV = WebMercator.V(latitude);
		ClampCenter();
		InvalidateVisual();
	}

	/// <summary>Where a latitude and longitude is on this control.</summary>
	public Point ToScreen(double latitude, double longitude)
	{
		double scale = WebMercator.WorldPixels(_zoom);
		return new Point((WebMercator.U(longitude) - _centerU) * scale + Bounds.Width / 2, (WebMercator.V(latitude) - _centerV) * scale + Bounds.Height / 2);
	}

	/// <summary>The latitude and longitude under a point on this control.</summary>
	public (double Latitude, double Longitude) ToLatLon(Point p)
	{
		double scale = WebMercator.WorldPixels(_zoom);
		double u = _centerU + (p.X - Bounds.Width / 2) / scale;
		double v = _centerV + (p.Y - Bounds.Height / 2) / scale;
		return (WebMercator.Latitude(Math.Clamp(v, 0, 1)), WebMercator.Longitude(Math.Clamp(u, 0, 1)));
	}

	/// <summary>The topmost dot within reach of a point, or null.</summary>
	public Guid? MarkerAt(Point p)
	{
		for (int i = _markers.Count - 1; i >= 0; i--)
		{
			Point m = ToScreen(_markers[i].Latitude, _markers[i].Longitude);
			if ((m - p).X * (m - p).X + (m - p).Y * (m - p).Y <= HitRadius * HitRadius)
			{
				return _markers[i].Id;
			}
		}
		return null;
	}

	/// <summary>The dots inside a rectangle of the control.</summary>
	public IReadOnlyList<Guid> MarkersIn(Rect area) =>
		_markers.Where(m => area.Contains(ToScreen(m.Latitude, m.Longitude))).Select(m => m.Id).ToList();

	private void ZoomAround(Point at, double steps)
	{
		double before = _zoom;
		double after = Math.Clamp(before + steps, MinZoom, MaxZoom);
		if (after == before)
		{
			return;
		}
		// Keep the spot under the pointer where it is.
		(double lat, double lon) = ToLatLon(at);
		_zoom = after;
		double scale = WebMercator.WorldPixels(_zoom);
		_centerU = WebMercator.U(lon) - (at.X - Bounds.Width / 2) / scale;
		_centerV = WebMercator.V(lat) - (at.Y - Bounds.Height / 2) / scale;
		ClampCenter();
		InvalidateVisual();
	}

	private void ClampCenter()
	{
		_centerU = Math.Clamp(_centerU, 0, 1);
		_centerV = Math.Clamp(_centerV, 0, 1);
	}

	// ───────────── Drawing ─────────────

	public override void Render(DrawingContext context)
	{
		Rect all = new Rect(Bounds.Size);
		context.DrawRectangle(Background ?? Brushes.LightGray, null, all);
		if (all.Width < 1 || all.Height < 1)
		{
			return;
		}
		DrawTiles(context);
		DrawMarkers(context);
		if (_boxing)
		{
			IBrush accent = this.TryFindResource("Accent", ActualThemeVariant, out object r) && r is IBrush b ? b : Brushes.DodgerBlue;
			context.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, 40, 110, 220)), new Pen(accent, 1.5), _box);
		}
	}

	private void DrawTiles(DrawingContext context)
	{
		if (_tiles == null || !_basemap.HasTiles)
		{
			return;
		}
		int tz = Math.Clamp((int)Math.Round(_zoom), 0, _basemap.MaxZoom);
		int count = 1 << tz;
		double scale = WebMercator.WorldPixels(_zoom);
		double tilePixels = scale / count;
		double left = _centerU - Bounds.Width / 2 / scale;
		double right = _centerU + Bounds.Width / 2 / scale;
		double top = _centerV - Bounds.Height / 2 / scale;
		double bottom = _centerV + Bounds.Height / 2 / scale;
		int x0 = Math.Max(0, (int)Math.Floor(left * count));
		int x1 = Math.Min(count - 1, (int)Math.Floor(right * count));
		int y0 = Math.Max(0, (int)Math.Floor(top * count));
		int y1 = Math.Min(count - 1, (int)Math.Floor(bottom * count));
		if ((x1 - x0 + 1) * (y1 - y0 + 1) > 120)
		{
			return;
		}
		for (int x = x0; x <= x1; x++)
		{
			for (int y = y0; y <= y1; y++)
			{
				Avalonia.Media.Imaging.Bitmap tile = _tiles.TryGet(_basemap, tz, x, y);
				if (tile == null)
				{
					continue;
				}
				double px = (x / (double)count - _centerU) * scale + Bounds.Width / 2;
				double py = (y / (double)count - _centerV) * scale + Bounds.Height / 2;
				// A little overlap hides hairline gaps between tiles at fractional zoom.
				context.DrawImage(tile, new Rect(tile.Size), new Rect(px, py, tilePixels + 0.5, tilePixels + 0.5));
			}
		}
	}

	private void DrawMarkers(DrawingContext context)
	{
		IBrush halo = this.TryFindResource("CardBg", ActualThemeVariant, out object r) && r is IBrush b ? b : Brushes.White;
		Pen outline = new Pen(halo, 1.8);
		Rect view = new Rect(Bounds.Size).Inflate(DotRadius * 2);
		foreach (MapMarker m in _markers)
		{
			Point p = ToScreen(m.Latitude, m.Longitude);
			if (!view.Contains(p))
			{
				continue;
			}
			context.DrawEllipse(m.Fill ?? Brushes.Gray, outline, p, DotRadius, DotRadius);
		}
		if (_highlighted == null)
		{
			return;
		}
		IBrush ring = this.TryFindResource("Accent", ActualThemeVariant, out object a) && a is IBrush ab ? ab : Brushes.DodgerBlue;
		foreach (MapMarker m in _markers)
		{
			if (m.Id == _highlighted)
			{
				context.DrawEllipse(null, new Pen(ring, 3), ToScreen(m.Latitude, m.Longitude), DotRadius + 6, DotRadius + 6);
			}
		}
	}

	// ───────────── Mouse and keyboard ─────────────

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		Focus();
		PointerPoint point = e.GetCurrentPoint(this);
		if (!point.Properties.IsLeftButtonPressed)
		{
			return;
		}
		_pressedAt = point.Position;
		_lastPoint = point.Position;
		_moved = false;
		_boxing = Drag == MapDrag.Box || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
		e.Pointer.Capture(this);
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (_pressedAt is not Point start)
		{
			return;
		}
		Point now = e.GetPosition(this);
		if (!_moved && (now - start).X * (now - start).X + (now - start).Y * (now - start).Y < 25)
		{
			return;
		}
		_moved = true;
		if (_boxing)
		{
			_box = new Rect(Math.Min(start.X, now.X), Math.Min(start.Y, now.Y), Math.Abs(now.X - start.X), Math.Abs(now.Y - start.Y));
		}
		else
		{
			double scale = WebMercator.WorldPixels(_zoom);
			_centerU -= (now.X - _lastPoint.X) / scale;
			_centerV -= (now.Y - _lastPoint.Y) / scale;
			ClampCenter();
		}
		_lastPoint = now;
		InvalidateVisual();
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (_pressedAt is not Point start)
		{
			return;
		}
		_pressedAt = null;
		e.Pointer.Capture(null);
		bool wasBoxing = _boxing;
		Rect box = _box;
		_boxing = false;
		InvalidateVisual();
		if (_moved && wasBoxing)
		{
			AreaSelected?.Invoke(MarkersIn(box), e.KeyModifiers.HasFlag(KeyModifiers.Meta));
		}
		else if (!_moved)
		{
			Click(start);
		}
	}

	/// <summary>A click at a point of the control: opens the dot there, or reports the empty spot.</summary>
	public void Click(Point at)
	{
		if (MarkerAt(at) is Guid id)
		{
			MarkerClicked?.Invoke(id);
			return;
		}
		(double lat, double lon) = ToLatLon(at);
		MapClicked?.Invoke(lat, lon);
	}

	protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
	{
		base.OnPointerWheelChanged(e);
		ZoomAround(e.GetPosition(this), Math.Clamp(e.Delta.Y, -3, 3) * 0.35);
		e.Handled = true;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		double step = 80.0 / WebMercator.WorldPixels(_zoom);
		switch (e.Key)
		{
			case Key.Left:
				_centerU -= step;
				break;
			case Key.Right:
				_centerU += step;
				break;
			case Key.Up:
				_centerV -= step;
				break;
			case Key.Down:
				_centerV += step;
				break;
			case Key.OemPlus or Key.Add:
				ZoomBy(1);
				e.Handled = true;
				return;
			case Key.OemMinus or Key.Subtract:
				ZoomBy(-1);
				e.Handled = true;
				return;
			case Key.D0 or Key.NumPad0:
				FitToMarkers();
				e.Handled = true;
				return;
			default:
				return;
		}
		ClampCenter();
		InvalidateVisual();
		e.Handled = true;
	}
}
