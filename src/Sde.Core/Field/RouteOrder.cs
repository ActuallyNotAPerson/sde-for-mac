using System.Globalization;
using Sde.Core.Geo;

namespace Sde.Core.Field;

/// <summary>A place a field team has to visit: usually a structure, with its position and address.</summary>
/// <param name="Id">What the stop is (a PropertyID or an Assignment ID); the route keeps it as given.</param>
/// <param name="Latitude">Decimal degrees, or null when the structure isn't on the map yet.</param>
/// <param name="Longitude">Decimal degrees, or null.</param>
/// <param name="Address">The address, for the route sheet and for Maps when there's no position.</param>
public sealed record RouteStop(Guid Id, double? Latitude, double? Longitude, string Address = null)
{
	/// <summary>True when the stop has a usable position (both values, in range, and not 0, 0).</summary>
	public bool HasPosition => Latitude is double lat && Longitude is double lon
		&& lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180 && !(lat == 0 && lon == 0);
}

/// <summary>Stops in the order to visit them, and how far that is.</summary>
public sealed class RoutePlan
{
	/// <summary>Every stop: those with a position in route order, then those without one in the order they were given.</summary>
	public IReadOnlyList<RouteStop> Stops { get; init; } = Array.Empty<RouteStop>();

	/// <summary>The straight-line length of the route through the stops with a position, from the start point when one was given.</summary>
	public double Meters { get; init; }

	/// <summary>How many stops have no position (they are at the end of <see cref="Stops"/>).</summary>
	public int WithoutPosition { get; init; }
}

/// <summary>
/// Puts a team's stops in a sensible order: nearest neighbour from the start point, then 2-opt (reversing any stretch
/// of the route that makes it shorter, which removes every crossing), on straight-line distance. Not the shortest
/// possible route, but close, quick for a few hundred stops, and the same every time for the same stops.
/// </summary>
public static class RouteOrder
{
	/// <summary>A change has to save more than this to count, so rounding can't make the search go round in circles.</summary>
	private const double MinimumGainMeters = 1e-6;

	/// <summary>
	/// Orders the stops. With a start point (the office, or where the team is), the route starts there; without one,
	/// nearest neighbour starts at the first stop with a position and 2-opt may then turn either end of the route round.
	/// Stops without a position go last, in their original order. Ties go to the stop given first, so the same stops
	/// always give the same route.
	/// </summary>
	public static RoutePlan Plan(IEnumerable<RouteStop> stops, double? startLatitude = null, double? startLongitude = null)
	{
		List<RouteStop> all = (stops ?? Enumerable.Empty<RouteStop>()).Where(s => s != null).ToList();
		List<RouteStop> placed = all.Where(s => s.HasPosition).ToList();
		List<RouteStop> unplaced = all.Where(s => !s.HasPosition).ToList();
		RouteStop start = startLatitude is double sla && startLongitude is double slo ? new RouteStop(Guid.Empty, sla, slo) : null;
		if (start != null && !start.HasPosition)
		{
			start = null;
		}

		List<RouteStop> route = NearestNeighbour(placed, start);
		TwoOpt(route, start);
		return new RoutePlan
		{
			Stops = route.Concat(unplaced).ToList(),
			Meters = Length(route, start),
			WithoutPosition = unplaced.Count,
		};
	}

	/// <summary>
	/// A link that opens Apple Maps with directions to the stop: to its position when it has one
	/// (<c>https://maps.apple.com/?daddr=39.712345,-84.178901</c>), otherwise to its address; null when it has neither.
	/// </summary>
	public static string MapsLink(RouteStop stop)
	{
		if (stop == null)
		{
			return null;
		}
		if (stop.HasPosition)
		{
			return "https://maps.apple.com/?daddr=" + stop.Latitude.Value.ToString("0.######", CultureInfo.InvariantCulture)
				+ "," + stop.Longitude.Value.ToString("0.######", CultureInfo.InvariantCulture);
		}
		return string.IsNullOrWhiteSpace(stop.Address) ? null : "https://maps.apple.com/?daddr=" + Uri.EscapeDataString(stop.Address.Trim());
	}

	/// <summary>The straight-line length in metres of visiting the stops in order, from <paramref name="start"/> when given.</summary>
	public static double Length(IReadOnlyList<RouteStop> route, RouteStop start = null)
	{
		double total = 0;
		RouteStop previous = start;
		foreach (RouteStop s in route)
		{
			if (previous != null)
			{
				total += Distance(previous, s);
			}
			previous = s;
		}
		return total;
	}

	private static List<RouteStop> NearestNeighbour(List<RouteStop> stops, RouteStop start)
	{
		List<RouteStop> route = new List<RouteStop>(stops.Count);
		List<RouteStop> left = new List<RouteStop>(stops);
		RouteStop current = start;
		if (current == null && left.Count > 0)
		{
			current = left[0];
			left.RemoveAt(0);
			route.Add(current);
		}
		while (left.Count > 0)
		{
			int best = 0;
			double bestDistance = Distance(current, left[0]);
			for (int i = 1; i < left.Count; i++)
			{
				double d = Distance(current, left[i]);
				if (d < bestDistance)
				{
					best = i;
					bestDistance = d;
				}
			}
			current = left[best];
			left.RemoveAt(best);
			route.Add(current);
		}
		return route;
	}

	/// <summary>
	/// Improves an open route (it doesn't return to the start) by reversing stretches: for stops i to j, the legs
	/// (before i, i) and (j, after j) become (before i, j) and (i, after j). The leg before the first stop exists only
	/// with a start point, and the leg after the last stop never does, so either end of the route can turn round too.
	/// Repeats until no reversal shortens it.
	/// </summary>
	private static void TwoOpt(List<RouteStop> route, RouteStop start)
	{
		int n = route.Count;
		if (n < 2)
		{
			return;
		}
		RouteStop At(int k) => k < 0 ? start : k >= n ? null : route[k];
		double Leg(RouteStop a, RouteStop b) => a == null || b == null ? 0 : Distance(a, b);
		int passes = 0;
		bool improved = true;
		while (improved && passes++ < 10 * n + 10)
		{
			improved = false;
			for (int i = 0; i < n - 1; i++)
			{
				for (int j = i + 1; j < n; j++)
				{
					RouteStop before = At(i - 1);
					RouteStop after = At(j + 1);
					double now = Leg(before, route[i]) + Leg(route[j], after);
					double reversed = Leg(before, route[j]) + Leg(route[i], after);
					if (reversed < now - MinimumGainMeters)
					{
						route.Reverse(i, j - i + 1);
						improved = true;
					}
				}
			}
		}
	}

	private static double Distance(RouteStop a, RouteStop b) => GeoMath.Meters(a.Latitude.Value, a.Longitude.Value, b.Latitude.Value, b.Longitude.Value);
}
