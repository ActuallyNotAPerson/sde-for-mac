using System.Globalization;
using System.Text.Json;

namespace Sde.Interop.Online;

/// <summary>What FEMA's National Flood Hazard Layer says about one point.</summary>
/// <param name="Zone">FLD_ZONE, e.g. AE, X, VE, A99.</param>
/// <param name="ZoneSubtype">ZONE_SUBTY, e.g. "FLOODWAY" or "0.2 PCT ANNUAL CHANCE FLOOD HAZARD"; null when blank.</param>
/// <param name="InFloodway">The zone subtype says floodway.</param>
/// <param name="Bfe">Base flood elevation in feet, or null when the map shows none (the service uses -9999).</param>
/// <param name="BfeDatum">Vertical datum of the BFE (V_DATUM), e.g. NAVD88.</param>
/// <param name="PanelNumber">FIRM panel (FIRM_PAN).</param>
/// <param name="PanelSuffix">Panel suffix letter.</param>
/// <param name="PanelEffectiveDate">Effective date of the panel.</param>
/// <param name="CommunityId">NFIP community ID (CID).</param>
/// <param name="CommunityName">Community name (POL_NAME1).</param>
/// <param name="RetrievedOn">When SDE asked the service (UTC).</param>
public sealed record FloodInfo(
	string Zone,
	string ZoneSubtype,
	bool InFloodway,
	decimal? Bfe,
	string BfeDatum,
	string PanelNumber,
	string PanelSuffix,
	DateTime? PanelEffectiveDate,
	string CommunityId,
	string CommunityName,
	DateTime RetrievedOn);

/// <summary>
/// Looks up flood zone, BFE, FIRM panel and community for a point in FEMA's NFHL ArcGIS REST service
/// (MapServer layers: flood hazard zones, FIRM panels, political jurisdictions). The layer numbers are properties
/// because FEMA has renumbered them before; see docs/ONLINE-SERVICES.md for what to check.
/// </summary>
public sealed class NfhlClient
{
	private readonly HttpClient _http;

	public NfhlClient(HttpClient http) : this(http, "https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer")
	{
	}

	public NfhlClient(HttpClient http, string mapServerUrl)
	{
		_http = http;
		BaseUrl = mapServerUrl;
	}

	/// <summary>The MapServer address, without a layer number or "/query".</summary>
	public string BaseUrl { get; set; }

	public int FloodZoneLayer { get; set; } = 28;
	public int PanelLayer { get; set; } = 3;
	public int CommunityLayer { get; set; } = 22;

	/// <summary>How long to wait for each answer before giving up.</summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

	/// <summary>The flood information at the point, or null when the map has no flood zone there.</summary>
	public async Task<FloodInfo> LookupAsync(double latitude, double longitude, CancellationToken ct)
	{
		// The three layers are asked at once. Their answers are then read in order, so a failure is reported just as if
		// they had been asked one after another: the zone layer's first, and none from the other two when the point
		// has no flood zone.
		Task<List<JsonElement>> zonesQuery = QueryAsync(FloodZoneLayer, latitude, longitude, ct);
		Task<List<JsonElement>> panelsQuery = QueryAsync(PanelLayer, latitude, longitude, ct);
		Task<List<JsonElement>> communitiesQuery = QueryAsync(CommunityLayer, latitude, longitude, ct);
		await Task.WhenAll(new Task[] { zonesQuery, panelsQuery, communitiesQuery }).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		List<JsonElement> zones = await zonesQuery.ConfigureAwait(false);
		if (zones.Count == 0)
		{
			return null;
		}
		List<JsonElement> panels = await panelsQuery.ConfigureAwait(false);
		List<JsonElement> communities = await communitiesQuery.ConfigureAwait(false);

		JsonElement zone = StrictestZone(zones);
		string subtype = OnlineHttp.Text(zone, "ZONE_SUBTY");

		decimal? bfe = null;
		double? rawBfe = OnlineHttp.Number(zone, "STATIC_BFE");
		if (rawBfe != null && rawBfe.Value > -9998)
		{
			bfe = (decimal)Math.Round(rawBfe.Value, 2);
		}

		string panelNumber = null;
		string suffix = null;
		DateTime? effective = null;
		if (panels.Count > 0)
		{
			panelNumber = OnlineHttp.Text(panels[0], "FIRM_PAN");
			suffix = OnlineHttp.Text(panels[0], "SUFFIX");
			double? ms = OnlineHttp.Number(panels[0], "EFF_DATE");
			if (ms != null)
			{
				effective = DateTime.UnixEpoch.AddMilliseconds(ms.Value).Date;
			}
		}

		string communityId = null;
		string communityName = null;
		if (communities.Count > 0)
		{
			communityId = OnlineHttp.Text(communities[0], "CID");
			communityName = OnlineHttp.Text(communities[0], "POL_NAME1");
		}

		return new FloodInfo(OnlineHttp.Text(zone, "FLD_ZONE"), subtype, IsFloodway(subtype), bfe, OnlineHttp.Text(zone, "V_DATUM"),
			panelNumber, suffix, effective, communityId, communityName, DateTime.UtcNow);
	}

