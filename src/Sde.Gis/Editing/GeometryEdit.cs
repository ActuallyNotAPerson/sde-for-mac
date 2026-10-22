using NetTopologySuite.Geometries;

namespace Sde.Gis.Editing;

/// <summary>
/// A corner of a geometry: which part (the polygon of a multipolygon, the line of a multiline, the point of a
/// multipoint; 0 for a single shape), which ring of a polygon (0 is the outside, 1 and up are holes; 0 for lines and
/// points) and which coordinate along it. A ring's closing coordinate is never a corner of its own: moving the first
/// corner moves it too.
/// </summary>
public readonly record struct VertexRef(int Part, int Ring, int Index);

/// <summary>
/// Changes to the corners of points, lines and polygons, the way the map's editor makes them: move a corner, add one on
/// an edge, take one away, or move the whole shape. Every method returns a new geometry and leaves the one it was given
/// alone, so an edit can be undone by keeping the old one. The coordinates can be in any units (the editor works in
/// longitude and latitude, the map in Web Mercator metres); the structure of the shape is the same in both, so a
/// <see cref="VertexRef"/> found in one applies to the other.
/// </summary>
public static class GeometryEdit
{
	/// <summary>Every corner of a geometry, in order (rings without their closing coordinate).</summary>
	public static IReadOnlyList<(VertexRef Ref, Coordinate At)> Vertices(Geometry geometry)
	{
		List<(VertexRef, Coordinate)> list = new List<(VertexRef, Coordinate)>();
		if (geometry == null || geometry.IsEmpty)
		{
			return list;
		}
		Shape shape = Shape.Of(geometry);
		for (int p = 0; p < shape.Parts.Count; p++)
		{
			for (int r = 0; r < shape.Parts[p].Count; r++)
			{
				Coordinate[] ring = shape.Parts[p][r];
				for (int i = 0; i < ring.Length; i++)
				{
					list.Add((new VertexRef(p, r, i), ring[i]));
				}
			}
		}
		return list;
	}

	/// <summary>
	/// Every edge between two corners, as the corner it starts from and its two ends (a polygon's ring includes the edge
	/// back to its first corner). A new corner on an edge goes in after <c>From</c>.
	/// </summary>
	public static IReadOnlyList<(VertexRef From, Coordinate A, Coordinate B)> Edges(Geometry geometry)
	{
		List<(VertexRef, Coordinate, Coordinate)> list = new List<(VertexRef, Coordinate, Coordinate)>();
		if (geometry == null || geometry.IsEmpty)
		{
			return list;
		}
		Shape shape = Shape.Of(geometry);
		if (shape.Kind == ShapeKind.Point)
		{
			return list;
		}
		for (int p = 0; p < shape.Parts.Count; p++)
		{
			for (int r = 0; r < shape.Parts[p].Count; r++)
			{
				Coordinate[] ring = shape.Parts[p][r];
				int count = shape.Kind == ShapeKind.Polygon ? ring.Length : ring.Length - 1;
				for (int i = 0; i < count; i++)
				{
					list.Add((new VertexRef(p, r, i), ring[i], ring[(i + 1) % ring.Length]));
				}
			}
		}
		return list;
	}

	/// <summary>The coordinate of a corner, or null when the geometry has no such corner.</summary>
	public static Coordinate VertexAt(Geometry geometry, VertexRef vertex)
	{
		if (geometry == null || geometry.IsEmpty)
		{
			return null;
		}
		Shape shape = Shape.Of(geometry);
		return shape.Has(vertex) ? shape.Parts[vertex.Part][vertex.Ring][vertex.Index].Copy() : null;
	}

	/// <summary>The geometry with one corner moved to <paramref name="to"/>.</summary>
	public static Geometry MoveVertex(Geometry geometry, VertexRef vertex, Coordinate to)
	{
		ArgumentNullException.ThrowIfNull(to);
		Shape shape = Shape.Of(geometry);
		if (!shape.Has(vertex))
		{
			throw new ArgumentOutOfRangeException(nameof(vertex), "The shape has no such corner.");
		}
		shape.Parts[vertex.Part][vertex.Ring][vertex.Index] = new Coordinate(to.X, to.Y);
		return shape.Build(geometry.Factory);
	}

