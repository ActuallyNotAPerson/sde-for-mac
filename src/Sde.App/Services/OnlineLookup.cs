using System.Globalization;
using System.Net.Http.Headers;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Interop.Online;

namespace Sde.App.Services;

/// <summary>Where a suggested value came from.</summary>
public enum OnlineSource
{
	CensusGeocoder,
	FemaNfhl,
}

/// <summary>A suggested value and the service that gave it.</summary>
public sealed record OnlineProposal(ProposedValue Value, OnlineSource Source);

/// <summary>What the online look-up found for one structure. Nothing here has been saved.</summary>
/// <param name="Proposals">Suggested values, coordinates first.</param>
/// <param name="Note">Why something wasn't found (no address match, no mapped flood zone), or null.</param>
/// <param name="RetrievedOn">When the services were asked (UTC).</param>
public sealed record OnlineLookupResult(IReadOnlyList<OnlineProposal> Proposals, string Note, DateTime RetrievedOn);

/// <summary>
/// The online auto-fill for a structure: when it has an address but no coordinates, the Census geocoder first, then
/// FEMA's National Flood Hazard Layer at the coordinates. Used by the editor's "Look up online" and by the bulk look-up
/// on Structures. Failures throw <see cref="OnlineServiceException"/> with a plain-English message.
/// </summary>
public sealed class OnlineLookup
{
	private readonly HttpClient _http;
	private readonly ReferenceData _reference;

	public OnlineLookup(HttpClient http, ReferenceData reference)
	{
		_http = http;
		_reference = reference;
	}

	/// <summary>How long to wait for each answer.</summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

	public async Task<OnlineLookupResult> LookupAsync(PropertyRecord property, CancellationToken ct)
	{
		List<OnlineProposal> proposals = new List<OnlineProposal>();
		DateTime asked = DateTime.UtcNow;
		double? latitude = property.Latitude is decimal la ? (double)la : null;
		double? longitude = property.Longitude is decimal lo ? (double)lo : null;
		if (latitude == null || longitude == null)
		{
			string address = OneLineAddress(property, _reference);
			if (address == null)
			{
				return new OnlineLookupResult(proposals, "Enter the structure's street address (or its latitude and longitude) first.", asked);
			}
			CensusGeocoder geocoder = new CensusGeocoder(_http) { Timeout = Timeout };
			GeocodeMatch match = await geocoder.GeocodeAsync(address, ct).ConfigureAwait(false);
			if (match == null)
			{
				return new OnlineLookupResult(proposals, $"The Census geocoder didn't find \"{address}\". Check the address, or enter the latitude and longitude.", asked);
			}
			proposals.AddRange(FloodInfoMapper.Propose(match).Select(p => new OnlineProposal(p, OnlineSource.CensusGeocoder)));
			latitude = match.Latitude;
			longitude = match.Longitude;
		}
		NfhlClient nfhl = new NfhlClient(_http) { Timeout = Timeout };
		FloodInfo flood = await nfhl.LookupAsync(latitude.Value, longitude.Value, ct).ConfigureAwait(false);
		if (flood == null)
		{
			return new OnlineLookupResult(proposals, "FEMA's flood map has no flood zone mapped at this location.", asked);
		}
		proposals.AddRange(FloodInfoMapper.Propose(flood, _reference).Select(p => new OnlineProposal(p, OnlineSource.FemaNfhl)));
		return new OnlineLookupResult(proposals, null, flood.RetrievedOn);
	}

	/// <summary>"123 River Street, Exampleville, OH 43702", or null when there is no street name to search for.</summary>
	public static string OneLineAddress(PropertyRecord p, ReferenceData reference)
	{
		string street = string.Join(" ", new[] { p.StreetNumber, p.StreetName, reference.LookupName(p.StreetSuffixID) }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
		if (string.IsNullOrWhiteSpace(p.StreetName))
		{
			return null;
		}
		string state = reference.States.FirstOrDefault(s => s.StateID == p.StateID)?.StateAbb;
		string place = string.Join(" ", new[] { state, p.ZipCode }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
		return string.Join(", ", new[] { street, p.City?.Trim(), place }.Where(s => !string.IsNullOrWhiteSpace(s)));
	}

	/// <summary>The history event recorded when looked-up values are applied (Flag 3: NFHL may differ from the effective FIRM).</summary>
	public static string EventLabel(bool fromNfhl, bool fromGeocoder, DateTime retrievedOn)
	{
		string date = retrievedOn.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
		if (fromNfhl && fromGeocoder)
		{
			return $"Flood data from FEMA NFHL {date}, coordinates from the Census geocoder";
		}
		return fromNfhl ? $"Flood data from FEMA NFHL {date}" : $"Coordinates from the Census geocoder {date}";
	}

	/// <summary>The catalog field a proposal fills (all are structure fields).</summary>
	public static FieldInfo FieldFor(ProposedValue p) => FieldCatalog.Find(FieldModel.Property, p.Member);

	/// <summary>The change that applies a proposal through the editor's rules.</summary>
	public static FieldChange Change(ProposedValue p) => new FieldChange(FieldFor(p)?.Key ?? "Property." + p.Member, p.Value);

	/// <summary>The app's shared client for the online services (one per app, as HttpClient is meant to be used).</summary>
	public static readonly Lazy<HttpClient> SharedClient = new Lazy<HttpClient>(() =>
	{
		HttpClient client = new HttpClient();
		client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SDE-for-Mac", "2.0"));
		return client;
	});

	/// <summary>True when the saved value is empty (a proposal for it is ticked by default).</summary>
	public static bool IsEmpty(object value) => value == null || value is string { Length: 0 } || (value is string s && string.IsNullOrWhiteSpace(s));

	/// <summary>True when a proposal would leave the field as it is.</summary>
	public static bool IsSame(FieldInfo field, object current, ProposedValue p) =>
		field != null && FieldCatalog.Canonical(current) == FieldCatalog.Canonical(FieldCatalog.Convert(p.Value, field.RecordProperty.PropertyType));
}
