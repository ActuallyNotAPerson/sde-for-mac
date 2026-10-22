using NetTopologySuite.Geometries;

namespace Sde.Gis.Projections;

/// <summary>Converts NetTopologySuite geometries between a <see cref="CoordinateSystem"/> and WGS 84 longitude and latitude.</summary>
public static class Reproject
{
	/// <summary>
	/// A copy of <paramref name="geometry"/> in WGS 84 longitude and latitude. Geographic systems (NAD83 or WGS 84) need
	/// no change, so the geometry itself is returned.
	/// </summary>
	public static Geometry ToWgs84(Geometry geometry, CoordinateSystem source)
	{
		if (geometry == null || source == null || source.IsGeographic)
		{
			return geometry;
		}
		Geometry copy = geometry.Copy();
		copy.Apply(new CoordinateFilter((x, y) => source.ToWgs84(x, y)));
		copy.GeometryChanged();
		return copy;
	}

	/// <summary>A copy of a WGS 84 geometry in <paramref name="target"/>'s coordinates.</summary>
	public static Geometry FromWgs84(Geometry geometry, CoordinateSystem target)
	{
		if (geometry == null || target == null || target.IsGeographic)
		{
			return geometry;
		}
		Geometry copy = geometry.Copy();
		copy.Apply(new CoordinateFilter((lon, lat) => target.FromWgs84(lon, lat)));
		copy.GeometryChanged();
		return copy;
	}

	/// <summary>A copy of a geometry with every coordinate passed through <paramref name="convert"/>.</summary>
	public static Geometry Transform(Geometry geometry, Func<double, double, (double, double)> convert)
	{
		if (geometry == null)
		{
			return null;
		}
		Geometry copy = geometry.Copy();
		copy.Apply(new CoordinateFilter(convert));
		copy.GeometryChanged();
		return copy;
	}

	private sealed class CoordinateFilter : ICoordinateSequenceFilter
	{
		private readonly Func<double, double, (double, double)> _convert;

		public CoordinateFilter(Func<double, double, (double, double)> convert)
		{
			_convert = convert;
		}

		public bool Done => false;

		public bool GeometryChanged => true;

		public void Filter(CoordinateSequence seq, int i)
		{
			double x = seq.GetX(i);
			double y = seq.GetY(i);
			if (double.IsNaN(x) || double.IsNaN(y))
			{
				return;
			}
			(double nx, double ny) = _convert(x, y);
			seq.SetX(i, nx);
			seq.SetY(i, ny);
		}
	}
}
