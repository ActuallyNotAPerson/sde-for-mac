using System.Net;
using System.Text;

namespace Sde.Interop.Tests;

/// <summary>An HttpMessageHandler that answers from a function, so online tests never touch the network.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
	private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

	public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
	{
		_respond = respond;
	}

	public List<string> Urls { get; } = new();

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		lock (Urls)
		{
			Urls.Add(request.RequestUri.AbsoluteUri);
		}
		return _respond(request, cancellationToken);
	}

	public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
		new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	public static string Fixture(string name) =>
		File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "online", name));

	/// <summary>Answers NFHL queries by layer number (28 zones, 3 panels, 22 communities) with the named fixtures.</summary>
	public static FakeHandler Nfhl(string zone, string panel = "nfhl-panel.json", string community = "nfhl-community.json") =>
		new FakeHandler((request, ct) =>
		{
			string path = request.RequestUri.AbsolutePath;
			string file = path.Contains("/28/query") ? zone : path.Contains("/3/query") ? panel : path.Contains("/22/query") ? community : null;
			return Task.FromResult(file == null ? Json("{}", HttpStatusCode.NotFound) : Json(Fixture(file)));
		});

	public static string ZoneJson(string zone, string subtype = null, double bfe = -9999, string datum = null) =>
		"{\"features\":[{\"attributes\":{\"FLD_ZONE\":\"" + zone + "\",\"ZONE_SUBTY\":" + (subtype == null ? "null" : "\"" + subtype + "\"")
		+ ",\"STATIC_BFE\":" + bfe.ToString(System.Globalization.CultureInfo.InvariantCulture)
		+ ",\"V_DATUM\":" + (datum == null ? "null" : "\"" + datum + "\"") + "}}]}";

	/// <summary>A flood zone layer answer with one feature per zone, in the order given. Each is "ZONE" or "ZONE|SUBTYPE".</summary>
	public static string ZonesJson(params string[] zones) =>
		"{\"features\":[" + string.Join(",", zones.Select(z =>
		{
			string[] parts = z.Split('|');
			return "{\"attributes\":{\"FLD_ZONE\":\"" + parts[0] + "\",\"ZONE_SUBTY\":" + (parts.Length > 1 ? "\"" + parts[1] + "\"" : "null")
				+ ",\"STATIC_BFE\":-9999,\"V_DATUM\":null}}";
		})) + "]}";

	/// <summary>Answers the flood zone layer with <paramref name="zoneBody"/>, and the panel and community layers with their fixtures.</summary>
	public static FakeHandler NfhlZones(string zoneBody) =>
		new FakeHandler((request, ct) =>
		{
			string path = request.RequestUri.AbsolutePath;
			string body = path.Contains("/28/query") ? zoneBody : path.Contains("/3/query") ? Fixture("nfhl-panel.json") : Fixture("nfhl-community.json");
			return Task.FromResult(Json(body));
		});
}
