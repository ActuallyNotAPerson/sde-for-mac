using System.Net;
using System.Text.Json;
using Sde.Core.Depth;
using Sde.Interop.Online;

namespace Sde.Interop.Tests;

/// <summary>The USGS STN client, answered from recorded JSON (never the live service).</summary>
public class StnClientTests
{
	private static StnClient Client(FakeHandler handler, string baseUrl = null) =>
		new StnClient(new HttpClient(handler), baseUrl) { RetryDelay = TimeSpan.Zero };

	private static FakeHandler Answer(string fixture) => new FakeHandler((request, ct) => Task.FromResult(FakeHandler.Json(FakeHandler.Fixture(fixture))));

	[Fact]
	public async Task HighWaterMarks_AreRead_WithNumbersAsTextToo_AndUnusableOnesSkipped()
	{
		FakeHandler handler = Answer("stn-hwms.json");
		StnHighWaterMarks result = await Client(handler).GetHighWaterMarksAsync(901, new[] { "oh", " in " }, CancellationToken.None);

		Assert.Equal("https://api.waterdata.usgs.gov/stnservices/hwms/filteredhwms/?event=901&states=OH,IN", Assert.Single(handler.Urls));
		Assert.Equal(901, result.EventId);
		Assert.Equal(new[] { "OH", "IN" }, result.States);
		Assert.Equal(3, result.Marks.Count);
		Assert.Equal(2, result.Skipped);   // one not surveyed yet, one at 0, 0

		HighWaterMark first = result.Marks[0];
		Assert.Equal(("90001", "EX HWM 1", 39.70012, -84.20015, 742.31m, "NAVD88", 2, "Debris"),
			(first.Id, first.Label, first.Latitude, first.Longitude, first.ElevationFt, first.DatumName, first.Quality.Value, first.Kind));
		HighWaterMark text = result.Marks[1];
		Assert.Equal(("90002", 39.7011, -84.1999, 742.58m, 1), (text.Id, text.Latitude, text.Longitude, text.ElevationFt, text.Quality.Value));
		HighWaterMark noDatum = result.Marks[2];
		Assert.Null(noDatum.DatumName);
		Assert.Equal(VerticalDatum.Unknown, noDatum.Datum);
		Assert.Equal(6, noDatum.Quality);

		Assert.Contains("provisional", result.Notice);
		Assert.Equal(StnClient.ProvisionalNotice, result.Notice);
	}

	[Fact]
	public async Task StaffNames_NotesAndDescriptions_AreNeverReturned()
	{
		StnHighWaterMarks result = await Client(Answer("stn-hwms.json")).GetHighWaterMarksAsync(901, new[] { "OH" }, CancellationToken.None);
		string everything = JsonSerializer.Serialize(result.Marks);
		Assert.DoesNotContain("Team member", everything);
		Assert.DoesNotContain("Behind the example building", everything);
		Assert.DoesNotContain("Debris line on fence", everything);
		Assert.DoesNotContain("member", typeof(HighWaterMark).GetProperties().Select(p => p.Name.ToLowerInvariant()).Aggregate("", (a, b) => a + " " + b));
	}

	[Fact]
	public async Task Events_AreListedNewestFirst_AndNamelessOnesSkipped()
	{
		FakeHandler handler = Answer("stn-events.json");
		IReadOnlyList<StnEvent> events = await Client(handler, "https://example.test/stn").GetEventsAsync(CancellationToken.None);
		Assert.Equal("https://example.test/stn/events/", Assert.Single(handler.Urls));
		Assert.Equal(new[] { 901, 334, 23 }, events.Select(e => e.Id));
		StnEvent flood = events[0];
		Assert.Equal(("2026 Example River Flood", new DateTime(2026, 9, 14), (DateTime?)null, "River Flood", "Active"),
			(flood.Name, flood.StartDate.Value, flood.EndDate, flood.Type, flood.Status));
		Assert.Equal(new DateTime(2011, 9, 5), events[2].EndDate);
	}

	[Fact]
	public async Task BusyGateways_AreAskedAgain()
	{
		int calls = 0;
		FakeHandler handler = new FakeHandler((request, ct) =>
		{
			calls++;
			return Task.FromResult(calls < 3 ? FakeHandler.Json("<html>Bad gateway</html>", HttpStatusCode.BadGateway) : FakeHandler.Json(FakeHandler.Fixture("stn-hwms.json")));
		});
		StnHighWaterMarks result = await Client(handler).GetHighWaterMarksAsync(901, new[] { "OH" }, CancellationToken.None);
		Assert.Equal(3, calls);
		Assert.Equal(3, result.Marks.Count);

		int failures = 0;
		FakeHandler down = new FakeHandler((request, ct) =>
		{
			failures++;
			return Task.FromResult(FakeHandler.Json("", HttpStatusCode.BadGateway));
		});
		OnlineServiceException ex = await Assert.ThrowsAsync<OnlineServiceException>(() => Client(down).GetEventsAsync(CancellationToken.None));
		Assert.Equal(3, failures);   // the first try and two more
		Assert.Equal(502, ex.StatusCode);
		Assert.Contains("USGS high-water mark service", ex.Message);

		int notFound = 0;
		FakeHandler missing = new FakeHandler((request, ct) =>
		{
			notFound++;
			return Task.FromResult(FakeHandler.Json("{}", HttpStatusCode.NotFound));
		});
		await Assert.ThrowsAsync<OnlineServiceException>(() => Client(missing).GetEventsAsync(CancellationToken.None));
		Assert.Equal(1, notFound);   // other errors aren't retried
	}

	[Fact]
	public async Task OddAnswers_AndBadStateCodes_AreRefusedPlainly()
	{
		FakeHandler error = new FakeHandler((request, ct) => Task.FromResult(FakeHandler.Json("{\"message\":\"Something went wrong\"}")));
		OnlineServiceException ex = await Assert.ThrowsAsync<OnlineServiceException>(() => Client(error).GetHighWaterMarksAsync(1, new[] { "OH" }, CancellationToken.None));
		Assert.Equal("The USGS high-water mark service sent back something SDE couldn't read. Try again later.", ex.Message);

		StnClient client = Client(Answer("stn-hwms.json"));
		await Assert.ThrowsAsync<ArgumentException>(() => client.GetHighWaterMarksAsync(1, Array.Empty<string>(), CancellationToken.None));
		ArgumentException bad = await Assert.ThrowsAsync<ArgumentException>(() => client.GetHighWaterMarksAsync(1, new[] { "OH&event=2" }, CancellationToken.None));
		Assert.StartsWith("\"OH&EVENT=2\" isn't a two-letter state code.", bad.Message);
		Assert.Equal(StnClient.DefaultBaseUrl, new StnClient(new HttpClient()).BaseUrl);
	}
}
