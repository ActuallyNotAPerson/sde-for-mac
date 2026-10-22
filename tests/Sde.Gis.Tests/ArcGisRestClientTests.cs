using System.Net;
using System.Text;
using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Online;

namespace Sde.Gis.Tests;

/// <summary>An HttpMessageHandler that answers from a function, so the ArcGIS tests never touch the network.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
	private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _respond;

	public FakeHttpHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond)
	{
		_respond = respond;
	}

	/// <summary>Each request: method, the URL, and (for a POST) the form body.</summary>
	public List<(string Method, string Url, string Body)> Requests { get; } = new();

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		lock (Requests)
		{
			Requests.Add((request.Method.Method, request.RequestUri.AbsoluteUri, body));
		}
		return await _respond(request, body, cancellationToken);
	}

	public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
		new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	public static string Fixture(string name) => File.ReadAllText(Fixtures.Path("arcgis", name));

	/// <summary>The decoded query string (or form body) as name and value pairs.</summary>
	public static Dictionary<string, string> Query(string url, string body)
	{
		string q = body ?? (url.Contains('?') ? url.Substring(url.IndexOf('?') + 1) : "");
		return q.Split('&', StringSplitOptions.RemoveEmptyEntries)
			.Select(p => p.Split('=', 2))
			.ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
	}
}

/// <summary>The ArcGIS REST client against recorded answers: paging, exceededTransferLimit, the object-id fallback, Esri JSON, retries.</summary>
public class ArcGisRestClientTests
{
	private const string ParcelsUrl = "https://gis.cedarford.example/arcgis/rest/services/Parcels/FeatureServer/0";
	private const string ZonesUrl = "https://gis.cedarford.example/arcgis/rest/services/Flood/MapServer/3";
	private const string OldUrl = "https://old.cedarford.example/ArcGIS/rest/services/Levees/MapServer/1";
	private static readonly Envelope Town = new Envelope(-84.06, -84.04, 39.61, 39.65);

	private static ArcGisRestClient Client(FakeHttpHandler handler) => new ArcGisRestClient(new HttpClient(handler)) { RetryDelay = TimeSpan.Zero };

	/// <summary>Answers by what is asked: metadata, paged queries, id queries and id batches.</summary>
	private static FakeHttpHandler Server() => new FakeHttpHandler((request, body, ct) =>
	{
		string url = request.RequestUri.AbsoluteUri;
		Dictionary<string, string> q = FakeHttpHandler.Query(url, body);
		string path = request.RequestUri.AbsolutePath;
		string file;
		if (!path.EndsWith("/query", StringComparison.Ordinal))
		{
			file = path.Contains("/FeatureServer/0") ? "parcels-layer.json" : path.Contains("/MapServer/3") ? "zones-layer.json" : "old-layer.json";
		}
		else if (path.Contains("/FeatureServer/0"))
		{
			file = q["resultOffset"] switch { "0" => "parcels-page1.geojson", "2" => "parcels-page2.geojson", _ => "parcels-page3.geojson" };
		}
		else if (path.Contains("/MapServer/3"))
		{
			file = q.ContainsKey("returnIdsOnly") ? "zones-ids.json"
				: q.TryGetValue("objectIds", out string ids) ? (ids == "12,13" ? "zones-batch1.geojson" : "zones-batch2.geojson")
				: "zones-first.geojson";
		}
		else
		{
			file = "old-page.json";
		}
		return Task.FromResult(FakeHttpHandler.Json(FakeHttpHandler.Fixture(file)));
	});

	[Fact]
	public async Task LayerInfo_IsRead()
	{
		ArcGisLayerInfo info = await Client(Server()).GetLayerInfoAsync(ParcelsUrl + "/?f=pjson", TestContext.Current.CancellationToken);
		Assert.Equal(ParcelsUrl, info.Url);
		Assert.Equal("Cedar Ford Parcels", info.Name);
		Assert.Equal(GeometryKind.Polygon, info.GeometryKind);
		Assert.Equal(2, info.MaxRecordCount);
		Assert.True(info.SupportsPagination);
		Assert.True(info.SupportsGeoJson);
		Assert.Equal("OBJECTID", info.ObjectIdField);
		Assert.Equal(3857, info.ExtentWkid);
		Assert.Equal("Cedar Ford County GIS (synthetic)", info.Copyright);
		Assert.Equal(FieldType.DateTime, info.Fields.Single(f => f.Name == "SALE_DATE").Type);
		Assert.Null(info.Fields.Single(f => f.Name == "Shape").Type);
	}

