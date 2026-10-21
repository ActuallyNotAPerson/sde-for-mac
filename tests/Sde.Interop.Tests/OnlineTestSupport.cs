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
}
