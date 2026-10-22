using System.Net;
using Sde.Interop.Online;

namespace Sde.Interop.Tests;

/// <summary>The Census geocoder and NFHL clients, run against a fake HTTP handler and hand-written fixtures.</summary>
public class OnlineClientTests
{
	private static CensusGeocoder Geocoder(FakeHandler handler) => new CensusGeocoder(new HttpClient(handler));

	private static NfhlClient Nfhl(FakeHandler handler) => new NfhlClient(new HttpClient(handler));

	[Fact]
	public async Task Geocode_ReturnsFirstMatch_WithLongitudeFromX()
	{
		FakeHandler handler = new FakeHandler((r, ct) => Task.FromResult(FakeHandler.Json(FakeHandler.Fixture("census-capitol-match.json"))));
		GeocodeMatch match = await Geocoder(handler).GeocodeAsync("1 First St NE, Washington, DC", CancellationToken.None);

		Assert.NotNull(match);
		Assert.Equal(38.88991, match.Latitude, 5);
		Assert.Equal(-77.00911, match.Longitude, 5);
		Assert.Equal("1 1ST ST NE, WASHINGTON, DC, 20004", match.MatchedAddress);
		string url = handler.Urls.Single();
		Assert.StartsWith("https://geocoding.geo.census.gov/geocoder/locations/onelineaddress?address=1%20First%20St%20NE%2C%20Washington%2C%20DC", url);
		Assert.Contains("benchmark=Public_AR_Current", url);
		Assert.Contains("format=json", url);
	}

	[Fact]
	public async Task Geocode_NoMatches_ReturnsNull()
	{
		FakeHandler handler = new FakeHandler((r, ct) => Task.FromResult(FakeHandler.Json(FakeHandler.Fixture("census-no-match.json"))));
		Assert.Null(await Geocoder(handler).GeocodeAsync("99999 Nowhere Rd", CancellationToken.None));
	}

	[Fact]
	public async Task Geocode_Timeout_IsPlainEnglish()
	{
		FakeHandler handler = new FakeHandler(async (r, ct) =>
		{
			await Task.Delay(Timeout.Infinite, ct);
			return null;
		});
		CensusGeocoder geocoder = Geocoder(handler);
		geocoder.Timeout = TimeSpan.FromMilliseconds(100);

		OnlineServiceException ex = await Assert.ThrowsAsync<OnlineServiceException>(() => geocoder.GeocodeAsync("1 First St NE", CancellationToken.None));
		Assert.Equal("The Census geocoder didn't answer within 1 seconds. Check the internet connection and try again.", ex.Message);
		Assert.NotNull(ex.InnerException);
		Assert.Contains("timed out", ex.Detail);
	}

