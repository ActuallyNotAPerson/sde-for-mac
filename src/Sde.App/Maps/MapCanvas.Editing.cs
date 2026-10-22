using Avalonia.Input;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Styles;
using NetTopologySuite.Geometries;
using Sde.Gis;
using Sde.Gis.Editing;
using Sde.Gis.Features;
using Brush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using Pen = Mapsui.Styles.Pen;
using Geometry = NetTopologySuite.Geometries.Geometry;
using Point = Avalonia.Point;

namespace Sde.App.Maps;

/// <summary>
/// Editing on the map: the shape being changed is drawn with a square on each corner and a dot in the middle of each
/// edge. Dragging a corner moves it, dragging a middle dot adds a corner there, Option-click takes a corner away,
/// dragging the shape moves it, and Delete removes the corner picked (or the shape). New corners snap to the corners
/// and edges of the visible layers and to structure dots. In Set location, a structure's dot can be dragged. The
/// canvas only reports what the person did (<see cref="EditShapeChanged"/> and the others); the page keeps the edits.
/// </summary>
public sealed partial class MapCanvas
{
	/// <summary>How close (in pixels) a press has to be to a corner or a middle dot to grab it.</summary>
	private const double HandleHitRadius = 9;

	/// <summary>How close (in pixels) a corner has to come to another shape's corner or edge to snap to it.</summary>
	public const double SnapRadius = 10;

	private MemoryLayer _handles;
	private string _editLayer;
	private MapEditTool _editTool;
	private Feature _editFeature;
	private Geometry _editMetres;
	private VertexRef? _selectedVertex;
	private EditDrag _drag;

	/// <summary>A dot dragged to a new place whose move hasn't been answered yet (metres: from, to), drawn until then.</summary>
	private (double FromX, double FromY, double ToX, double ToY)? _pendingMove;

	/// <summary>The key of the layer being edited, or null when nothing is.</summary>
	public string EditLayer
	{
		get => _editLayer;
		set
		{
			if (_editLayer == value)
			{
				return;
			}
			_editLayer = value;
			_drag = null;
			_selectedVertex = null;
			ShowHandles();
		}
	}

	/// <summary>What clicks and drags do to the layer being edited.</summary>
	public MapEditTool EditTool
	{
		get => _editTool;
		set
		{
			_editTool = value;
			_drag = null;
			ShowHandles();
		}
	}

	/// <summary>The shape being changed (as it is now, in longitude and latitude), drawn with its corners; null for none.</summary>
	public Feature EditFeature
	{
		get => _editFeature;
		set
		{
			if (value == null || _editFeature?.Id != value.Id)
			{
				_selectedVertex = null;
			}
			_editFeature = value;
			_editMetres = value?.Geometry == null || value.Geometry.IsEmpty ? null : BuiltFeatureLayer.ToMetres(value.Geometry);
			if (_selectedVertex is VertexRef v && GeometryEdit.VertexAt(_editMetres, v) == null)
			{
				_selectedVertex = null;
			}
			ShowHandles();
		}
	}

	/// <summary>The corner picked on <see cref="EditFeature"/> (Delete takes it away), if any.</summary>
	public VertexRef? SelectedVertex => _selectedVertex;

	/// <summary>True: corners snap to nearby corners and edges of the visible layers and to structure dots.</summary>
	public bool Snap { get; set; } = true;

	/// <summary>True: structure dots can be dragged to a new place (Set location).</summary>
	public bool MarkersDraggable { get; set; }

	/// <summary>A shape of the layer being edited was clicked (null: a click away from every shape).</summary>
	public event Action<Feature> EditFeaturePicked;

	/// <summary>The shape being changed has a new shape (longitude and latitude), and what was done ("Move corner").</summary>
	public event Action<Geometry, string> EditShapeChanged;

	/// <summary>A point was clicked with Add points (longitude and latitude).</summary>
	public event Action<Geometry> EditPointAdded;

	/// <summary>Delete was pressed with a shape picked and no corner picked.</summary>
	public event Action EditDeleteRequested;

	/// <summary>A change the person tried can't be made; the reason in plain English.</summary>
	public event Action<string> EditRefused;

