using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Formats;
using Sde.Gis.Projections;

namespace Sde.Gis.Online;

/// <summary>Choices for downloading features from an ArcGIS layer.</summary>
public sealed class ArcGisQueryOptions
{
	/// <summary>The SQL where clause; every feature by default.</summary>
	public string Where { get; init; } = "1=1";

	/// <summary>Features per request; the layer's maxRecordCount when null.</summary>
	public int? PageSize { get; init; }

	/// <summary>The most requests for features (pages or id batches) before stopping with what has arrived.</summary>
	public int MaxPages { get; init; } = 200;

	/// <summary>Told the number of features downloaded so far after each page.</summary>
	public IProgress<int> Progress { get; init; }
}

/// <summary>What <see cref="ArcGisRestClient.DownloadAsync"/> brought back.</summary>
public sealed class ArcGisDownload
{
	public ArcGisLayerInfo Info { get; init; }

	/// <summary>The features, in WGS 84, with the layer's fields; <see cref="Feature.Id"/> is the object id.</summary>
	public FeatureLayer Layer { get; init; }

	/// <summary>Requests made for features (not counting the metadata).</summary>
	public int Requests { get; set; }

	/// <summary>True when the page cap stopped the download before every feature arrived.</summary>
	public bool Truncated { get; set; }

	/// <summary>True when the layer couldn't be paged, so features were fetched by object id in batches.</summary>
	public bool UsedObjectIds { get; set; }
}

/// <summary>
/// Reads features from an ArcGIS REST FeatureServer or MapServer layer: the layer's metadata, then
/// query?where=1=1&amp;outFields=*&amp;f=geojson&amp;outSR=4326, limited to an extent, paged with resultOffset and
/// resultRecordCount until exceededTransferLimit (at the top level or under "properties") is false. Layers that can't be
/// paged are read by object id in batches (returnIdsOnly, then objectIds). Servers that can't answer in GeoJSON are read
/// as Esri JSON. Server errors (5xx, 429) and timeouts are retried with backoff; everything honours the cancellation token.
/// </summary>
public sealed class ArcGisRestClient
{
	private static readonly Regex LayerUrl = new Regex(@"^(https?://[^?#]+/(FeatureServer|MapServer)/\d+)(/query)?/?(\?.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private readonly HttpClient _http;

	public ArcGisRestClient(HttpClient http)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
	}

	/// <summary>How long one request may take.</summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>How many times a failed request (server error, rate limit or timeout) is tried again.</summary>
	public int MaxRetries { get; set; } = 3;

	/// <summary>The wait before the first retry; it doubles each time (unless the server sends Retry-After).</summary>
	public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

	/// <summary>Requests longer than this are sent as a form POST (ArcGIS accepts both).</summary>
	public int MaxGetLength { get; set; } = 1800;

	/// <summary>
	/// The layer address without query or trailing slash. Throws <see cref="GisException"/> for anything that isn't a
	/// FeatureServer or MapServer layer address.
	/// </summary>
	public static string NormalizeLayerUrl(string url)
	{
		Match m = LayerUrl.Match((url ?? "").Trim());
		if (!m.Success)
		{
			throw new GisException("That isn't an ArcGIS layer address. It should end in /FeatureServer/ or /MapServer/ and the layer's number, such as /FeatureServer/0.", url);
		}
		return m.Groups[1].Value;
	}

	/// <summary>Reads the layer's metadata (name, geometry type, fields, paging support, extent).</summary>
	public async Task<ArcGisLayerInfo> GetLayerInfoAsync(string layerUrl, CancellationToken ct)
	{
		string url = NormalizeLayerUrl(layerUrl);
		using JsonDocument doc = await RequestAsync(url, new List<KeyValuePair<string, string>> { new("f", "json") }, ct).ConfigureAwait(false);
		ArcGisLayerInfo info = ArcGisLayerInfo.Parse(url, doc.RootElement);
		if (info.LayerType != null && !info.LayerType.Contains("Feature", StringComparison.OrdinalIgnoreCase) && !info.LayerType.Contains("Table", StringComparison.OrdinalIgnoreCase))
		{
			throw new GisException($"This ArcGIS layer is a {info.LayerType.ToLowerInvariant()}, not a layer of features SDE can download.", url);
		}
		return info;
	}

