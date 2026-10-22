namespace Sde.Core.Geo;

/// <summary>Straight-line distances on the earth, for the checks, routes and depth estimates that compare locations.</summary>
public static class GeoMath
{
	/// <summary>The earth's mean radius in metres (the sphere the haversine formula uses).</summary>
	public const double EarthRadiusMeters = 6371008.8;

	/// <summary>Metres in one degree of latitude on that sphere.</summary>
	public const double MetersPerDegreeLatitude = EarthRadiusMeters * Math.PI / 180.0;

	/// <summary>
	/// The great-circle distance in metres between two points given in decimal degrees (haversine formula). Good to
	/// about 0.5% anywhere, which is far closer than any of its uses need.
	/// </summary>
	public static double Meters(double latitude1, double longitude1, double latitude2, double longitude2)
	{
		double p1 = latitude1 * Math.PI / 180.0;
		double p2 = latitude2 * Math.PI / 180.0;
		double dp = p2 - p1;
		double dl = (longitude2 - longitude1) * Math.PI / 180.0;
		double h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
		return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
	}

	/// <summary>True when a latitude and longitude are a usable position: both present, in range, and not the 0, 0 placeholder.</summary>
	public static bool IsPosition(decimal? latitude, decimal? longitude) =>
		latitude is decimal lat && longitude is decimal lon
		&& lat >= -90m && lat <= 90m && lon >= -180m && lon <= 180m
		&& !(lat == 0m && lon == 0m);
}

/// <summary>
/// Items placed on a grid of cells about <c>cellMeters</c> across, so "everything within r metres" looks at a few
/// cells instead of every item. Used by the quality checks to find neighbours among thousands of structures.
/// </summary>
public sealed class PointIndex<T>
{
	private readonly Dictionary<(long Row, long Column), List<Entry>> _cells = new();
	private readonly double _cellLatitude;
	private readonly double _cellLongitude;

	/// <param name="items">The items with their positions.</param>
	/// <param name="cellMeters">The cell size; the radius searched is usually a good choice.</param>
	public PointIndex(IEnumerable<(T Item, double Latitude, double Longitude)> items, double cellMeters)
	{
		List<(T Item, double Latitude, double Longitude)> list = items.ToList();
		_cellLatitude = Math.Max(cellMeters, 1.0) / GeoMath.MetersPerDegreeLatitude;
		// A degree of longitude is shortest at the highest latitude, so cells are sized for that one: never too narrow.
		double maxLatitude = list.Count == 0 ? 0 : list.Max(x => Math.Abs(x.Latitude));
		_cellLongitude = _cellLatitude / Cos(maxLatitude);
		for (int i = 0; i < list.Count; i++)
		{
			(long, long) key = Cell(list[i].Latitude, list[i].Longitude);
			if (!_cells.TryGetValue(key, out List<Entry> cell))
			{
				cell = new List<Entry>();
				_cells[key] = cell;
			}
			cell.Add(new Entry(list[i].Item, list[i].Latitude, list[i].Longitude, i));
		}
	}

	/// <summary>Every item within <paramref name="radiusMeters"/> of the point, with its distance, nearest first (ties keep the order they were added in).</summary>
	public List<(T Item, double Meters)> Within(double latitude, double longitude, double radiusMeters)
	{
		List<(Entry Entry, double Meters)> found = new();
		(long row, long column) = Cell(latitude, longitude);
		double radiusDegrees = radiusMeters / GeoMath.MetersPerDegreeLatitude;
		long reachRows = (long)Math.Ceiling(radiusDegrees / _cellLatitude);
		long reachColumns = (long)Math.Ceiling(radiusDegrees / Cos(latitude) / _cellLongitude);
		for (long r = row - reachRows; r <= row + reachRows; r++)
		{
			for (long c = column - reachColumns; c <= column + reachColumns; c++)
			{
				if (!_cells.TryGetValue((r, c), out List<Entry> cell))
				{
					continue;
				}
				foreach (Entry e in cell)
				{
					double d = GeoMath.Meters(latitude, longitude, e.Latitude, e.Longitude);
					if (d <= radiusMeters)
					{
						found.Add((e, d));
					}
				}
			}
		}
		return found.OrderBy(x => x.Meters).ThenBy(x => x.Entry.Order).Select(x => (x.Entry.Item, x.Meters)).ToList();
	}

	private (long Row, long Column) Cell(double latitude, double longitude) =>
		((long)Math.Floor(latitude / _cellLatitude), (long)Math.Floor(longitude / _cellLongitude));

	/// <summary>The cosine of a latitude, kept away from 0 near the poles so cells stay a sensible size.</summary>
	private static double Cos(double latitude) => Math.Max(Math.Cos(Math.Min(Math.Abs(latitude), 89.0) * Math.PI / 180.0), 0.01);

	private sealed record Entry(T Item, double Latitude, double Longitude, int Order);
}