	/// <summary>A structure's dot was dragged to a new place.</summary>
	public event Action<Guid, LatLon> MarkerDragged;

	/// <summary>The engine layer for the corners, middle dots, snapping ring and dragged shapes, on top of everything.</summary>
	private MemoryLayer Handles
	{
		get
		{
			if (_handles == null)
			{
				_handles = new MemoryLayer("Editing") { Style = null };
				_handles.Attribution.Enabled = false;
			}
			return _handles;
		}
	}

	// ───────────── Clicks and keys ─────────────

	/// <summary>A click while editing: adds a point, or picks the shape under it (or none).</summary>
	private void EditClick(Point at)
	{
		if (_editTool == MapEditTool.AddPoint)
		{
			(double lat, double lon) = SnappedLatLon(at);
			EditPointAdded?.Invoke(GisSetup.Factory.CreatePoint(new Coordinate(lon, lat)));
			return;
		}
		if (_editTool != MapEditTool.Change)
		{
			return;
		}
		EditFeaturePicked?.Invoke(FeatureAt(at, _editLayer)?.Feature);
	}

	/// <summary>Delete takes away the picked corner (or the shape); Escape lets go of the shape. True when the key was used.</summary>
	private bool EditKey(Key key)
	{
		if (_editLayer == null || _sketchKind != MapSketch.None || _editTool != MapEditTool.Change || _editFeature == null)
		{
			return false;
		}
		switch (key)
		{
			case Key.Back or Key.Delete:
				if (_selectedVertex is VertexRef v)
				{
					DeleteCorner(v);
				}
				else
				{
					EditDeleteRequested?.Invoke();
				}
				return true;
			case Key.Escape:
				_selectedVertex = null;
				EditFeaturePicked?.Invoke(null);
				return true;
			default:
				return false;
		}
	}

	private void DeleteCorner(VertexRef v)
	{
		Geometry fewer = GeometryEdit.DeleteVertex(_editFeature.Geometry, v);
		if (fewer == null)
		{
			EditRefused?.Invoke(FeatureLayer.KindOf(_editFeature.Geometry) == GeometryKind.Point
				? "A point has no corners to take away. Press Delete with no corner picked to remove it."
				: "A line needs at least two corners and an area three. To remove the whole shape, click away from the corners and press Delete.");
			return;
		}
		_selectedVertex = null;
		EditShapeChanged?.Invoke(fewer, "Delete corner");
	}

	// ───────────── Dragging ─────────────

	private enum DragKind
	{
		Corner,
		NewCorner,
		Shape,
		Marker,
	}

	/// <summary>A drag under way: what is dragged, where it started and the shape as it would be now (metres).</summary>
	private sealed class EditDrag
	{
		public DragKind Kind { get; init; }

		public VertexRef Vertex { get; init; }

		public Point Start { get; init; }

		public double StartX { get; init; }

		public double StartY { get; init; }

		/// <summary>The shape the drag changes (metres); for a new corner, with the corner already added.</summary>
		public Geometry Base { get; init; }

		/// <summary>The other corners of the shape, which a dragged corner may snap to (metres, and the same in longitude and latitude).</summary>
		public Geometry OwnCorners { get; init; }

		public Geometry OwnCornersLonLat { get; init; }

		/// <summary>Where the dragged corner is now in longitude and latitude (exactly the corner it snapped to, if any).</summary>
		public Coordinate LonLat { get; set; }

		public Guid Marker { get; init; }

		public bool Moved { get; set; }

		public Geometry Preview { get; set; }

		public double X { get; set; }

		public double Y { get; set; }

		public bool Snapped { get; set; }
	}

