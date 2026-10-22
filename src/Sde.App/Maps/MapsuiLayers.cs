using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Styles;
using Mapsui.Tiling.Fetcher;
using Mapsui.Tiling.Layers;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Distance;
using Sde.Gis.Features;
using Sde.Gis.Styles;
using Color = Mapsui.Styles.Color;
using Feature = Sde.Gis.Features.Feature;

namespace Sde.App.Maps;

/// <summary>
/// One <see cref="MapFeatureLayer"/> as the map engine draws it: the features in Web Mercator, each with its symbol;
/// a labels layer and a selection highlight drawn directly over it; and a spatial index (an STRtree) for clicks and
/// boxes. Layers with many features are drawn through the engine's rasterizing tile layer, so panning only moves
/// pictures.
/// </summary>
internal sealed class BuiltFeatureLayer : IDisposable
{
	/// <summary>Layers with more features than this are drawn as raster tiles.</summary>
	public const int RasterizeAbove = 1500;

	private readonly STRtree<int> _index = new STRtree<int>();

	/// <summary>One engine style per symbol and kind of geometry (a layer may mix points, lines and polygons).</summary>
	private readonly Dictionary<Symbol, IStyle[]> _styles = new Dictionary<Symbol, IStyle[]>(ReferenceEqualityComparer.Instance);

	/// <summary>Every drawn feature in drawing order, hidden ones included (<see cref="Source"/> leaves the hidden out).</summary>
	private List<IFeature> _drawn = new List<IFeature>();

	/// <summary>Ids of features not drawn from the layer itself because the editor draws their new versions.</summary>
	private HashSet<long> _hidden = new HashSet<long>();

	/// <summary>The label points, with the id of the feature each belongs to.</summary>
	private List<(IFeature Label, long Id)> _labelPoints = new List<(IFeature, long)>();

	/// <summary>The edited features drawn over the layer, in Web Mercator, and the features themselves.</summary>
	private Geometry[] _editShapes = Array.Empty<Geometry>();
	private Feature[] _editFeatures = Array.Empty<Feature>();

	private BuiltFeatureLayer(MapFeatureLayer spec)
	{
		Spec = spec;
	}

	public MapFeatureLayer Spec { get; private set; }

	/// <summary>The layer drawn: the features themselves, or raster tiles of them.</summary>
	public ILayer Layer { get; private set; }

	/// <summary>The features as the engine holds them.</summary>
	public MemoryLayer Source { get; private set; }

	/// <summary>Labels, drawn over the features; null when the layer has none.</summary>
	public MemoryLayer Labels { get; private set; }

	/// <summary>The selected features, outlined in the accent colour.</summary>
	public MemoryLayer Selection { get; private set; }

	/// <summary>Unsaved edits: the changed and new features as they are now, drawn in the layer's style over it.</summary>
	public MemoryLayer Edits { get; private set; }

	/// <summary>The <see cref="MapLayerEdits.Revision"/> drawn now (0 when there are no edits).</summary>
	public int EditRevision { get; private set; }

	public bool IsRasterized { get; private set; }

	/// <summary>The drawn features' geometries in Web Mercator metres, in drawing order.</summary>
	public Geometry[] Shapes { get; private set; }

	/// <summary>For each drawn feature, the feature in <see cref="MapFeatureLayer.Data"/>.</summary>
	public Feature[] Features { get; private set; }