	/// <summary>
	/// The geometry with a new corner at <paramref name="at"/> on the edge that starts at <paramref name="after"/>
	/// (so the new corner's index is one more than <paramref name="after"/>'s). Points have no edges.
	/// </summary>
	public static Geometry InsertVertex(Geometry geometry, VertexRef after, Coordinate at)
	{
		ArgumentNullException.ThrowIfNull(at);
		Shape shape = Shape.Of(geometry);
		if (shape.Kind == ShapeKind.Point || !shape.Has(after))
		{
			throw new ArgumentOutOfRangeException(nameof(after), "The shape has no such edge.");
		}
		List<Coordinate> ring = shape.Parts[after.Part][after.Ring].ToList();
		ring.Insert(after.Index + 1, new Coordinate(at.X, at.Y));
		shape.Parts[after.Part][after.Ring] = ring.ToArray();
		return shape.Build(geometry.Factory);
	}

	/// <summary>
	/// True when a corner can be taken away and leave a shape: a line keeps at least two corners and a polygon's ring
	/// at least three. A point (or a point of a multipoint) is not a corner to take away: delete the feature instead.
	/// </summary>
	public static bool CanDeleteVertex(Geometry geometry, VertexRef vertex)
	{
		if (geometry == null || geometry.IsEmpty)
		{
			return false;
		}
		Shape shape = Shape.Of(geometry);
		if (!shape.Has(vertex))
		{
			return false;
		}
		int left = shape.Parts[vertex.Part][vertex.Ring].Length - 1;
		return shape.Kind switch
		{
			ShapeKind.Line => left >= 2,
			ShapeKind.Polygon => left >= 3,
			_ => false,
		};
	}

	/// <summary>The geometry without one corner, or null when that would leave too few corners (see <see cref="CanDeleteVertex"/>).</summary>
	public static Geometry DeleteVertex(Geometry geometry, VertexRef vertex)
	{
		if (!CanDeleteVertex(geometry, vertex))
		{
			return null;
		}
		Shape shape = Shape.Of(geometry);
		List<Coordinate> ring = shape.Parts[vertex.Part][vertex.Ring].ToList();
		ring.RemoveAt(vertex.Index);
		shape.Parts[vertex.Part][vertex.Ring] = ring.ToArray();
		return shape.Build(geometry.Factory);
	}

