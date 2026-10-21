using System.Text.Json;

namespace Sde.Interop.Online;

/// <summary>A geocoder hit: where the address is and how the service wrote it.</summary>
public sealed record GeocodeMatch(double Latitude, double Longitude, string MatchedAddress);

/// <summary>
/// Looks up an address with the U.S. Census Bureau's free geocoder (one-line address search).
/// Format: https://geocoding.geo.census.gov/geocoder/locations/onelineaddress?address=...&amp;benchmark=Public_AR_Current&amp;format=json;
/// the answer is result.addressMatches[], each with matchedAddress and coordinates.x (longitude) / coordinates.y (latitude).
/// </summary>
public sealed class CensusGeocoder
{
	private readonly HttpClient _http;

	public CensusGeocoder(HttpClient http)
	{
		_http = http;
	}

	/// <summary>The service's address; change it only for testing.</summary>
	public string BaseUrl { get; set; } = "https://geocoding.geo.census.gov/geocoder/locations/onelineaddress";

	public string Benchmark { get; set; } = "Public_AR_Current";

	/// <summary>How long to wait for an answer before giving up.</summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

	/// <summary>The first match for the address, or null when the service finds none.</summary>
	public async Task<GeocodeMatch> GeocodeAsync(string oneLineAddress, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(oneLineAddress))
		{
			return null;
		}
		string url = BaseUrl + "?address=" + Uri.EscapeDataString(oneLineAddress.Trim())
			+ "&benchmark=" + Uri.EscapeDataString(Benchmark) + "&format=json";
		using JsonDocument doc = await OnlineHttp.GetJsonAsync(_http, url, "Census geocoder", Timeout, ct).ConfigureAwait(false);
		try
		{
			JsonElement? result = OnlineHttp.Prop(doc.RootElement, "result");
			JsonElement? matches = result == null ? null : OnlineHttp.Prop(result.Value, "addressMatches");
			if (matches == null || matches.Value.ValueKind != JsonValueKind.Array)
			{
				throw new FormatException("result.addressMatches is missing.");
			}
			if (matches.Value.GetArrayLength() == 0)
			{
				return null;
			}
			JsonElement first = matches.Value[0];
			JsonElement? coordinates = OnlineHttp.Prop(first, "coordinates");
			double? x = coordinates == null ? null : OnlineHttp.Number(coordinates.Value, "x");
			double? y = coordinates == null ? null : OnlineHttp.Number(coordinates.Value, "y");
			if (x == null || y == null)
			{
				throw new FormatException("The first match has no coordinates.");
			}
			return new GeocodeMatch(y.Value, x.Value, OnlineHttp.Text(first, "matchedAddress") ?? oneLineAddress.Trim());
		}
		catch (FormatException ex)
		{
			throw new OnlineServiceException("The Census geocoder sent back something SDE couldn't read. Try again later.",
				$"Unexpected response shape for {url}: {ex.Message}", ex);
		}
	}
}