	/// <summary>Builds the engine's layers for a feature layer.</summary>
	public static BuiltFeatureLayer Build(MapFeatureLayer spec, MapTheme theme, float pixelDensity)
	{
		BuiltFeatureLayer built = new BuiltFeatureLayer(spec);
		List<IFeature> drawn = new List<IFeature>();
		List<Geometry> shapes = new List<Geometry>();
		List<Feature> features = new List<Feature>();
		LayerStyle style = spec.Style ?? new LayerStyle();
		foreach (Feature f in spec.Data?.Features ?? new List<Feature>())
		{
			if (f.Geometry == null || f.Geometry.IsEmpty)
			{
				continue;
			}
			Symbol symbol = style.SymbolFor(f);
			if (symbol == null)
			{
				continue;
			}
			Geometry metres = ToMetres(f.Geometry);
			GeometryFeature g = new GeometryFeature(metres);
			g.Styles.Add(built.StyleFor(symbol, FeatureLayer.KindOf(f.Geometry)));
			built._index.Insert(metres.EnvelopeInternal, shapes.Count);
			drawn.Add(g);
			shapes.Add(metres);
			features.Add(f);
		}
		built._index.Build();
		built.Shapes = shapes.ToArray();
		built.Features = features.ToArray();
		built._drawn = drawn;
		built.Source = new MemoryLayer(spec.Name ?? spec.Key) { Features = drawn, Style = null };
		built.IsRasterized = drawn.Count > RasterizeAbove;
		built.Layer = built.IsRasterized
			? new RasterizingTileLayer(built.Source, pixelDensity, dataFetchStrategy: new DataFetchStrategy(0)) { Name = spec.Name ?? spec.Key }
			: built.Source;
		built.Layer.Attribution.Enabled = false;
		built.Layer.Opacity = spec.Display?.ClampedOpacity ?? 1;
		built.Labels = BuildLabels(spec, built);
		built.Selection = new MemoryLayer((spec.Name ?? spec.Key) + " selected") { Style = theme.SelectionStyle(spec.Data?.GeometryKind ?? GeometryKind.Unknown) };
		built.Edits = new MemoryLayer((spec.Name ?? spec.Key) + " edits") { Style = null };
		built.Edits.Attribution.Enabled = false;
		built.ShowEdits(spec.Edits);
		built.ShowSelection(spec, theme);
		return built;
	}

	/// <summary>The engine style for a symbol on a kind of geometry, made once.</summary>
	private IStyle StyleFor(Symbol symbol, GeometryKind kind)
	{
		if (!_styles.TryGetValue(symbol, out IStyle[] byKind))
		{
			byKind = new IStyle[4];
			_styles[symbol] = byKind;
		}
		return byKind[(int)kind] ??= SymbolStyleFor(symbol, kind);
	}

	/// <summary>
	/// Draws unsaved edits: the layer's own copies of <see cref="MapLayerEdits.Hidden"/> features are left out (a large
	/// layer's raster tiles are drawn again for the view only), and the edited features are drawn over it.
	/// </summary>
	public void ShowEdits(MapLayerEdits edits)
	{
		HashSet<long> hidden = new HashSet<long>(edits?.Hidden ?? Array.Empty<long>());
		if (!hidden.SetEquals(_hidden))
		{
			_hidden = hidden;
			Source.Features = _hidden.Count == 0 ? _drawn : _drawn.Where((f, i) => !_hidden.Contains(Features[i].Id)).ToList();
			Source.DataHasChanged();
			if (Labels != null)
			{
				Labels.Features = _labelPoints.Where(l => !_hidden.Contains(l.Id)).Select(l => l.Label).ToList();
				Labels.DataHasChanged();
			}
		}
		LayerStyle style = Spec.Style ?? new LayerStyle();
		List<IFeature> overlay = new List<IFeature>();
		List<Geometry> shapes = new List<Geometry>();
		List<Feature> features = new List<Feature>();
		foreach (Feature f in edits?.Drawn ?? Array.Empty<Feature>())
		{
			if (f.Geometry == null || f.Geometry.IsEmpty || style.SymbolFor(f) is not Symbol symbol)
			{
				continue;
			}
			Geometry metres = ToMetres(f.Geometry);
			GeometryFeature g = new GeometryFeature(metres);
			g.Styles.Add(StyleFor(symbol, FeatureLayer.KindOf(f.Geometry)));
			overlay.Add(g);
			shapes.Add(metres);
			features.Add(f);
		}
		_editShapes = shapes.ToArray();
		_editFeatures = features.ToArray();
		Edits.Features = overlay;
		Edits.DataHasChanged();
		EditRevision = edits?.Revision ?? 0;
	}

	/// <summary>True when the layer's own copy of a feature is hidden (the editor draws its new version).</summary>
	public bool IsHidden(long id) => _hidden.Contains(id);

