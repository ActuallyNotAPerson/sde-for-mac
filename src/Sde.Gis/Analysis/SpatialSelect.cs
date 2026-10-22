using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using Sde.Gis.Features;

namespace Sde.Gis.Analysis;

/// <summary>How a feature must relate to the selecting shapes to be selected.</summary>
public enum SpatialRelation
{
	/// <summary>Touches or overlaps a selecting shape at all.</summary>
	Intersects,

	/// <summary>Lies completely inside a selecting shape (its edge counts as inside).</summary>
	Within,

	/// <summary>Completely contains a selecting shape (its edge counts as inside).</summary>
	Contains,

	/// <summary>Comes within a given distance of a selecting shape.</summary>
	WithinDistance,
}

/// <summary>Select by location: the features of one layer that relate in a given way to the shapes of another (or a drawn shape).</summary>
public static class SpatialSelect
{
	private const double MetresPerDegree = 111320.0;

	/// <summary>
	/// The candidates that relate to any of <paramref name="selectors"/> (all in WGS 84). Distances are in metres,
	/// measured in a <see cref="LocalProjection"/> centred on the data, so they are right at any latitude.
	/// </summary>
	public static List<Feature> ByLocation(IEnumerable<Feature> candidates, IEnumerable<Geometry> selectors, SpatialRelation relation, double distanceMetres = 0)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(selectors);
		if (relation == SpatialRelation.WithinDistance && !(distanceMetres >= 0))
		{
			throw new ArgumentOutOfRangeException(nameof(distanceMetres), "The distance must be zero or more.");
		}
		List<Geometry> shapes = selectors.Where(g => g != null && !g.IsEmpty).ToList();
		List<Feature> features = candidates.Where(f => f.Geometry != null && !f.Geometry.IsEmpty).ToList();
		List<Feature> selected = new List<Feature>();
		if (shapes.Count == 0 || features.Count == 0)
		{
			return selected;
		}

		LocalProjection local = null;
		double padLat = 0, padLon = 0;
		if (relation == SpatialRelation.WithinDistance)
		{
			Envelope all = new Envelope();
			shapes.ForEach(s => all.ExpandToInclude(s.EnvelopeInternal));
			local = LocalProjection.For(all);
			double maxLat = Math.Min(89.0, Math.Max(Math.Abs(all.MinY), Math.Abs(all.MaxY)) + distanceMetres / MetresPerDegree);
			padLat = distanceMetres / MetresPerDegree * 1.01;
			padLon = distanceMetres / (MetresPerDegree * Math.Cos(maxLat * Math.PI / 180.0)) * 1.01;
		}

		STRtree<int> index = new STRtree<int>();
		IPreparedGeometry[] prepared = new IPreparedGeometry[shapes.Count];
		Geometry[] projected = new Geometry[shapes.Count];
		for (int i = 0; i < shapes.Count; i++)
		{
			Envelope e = shapes[i].EnvelopeInternal.Copy();
			e.ExpandBy(padLon, padLat);
			index.Insert(e, i);
			prepared[i] = PreparedGeometryFactory.Prepare(shapes[i]);
		}
		index.Build();

		foreach (Feature f in features)
		{
			Geometry featureMetres = null;
			foreach (int i in index.Query(f.Geometry.EnvelopeInternal))
			{
				bool match;
				switch (relation)
				{
					case SpatialRelation.Intersects:
						match = prepared[i].Intersects(f.Geometry);
						break;
					case SpatialRelation.Within:
						match = prepared[i].Covers(f.Geometry);
						break;
					case SpatialRelation.Contains:
						match = f.Geometry.Covers(shapes[i]);
						break;
					default:
						if (prepared[i].Intersects(f.Geometry))
						{
							match = true;
							break;
						}
						projected[i] ??= local.Project(shapes[i]);
						featureMetres ??= local.Project(f.Geometry);
						match = featureMetres.IsWithinDistance(projected[i], distanceMetres);
						break;
				}
				if (match)
				{
					selected.Add(f);
					break;
				}
			}
		}
		return selected;
	}
}