	/// <summary>Starts dragging a corner, a middle dot, the picked shape or (in Set location) a structure's dot. True when a drag started.</summary>
	private bool BeginEditDrag(Point at, KeyModifiers modifiers)
	{
		_drag = null;
		(double x, double y) = Viewport.ScreenToWorldXY(at.X, at.Y);
		if (MarkersDraggable && _editLayer == null && _sketchKind == MapSketch.None && MarkerAt(at) is Guid marker)
		{
			int i = 0;
			while (_markers[i].Id != marker)
			{
				i++;
			}
			_drag = new EditDrag { Kind = DragKind.Marker, Marker = marker, Start = at, StartX = _markerMetres[i].X, StartY = _markerMetres[i].Y };
			return true;
		}
		if (_editLayer == null || _editTool != MapEditTool.Change || _editMetres == null || _sketchKind != MapSketch.None)
		{
			return false;
		}
		// A corner: Option takes it away, otherwise it is dragged (or picked, if it isn't moved).
		if (CornerAt(at) is VertexRef corner)
		{
			if (modifiers.HasFlag(KeyModifiers.Alt))
			{
				DeleteCorner(corner);
				return true;
			}
			_drag = new EditDrag
			{
				Kind = DragKind.Corner,
				Vertex = corner,
				Start = at,
				StartX = x,
				StartY = y,
				Base = _editMetres,
				OwnCorners = OtherCorners(_editMetres, corner),
				OwnCornersLonLat = OtherCorners(_editFeature.Geometry, corner),
			};
			return true;
		}
		// A middle dot: a new corner there, dragged.
		if (MiddleAt(at) is (VertexRef edge, Coordinate middle))
		{
			Geometry withCorner = GeometryEdit.InsertVertex(_editMetres, edge, middle);
			VertexRef added = new VertexRef(edge.Part, edge.Ring, edge.Index + 1);
			_drag = new EditDrag
			{
				Kind = DragKind.NewCorner,
				Vertex = added,
				Start = at,
				StartX = middle.X,
				StartY = middle.Y,
				Base = withCorner,
				OwnCorners = OtherCorners(withCorner, added),
				OwnCornersLonLat = OtherCorners(GeometryEdit.InsertVertex(_editFeature.Geometry, edge, ToLonLat(middle.X, middle.Y)), added),
				Preview = withCorner,
				X = middle.X,
				Y = middle.Y,
				LonLat = ToLonLat(middle.X, middle.Y),
			};
			return true;
		}
		// The shape itself: moved as a whole.
		double reach = FeatureHitRadius * Viewport.Resolution;
		if (NetTopologySuite.Operation.Distance.DistanceOp.Distance(_editMetres, new NetTopologySuite.Geometries.Point(x, y)) <= reach)
		{
			_drag = new EditDrag { Kind = DragKind.Shape, Start = at, StartX = x, StartY = y, Base = _editMetres };
			return true;
		}
		return false;
	}

	private void UpdateEditDrag(Point now)
	{
		EditDrag d = _drag;
		if (!d.Moved && Math.Abs(now.X - d.Start.X) < DragThreshold && Math.Abs(now.Y - d.Start.Y) < DragThreshold)
		{
			return;
		}
		d.Moved = true;
		(double x, double y) = Viewport.ScreenToWorldXY(now.X, now.Y);
		switch (d.Kind)
		{
			case DragKind.Corner or DragKind.NewCorner:
				(double sx, double sy, bool snapped, Coordinate lonLat) = SnapWorld(x, y, _editFeature.Id, d.OwnCorners, d.OwnCornersLonLat);
				d.X = sx;
				d.Y = sy;
				d.Snapped = snapped;
				d.LonLat = lonLat;
				d.Preview = GeometryEdit.MoveVertex(d.Base, d.Vertex, new Coordinate(sx, sy));
				break;
			case DragKind.Shape:
				d.Preview = GeometryEdit.Translate(d.Base, x - d.StartX, y - d.StartY);
				break;
			case DragKind.Marker:
				d.X = x;
				d.Y = y;
				break;
		}
		ShowHandles();
	}

