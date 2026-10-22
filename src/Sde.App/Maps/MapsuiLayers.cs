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

	public bool IsRasterized { get; private set; }

	/// <summary>The drawn features' geometries in Web Mercator metres, in drawing order.</summary>
	public Geometry[] Shapes { get; private set; }

	/// <summary>For each drawn feature, the feature in <see cref="MapFeatureLayer.Data"/>.</summary>
	public Feature[] Features { get; private set; }

	/// <summary>Builds the engine's layers for a feature layer.</summary>
	public static BuiltFeatureLayer Build(MapFeatureLayer spec, MapTheme theme, float pixelDensity)
	{
		BuiltFeatureLayer built = new BuiltFeatureLayer(spec);
		// One engine style per symbol and kind of geometry (a layer may mix points, lines and polygons).
		Dictionary<Symbol, IStyle[]> styles = new Dictionary<Symbol, IStyle[]>(ReferenceEqualityComparer.Instance);
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
			GeometryKind kind = FeatureLayer.KindOf(f.Geometry);
			if (!styles.TryGetValue(symbol, out IStyle[] byKind))
			{
				byKind = new IStyle[4];
				styles[symbol] = byKind;
			}
			IStyle s = byKind[(int)kind] ??= SymbolStyleFor(symbol, kind);
			GeometryFeature g = new GeometryFeature(metres);
			g.Styles.Add(s);
			built._index.Insert(metres.EnvelopeInternal, shapes.Count);
			drawn.Add(g);
			shapes.Add(metres);
			features.Add(f);
		}
		built._index.Build();
		built.Shapes = shapes.ToArray();
		built.Features = features.ToArray();
		built.Source = new MemoryLayer(spec.Name ?? spec.Key) { Features = drawn, Style = null };
		built.IsRasterized = drawn.Count > RasterizeAbove;
		built.Layer = built.IsRasterized
			? new RasterizingTileLayer(built.Source, pixelDensity, dataFetchStrategy: new DataFetchStrategy(0)) { Name = spec.Name ?? spec.Key }
			: built.Source;
		built.Layer.Attribution.Enabled = false;
		built.Layer.Opacity = spec.Display?.ClampedOpacity ?? 1;
		built.Labels = BuildLabels(spec, built);
		built.Selection = new MemoryLayer((spec.Name ?? spec.Key) + " selected") { Style = theme.SelectionStyle(spec.Data?.GeometryKind ?? GeometryKind.Unknown) };
		built.ShowSelection(spec, theme);
		return built;
	}

	/// <summary>Brings the selection highlight up to date (cheap: the features are not rebuilt).</summary>
	public void ShowSelection(MapFeatureLayer spec, MapTheme theme)
	{
		Spec = spec;
		HashSet<long> ids = spec.Selected as HashSet<long> ?? new HashSet<long>(spec.Selected ?? Array.Empty<long>());
		List<IFeature> selected = new List<IFeature>();
		for (int i = 0; i < Features.Length; i++)
		{
			if (ids.Contains(Features[i].Id))
			{
				selected.Add(new GeometryFeature(Shapes[i]));
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
		int best = -1;
		double bestDistance = double.MaxValue;
		foreach (int i in _index.Query(around))
		{
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