	/// <summary>
	/// Downloads the features that meet <paramref name="extent"/> (WGS 84 longitude and latitude; the whole layer when
	/// null), in WGS 84.
	/// </summary>
	public async Task<ArcGisDownload> DownloadAsync(string layerUrl, Envelope extent, ArcGisQueryOptions options, CancellationToken ct)
	{
		options ??= new ArcGisQueryOptions();
		ArcGisLayerInfo info = await GetLayerInfoAsync(layerUrl, ct).ConfigureAwait(false);
		FeatureLayer layer = new FeatureLayer(info.Name ?? "ArcGIS layer", info.GeometryKind);
		List<(string Name, FieldType Type)> fields = new List<(string, FieldType)>();
		foreach (ArcGisField f in info.Fields)
		{
			if (f.Type is FieldType type && !layer.Fields.Contains(f.Name))
			{
				layer.AddField(f.Name, type, string.Equals(f.Alias, f.Name, StringComparison.Ordinal) ? null : f.Alias);
				fields.Add((f.Name, type));
			}
		}
		ArcGisDownload download = new ArcGisDownload { Info = info, Layer = layer };
		int pageSize = Math.Max(1, Math.Min(options.PageSize ?? info.MaxRecordCount, info.MaxRecordCount));
		HashSet<long> seen = new HashSet<long>();

		List<KeyValuePair<string, string>> Base()
		{
			List<KeyValuePair<string, string>> q = new List<KeyValuePair<string, string>>
			{
				new("where", string.IsNullOrWhiteSpace(options.Where) ? "1=1" : options.Where),
				new("outFields", "*"),
				new("f", info.SupportsGeoJson ? "geojson" : "json"),
				new("outSR", "4326"),
				new("returnGeometry", "true"),
			};
			if (extent != null && !extent.IsNull)
			{
				q.Add(new("geometry", string.Join(",", new[] { extent.MinX, extent.MinY, extent.MaxX, extent.MaxY }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));
				q.Add(new("geometryType", "esriGeometryEnvelope"));
				q.Add(new("inSR", "4326"));
				q.Add(new("spatialRel", "esriSpatialRelIntersects"));
			}
			return q;
		}

		string queryUrl = info.Url + "/query";
		if (info.SupportsPagination)
		{
			for (int offset = 0; ; )
			{
				if (download.Requests >= options.MaxPages)
				{
					download.Truncated = true;
					break;
				}
				List<KeyValuePair<string, string>> q = Base();
				q.Add(new("resultOffset", offset.ToString(CultureInfo.InvariantCulture)));
				q.Add(new("resultRecordCount", pageSize.ToString(CultureInfo.InvariantCulture)));
				if (info.ObjectIdField != null)
				{
					q.Add(new("orderByFields", info.ObjectIdField));
				}
				(int count, bool exceeded) = await PageAsync(queryUrl, q, info, layer, fields, seen, download, ct).ConfigureAwait(false);
				options.Progress?.Report(layer.Features.Count);
				if (!exceeded || count == 0)
				{
					break;
				}
				offset += count;
			}
		}
		else
		{
			(_, bool exceeded) = await PageAsync(queryUrl, Base(), info, layer, fields, seen, download, ct).ConfigureAwait(false);
			options.Progress?.Report(layer.Features.Count);
			if (exceeded)
			{
				download.UsedObjectIds = true;
				List<KeyValuePair<string, string>> idQuery = Base();
				idQuery.RemoveAll(p => p.Key is "outFields" or "f" or "outSR" or "returnGeometry");
				idQuery.Add(new("returnIdsOnly", "true"));
				idQuery.Add(new("f", "json"));
				List<long> ids;
				using (JsonDocument idDoc = await RequestAsync(queryUrl, idQuery, ct).ConfigureAwait(false))
				{
					ids = new List<long>();
					if (idDoc.RootElement.TryGetProperty("objectIds", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement id in array.EnumerateArray())
						{
							if (id.TryGetInt64(out long v))
							{
								ids.Add(v);
							}
						}
					}
				}
				ids.Sort();
				List<long> missing = ids.Where(id => !seen.Contains(id)).ToList();
				for (int start = 0; start < missing.Count; start += pageSize)
				{
					if (download.Requests >= options.MaxPages)
					{
						download.Truncated = true;
						break;
					}
					List<KeyValuePair<string, string>> q = Base();
					q.RemoveAll(p => p.Key is "where" or "geometry" or "geometryType" or "inSR" or "spatialRel");
					q.Add(new("objectIds", string.Join(",", missing.Skip(start).Take(pageSize).Select(i => i.ToString(CultureInfo.InvariantCulture)))));
					await PageAsync(queryUrl, q, info, layer, fields, seen, download, ct).ConfigureAwait(false);
					options.Progress?.Report(layer.Features.Count);
				}
			}
		}
		if (download.Truncated)
		{
			layer.Warnings.Add($"Only the first {layer.Features.Count:N0} features were downloaded. Zoom in, or use a smaller area, to get the rest.");
		}
		if (layer.GeometryKind == GeometryKind.Unknown)
		{
			layer.DetectGeometryKind();
		}
		return download;
	}

	/// <summary>One page of features: adds the new ones to the layer and says how many came and whether there are more.</summary>
	private async Task<(int Count, bool Exceeded)> PageAsync(string url, List<KeyValuePair<string, string>> query, ArcGisLayerInfo info, FeatureLayer layer,
		List<(string Name, FieldType Type)> fields, HashSet<long> seen, ArcGisDownload download, CancellationToken ct)
	{
		download.Requests++;
		using JsonDocument doc = await RequestAsync(url, query, ct).ConfigureAwait(false);
		JsonElement root = doc.RootElement;
		bool exceeded = Flag(root, "exceededTransferLimit")
			|| (root.TryGetProperty("properties", out JsonElement props) && Flag(props, "exceededTransferLimit"));
		int count = 0;
		if (!root.TryGetProperty("features", out JsonElement features) || features.ValueKind != JsonValueKind.Array)
		{
			throw new GisException("The ArcGIS server sent back something SDE couldn't read. Try again later.", $"{url}: no features array.");
		}
		bool geoJson = root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "FeatureCollection";
		foreach (JsonElement f in features.EnumerateArray())
		{
			count++;
			JsonElement attributes = geoJson ? Prop(f, "properties") : Prop(f, "attributes");
			Geometry geometry = null;
			JsonElement g = Prop(f, "geometry");
			if (g.ValueKind == JsonValueKind.Object)
			{
				geometry = geoJson ? GeoJsonFormat.ReadGeometry(g, CoordinateSystem.Wgs84) : EsriJson.ReadGeometry(g);
			}
			long id = 0;
			JsonElement idElement = Prop(f, "id");
			if (geoJson && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out long fid))
			{
				id = fid;
			}
			else if (info.ObjectIdField != null && attributes.ValueKind == JsonValueKind.Object
				&& TryGetProperty(attributes, info.ObjectIdField, out JsonElement oid) && oid.TryGetInt64(out long aid))
			{
				id = aid;
			}
			if (id != 0 && !seen.Add(id))
			{
				continue;
			}
			Feature feature = layer.NewFeature(geometry, id);
			if (attributes.ValueKind == JsonValueKind.Object)
			{
				for (int i = 0; i < fields.Count; i++)
				{
					if (TryGetProperty(attributes, fields[i].Name, out JsonElement value))
					{
						feature.SetRaw(layer.Fields.IndexOf(fields[i].Name), Value(value, fields[i].Type));
					}
				}
			}
			layer.Features.Add(feature);
		}
		return (count, exceeded);
	}