	[Fact]
	public async Task Geocode_UserCancel_IsNotWrapped()
	{
		using CancellationTokenSource cts = new CancellationTokenSource();
		FakeHandler handler = new FakeHandler(async (r, ct) =>
		{
			cts.Cancel();
			await Task.Delay(Timeout.Infinite, ct);
			return null;
		});
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Geocoder(handler).GeocodeAsync("1 First St NE", cts.Token));
	}

	[Fact]
	public async Task Geocode_ServerError_KeepsDetail()
	{
		FakeHandler handler = new FakeHandler((r, ct) => Task.FromResult(FakeHandler.Json("boom", HttpStatusCode.InternalServerError)));
		OnlineServiceException ex = await Assert.ThrowsAsync<OnlineServiceException>(() => Geocoder(handler).GeocodeAsync("1 First St NE", CancellationToken.None));
		Assert.Contains("error 500", ex.Message);
		Assert.DoesNotContain("boom", ex.Message);
		Assert.Contains("boom", ex.Detail);
	}

	[Fact]
	public async Task Geocode_NetworkFailure_IsWrapped()
	{
		FakeHandler handler = new FakeHandler((r, ct) => throw new HttpRequestException("name not resolved"));
		OnlineServiceException ex = await Assert.ThrowsAsync<OnlineServiceException>(() => Geocoder(handler).GeocodeAsync("1 First St NE", CancellationToken.None));
		Assert.StartsWith("Couldn't reach the Census geocoder", ex.Message);
		Assert.IsType<HttpRequestException>(ex.InnerException);
	}

	[Theory]
	[InlineData("this is not json")]
	[InlineData("{\"result\":{}}")]
	[InlineData("{\"result\":{\"addressMatches\":[{\"matchedAddress\":\"X\"}]}}")]
	public async Task Geocode_BadJson_IsWrapped(string body)
	{
		FakeHandler handler = new FakeHandler((r, ct) => Task.FromResult(FakeHandler.Json(body)));
		OnlineServiceException ex = await Assert.ThrowsAsync<OnlineServiceException>(() => Geocoder(handler).GeocodeAsync("1 First St NE", CancellationToken.None));
		Assert.Contains("couldn't read", ex.Message);
	}

	[Fact]
	public async Task Nfhl_ReadsZonePanelAndCommunity()
	{
		FakeHandler handler = FakeHandler.Nfhl("nfhl-zone-ae.json");
		FloodInfo info = await Nfhl(handler).LookupAsync(38.88991, -77.00911, CancellationToken.None);

		Assert.NotNull(info);
		Assert.Equal("AE", info.Zone);
		Assert.Null(info.ZoneSubtype);
		Assert.False(info.InFloodway);
		Assert.Equal(12.5m, info.Bfe);
		Assert.Equal("NAVD88", info.BfeDatum);
		Assert.Equal("11001C0359E", info.PanelNumber);
		Assert.Equal("E", info.PanelSuffix);
		Assert.Equal(new DateTime(2014, 2, 24), info.PanelEffectiveDate);
		Assert.Equal("110001", info.CommunityId);
		Assert.Equal("DISTRICT OF COLUMBIA", info.CommunityName);
		Assert.True((DateTime.UtcNow - info.RetrievedOn).TotalMinutes < 1);

		Assert.Equal(3, handler.Urls.Count);
		string zoneUrl = handler.Urls.Single(u => u.Contains("/MapServer/28/query"));
		Assert.StartsWith("https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/28/query?", zoneUrl);
		Assert.Contains("geometry=-77.00911%2C38.88991", zoneUrl);
		Assert.Contains("geometryType=esriGeometryPoint", zoneUrl);
		Assert.Contains("inSR=4326", zoneUrl);
		Assert.Contains("spatialRel=esriSpatialRelIntersects", zoneUrl);
		Assert.Contains("outFields=*", zoneUrl);
		Assert.Contains("returnGeometry=false", zoneUrl);
		Assert.Contains("f=json", zoneUrl);
		Assert.Contains(handler.Urls, u => u.Contains("/MapServer/3/query"));
		Assert.Contains(handler.Urls, u => u.Contains("/MapServer/22/query"));
	}

	[Fact]
	public async Task Nfhl_Floodway_WithNoBfe()
	{
		FloodInfo info = await Nfhl(FakeHandler.Nfhl("nfhl-zone-floodway.json")).LookupAsync(38.9, -77.0, CancellationToken.None);
		Assert.True(info.InFloodway);
		Assert.Equal("FLOODWAY", info.ZoneSubtype);
		Assert.Null(info.Bfe);
	}

	[Fact]
	public async Task Nfhl_MinusNineThousandNineHundredNinetyNine_MeansNoBfe()
	{
		FloodInfo info = await Nfhl(FakeHandler.Nfhl("nfhl-zone-x-no-bfe.json")).LookupAsync(38.9, -77.0, CancellationToken.None);
		Assert.Equal("X", info.Zone);
		Assert.Null(info.Bfe);
		Assert.False(info.InFloodway);
	}

	[Fact]
	public async Task Nfhl_SeveralZones_PicksTheStrictest_NotTheFirst()
	{
		FloodInfo info = await Nfhl(FakeHandler.Nfhl("nfhl-zone-several.json")).LookupAsync(38.9, -77.0, CancellationToken.None);
		Assert.Equal("VE", info.Zone);
		Assert.Null(info.ZoneSubtype);
		Assert.False(info.InFloodway);
		Assert.Equal(16m, info.Bfe);
		Assert.Equal("NAVD88", info.BfeDatum);
	}

	[Theory]
	[InlineData("X", "0.2 PCT ANNUAL CHANCE FLOOD HAZARD", "X|AREA OF MINIMAL FLOOD HAZARD", "X|0.2 PCT ANNUAL CHANCE FLOOD HAZARD")]
	[InlineData("AO", null, "X", "AO")]
	[InlineData("A99", null, "AREA NOT INCLUDED", "A99")]
	[InlineData("A12", null, "X|0.2 PCT ANNUAL CHANCE FLOOD HAZARD", "A12")]
	[InlineData("V", null, "A", "V")]
	[InlineData("AE", null, "OPEN WATER", "AE")]
	[InlineData("X", "AREA OF MINIMAL FLOOD HAZARD", "D", "X|AREA OF MINIMAL FLOOD HAZARD")]
	[InlineData("AE", "FLOODWAY", "VE", "AE|FLOODWAY")]
	[InlineData("AE", null, "AE", "AH")]
	public async Task Nfhl_SeveralZones_FloodwayThenVThenAThenShadedXThenX(string zone, string subtype, params string[] zones)
	{
		FloodInfo info = await Nfhl(FakeHandler.NfhlZones(FakeHandler.ZonesJson(zones))).LookupAsync(38.9, -77.0, CancellationToken.None);
		Assert.Equal(zone, info.Zone);
		Assert.Equal(subtype, info.ZoneSubtype);
	}

	[Fact]
	public async Task Nfhl_AsksTheThreeLayersAtOnce()
	{
		// Each answer waits until all three questions have arrived, which only happens when they are sent together.
		int arrived = 0;
		TaskCompletionSource allThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeHandler handler = new FakeHandler(async (request, ct) =>
		{
			if (Interlocked.Increment(ref arrived) == 3)
			{
				allThree.SetResult();
			}
			await allThree.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
			string path = request.RequestUri.AbsolutePath;
			return FakeHandler.Json(FakeHandler.Fixture(path.Contains("/28/query") ? "nfhl-zone-ae.json" : path.Contains("/3/query") ? "nfhl-panel.json" : "nfhl-community.json"));
		});

		FloodInfo info = await Nfhl(handler).LookupAsync(38.9, -77.0, CancellationToken.None);
		Assert.Equal("AE", info.Zone);
		Assert.Equal("11001C0359E", info.PanelNumber);
		Assert.Equal("110001", info.CommunityId);
		Assert.Equal(3, handler.Urls.Count);
	}

	[Fact]
	public async Task Nfhl_NoZoneFeature_ReturnsNull()
	{
		FakeHandler handler = FakeHandler.Nfhl("nfhl-empty.json");
		Assert.Null(await Nfhl(handler).LookupAsync(0, 0, CancellationToken.None));
	}

	[Fact]
	public async Task Nfhl_NoZoneFeature_IgnoresAFailedPanelOrCommunityQuery()
	{
		// Without a flood zone the answer is "no flood information", whatever the other two layers said.
		FakeHandler handler = new FakeHandler((r, ct) => Task.FromResult(r.RequestUri.AbsolutePath.Contains("/28/query")
			? FakeHandler.Json(FakeHandler.Fixture("nfhl-empty.json"))
			: FakeHandler.Json("down", HttpStatusCode.ServiceUnavailable)));
		Assert.Null(await Nfhl(handler).LookupAsync(0, 0, CancellationToken.None));
	}

	[Fact]
	public async Task Nfhl_ZoneQueryFailure_IsTheOneReported()
	{
		FakeHandler handler = new FakeHandler((r, ct) => Task.FromResult(r.RequestUri.AbsolutePath.Contains("/28/query")
			? FakeHandler.Json(FakeHandler.Fixture("nfhl-error.json"))
			: FakeHandler.Json("down", HttpStatusCode.ServiceUnavailable)));
		OnlineServiceException ex = await Assert.ThrowsAsync<OnlineServiceException>(() => Nfhl(handler).LookupAsync(1, 2, CancellationToken.None));
		Assert.Contains("Unable to complete operation", ex.Detail);
	}

	[Fact]
	public async Task Nfhl_MissingPanelAndCommunity_LeavesThemNull()
	{
		FloodInfo info = await Nfhl(FakeHandler.Nfhl("nfhl-zone-ae.json", "nfhl-empty.json", "nfhl-empty.json")).LookupAsync(38.9, -77.0, CancellationToken.None);
		Assert.Equal("AE", info.Zone);
		Assert.Null(info.PanelNumber);
		Assert.Null(info.PanelEffectiveDate);
		Assert.Null(info.CommunityId);
	}

	[Fact]
	public async Task Nfhl_LayersAndBaseUrlAreConfigurable()
	{
		FakeHandler handler = new FakeHandler((r, ct) => Task.FromResult(FakeHandler.Json("{\"features\":[]}")));
		NfhlClient client = new NfhlClient(new HttpClient(handler), "https://example.test/maps/MapServer/") { FloodZoneLayer = 7 };
		await client.LookupAsync(1, 2, CancellationToken.None);
		Assert.Single(handler.Urls, u => u.StartsWith("https://example.test/maps/MapServer/7/query?", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Nfhl_ArcGisErrorObject_IsWrapped()
	{
		FakeHandler handler = new FakeHandler((r, ct) => Task.FromResult(FakeHandler.Json(FakeHandler.Fixture("nfhl-error.json"))));
		OnlineServiceException ex = await Assert.ThrowsAsync<OnlineServiceException>(() => Nfhl(handler).LookupAsync(1, 2, CancellationToken.None));
		Assert.Contains("Unable to complete operation", ex.Detail);
	}

	[Fact]
	public async Task Nfhl_ServerError_BadJson_AndTimeout_AreWrapped()
	{
		OnlineServiceException server = await Assert.ThrowsAsync<OnlineServiceException>(() =>
			Nfhl(new FakeHandler((r, ct) => Task.FromResult(FakeHandler.Json("down", HttpStatusCode.ServiceUnavailable)))).LookupAsync(1, 2, CancellationToken.None));
		Assert.Contains("error 503", server.Message);

		OnlineServiceException bad = await Assert.ThrowsAsync<OnlineServiceException>(() =>
			Nfhl(new FakeHandler((r, ct) => Task.FromResult(FakeHandler.Json("<html>")))).LookupAsync(1, 2, CancellationToken.None));
		Assert.Contains("couldn't read", bad.Message);

		NfhlClient slow = Nfhl(new FakeHandler(async (r, ct) =>
		{
			await Task.Delay(Timeout.Infinite, ct);
			return null;
		}));
		slow.Timeout = TimeSpan.FromMilliseconds(100);
		OnlineServiceException timeout = await Assert.ThrowsAsync<OnlineServiceException>(() => slow.LookupAsync(1, 2, CancellationToken.None));
		Assert.Contains("didn't answer within", timeout.Message);
	}
}
