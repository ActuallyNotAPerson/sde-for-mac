using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sde.Core.Depth;

namespace Sde.Interop.Online;

/// <summary>A flood or storm event in USGS's Short-Term Network (STN), which high-water marks are filed under.</summary>
/// <param name="Id">STN's event ID, used to ask for the event's marks.</param>
/// <param name="Name">E.g. "2011 Irene".</param>
/// <param name="StartDate">The event's start date, when STN gives one.</param>
/// <param name="EndDate">The event's end date, when STN gives one.</param>
/// <param name="Type">E.g. "Hurricane" or "River Flood", when STN gives one.</param>
/// <param name="Status">E.g. "Active" or "Completed", when STN gives one.</param>
public sealed record StnEvent(int Id, string Name, DateTime? StartDate, DateTime? EndDate, string Type, string Status);

/// <summary>High-water marks from STN for one event and one or more states, with USGS's notice that the data are provisional.</summary>
public sealed class StnHighWaterMarks
{
	public int EventId { get; init; }

	/// <summary>The state codes asked for, e.g. "VT".</summary>
	public IReadOnlyList<string> States { get; init; } = Array.Empty<string>();

	/// <summary>The marks with a position and an elevation. They never carry the names of the people who flagged, surveyed or approved them.</summary>
	public IReadOnlyList<HighWaterMark> Marks { get; init; } = Array.Empty<HighWaterMark>();

	/// <summary>Marks STN listed that had no usable position or elevation (not surveyed yet, usually).</summary>
	public int Skipped { get; init; }

	/// <summary>USGS's notice for provisional data, to show wherever the marks are shown.</summary>
	public string Notice => StnClient.ProvisionalNotice;

	/// <summary>When SDE asked (UTC).</summary>
	public DateTime RetrievedOn { get; init; }
}

/// <summary>
/// Reads events and high-water marks from USGS's Short-Term Network (STN) services. The API is undocumented (the old
/// stn.wim.usgs.gov address now redirects), so its address is a setting, like the flood map's: see
/// docs/ONLINE-SERVICES.md. Parameter names are lowercase, and numbers are read whether they come as numbers or as
/// text. A busy gateway (502, 503, 504) is asked again. The names of USGS staff in the answers (who flagged, surveyed
/// or approved a mark) are never read, stored or shown, and neither are the free-text notes and descriptions.
/// </summary>
public sealed class StnClient
{
	/// <summary>The address SDE uses unless Settings says otherwise.</summary>
	public const string DefaultBaseUrl = "https://api.waterdata.usgs.gov/stnservices/";

	/// <summary>USGS's standard notice for provisional data (a US government text, public domain).</summary>
	public const string ProvisionalNotice =
		"This information is preliminary or provisional and is subject to revision. It is being provided to meet the need for timely best science. "
		+ "The information has not received final approval by the U.S. Geological Survey (USGS) and is provided on the condition that neither the USGS "
		+ "nor the U.S. Government shall be held liable for any damages resulting from the authorized or unauthorized use of the information.";

	private const string ServiceName = "USGS high-water mark service";

	private static readonly Regex StateCode = new Regex("^[A-Za-z]{2}$", RegexOptions.Compiled);

	private readonly HttpClient _http;

	public StnClient(HttpClient http) : this(http, DefaultBaseUrl)
	{
	}

	public StnClient(HttpClient http, string baseUrl)
	{
		_http = http;
		BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl;
	}

	/// <summary>The service's address, ending before "events/" and "hwms/".</summary>
	public string BaseUrl { get; set; }

	/// <summary>How long to wait for each answer. An event's marks for a state can run to a few megabytes.</summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>How many more times to ask after a 502, 503 or 504.</summary>
	public int Retries { get; set; } = 2;

	/// <summary>How long to wait before asking again.</summary>
	public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

	/// <summary>Every STN event, newest first.</summary>
	public async Task<IReadOnlyList<StnEvent>> GetEventsAsync(CancellationToken ct)
	{
		string url = Url("events/");
		using JsonDocument doc = await OnlineHttp.GetJsonAsync(_http, url, ServiceName, Timeout, ct, Retries, RetryDelay).ConfigureAwait(false);
		List<StnEvent> events = new List<StnEvent>();
		foreach (JsonElement e in Items(doc.RootElement, url))
		{
			double? id = OnlineHttp.Number(e, "id") ?? OnlineHttp.Number(e, "event_id");
			string name = OnlineHttp.Text(e, "event_name") ?? OnlineHttp.Text(e, "name");
			if (id == null || name == null)
			{
				continue;
			}
			events.Add(new StnEvent(
				(int)id.Value,
				name,
				Date(OnlineHttp.Text(e, "event_start_date") ?? OnlineHttp.Text(e, "start_date")),
				Date(OnlineHttp.Text(e, "event_end_date") ?? OnlineHttp.Text(e, "end_date")),
				OnlineHttp.Text(e, "event_type_name") ?? OnlineHttp.Text(e, "event_type"),
				OnlineHttp.Text(e, "event_status_name") ?? OnlineHttp.Text(e, "event_status")));
		}
		return events.OrderByDescending(e => e.StartDate ?? DateTime.MinValue).ThenByDescending(e => e.Id).ToList();
	}