	/// <summary>
	/// The features drawn now whose bounding boxes meet a Web Mercator rectangle, with their shapes in metres: edited
	/// ones first (they are on top), then the layer's own (not the hidden ones).
	/// </summary>
	public IEnumerable<(Feature Feature, Geometry Metres)> FeaturesNear(Envelope metres)
	{
		for (int i = _editShapes.Length - 1; i >= 0; i--)
		{
			if (_editShapes[i].EnvelopeInternal.Intersects(metres))
			{
				yield return (_editFeatures[i], _editShapes[i]);
			}
		}
		foreach (int i in _index.Query(metres).OrderByDescending(i => i))
		{
			if (!_hidden.Contains(Features[i].Id))
			{
				yield return (Features[i], Shapes[i]);
			}
		}
	}

	/// <summary>Brings the selection highlight up to date (cheap: the features are not rebuilt).</summary>
	public void ShowSelection(MapFeatureLayer spec, MapTheme theme)
	{
		Spec = spec;
		HashSet<long> ids = spec.Selected as HashSet<long> ?? new HashSet<long>(spec.Selected ?? Array.Empty<long>());
		List<IFeature> selected = new List<IFeature>();
		for (int i = 0; i < Features.Length; i++)
		{
			if (ids.Contains(Features[i].Id) && !_hidden.Contains(Features[i].Id))
			{
				selected.Add(new GeometryFeature(Shapes[i]));
			}
		}
		for (int i = 0; i < _editFeatures.Length; i++)
		{
			if (ids.Contains(_editFeatures[i].Id))
			{
				selected.Add(new GeometryFeature(_editShapes[i]));
			}
		}
		Selection.Style = theme.SelectionStyle(spec.Data?.GeometryKind ?? GeometryKind.Unknown);
		Selection.Features = selected;
		Selection.DataHasChanged();
	}

	/// <summary>The drawn features whose bounding boxes meet a Web Mercator rectangle (indexes into <see cref="Features"/>).</summary>
	public IList<int> Query(Envelope metres) => _index.Query(metres);

	/// <summary>
	/// The topmost feature under a point (Web Mercator metres), within <paramref name="tolerance"/> metres of a point or
	/// line, or inside a polygon; null when there is none.
	/// </summary>
	public Feature FeatureAt(double x, double y, double tolerance)
	{
		Envelope around = new Envelope(x - tolerance, x + tolerance, y - tolerance, y + tolerance);
		Point p = new Point(x, y);
		// Edited features are drawn on top, so they are hit first.
		for (int i = _editShapes.Length - 1; i >= 0; i--)
		{
			if (_editShapes[i].EnvelopeInternal.Intersects(around) && DistanceOp.Distance(_editShapes[i], p) <= tolerance)
			{
				return _editFeatures[i];
			}
		}
		int best = -1;
		double bestDistance = double.MaxValue;
		foreach (int i in _index.Query(around))
		{
			if (_hidden.Contains(Features[i].Id))
			{
				continue;
			}
			double d = DistanceOp.Distance(Shapes[i], p);
			if (d > tolerance)
			{
				continue;
			}
			// Nearest wins; among equals (overlapping polygons), the one drawn last is on top.
			if (d < bestDistance || d == bestDistance && i > best)
			{
				best = i;
				bestDistance = d;
			}
		}
		return best < 0 ? null : Features[best];
	}

	/// <summary>The engine's style for one of our symbols.</summary>
	public static IStyle SymbolStyleFor(Symbol symbol, GeometryKind kind)
	{
		Color fill = ToColor(symbol.Fill, new Rgba(0x2F, 0x6F, 0xB3));
		Color stroke = ToColor(symbol.Stroke, new Rgba(0x1B, 0x3F, 0x66));
		double width = Math.Max(0, symbol.StrokeWidth);
		switch (kind)
		{
			case GeometryKind.Point:
				return new SymbolStyle
				{
					SymbolType = symbol.Shape switch
					{
						PointShape.Square or PointShape.Diamond => SymbolType.Rectangle,
						PointShape.Triangle => SymbolType.Triangle,
						_ => SymbolType.Ellipse,
					},
					SymbolRotation = symbol.Shape == PointShape.Diamond ? 45 : 0,
					SymbolScale = Math.Max(2, symbol.PointSize) / SymbolStyle.DefaultWidth,
					Fill = new Brush(fill),
					Outline = width > 0 ? new Pen(stroke, width) : null,
				};
			case GeometryKind.Line:
				return new VectorStyle { Fill = null, Outline = null, Line = new Pen(stroke, Math.Max(1, width)) };
			default:
				return new VectorStyle { Fill = new Brush(fill), Outline = width > 0 ? new Pen(stroke, width) : null, Line = new Pen(stroke, Math.Max(1, width)) };
		}
	}

