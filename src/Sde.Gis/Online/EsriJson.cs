using System.Text.Json;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;

namespace Sde.Gis.Online;

/// <summary>
/// Esri JSON geometries (points, multipoints, paths and rings), for ArcGIS servers too old to answer in GeoJSON.
/// Rings follow Esri's rule: clockwise rings are outer boundaries, counter-clockwise rings are holes in the outer ring
/// that contains them.
/// </summary>
internal static class EsriJson
{
	public static Geometry ReadGeometry(JsonElement g)
	{
		GeometryFactory f = GisSetup.Factory;
		if (g.TryGetProperty("x", out JsonElement x) && g.TryGetProperty("y", out JsonElement y))
		{
			return x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number ? f.CreatePoint(new Coordinate(x.GetDouble(), y.GetDouble())) : null;
		}
		if (g.TryGetProperty("points", out JsonElement points) && points.ValueKind == JsonValueKind.Array)
		{
			Point[] pts = Coordinates(points).Select(c => f.CreatePoint(c)).ToArray();
			return pts.Length == 0 ? null : pts.Length == 1 ? pts[0] : f.CreateMultiPoint(pts);
		}
		if (g.TryGetProperty("paths", out JsonElement paths) && paths.ValueKind == JsonValueKind.Array)
		{
			LineString[] lines = paths.EnumerateArray().Select(p => Coordinates(p)).Where(c => c.Length >= 2).Select(c => f.CreateLineString(c)).ToArray();
			return lines.Length == 0 ? null : lines.Length == 1 ? lines[0] : f.CreateMultiLineString(lines);
		}
		if (g.TryGetProperty("rings", out JsonElement rings) && rings.ValueKind == JsonValueKind.Array)
		{
			return Polygons(rings, f);
		}
		return null;
	}

	private static Geometry Polygons(JsonElement rings, GeometryFactory f)
	{
		List<LinearRing> shells = new List<LinearRing>();
		List<LinearRing> holes = new List<LinearRing>();
		foreach (JsonElement r in rings.EnumerateArray())
		{
			Coordinate[] c = Coordinates(r);
			if (c.Length < 3)
			{
				continue;
			}
			if (!c[0].Equals2D(c[^1]))
			{
				c = c.Append(c[0].Copy()).ToArray();
			}
			if (c.Length < 4)
			{
				continue;
			}
			LinearRing ring = f.CreateLinearRing(c);
			(Orientation.IsCCW(ring.CoordinateSequence) ? holes : shells).Add(ring);
		}
		if (shells.Count == 0)
		{
			// Rings wound the wrong way round: treat them all as outer boundaries.
			shells.AddRange(holes);
			holes.Clear();
		}
		List<List<LinearRing>> holesOf = shells.Select(_ => new List<LinearRing>()).ToList();
		List<Polygon> shellPolygons = shells.Select(s => f.CreatePolygon(s)).ToList();
		foreach (LinearRing hole in holes)
		{
			int owner = -1;
			for (int i = 0; i < shellPolygons.Count; i++)
			{
				if (shellPolygons[i].Contains(f.CreatePoint(hole.Coordinates[0])) || shellPolygons[i].Covers(hole))
				{
					if (owner < 0 || shellPolygons[i].Area < shellPolygons[owner].Area)
					{
						owner = i;
					}
				}
			}
			if (owner >= 0)
			{
				holesOf[owner].Add(hole);
			}
		}
		Polygon[] polygons = shells.Select((s, i) => f.CreatePolygon(s, holesOf[i].ToArray())).ToArray();
		return polygons.Length == 1 ? polygons[0] : f.CreateMultiPolygon(polygons);
	}

	private static Coordinate[] Coordinates(JsonElement array)
	{
		List<Coordinate> list = new List<Coordinate>();
		if (array.ValueKind != JsonValueKind.Array)
		{
			return list.ToArray();
		}
		foreach (JsonElement p in array.EnumerateArray())
		{
			if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 && p[0].ValueKind == JsonValueKind.Number && p[1].ValueKind == JsonValueKind.Number)
			{
				list.Add(new Coordinate(p[0].GetDouble(), p[1].GetDouble()));
			}
		}
		return list.ToArray();
	}
}