	private void EndEditDrag(Point end)
	{
		EditDrag d = _drag;
		_drag = null;
		if (!d.Moved)
		{
			switch (d.Kind)
			{
				case DragKind.Corner:
					// A corner pressed and let go: it is picked, for Delete.
					_selectedVertex = d.Vertex;
					ShowHandles();
					return;
				case DragKind.NewCorner:
					// A middle dot clicked: a corner there.
					EditShapeChanged?.Invoke(GeometryEdit.InsertVertex(_editFeature.Geometry, new VertexRef(d.Vertex.Part, d.Vertex.Ring, d.Vertex.Index - 1), d.LonLat), "Add corner");
					return;
				default:
					// A click on the shape or on a dot.
					ShowHandles();
					Click(end);
					return;
			}
		}
		switch (d.Kind)
		{
			case DragKind.Corner:
				_selectedVertex = d.Vertex;
				EditShapeChanged?.Invoke(GeometryEdit.MoveVertex(_editFeature.Geometry, d.Vertex, d.LonLat), "Move corner");
				break;
			case DragKind.NewCorner:
				_selectedVertex = d.Vertex;
				Geometry withCorner = GeometryEdit.InsertVertex(_editFeature.Geometry, new VertexRef(d.Vertex.Part, d.Vertex.Ring, d.Vertex.Index - 1), d.LonLat);
				EditShapeChanged?.Invoke(withCorner, "Add corner");
				break;
			case DragKind.Shape:
				EditShapeChanged?.Invoke(BuiltFeatureLayer.ToWgs84(d.Preview), "Move shape");
				break;
			case DragKind.Marker:
				// The dot's new place stays drawn while the move is asked about.
				_pendingMove = (d.StartX, d.StartY, d.X, d.Y);
				ShowHandles();
				(double lon, double lat) = WebMercator.FromMetres(d.X, d.Y);
				MarkerDragged?.Invoke(d.Marker, new LatLon(lat, lon));
				return;
		}
		ShowHandles();
	}

	/// <summary>Stops drawing a dot's new place (the move was answered).</summary>
	public void ClearPendingMove()
	{
		if (_pendingMove != null)
		{
			_pendingMove = null;
			ShowHandles();
		}
	}

