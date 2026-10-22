using Sde.Core.Field;
using Sde.Core.Geo;

namespace Sde.Tests.Core;

/// <summary>Route order (nearest neighbour, then 2-opt) and the Apple Maps links.</summary>
public class RouteOrderTests
{
	/// <summary>
	/// A stop on a small grid near the equator, where a degree of longitude is as long as a degree of latitude, so the
	/// straight-line distances are those of the grid. Units are 0.001 degree (about 111 m).
	/// </summary>
	private static RouteStop Grid(string name, double x, double y) => new RouteStop(Named(name), 1 + y * 0.001, 1 + x * 0.001, name);

	private static Guid Named(string name) => new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

	private static string Order(RoutePlan plan) => string.Join(" ", plan.Stops.Select(s => s.Address));

	[Fact]
	public void TwoOpt_RemovesTheCrossingNearestNeighbourLeaves()
	{
		// From the office at (0,0) nearest neighbour goes A (1,0), B (1,2), C (0,2), then all the way across to D (3,1.2),
		// crossing its own A to B leg. Reversing B and C removes the crossing: A, C, B, D, the shortest route.
		RouteStop[] stops = { Grid("C", 0, 2), Grid("A", 1, 0), Grid("B", 1, 2), Grid("D", 3, 1.2) };
		RoutePlan plan = RouteOrder.Plan(stops, 1, 1);
		Assert.Equal("A C B D", Order(plan));
		double unit = GeoMath.Meters(1, 1, 1.001, 1);
		Assert.Equal(1 + Math.Sqrt(5) + 1 + Math.Sqrt(4.64), plan.Meters / unit, 2);
		Assert.Equal(0, plan.WithoutPosition);
	}

	[Fact]
	public void FiveStops_ReachTheShortestRoute()
	{
		// Nearest neighbour: B C A E D (18.2 units, with crossings); the shortest is E C A B D (14.3 units).
		RouteStop[] stops = { Grid("A", 4, 6), Grid("B", 4, 3), Grid("C", 3, 5), Grid("D", 6, 1), Grid("E", 1, 5) };
		RoutePlan plan = RouteOrder.Plan(stops, 1, 1);
		Assert.Equal("E C A B D", Order(plan));
		double unit = GeoMath.Meters(1, 1, 1.001, 1);
		Assert.Equal(14.34, plan.Meters / unit, 1);
	}

	[Fact]
	public void StopsWithoutPosition_GoLast_InTheirOriginalOrder()
	{
		RouteStop noPlace = new RouteStop(Named("n1"), null, null, "N1");
		RouteStop zero = new RouteStop(Named("n2"), 0, 0, "N2");
		RouteStop half = new RouteStop(Named("n3"), 1.002, null, "N3");
		RoutePlan plan = RouteOrder.Plan(new[] { noPlace, Grid("B", 2, 0), zero, Grid("A", 1, 0), half }, 1, 1);
		Assert.Equal("A B N1 N2 N3", Order(plan));
		Assert.Equal(3, plan.WithoutPosition);
	}

	[Fact]
	public void TheSameStops_AlwaysGiveTheSameRoute_AndTiesGoToTheFirstGiven()
	{
		// From (0,0), P and Q are equally near; P is given first, so it comes first.
		RouteStop[] stops = { Grid("P", 1, 0), Grid("Q", 0, 1), Grid("R", 5, 5) };
		string first = Order(RouteOrder.Plan(stops, 1, 1));
		Assert.Equal(first, Order(RouteOrder.Plan(stops, 1, 1)));
		Assert.StartsWith("P", first);
	}

	[Fact]
	public void WithoutAStart_TheRouteStartsFromTheFirstStop_AndEmptyInputsWork()
	{
		RoutePlan plan = RouteOrder.Plan(new[] { Grid("A", 0, 0), Grid("C", 2, 0), Grid("B", 1, 0) });
		Assert.Equal("A B C", Order(plan));
		Assert.Empty(RouteOrder.Plan(null).Stops);
		RoutePlan one = RouteOrder.Plan(new[] { Grid("A", 0, 0) }, 1, 1);
		Assert.Equal("A", Order(one));
		Assert.Equal(0, one.Meters, 6);
	}

	[Fact]
	public void MapsLink_UsesThePosition_ThenTheAddress()
	{
		Assert.Equal("https://maps.apple.com/?daddr=39.712345,-84.178901", RouteOrder.MapsLink(new RouteStop(Guid.NewGuid(), 39.712345, -84.178901, "123 River Road")));
		Assert.Equal("https://maps.apple.com/?daddr=123%20River%20Road%2C%20Exampleville%20OH",
			RouteOrder.MapsLink(new RouteStop(Guid.NewGuid(), null, null, " 123 River Road, Exampleville OH ")));
		Assert.Null(RouteOrder.MapsLink(new RouteStop(Guid.NewGuid(), null, null, " ")));
	}

	[Fact]
	public void Haversine_AndPointIndex()
	{
		// One degree of latitude on the mean sphere; a degree of longitude at 60 degrees north is half as long.
		Assert.Equal(111195, GeoMath.Meters(0, 0, 1, 0), 0);
		Assert.Equal(55597, GeoMath.Meters(60, 0, 60, 1), 0);
		PointIndex<string> index = new PointIndex<string>(new[] { ("a", 39.7, -84.2), ("b", 39.70001, -84.2), ("c", 39.71, -84.2) }, 5);
		Assert.Equal(new[] { "a", "b" }, index.Within(39.7, -84.2, 5).Select(x => x.Item));
		Assert.Equal(new[] { "a", "b", "c" }, index.Within(39.7, -84.2, 1200).Select(x => x.Item));
	}
}
