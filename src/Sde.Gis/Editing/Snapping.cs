using NetTopologySuite.Geometries;

namespace Sde.Gis.Editing;

/// <summary>What a point snapped to.</summary>
public enum SnapKind
{
	/// <summary>Nothing was close enough; the point stays where it was.</summary>
	None,

	/// <summary>A corner of a shape (or a point).</summary>
	Vertex,

	/// <summary>The nearest point on an edge of a shape.</summary>
	Edge,
}

/// <summary>Where a point ends up after snapping, and what it snapped to.</summary>
public readonly record struct SnapResult(Coordinate At, SnapKind Kind)
{
	public bool Snapped => Kind != SnapKind.None;
}

/// <summary>
/// Snapping for the map's editor: a point dropped near another shape's corner moves onto that corner, and otherwise a
/// point near an edge moves onto the edge, so shapes drawn next to each other share their boundaries. Corners win over
/// edges within the tolerance, because a corner is what a person aims for. Coordinates are planar (the map uses Web
/// Mercator metres, so the tolerance can be a number of screen pixels times the map's resolution).
/// </summary>
public static class Snapping
{
	/// <summary>
	/// The nearest corner of <paramref name="candidates"/> within <paramref name="tolerance"/> of <paramref name="p"/>;
	/// otherwise the nearest point on an edge within it; otherwise <paramref name="p"/> itself.
	/// </summary>
	public static SnapResult Snap(Coordinate p, IEnumerable<Geometry> candidates, double tolerance)
	{
		ArgumentNullException.ThrowIfNull(p);
		if (candidates == null || !(tolerance > 0))
		{
			return new SnapResult(p, SnapKind.None);
		}
		Coordinate bestVertex = null;
		double vertexDistance = double.MaxValue;
		Coordinate bestEdge = null;
		double edgeDistance = double.MaxValue;
		foreach (Geometry g in candidates)
		{
			if (g == null || g.IsEmpty)
			{
				continue;
			}
			Envelope near = g.EnvelopeInternal;
			if (near.Distance(new Envelope(p)) > tolerance)
			{
				continue;
			}
			foreach ((VertexRef _, Coordinate at) in GeometryEdit.Vertices(g))
			{
				double d = at.Distance(p);
				if (d <= tolerance && d < vertexDistance)
				{
					bestVertex = at;
					vertexDistance = d;
				}
			}
			if (bestVertex != null)
			{
				// Once a corner is within reach, no edge can win; the other shapes may still have a nearer corner.
				continue;
			}
			foreach ((VertexRef _, Coordinate a, Coordinate b) in GeometryEdit.Edges(g))
			{
				Coordinate on = new LineSegment(a, b).ClosestPoint(p);
				double d = on.Distance(p);
				if (d <= tolerance && d < edgeDistance)
				{
					bestEdge = on;
					edgeDistance = d;
				}
			}
		}
		if (bestVertex != null)
		{
			return new SnapResult(new Coordinate(bestVertex.X, bestVertex.Y), SnapKind.Vertex);
		}
		return bestEdge != null
			? new SnapResult(new Coordinate(bestEdge.X, bestEdge.Y), SnapKind.Edge)
			: new SnapResult(p, SnapKind.None);
	}
}