	/// <summary>Drags something on the map from one point of the control to another (for tests: the same path as the mouse).</summary>
	public bool DragOnMap(Point from, Point to, KeyModifiers modifiers = KeyModifiers.None)
	{
		if (!BeginEditDrag(from, modifiers))
		{
			return false;
		}
		if (_drag == null)
		{
			// Option-click took a corner away.
			return true;
		}
		UpdateEditDrag(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
		UpdateEditDrag(to);
		EndEditDrag(to);
		return true;
	}

	// ───────────── Finding corners and middle dots ─────────────

	/// <summary>The corner of the picked shape within reach of a point of the control (the nearest), or null.</summary>
	public VertexRef? CornerAt(Point at)
	{
		if (_editMetres == null)
		{
			return null;
		}
		VertexRef? best = null;
		double bestDistance = HandleHitRadius;
		foreach ((VertexRef v, Coordinate c) in GeometryEdit.Vertices(_editMetres))
		{
			(double sx, double sy) = Viewport.WorldToScreenXY(c.X, c.Y);
			double d = Math.Sqrt((sx - at.X) * (sx - at.X) + (sy - at.Y) * (sy - at.Y));
			if (d <= bestDistance)
			{
				best = v;
				bestDistance = d;
			}
		}
		return best;
	}

	/// <summary>The middle dot of an edge of the picked shape within reach of a point (the edge and the middle, in metres), or null.</summary>
	private (VertexRef Edge, Coordinate Middle)? MiddleAt(Point at)
	{
		if (_editMetres == null)
		{
			return null;
		}
		foreach ((VertexRef from, Coordinate a, Coordinate b) in GeometryEdit.Edges(_editMetres))
		{
			Coordinate middle = new Coordinate((a.X + b.X) / 2, (a.Y + b.Y) / 2);
			(double sx, double sy) = Viewport.WorldToScreenXY(middle.X, middle.Y);
			if (Math.Sqrt((sx - at.X) * (sx - at.X) + (sy - at.Y) * (sy - at.Y)) <= HandleHitRadius)
			{
				return (from, middle);
			}
		}
		return null;
	}

	/// <summary>Where a corner of the picked shape (or a point of the control) is on the screen, for tests.</summary>
	public Point CornerOnScreen(VertexRef v)
	{
		Coordinate c = GeometryEdit.VertexAt(_editMetres, v);
		(double sx, double sy) = Viewport.WorldToScreenXY(c.X, c.Y);
		return new Point(sx, sy);
	}

	/// <summary>Where the middle dot of the edge starting at a corner is on the screen, for tests.</summary>
	public Point MiddleOnScreen(VertexRef from)
	{
		(VertexRef _, Coordinate a, Coordinate b) = GeometryEdit.Edges(_editMetres).First(e => e.From == from);
		(double sx, double sy) = Viewport.WorldToScreenXY((a.X + b.X) / 2, (a.Y + b.Y) / 2);
		return new Point(sx, sy);
	}

	/// <summary>A shape's corners but one, as points in the same order (in whatever units the shape is in).</summary>
	private static Geometry OtherCorners(Geometry shape, VertexRef except)
	{
		Coordinate[] others = GeometryEdit.Vertices(shape).Where(v => v.Ref != except).Select(v => v.At).ToArray();
		return others.Length == 0 ? null : new GeometryFactory().CreateMultiPointFromCoords(others);
	}

	// ───────────── Snapping ─────────────

	/// <summary>The latitude and longitude under a point of the control, snapped when snapping is on.</summary>
	public (double Latitude, double Longitude) SnappedLatLon(Point at)
	{
		(double x, double y) = Viewport.ScreenToWorldXY(at.X, at.Y);
		(double _, double _, bool _, Coordinate lonLat) = SnapWorld(x, y, null, null, null);
		return (lonLat.Y, lonLat.X);
	}

	/// <summary>
	/// A map position (metres) snapped to the nearest corner, else edge, of the visible layers' shapes and the structure
	/// dots within <see cref="SnapRadius"/> pixels; <paramref name="skip"/> leaves out the shape being changed (its
	/// other corners come in <paramref name="own"/>, and the same in longitude and latitude in <paramref name="ownLonLat"/>).
	/// The position comes back in metres (to draw) and in longitude and latitude: exactly the corner's own coordinate when
	/// it snapped to a corner, so shapes that share a corner really share it.
	/// </summary>
	private (double X, double Y, bool Snapped, Coordinate LonLat) SnapWorld(double x, double y, long? skip, Geometry own, Geometry ownLonLat)
	{
		if (!Snap)
		{
			return (x, y, false, ToLonLat(x, y));
		}
		double tolerance = SnapRadius * Viewport.Resolution;
		Envelope around = new Envelope(x - tolerance, x + tolerance, y - tolerance, y + tolerance);
		List<Geometry> candidates = new List<Geometry>();
		List<Geometry> lonLats = new List<Geometry>();
		foreach (object layer in _layers)
		{
			if (layer is MapFeatureLayer spec && spec.Visible && _built.TryGetValue(spec.Key, out BuiltFeatureLayer built) && built.Layer.Enabled)
			{
				foreach ((Feature f, Geometry metres) in built.FeaturesNear(around))
				{
					if (!(spec.Key == _editLayer && skip == f.Id))
					{
						candidates.Add(metres);
						lonLats.Add(f.Geometry);
					}
				}
			}
		}
		if (own != null)
		{
			candidates.Add(own);
			lonLats.Add(ownLonLat);
		}
		GeometryFactory planar = new GeometryFactory();
		foreach (int i in _markerIndex.Query(around))
		{
			candidates.Add(planar.CreatePoint(new Coordinate(_markerMetres[i].X, _markerMetres[i].Y)));
			lonLats.Add(GisSetup.Factory.CreatePoint(new Coordinate(_markers[i].Longitude, _markers[i].Latitude)));
		}
		SnapResult r = Snapping.Snap(new Coordinate(x, y), candidates, tolerance);
		Coordinate lonLat = r.Kind == SnapKind.Vertex && r.Vertex is VertexRef v && lonLats[r.Candidate] is Geometry source && GeometryEdit.VertexAt(source, v) is Coordinate exact
			? exact
			: ToLonLat(r.At.X, r.At.Y);
		return (r.At.X, r.At.Y, r.Snapped, lonLat);
	}

	private static Coordinate ToLonLat(double x, double y)
	{
		(double lon, double lat) = WebMercator.FromMetres(x, y);
		return new Coordinate(lon, lat);
	}

	// ───────────── Drawing the handles ─────────────

	/// <summary>Draws the picked shape's outline, corners and middle dots, a shape or dot being dragged, and the snapping ring.</summary>
	private void ShowHandles()
	{
		if (_control == null)
		{
			return;
		}
		List<Mapsui.IFeature> features = new List<Mapsui.IFeature>();
		MColor accent = _theme.Accent;
		MColor card = _theme.DotOutline;
		(double FromX, double FromY, double ToX, double ToY)? move = _drag is { Kind: DragKind.Marker, Moved: true } marker
			? (marker.StartX, marker.StartY, marker.X, marker.Y)
			: _pendingMove;
		if (move is (double fromX, double fromY, double toX, double toY))
		{
			// The dot's way from where it was to where it is going.
			GeometryFeature path = new GeometryFeature(new GeometryFactory().CreateLineString(new[] { new Coordinate(fromX, fromY), new Coordinate(toX, toY) }));
			path.Styles.Add(new VectorStyle { Line = new Pen(accent, 2) { PenStyle = PenStyle.Dash } });
			features.Add(path);
			PointFeature dot = new PointFeature(toX, toY);
			dot.Styles.Add(new SymbolStyle
			{
				SymbolType = SymbolType.Ellipse,
				SymbolScale = (DotDiameter + 4) / SymbolStyle.DefaultWidth,
				Fill = new Brush(accent),
				Outline = new Pen(card, 2),
			});
			features.Add(dot);
		}
		Geometry shape = _drag?.Preview ?? _editMetres;
		if (_editLayer != null && shape != null && _editTool == MapEditTool.Change)
		{
			GeometryKind kind = FeatureLayer.KindOf(shape);
			GeometryFeature outline = new GeometryFeature(shape);
			outline.Styles.Add(kind == GeometryKind.Point
				? new SymbolStyle { SymbolType = SymbolType.Ellipse, SymbolScale = 20 / SymbolStyle.DefaultWidth, Fill = null, Outline = new Pen(accent, 2.5) }
				: new VectorStyle
				{
					Fill = kind == GeometryKind.Polygon ? new Brush(new MColor(accent.R, accent.G, accent.B, 40)) : null,
					Outline = new Pen(accent, 2),
					Line = new Pen(accent, 2.5),
				});
			features.Add(outline);
			if (kind != GeometryKind.Point)
			{
				if (_drag == null)
				{
					foreach ((VertexRef _, Coordinate a, Coordinate b) in GeometryEdit.Edges(shape))
					{
						PointFeature middle = new PointFeature((a.X + b.X) / 2, (a.Y + b.Y) / 2);
						middle.Styles.Add(new SymbolStyle
						{
							SymbolType = SymbolType.Ellipse,
							SymbolScale = 7 / SymbolStyle.DefaultWidth,
							Fill = new Brush(new MColor(accent.R, accent.G, accent.B, 170)),
							Outline = new Pen(card, 1.5),
						});
						features.Add(middle);
					}
				}
				foreach ((VertexRef v, Coordinate c) in GeometryEdit.Vertices(shape))
				{
					bool picked = _selectedVertex == v || _drag != null && _drag.Kind is DragKind.Corner or DragKind.NewCorner && _drag.Vertex == v;
					PointFeature corner = new PointFeature(c.X, c.Y);
					corner.Styles.Add(new SymbolStyle
					{
						SymbolType = SymbolType.Rectangle,
						SymbolScale = (picked ? 11 : 9) / SymbolStyle.DefaultWidth,
						Fill = new Brush(picked ? accent : card),
						Outline = new Pen(picked ? card : accent, 2),
					});
					features.Add(corner);
				}
			}
		}
		if (_drag is { Snapped: true, Moved: true } snapping)
		{
			PointFeature ring = new PointFeature(snapping.X, snapping.Y);
			ring.Styles.Add(new SymbolStyle { SymbolType = SymbolType.Ellipse, SymbolScale = 18 / SymbolStyle.DefaultWidth, Fill = null, Outline = new Pen(accent, 2.5) });
			features.Add(ring);
		}
		Handles.Features = features;
		Handles.DataHasChanged();
		Refresh();
	}
}
