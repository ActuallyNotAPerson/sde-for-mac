using System.Globalization;
using System.Text.Json;

namespace Sde.Interop.Online;

/// <summary>Shared request handling for the online services: time limit, HTTP errors and bad JSON become <see cref="OnlineServiceException"/>.</summary>
internal static class OnlineHttp
{
	/// <summary>Fetches a URL and parses the body as JSON. The caller owns the returned document.</summary>
	public static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, string serviceName, TimeSpan timeout, CancellationToken ct)
	{
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
		limit.CancelAfter(timeout);
		string body;
		try
		{
			using HttpResponseMessage response = await http.GetAsync(url, limit.Token).ConfigureAwait(false);
			body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				throw new OnlineServiceException(
					$"The {serviceName} had a problem and couldn't answer (error {(int)response.StatusCode}). Try again in a few minutes.",
					$"GET {url} returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}");
			}
		}
		catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
		{
			throw new OnlineServiceException(
				$"The {serviceName} didn't answer within {(int)Math.Ceiling(timeout.TotalSeconds)} seconds. Check the internet connection and try again.",
				$"GET {url} timed out.", ex);
		}
		catch (HttpRequestException ex)
		{
			throw new OnlineServiceException(
				$"Couldn't reach the {serviceName}. Check the internet connection and try again.",
				$"GET {url} failed: {ex.Message}", ex);
		}

		try
		{
			return JsonDocument.Parse(body);
		}
		catch (JsonException ex)
		{
			throw new OnlineServiceException(
				$"The {serviceName} sent back something SDE couldn't read. Try again later.",
				$"GET {url} returned invalid JSON: {Truncate(body)}", ex);
		}
	}

	/// <summary>An object property, or null when the element isn't an object or lacks it (or it is JSON null).</summary>
	public static JsonElement? Prop(JsonElement element, string name)
	{
		if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null)
		{
			return value;
		}
		return null;
	}

	/// <summary>A property as trimmed text, or null when missing or blank.</summary>
	public static string Text(JsonElement element, string name)
	{
		JsonElement? v = Prop(element, name);
		if (v == null)
		{
			return null;
		}
		string s = v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.ToString();
		return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
	}

	/// <summary>A property as a number (JSON numbers or numeric text), or null.</summary>
	public static double? Number(JsonElement element, string name)
	{
		JsonElement? v = Prop(element, name);
		if (v == null)
		{
			return null;
		}
		if (v.Value.ValueKind == JsonValueKind.Number)
		{
			return v.Value.GetDouble();
		}
		if (v.Value.ValueKind == JsonValueKind.String && double.TryParse(v.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
		{
			return d;
		}
		return null;
	}

	private static string Truncate(string s) => s != null && s.Length > 500 ? s.Substring(0, 500) + "..." : s;
}