	[Fact]
	public async Task Paging_FollowsExceededTransferLimit_AtTheTopAndUnderProperties()
	{
		FakeHttpHandler server = Server();
		List<int> progress = new List<int>();
		ArcGisDownload download = await Client(server).DownloadAsync(ParcelsUrl, Town, new ArcGisQueryOptions { Progress = new SyncProgress(progress) }, TestContext.Current.CancellationToken);
		FeatureLayer layer = download.Layer;
		Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, layer.Features.Select(f => f.Id));
		Assert.Equal(3, download.Requests);
		Assert.False(download.Truncated);
		Assert.False(download.UsedObjectIds);
		Assert.Equal(new[] { 2, 4, 5 }, progress);

		List<Dictionary<string, string>> queries = server.Requests.Where(r => r.Url.Contains("/query")).Select(r => FakeHttpHandler.Query(r.Url, r.Body)).ToList();
		Assert.Equal(new[] { "0", "2", "4" }, queries.Select(q => q["resultOffset"]));
		foreach (Dictionary<string, string> q in queries)
		{
			Assert.Equal("1=1", q["where"]);
			Assert.Equal("*", q["outFields"]);
			Assert.Equal("geojson", q["f"]);
			Assert.Equal("4326", q["outSR"]);
			Assert.Equal("-84.06,39.61,-84.04,39.65", q["geometry"]);
			Assert.Equal("esriGeometryEnvelope", q["geometryType"]);
			Assert.Equal("4326", q["inSR"]);
			Assert.Equal("esriSpatialRelIntersects", q["spatialRel"]);
			Assert.Equal("2", q["resultRecordCount"]);
			Assert.Equal("OBJECTID", q["orderByFields"]);
		}
		Assert.All(server.Requests, r => Assert.Equal("GET", r.Method));
	}

	[Fact]
	public async Task Features_HaveTypedValuesAndWgs84Geometry()
	{
		FeatureLayer layer = (await Client(Server()).DownloadAsync(ParcelsUrl, Town, null, TestContext.Current.CancellationToken)).Layer;
		Assert.Equal("Cedar Ford Parcels", layer.Name);
		Assert.Equal(FieldType.Integer, layer.Fields["OBJECTID"].Type);
		Assert.Equal("Parcel number", layer.Fields["PARCEL_ID"].Alias);
		Assert.False(layer.Fields.Contains("Shape"));
		Feature first = layer.Features[0];
		Assert.Equal("CF-0001", first["PARCEL_ID"]);
		Assert.Equal(21500.25, first["LAND_VAL"]);
		Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), first["SALE_DATE"]);
		Assert.Equal(DateTimeKind.Utc, ((DateTime)first["SALE_DATE"]).Kind);
		Assert.Null(first["OWNER_TYPE"]);
		Assert.Null(layer.Features[2]["SALE_DATE"]);
		Assert.Equal(-84.0496, first.Geometry.EnvelopeInternal.MinX, 1e-12);
		Assert.Equal(GeometryKind.Polygon, layer.GeometryKind);
	}

	[Fact]
	public async Task NoPaging_FallsBackToObjectIdBatches()
	{
		FakeHttpHandler server = Server();
		ArcGisDownload download = await Client(server).DownloadAsync(ZonesUrl, Town, null, TestContext.Current.CancellationToken);
		Assert.True(download.UsedObjectIds);
		Assert.Equal(new long[] { 10, 11, 12, 13, 14 }, download.Layer.Features.Select(f => f.Id));
		Assert.Equal(new[] { "AE", "X", "A", "AE", "X" }, download.Layer.Features.Select(f => (string)f["FLD_ZONE"]));
		List<Dictionary<string, string>> queries = server.Requests.Where(r => r.Url.Contains("/query")).Select(r => FakeHttpHandler.Query(r.Url, r.Body)).ToList();
		Assert.Equal(4, queries.Count);
		Assert.False(queries[0].ContainsKey("resultOffset"));
		Assert.Equal("true", queries[1]["returnIdsOnly"]);
		Assert.Equal("-84.06,39.61,-84.04,39.65", queries[1]["geometry"]);
		Assert.Equal("12,13", queries[2]["objectIds"]);
		Assert.Equal("14", queries[3]["objectIds"]);
		Assert.Equal("geojson", queries[3]["f"]);
		Assert.Equal("4326", queries[3]["outSR"]);
	}

	[Fact]
	public async Task OldServer_IsReadAsEsriJson()
	{
		FakeHttpHandler server = Server();
		FeatureLayer layer = (await Client(server).DownloadAsync(OldUrl, null, null, TestContext.Current.CancellationToken)).Layer;
		Assert.Equal("json", FakeHttpHandler.Query(server.Requests[1].Url, null)["f"]);
		Assert.False(FakeHttpHandler.Query(server.Requests[1].Url, null).ContainsKey("geometry"));
		Assert.Equal(new long[] { 7, 8 }, layer.Features.Select(f => f.Id));
		MultiPolygon north = Assert.IsType<MultiPolygon>(layer.Features[0].Geometry);
		Assert.Equal(2, north.NumGeometries);
		Assert.Equal(1, ((Polygon)north.GetGeometryN(0)).NumInteriorRings);
		Assert.True(north.IsValid);
		Assert.Equal(new DateTime(1960, 1, 1, 0, 0, 0, DateTimeKind.Utc), layer.Features[0]["BUILT"]);
		Assert.IsType<Polygon>(layer.Features[1].Geometry);
	}

	[Fact]
	public async Task ServerError_IsRetried_ThenSucceeds()
	{
		int metadataCalls = 0;
		FakeHttpHandler inner = Server();
		FakeHttpHandler server = new FakeHttpHandler(async (request, body, ct) =>
		{
			if (!request.RequestUri.AbsolutePath.EndsWith("/query", StringComparison.Ordinal) && Interlocked.Increment(ref metadataCalls) == 1)
			{
				return FakeHttpHandler.Json("<html>Bad gateway</html>", HttpStatusCode.InternalServerError);
			}
			if (request.RequestUri.Query.Contains("resultOffset=2") && Interlocked.Increment(ref metadataCalls) == 3)
			{
				// ArcGIS also reports failures as HTTP 200 with an error object.
				return FakeHttpHandler.Json(FakeHttpHandler.Fixture("error-500.json"));
			}
			return await inner.ForTest(request, body, ct);
		});
		ArcGisDownload download = await Client(server).DownloadAsync(ParcelsUrl, Town, null, TestContext.Current.CancellationToken);
		Assert.Equal(5, download.Layer.Features.Count);
		Assert.Equal(6, server.Requests.Count);
	}

	[Fact]
	public async Task ServerError_AfterEveryRetry_SaysSoInPlainEnglish()
	{
		FakeHttpHandler server = new FakeHttpHandler((request, body, ct) => Task.FromResult(FakeHttpHandler.Json("oops", HttpStatusCode.BadGateway)));
		ArcGisRestClient client = Client(server);
		client.MaxRetries = 2;
		GisException ex = await Assert.ThrowsAsync<GisException>(() => client.GetLayerInfoAsync(ParcelsUrl, TestContext.Current.CancellationToken));
		Assert.Equal("The ArcGIS server had a problem and couldn't answer (error 502). Try again in a few minutes.", ex.Message);
		Assert.Equal(3, server.Requests.Count);
	}

	[Fact]
	public async Task RefusedRequest_IsNotRetried()
	{
		FakeHttpHandler server = new FakeHttpHandler((request, body, ct) => Task.FromResult(FakeHttpHandler.Json(FakeHttpHandler.Fixture("error-400.json"))));
		GisException ex = await Assert.ThrowsAsync<GisException>(() => Client(server).GetLayerInfoAsync(ParcelsUrl, TestContext.Current.CancellationToken));
		Assert.Equal("The ArcGIS server refused the request: Invalid or missing input parameters.", ex.Message);
		Assert.Single(server.Requests);
	}

	[Fact]
	public async Task Timeout_IsRetried_ThenReported()
	{
		FakeHttpHandler server = new FakeHttpHandler(async (request, body, ct) =>
		{
			await Task.Delay(TimeSpan.FromSeconds(10), ct);
			return FakeHttpHandler.Json("{}");
		});
		ArcGisRestClient client = Client(server);
		client.Timeout = TimeSpan.FromMilliseconds(50);
		client.MaxRetries = 1;
		GisException ex = await Assert.ThrowsAsync<GisException>(() => client.GetLayerInfoAsync(ParcelsUrl, TestContext.Current.CancellationToken));
		Assert.Equal("The ArcGIS server didn't answer within 1 second. Check the internet connection and try again.", ex.Message);
		Assert.Equal(2, server.Requests.Count);
	}

	[Fact]
	public async Task Cancellation_StopsTheDownload()
	{
		using CancellationTokenSource cts = new CancellationTokenSource();
		FakeHttpHandler inner = Server();
		FakeHttpHandler server = new FakeHttpHandler((request, body, ct) =>
		{
			if (request.RequestUri.Query.Contains("resultOffset=2"))
			{
				cts.Cancel();
			}
			return inner.ForTest(request, body, ct);
		});
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(server).DownloadAsync(ParcelsUrl, Town, null, cts.Token));
		Assert.DoesNotContain(server.Requests, r => r.Url.Contains("resultOffset=4"));
	}

	[Fact]
	public async Task PageCap_StopsAndSaysSo()
	{
		ArcGisDownload download = await Client(Server()).DownloadAsync(ParcelsUrl, Town, new ArcGisQueryOptions { MaxPages = 2 }, TestContext.Current.CancellationToken);
		Assert.True(download.Truncated);
		Assert.Equal(4, download.Layer.Features.Count);
		Assert.Equal(new[] { "Only the first 4 features were downloaded. Zoom in, or use a smaller area, to get the rest." }, download.Layer.Warnings);
	}

	[Fact]
	public async Task LongRequests_AreSentAsAFormPost()
	{
		FakeHttpHandler server = Server();
		ArcGisRestClient client = Client(server);
		client.MaxGetLength = 100;
		ArcGisDownload download = await client.DownloadAsync(ZonesUrl, Town, null, TestContext.Current.CancellationToken);
		Assert.Equal(5, download.Layer.Features.Count);
		(string method, string url, string body) = server.Requests.Last();
		Assert.Equal("POST", method);
		Assert.DoesNotContain("?", url);
		Assert.Equal("14", FakeHttpHandler.Query(url, body)["objectIds"]);
	}

	[Theory]
	[InlineData("https://gis.example/arcgis/rest/services/A/FeatureServer/0", "https://gis.example/arcgis/rest/services/A/FeatureServer/0")]
	[InlineData(" https://gis.example/arcgis/rest/services/A/MapServer/12/?f=pjson ", "https://gis.example/arcgis/rest/services/A/MapServer/12")]
	[InlineData("http://gis.example/server/rest/services/Hosted/B/FeatureServer/3/query?where=1=1", "http://gis.example/server/rest/services/Hosted/B/FeatureServer/3")]
	public void LayerUrls_AreNormalized(string url, string expected) => Assert.Equal(expected, ArcGisRestClient.NormalizeLayerUrl(url));

	[Theory]
	[InlineData("https://gis.example/arcgis/rest/services/A/FeatureServer")]
	[InlineData("https://gis.example/arcgis/rest/services/A/ImageServer/0")]
	[InlineData("ftp://gis.example/A/FeatureServer/0")]
	[InlineData("")]
	public void NonLayerUrls_AreRefused(string url)
	{
		GisException ex = Assert.Throws<GisException>(() => ArcGisRestClient.NormalizeLayerUrl(url));
		Assert.Equal("That isn't an ArcGIS layer address. It should end in /FeatureServer/ or /MapServer/ and the layer's number, such as /FeatureServer/0.", ex.Message);
	}

	[Fact]
	public async Task RasterLayer_IsRefused()
	{
		FakeHttpHandler server = new FakeHttpHandler((request, body, ct) => Task.FromResult(FakeHttpHandler.Json(FakeHttpHandler.Fixture("raster-layer.json"))));
		GisException ex = await Assert.ThrowsAsync<GisException>(() => Client(server).GetLayerInfoAsync(ParcelsUrl, TestContext.Current.CancellationToken));
		Assert.Equal("This ArcGIS layer is a raster layer, not a layer of features SDE can download.", ex.Message);
	}

	/// <summary>An IProgress that records on the calling thread (Progress&lt;T&gt; posts asynchronously).</summary>
	private sealed class SyncProgress : IProgress<int>
	{
		private readonly List<int> _values;

		public SyncProgress(List<int> values)
		{
			_values = values;
		}

		public void Report(int value) => _values.Add(value);
	}
}

internal static class FakeHttpHandlerExtensions
{
	/// <summary>Lets one fake handler answer for another (recording the request in the inner one too).</summary>
	public static Task<HttpResponseMessage> ForTest(this FakeHttpHandler handler, HttpRequestMessage request, string body, CancellationToken ct) =>
		new HttpMessageInvoker(handler, disposeHandler: false).SendAsync(request, ct);
}