	/// <summary>An engine colour from "#RRGGBB" or "#RRGGBBAA".</summary>
	public static Color ToColor(string hex, Rgba fallback)
	{
		Rgba c = Rgba.Parse(hex, fallback);
		return new Color(c.R, c.G, c.B, c.A);
	}

	/// <summary>A WGS 84 geometry in Web Mercator metres (a copy).</summary>
	public static Geometry ToMetres(Geometry wgs84)
	{
		Geometry copy = wgs84.Copy();
		copy.Apply(new MercatorFilter());
		copy.GeometryChanged();
		return copy;
	}

	/// <summary>A Web Mercator geometry in WGS 84 longitude and latitude (a copy, with the WGS 84 factory).</summary>
	public static Geometry ToWgs84(Geometry metres)
	{
		Geometry copy = Sde.Gis.GisSetup.Factory.CreateGeometry(metres);
		copy.Apply(new LatLonFilter());
		copy.GeometryChanged();
		return copy;
	}

	private static MemoryLayer BuildLabels(MapFeatureLayer spec, BuiltFeatureLayer built)
	{
		Sde.Gis.Styles.LabelStyle labels = spec.Labels;
		if (labels == null || !labels.Enabled || string.IsNullOrEmpty(labels.Field))
		{
			return null;
		}
		List<IFeature> points = new List<IFeature>();
		for (int i = 0; i < built.Features.Length; i++)
		{
			string text = labels.TextFor(built.Features[i]);
			if (text == null)
			{
				continue;
			}
			Geometry shape = built.Shapes[i];
			Point anchor = shape is Polygon or MultiPolygon ? shape.InteriorPoint : shape is Point pt ? pt : shape.Centroid;
			PointFeature label = new PointFeature(anchor.X, anchor.Y);
			label["text"] = text;
			points.Add(label);
			built._labelPoints.Add((label, built.Features[i].Id));
		}
		MemoryLayer layer = new MemoryLayer((spec.Name ?? spec.Key) + " labels")
		{
			Features = points,
			Style = new MapLabelStyle
			{
				Text = f => f["text"] as string,
				Size = labels.Size > 0 ? labels.Size : 11,
				Color = ToColor(labels.Color, new Rgba(0x1F, 0x23, 0x28)),
				Halo = labels.Halo ? ToColor(labels.HaloColor, new Rgba(255, 255, 255)) : null,
				HaloWidth = labels.HaloWidth,
				Rise = (spec.Data?.GeometryKind ?? GeometryKind.Unknown) == GeometryKind.Point ? 9 : 0,
			},
		};
		layer.Attribution.Enabled = false;
		return layer;
	}

	public void Dispose()
	{
		Layer?.Dispose();
		if (!ReferenceEquals(Layer, Source))
		{
			Source?.Dispose();
		}
		Labels?.Dispose();
		Selection?.Dispose();
		Edits?.Dispose();
	}

	/// <summary>Turns Web Mercator metres into longitude and latitude, coordinate by coordinate.</summary>
	private sealed class LatLonFilter : ICoordinateSequenceFilter
	{
		public bool Done => false;

		public bool GeometryChanged => true;

		public void Filter(CoordinateSequence seq, int i)
		{
			(double lon, double lat) = WebMercator.FromMetres(seq.GetX(i), seq.GetY(i));
			seq.SetX(i, lon);
			seq.SetY(i, lat);
		}
	}

	/// <summary>Turns longitude and latitude into Web Mercator metres, coordinate by coordinate.</summary>
	private sealed class MercatorFilter : ICoordinateSequenceFilter
	{
		public bool Done => false;

		public bool GeometryChanged => true;