	private static bool IsFloodway(string subtype) => subtype != null && subtype.Contains("FLOODWAY", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The zone to report when a point hits several polygons, for example on a boundary: the strictest, as ranked by
	/// <see cref="ZoneRank"/>. Zones of the same rank keep the service's order.
	/// </summary>
	private static JsonElement StrictestZone(List<JsonElement> zones)
	{
		JsonElement best = zones[0];
		int bestRank = ZoneRank(best);
		foreach (JsonElement candidate in zones.Skip(1))
		{
			int rank = ZoneRank(candidate);
			if (rank < bestRank)
			{
				best = candidate;
				bestRank = rank;
			}
		}
		return best;
	}

	/// <summary>
	/// How strict a zone is, strictest first: a floodway (0), a coastal V zone (1), any other A zone (2), a shaded X
	/// zone such as the 0.2% annual chance flood hazard (3), an unshaded X zone (4), and anything else, such as D or
	/// open water (5).
	/// </summary>
	private static int ZoneRank(JsonElement zone)
	{
		string subtype = OnlineHttp.Text(zone, "ZONE_SUBTY");
		if (IsFloodway(subtype))
		{
			return 0;
		}
		string code = (OnlineHttp.Text(zone, "FLD_ZONE") ?? "").Trim().ToUpperInvariant();
		if (IsZone(code, 'V'))
		{
			return 1;
		}
		if (IsZone(code, 'A'))
		{
			return 2;
		}
		if (code == "X")
		{
			// The unshaded X zone's subtype is "AREA OF MINIMAL FLOOD HAZARD" (or blank); every other X subtype is shaded.
			bool unshaded = string.IsNullOrWhiteSpace(subtype) || subtype.Contains("MINIMAL", StringComparison.OrdinalIgnoreCase);
			return unshaded ? 4 : 3;
		}
		return 5;
	}

	/// <summary>
	/// The zone code is an A or V zone: the letter alone, with E, H, O or R after it (AE, AH, AO, AR, VE), A99, or a
	/// numbered zone from older maps (A1 to A30, V1 to V30). "AREA NOT INCLUDED" is not an A zone.
	/// </summary>
	private static bool IsZone(string code, char letter)
	{
		if (code.Length == 0 || code[0] != letter)
		{
			return false;
		}
		string rest = code.Substring(1);
		return rest.Length == 0 || rest is "E" or "H" or "O" or "R" || rest.Length <= 2 && rest.All(char.IsAsciiDigit);
	}

	/// <summary>Runs one layer's point query and returns each feature's attributes.</summary>
	private async Task<List<JsonElement>> QueryAsync(int layer, double latitude, double longitude, CancellationToken ct)
	{
		string point = longitude.ToString("R", CultureInfo.InvariantCulture) + "," + latitude.ToString("R", CultureInfo.InvariantCulture);
		string url = BaseUrl.TrimEnd('/') + "/" + layer.ToString(CultureInfo.InvariantCulture) + "/query"
			+ "?geometry=" + Uri.EscapeDataString(point)
			+ "&geometryType=esriGeometryPoint&inSR=4326&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&f=json";
		using JsonDocument doc = await OnlineHttp.GetJsonAsync(_http, url, "FEMA flood map service", Timeout, ct).ConfigureAwait(false);

		// ArcGIS reports many failures as HTTP 200 with an "error" object.
		JsonElement? error = OnlineHttp.Prop(doc.RootElement, "error");
		if (error != null)
		{
			throw new OnlineServiceException("The FEMA flood map service couldn't answer that request. Try again later.",
				$"GET {url} returned an error: {error.Value.GetRawText()}");
		}
		JsonElement? features = OnlineHttp.Prop(doc.RootElement, "features");
		if (features == null || features.Value.ValueKind != JsonValueKind.Array)
		{
			throw new OnlineServiceException("The FEMA flood map service sent back something SDE couldn't read. Try again later.",
				$"Unexpected response shape for {url}: features is missing.");
		}
		List<JsonElement> list = new List<JsonElement>();
		foreach (JsonElement feature in features.Value.EnumerateArray())
		{
			JsonElement? attributes = OnlineHttp.Prop(feature, "attributes");
			if (attributes != null && attributes.Value.ValueKind == JsonValueKind.Object)
			{
				list.Add(attributes.Value.Clone());
			}
		}
		return list;
	}
}