	/// <summary>
	/// The high-water marks filed under an event in the given states (two-letter codes such as "VT"). Marks without a
	/// position or an elevation are counted in <see cref="StnHighWaterMarks.Skipped"/> and left out.
	/// </summary>
	public async Task<StnHighWaterMarks> GetHighWaterMarksAsync(int eventId, IEnumerable<string> states, CancellationToken ct)
	{
		List<string> codes = (states ?? Enumerable.Empty<string>()).Select(s => (s ?? "").Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();
		if (codes.Count == 0)
		{
			throw new ArgumentException("Choose at least one state.", nameof(states));
		}
		string bad = codes.FirstOrDefault(s => !StateCode.IsMatch(s));
		if (bad != null)
		{
			throw new ArgumentException($"\"{bad}\" isn't a two-letter state code.", nameof(states));
		}
		string url = Url("hwms/filteredhwms/") + "?event=" + eventId.ToString(CultureInfo.InvariantCulture) + "&states=" + string.Join(",", codes);
		using JsonDocument doc = await OnlineHttp.GetJsonAsync(_http, url, ServiceName, Timeout, ct, Retries, RetryDelay).ConfigureAwait(false);
		List<HighWaterMark> marks = new List<HighWaterMark>();
		int skipped = 0;
		foreach (JsonElement e in Items(doc.RootElement, url))
		{
			HighWaterMark mark = ToMark(e);
			if (mark == null)
			{
				skipped++;
			}
			else
			{
				marks.Add(mark);
			}
		}
		return new StnHighWaterMarks { EventId = eventId, States = codes, Marks = marks, Skipped = skipped, RetrievedOn = DateTime.UtcNow };
	}

	/// <summary>
	/// One mark from STN's answer. Only these fields are read: id, label, latitude and longitude, elev_ft,
	/// vertical_datum_name, hwm_quality_id (or the quality's name) and hwm_type_name. Staff names, notes and
	/// descriptions are never read.
	/// </summary>
	internal static HighWaterMark ToMark(JsonElement e)
	{
		double? lat = OnlineHttp.Number(e, "latitude_dd") ?? OnlineHttp.Number(e, "latitude");
		double? lon = OnlineHttp.Number(e, "longitude_dd") ?? OnlineHttp.Number(e, "longitude");
		double? elev = OnlineHttp.Number(e, "elev_ft");
		if (lat is not double la || lon is not double lo || elev is not double el
			|| la < -90 || la > 90 || lo < -180 || lo > 180 || (la == 0 && lo == 0) || !double.IsFinite(el))
		{
			return null;
		}
		double? quality = OnlineHttp.Number(e, "hwm_quality_id");
		double? id = OnlineHttp.Number(e, "id") ?? OnlineHttp.Number(e, "hwm_id");
		return new HighWaterMark
		{
			Id = id?.ToString("0", CultureInfo.InvariantCulture),
			Label = OnlineHttp.Text(e, "label") ?? OnlineHttp.Text(e, "hwm_label"),
			Latitude = la,
			Longitude = lo,
			ElevationFt = Math.Round((decimal)el, 4),
			DatumName = OnlineHttp.Text(e, "vertical_datum_name") ?? OnlineHttp.Text(e, "verticalDatumName"),
			Quality = quality is double q && q >= 1 && q <= 6 ? (int)q : HighWaterMarkFiles.ParseQuality(OnlineHttp.Text(e, "hwm_quality_name")),
			Kind = OnlineHttp.Text(e, "hwm_type_name"),
		};
	}

	private string Url(string path) => (BaseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/" + path;

	/// <summary>The objects in an answer that should be a JSON array.</summary>
	private static List<JsonElement> Items(JsonElement root, string url)
	{
		if (root.ValueKind != JsonValueKind.Array)
		{
			throw new OnlineServiceException($"The {ServiceName} sent back something SDE couldn't read. Try again later.",
				$"Unexpected response shape for {url}: expected an array, got {root.ValueKind}.");
		}
		return root.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
	}

	/// <summary>A date from STN's text ("2011-08-26T04:00:00Z" or "2011-08-26"), as a calendar date; null when it isn't one.</summary>
	private static DateTime? Date(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d))
		{
			return d.UtcDateTime.Date;
		}
		return null;
	}
}