	private static object Value(JsonElement v, FieldType type)
	{
		switch (v.ValueKind)
		{
			case JsonValueKind.Null:
			case JsonValueKind.Undefined:
				return null;
			case JsonValueKind.Number when type is FieldType.DateTime or FieldType.Date:
				// Esri dates are milliseconds since 1970 in UTC.
				DateTime t = DateTime.UnixEpoch.AddMilliseconds(v.GetDouble());
				return type == FieldType.Date ? t.Date : t;
			case JsonValueKind.Number:
				return type == FieldType.Integer && v.TryGetInt64(out long l) ? l : FieldValues.TryConvert(type, v.GetDouble(), out object n) ? n : v.GetRawText();
			case JsonValueKind.String:
				return FieldValues.TryConvert(type, v.GetString(), out object s) ? s : null;
			case JsonValueKind.True:
				return type == FieldType.Text ? "true" : true;
			case JsonValueKind.False:
				return type == FieldType.Text ? "false" : false;
			default:
				return type == FieldType.Text ? v.GetRawText() : null;
		}
	}

	/// <summary>
	/// Sends one request (GET, or a form POST when long) and parses the JSON, retrying server errors, rate limits and
	/// timeouts with backoff. ArcGIS also reports errors as HTTP 200 with an "error" object; those with a 5xx code are
	/// retried too, the rest become <see cref="GisException"/>.
	/// </summary>
	private async Task<JsonDocument> RequestAsync(string url, List<KeyValuePair<string, string>> query, CancellationToken ct)
	{
		string body = string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
		string getUrl = url + "?" + body;
		bool post = getUrl.Length > MaxGetLength;
		for (int attempt = 0; ; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			string problem;
			string detail;
			TimeSpan? retryAfter = null;
			using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
			{
				limit.CancelAfter(Timeout);
				try
				{
					using HttpRequestMessage request = post
						? new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") }
						: new HttpRequestMessage(HttpMethod.Get, getUrl);
					using HttpResponseMessage response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
					string text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
					int status = (int)response.StatusCode;
					if (status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
					{
						retryAfter = response.Headers.RetryAfter?.Delta;
						problem = $"The ArcGIS server had a problem and couldn't answer (error {status}). Try again in a few minutes.";
						detail = $"{(post ? "POST" : "GET")} {getUrl} returned {status}: {Truncate(text)}";
					}
					else if (!response.IsSuccessStatusCode)
					{
						throw new GisException(status is 401 or 403
							? "This ArcGIS layer isn't public: the server asked for a sign-in. SDE can only read public layers."
							: $"The ArcGIS server couldn't find or answer this layer (error {status}). Check the address.",
							$"{(post ? "POST" : "GET")} {getUrl} returned {status}: {Truncate(text)}");
					}
					else
					{
						JsonDocument doc;
						try
						{
							doc = JsonDocument.Parse(text);
						}
						catch (JsonException ex)
						{
							throw new GisException("The ArcGIS server sent back something SDE couldn't read. Check the address, or try again later.", $"{getUrl}: {Truncate(text)}", ex);
						}
						if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
						{
							int code = ArcGisLayerInfo.Number(error, "code") is double c ? (int)c : 0;
							string message = ArcGisLayerInfo.Text(error, "message") ?? "unknown error";
							doc.Dispose();
							if (code < 500 && code != 429)
							{
								throw new GisException(code is 498 or 499 or 401 or 403
									? "This ArcGIS layer isn't public: the server asked for a sign-in. SDE can only read public layers."
									: $"The ArcGIS server refused the request: {message.TrimEnd('.')}.",
									$"{getUrl}: error {code} {message}");
							}
							problem = $"The ArcGIS server had a problem and couldn't answer (error {code}). Try again in a few minutes.";
							detail = $"{getUrl}: error {code} {message}";
						}
						else
						{
							return doc;
						}
					}
				}
				catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
				{
					int seconds = (int)Math.Ceiling(Timeout.TotalSeconds);
					problem = $"The ArcGIS server didn't answer within {seconds} {(seconds == 1 ? "second" : "seconds")}. Check the internet connection and try again.";
					detail = $"{getUrl} timed out. {ex.Message}";
				}
				catch (HttpRequestException ex)
				{
					problem = "Couldn't reach the ArcGIS server. Check the internet connection and the address, and try again.";
					detail = $"{getUrl} failed: {ex.Message}";
				}
			}
			if (attempt >= MaxRetries)
			{
				throw new GisException(problem, $"{detail} (after {attempt + 1} tries)");
			}
			TimeSpan wait = retryAfter ?? TimeSpan.FromMilliseconds(RetryDelay.TotalMilliseconds * Math.Pow(2, attempt));
			if (wait > TimeSpan.Zero)
			{
				await Task.Delay(wait, ct).ConfigureAwait(false);
			}
		}
	}

	private static bool Flag(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

	private static JsonElement Prop(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) ? v : default;

	/// <summary>A property by name, ignoring case (services aren't consistent about OBJECTID's case).</summary>
	private static bool TryGetProperty(JsonElement e, string name, out JsonElement value)
	{
		if (e.TryGetProperty(name, out value))
		{
			return true;
		}
		foreach (JsonProperty p in e.EnumerateObject())
		{
			if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				value = p.Value;
				return true;
			}
		}
		return false;
	}

	private static string Truncate(string s) => s != null && s.Length > 500 ? s.Substring(0, 500) + "..." : s;
}
