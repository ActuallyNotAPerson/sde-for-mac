using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using Sde.Gis.Features;

namespace Sde.Gis.Analysis;

/// <summary>How a structure point was matched to a polygon.</summary>
public enum JoinOutcome
{
	/// <summary>The point is inside (or on the edge of) exactly one polygon.</summary>
	InOne,

	/// <summary>The point is inside more than one polygon (overlapping polygons); <see cref="PointJoinResult.Polygon"/> is the smallest.</summary>
	InSeveral,

	/// <summary>The point is in no polygon but within the tolerance of one; <see cref="PointJoinResult.Polygon"/> is the nearest.</summary>
	Nearest,

	/// <summary>The point is in no polygon (and none is within the tolerance).</summary>
	None,
}

/// <summary>The polygon found for one point.</summary>
public sealed class PointJoinResult
{
	public Feature Point { get; init; }

	/// <summary>The polygon whose values are copied: the only one, the smallest of several, or the nearest; null when none.</summary>
	public Feature Polygon { get; init; }

	/// <summary>Every polygon the point is in (empty for <see cref="JoinOutcome.Nearest"/> and <see cref="JoinOutcome.None"/>).</summary>
	public IReadOnlyList<Feature> Matches { get; init; } = Array.Empty<Feature>();

	public JoinOutcome Outcome { get; init; }

	/// <summary>For <see cref="JoinOutcome.Nearest"/>, how far the point is from the polygon, in metres.</summary>
	public double? DistanceMetres { get; init; }
}

/// <summary>Choices for <see cref="SpatialJoin.PointsInPolygons"/>.</summary>
public sealed class SpatialJoinOptions
{
	/// <summary>
	/// For a point in no polygon, take the nearest polygon within this many metres (a structure dot placed in the
	/// street, just outside its parcel). Zero turns this off.
	/// </summary>
	public double NearestToleranceMetres { get; init; }
}

/// <summary>
/// The spatial join behind "copy polygon values onto structures": for each structure point, the polygon it falls in.
/// Points in several polygons and points in none are reported, so the person can see them before anything is copied.
/// Edges count as inside. Distances are measured in a <see cref="LocalProjection"/>.
/// </summary>
public static class SpatialJoin
{
	private const double MetresPerDegree = 111320.0;

	public static List<PointJoinResult> PointsInPolygons(IEnumerable<Feature> points, IEnumerable<Feature> polygons, SpatialJoinOptions options = null)
	{
		ArgumentNullException.ThrowIfNull(points);
		ArgumentNullException.ThrowIfNull(polygons);
		options ??= new SpatialJoinOptions();
		List<Feature> pointList = points.ToList();
		List<Feature> polygonList = polygons.Where(p => p.Geometry != null && !p.Geometry.IsEmpty && p.Geometry.Dimension == Dimension.Surface).ToList();

		STRtree<int> index = new STRtree<int>();
		IPreparedGeometry[] prepared = new IPreparedGeometry[polygonList.Count];
		double[] areas = new double[polygonList.Count];
		for (int i = 0; i < polygonList.Count; i++)
		{
			index.Insert(polygonList[i].Geometry.EnvelopeInternal, i);
			prepared[i] = PreparedGeometryFactory.Prepare(polygonList[i].Geometry);
			areas[i] = polygonList[i].Geometry.Area;
		}
		index.Build();

		Envelope extent = new Envelope();
		foreach (Feature p in pointList)
		{
			if (p.Geometry != null && !p.Geometry.IsEmpty)
			{
				extent.ExpandToInclude(p.Geometry.EnvelopeInternal);
			}
		}
		LocalProjection local = options.NearestToleranceMetres > 0 ? LocalProjection.For(extent) : null;
		Dictionary<int, Geometry> projected = new Dictionary<int, Geometry>();

		List<PointJoinResult> results = new List<PointJoinResult>(pointList.Count);
		foreach (Feature point in pointList)
		{
			if (point.Geometry == null || point.Geometry.IsEmpty)
			{
				results.Add(new PointJoinResult { Point = point, Outcome = JoinOutcome.None });
				continue;
			}
			Geometry g = point.Geometry is MultiPoint mp && mp.NumGeometries > 0 ? mp.GetGeometryN(0) : point.Geometry;
			List<int> inside = index.Query(g.EnvelopeInternal).Where(i => prepared[i].Covers(g)).OrderBy(i => areas[i]).ThenBy(i => i).ToList();
			if (inside.Count > 0)
			{
				results.Add(new PointJoinResult
				{
					Point = point,
					Polygon = polygonList[inside[0]],
					Matches = inside.Select(i => polygonList[i]).ToList(),
					Outcome = inside.Count == 1 ? JoinOutcome.InOne : JoinOutcome.InSeveral,
				});
				continue;
			}
			if (local != null)
			{
				double tolerance = options.NearestToleranceMetres;
				double lat = g.Coordinate.Y;
				Envelope search = g.EnvelopeInternal.Copy();
				search.ExpandBy(tolerance / (MetresPerDegree * Math.Max(0.01, Math.Cos(lat * Math.PI / 180.0))) * 1.05, tolerance / MetresPerDegree * 1.05);
				Geometry pointMetres = local.Project(g);
				int best = -1;
				double bestDistance = double.MaxValue;
				foreach (int i in index.Query(search))
				{
					if (!projected.TryGetValue(i, out Geometry polygonMetres))
					{
						polygonMetres = local.Project(polygonList[i].Geometry);
						projected[i] = polygonMetres;
					}
					double d = pointMetres.Distance(polygonMetres);
					if (d <= tolerance && (d < bestDistance || (d == bestDistance && i < best)))
					{
						best = i;
						bestDistance = d;
					}
				}
				if (best >= 0)
				{
					results.Add(new PointJoinResult { Point = point, Polygon = polygonList[best], Outcome = JoinOutcome.Nearest, DistanceMetres = bestDistance });
					continue;
				}
			}
			results.Add(new PointJoinResult { Point = point, Outcome = JoinOutcome.None });
		}
		return results;
	}
}