		public void Filter(CoordinateSequence seq, int i)
		{
			(double x, double y) = WebMercator.ToMetres(seq.GetX(i), seq.GetY(i));
			seq.SetX(i, x);
			seq.SetY(i, y);
		}
	}
}

/// <summary>The colours the map takes from the app's theme (light or dark), as engine styles.</summary>
internal sealed class MapTheme
{
	public Color Background { get; init; } = new Color(242, 244, 246);

	/// <summary>The ring around structure dots (the card colour, so dots stand out on any basemap).</summary>
	public Color DotOutline { get; init; } = Color.White;

	/// <summary>Selection and the structure picked in the list.</summary>
	public Color Accent { get; init; } = new Color(30, 90, 168);

	/// <summary>Label text on structures.</summary>
	public Color Text { get; init; } = new Color(22, 25, 29);

	/// <summary>The halo behind label text.</summary>
	public Color Halo { get; init; } = Color.White;

	/// <summary>How selected features of a layer are highlighted.</summary>
	public IStyle SelectionStyle(GeometryKind kind) => kind switch
	{
		GeometryKind.Point => new SymbolStyle
		{
			SymbolType = SymbolType.Ellipse,
			SymbolScale = 18 / SymbolStyle.DefaultWidth,
			Fill = null,
			Outline = new Pen(Accent, 3),
		},
		GeometryKind.Line => new VectorStyle { Fill = null, Outline = null, Line = new Pen(Accent, 4) },
		_ => new VectorStyle
		{
			Fill = new Brush(new Color(Accent.R, Accent.G, Accent.B, 60)),
			Outline = new Pen(Accent, 3),
			Line = new Pen(Accent, 3),
		},
	};
}

/// <summary>The structure dots (and their labels) as the engine draws them, on screen and in print.</summary>
internal static class StructureDots
{
	/// <summary>One dot per marker, in its fill, with an outline in the theme's card colour.</summary>
	public static List<IFeature> Dots(IReadOnlyList<MapMarker> markers, MapTheme theme)
	{
		Dictionary<Color, IStyle> dots = new Dictionary<Color, IStyle>();
		List<IFeature> features = new List<IFeature>(markers.Count);
		foreach (MapMarker m in markers)
		{
			(double x, double y) = WebMercator.ToMetres(m.Longitude, m.Latitude);
			Color fill = MapCanvas.ToEngine(m.Fill, new Color(128, 128, 128));
			if (!dots.TryGetValue(fill, out IStyle dot))
			{
				dot = new SymbolStyle
				{
					SymbolType = SymbolType.Ellipse,
					SymbolScale = MapCanvas.DotDiameter / SymbolStyle.DefaultWidth,
					Fill = new Brush(fill),
					Outline = new Pen(theme.DotOutline, 1.8),
				};
				dots[fill] = dot;
			}
			PointFeature f = new PointFeature(x, y);
			f.Styles.Add(dot);
			features.Add(f);
		}
		return features;
	}

	/// <summary>A label point for each marker that has a label.</summary>
	public static List<IFeature> LabelPoints(IReadOnlyList<MapMarker> markers)
	{
		List<IFeature> labels = new List<IFeature>();
		foreach (MapMarker m in markers.Where(m => !string.IsNullOrWhiteSpace(m.Label)))
		{
			(double x, double y) = WebMercator.ToMetres(m.Longitude, m.Latitude);
			PointFeature label = new PointFeature(x, y);
			label["text"] = m.Label;
			labels.Add(label);
		}
		return labels;
	}

	/// <summary>The labels layer, sitting just above the dots.</summary>
	public static MemoryLayer LabelLayer(List<IFeature> labels, Sde.Gis.Styles.LabelStyle style, MapTheme theme)
	{
		MemoryLayer layer = new MemoryLayer("Structure labels")
		{
			Features = labels,
			Style = new MapLabelStyle
			{
				Text = f => f["text"] as string,
				Size = style.Size > 0 ? style.Size : 11,
				Color = theme.Text,
				Halo = style.Halo ? theme.Halo : null,
				HaloWidth = style.HaloWidth,
				Rise = MapCanvas.DotDiameter / 2 + 3,
			},
		};
		layer.Attribution.Enabled = false;
		return layer;
	}
}