	/// <summary>The geometry moved by <paramref name="dx"/> and <paramref name="dy"/> in its own units.</summary>
	public static Geometry Translate(Geometry geometry, double dx, double dy)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		Geometry copy = geometry.Copy();
		copy.Apply(new Shift(dx, dy));
		copy.GeometryChanged();
		return copy;
	}

	/// <summary>
	/// The point on a geometry's edges nearest to <paramref name="p"/> and the edge it is on, or null for a point or an
	/// empty geometry. Used to put a new corner where the person pressed on an edge.
	/// </summary>
	public static (VertexRef From, Coordinate At, double Distance)? NearestEdge(Geometry geometry, Coordinate p)
	{
		(VertexRef From, Coordinate At, double Distance)? best = null;
		foreach ((VertexRef from, Coordinate a, Coordinate b) in Edges(geometry))
		{
			LineSegment segment = new LineSegment(a, b);
			Coordinate on = segment.ClosestPoint(p);
			double d = on.Distance(p);
			if (best == null || d < best.Value.Distance)
			{
				best = (from, on, d);
			}
		}
		return best;
	}

	private enum ShapeKind
	{
		Point,
		Line,
		Polygon,
	}

	/// <summary>A geometry taken apart into parts, rings and corners (rings open), and put back together.</summary>
	private sealed class Shape
	{
		private Shape(ShapeKind kind, bool multi, List<List<Coordinate[]>> parts)
		{
			Kind = kind;
			Multi = multi;
			Parts = parts;
		}

		public ShapeKind Kind { get; }

		/// <summary>True for MultiPoint, MultiLineString and MultiPolygon, which are put back as the same type.</summary>
		public bool Multi { get; }

		public List<List<Coordinate[]>> Parts { get; }

		public bool Has(VertexRef v) => v.Part >= 0 && v.Part < Parts.Count && v.Ring >= 0 && v.Ring < Parts[v.Part].Count
			&& v.Index >= 0 && v.Index < Parts[v.Part][v.Ring].Length;

		public static Shape Of(Geometry g)
		{
			ArgumentNullException.ThrowIfNull(g);
			switch (g)
			{
				case Point p:
					return new Shape(ShapeKind.Point, false, new List<List<Coordinate[]>> { new List<Coordinate[]> { new[] { p.Coordinate.Copy() } } });
				case MultiPoint mp:
					return new Shape(ShapeKind.Point, true, Enumerable.Range(0, mp.NumGeometries)
						.Select(i => new List<Coordinate[]> { new[] { mp.GetGeometryN(i).Coordinate.Copy() } }).ToList());
				case LineString line:
					return new Shape(ShapeKind.Line, false, new List<List<Coordinate[]>> { new List<Coordinate[]> { Copy(line.Coordinates) } });
				case MultiLineString ml:
					return new Shape(ShapeKind.Line, true, Enumerable.Range(0, ml.NumGeometries)
						.Select(i => new List<Coordinate[]> { Copy(ml.GetGeometryN(i).Coordinates) }).ToList());
				case Polygon poly:
					return new Shape(ShapeKind.Polygon, false, new List<List<Coordinate[]>> { Rings(poly) });
				case MultiPolygon mpoly:
					return new Shape(ShapeKind.Polygon, true, Enumerable.Range(0, mpoly.NumGeometries).Select(i => Rings((Polygon)mpoly.GetGeometryN(i))).ToList());
				default:
					throw new ArgumentException("Only points, lines and polygons can be edited.", nameof(g));
			}
		}

		private static Coordinate[] Copy(Coordinate[] coords) => coords.Select(c => new Coordinate(c.X, c.Y)).ToArray();

		/// <summary>The outer ring then the holes, each without its closing coordinate.</summary>
		private static List<Coordinate[]> Rings(Polygon polygon)
		{
			List<Coordinate[]> rings = new List<Coordinate[]> { Open(polygon.ExteriorRing.Coordinates) };
			for (int i = 0; i < polygon.NumInteriorRings; i++)
			{
				rings.Add(Open(polygon.GetInteriorRingN(i).Coordinates));
			}
			return rings;
		}

		private static Coordinate[] Open(Coordinate[] ring)
		{
			int n = ring.Length > 1 && ring[0].Equals2D(ring[^1]) ? ring.Length - 1 : ring.Length;
			return ring.Take(n).Select(c => new Coordinate(c.X, c.Y)).ToArray();
		}

		private static Coordinate[] Close(Coordinate[] ring) => ring.Append(new Coordinate(ring[0].X, ring[0].Y)).ToArray();

		public Geometry Build(GeometryFactory factory)
		{
			factory ??= GisSetup.Factory;
			switch (Kind)
			{
				case ShapeKind.Point:
					Point[] points = Parts.Select(p => factory.CreatePoint(p[0][0])).ToArray();
					return Multi ? factory.CreateMultiPoint(points) : points[0];
				case ShapeKind.Line:
					LineString[] lines = Parts.Select(p => factory.CreateLineString(p[0])).ToArray();
					return Multi ? factory.CreateMultiLineString(lines) : lines[0];
				default:
					Polygon[] polygons = Parts.Select(p => factory.CreatePolygon(
						factory.CreateLinearRing(Close(p[0])),
						p.Skip(1).Select(h => factory.CreateLinearRing(Close(h))).ToArray())).ToArray();
					return Multi ? factory.CreateMultiPolygon(polygons) : polygons[0];
			}
		}
	}

	/// <summary>Adds an offset to every coordinate.</summary>
	private sealed class Shift(double dx, double dy) : ICoordinateSequenceFilter
	{
		public bool Done => false;

		public bool GeometryChanged => true;

		public void Filter(CoordinateSequence seq, int i)
		{
			seq.SetX(i, seq.GetX(i) + dx);
			seq.SetY(i, seq.GetY(i) + dy);
		}
	}
}
