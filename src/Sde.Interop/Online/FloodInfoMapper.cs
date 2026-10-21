using System.Globalization;
using System.Text.RegularExpressions;
using Sde.Core;
using Sde.Core.Records;

namespace Sde.Interop.Online;

/// <summary>A value an online lookup suggests for one field. Nothing is saved until the user accepts it.</summary>
/// <param name="Member">The PropertyRecord member it would fill, e.g. FIRMZone.</param>
/// <param name="Label">The field's name as shown to the user.</param>
/// <param name="Value">The value to store (a lookup ID, number, date or text, matching the member's type).</param>
/// <param name="Display">The value as shown to the user.</param>
public sealed record ProposedValue(string Member, string Label, object Value, string Display);

/// <summary>Turns online answers into suggested values for a structure's fields.</summary>
public static class FloodInfoMapper
{
	/// <summary>Suggested FIRM zone, floodway, BFE, panel, panel date, community ID and name. Fields the service left blank or SDE can't match get no suggestion.</summary>
	public static IReadOnlyList<ProposedValue> Propose(FloodInfo info, ReferenceData lookups)
	{
		List<ProposedValue> list = new List<ProposedValue>();
		if (info == null)
		{
			return list;
		}

		LookupRecord zone = FindZone(info, lookups);
		if (zone != null)
		{
			list.Add(new ProposedValue(nameof(PropertyRecord.FIRMZone), "FIRM zone", zone.LookupID, zone.LookupFieldName));
			LookupRecord floodway = FindByName(lookups, LookupTypes.RegulatoryFloodway, info.InFloodway ? "Yes" : "No");
			if (floodway != null)
			{
				list.Add(new ProposedValue(nameof(PropertyRecord.RegulatoryFloodway), "Regulatory floodway", floodway.LookupID, floodway.LookupFieldName));
			}
		}
		if (info.Bfe != null)
		{
			string datum = string.IsNullOrWhiteSpace(info.BfeDatum) ? "" : " (" + info.BfeDatum + ")";
			list.Add(new ProposedValue(nameof(PropertyRecord.BFE), "Base flood elevation", info.Bfe.Value, info.Bfe.Value.ToString("0.##", CultureInfo.InvariantCulture) + " ft" + datum));
		}
		if (!string.IsNullOrWhiteSpace(info.PanelNumber))
		{
			list.Add(new ProposedValue(nameof(PropertyRecord.FIRMPanelNumber), "FIRM panel number", info.PanelNumber, info.PanelNumber));
		}
		if (!string.IsNullOrWhiteSpace(info.PanelSuffix))
		{
			list.Add(new ProposedValue(nameof(PropertyRecord.PanelSuffix), "Panel suffix", info.PanelSuffix, info.PanelSuffix));
		}
		if (info.PanelEffectiveDate != null)
		{
			list.Add(new ProposedValue(nameof(PropertyRecord.FIRMPanelDate), "FIRM panel date", info.PanelEffectiveDate.Value, info.PanelEffectiveDate.Value.ToString("d", CultureInfo.CurrentCulture)));
		}
		if (!string.IsNullOrWhiteSpace(info.CommunityId))
		{
			list.Add(new ProposedValue(nameof(PropertyRecord.NFIPCID), "NFIP community ID", info.CommunityId, info.CommunityId));
		}
		if (!string.IsNullOrWhiteSpace(info.CommunityName))
		{
			list.Add(new ProposedValue(nameof(PropertyRecord.NFIPCommunityName), "NFIP community name", info.CommunityName, info.CommunityName));
		}
		return list;
	}

	/// <summary>Suggested latitude and longitude from a geocoder match, rounded to the 6 decimals SDE stores.</summary>
	public static IReadOnlyList<ProposedValue> Propose(GeocodeMatch match)
	{
		List<ProposedValue> list = new List<ProposedValue>();
		if (match == null)
		{
			return list;
		}
		decimal lat = Math.Round((decimal)match.Latitude, 6);
		decimal lon = Math.Round((decimal)match.Longitude, 6);
		list.Add(new ProposedValue(nameof(PropertyRecord.Latitude), "Latitude", lat, lat.ToString("0.000000", CultureInfo.InvariantCulture)));
		list.Add(new ProposedValue(nameof(PropertyRecord.Longitude), "Longitude", lon, lon.ToString("0.000000", CultureInfo.InvariantCulture)));
		return list;
	}

	private static readonly Regex AWithNumber = new Regex(@"^A\d{1,2}$", RegexOptions.Compiled);
	private static readonly Regex VWithNumber = new Regex(@"^V\d{1,2}$", RegexOptions.Compiled);

	private static LookupRecord FindZone(FloodInfo info, ReferenceData lookups)
	{
		string zone = (info.Zone ?? "").Trim().ToUpperInvariant();
		string subtype = (info.ZoneSubtype ?? "").Trim().ToUpperInvariant();
		if (zone.Length == 0)
		{
			return null;
		}

		string name;
		if (zone == "A" || zone == "AE" || zone == "AH" || zone == "AO" || zone == "AR" || zone == "VE" || zone == "V" || zone == "D")
		{
			name = zone;
		}
		else if (zone == "A99" || AWithNumber.IsMatch(zone))
		{
			name = "A1 thru A99";
		}
		else if (VWithNumber.IsMatch(zone))
		{
			name = "V";
		}
		else if (zone == "X" || zone == "B" || zone == "C"
			|| zone.Contains("0.2 PCT") || zone.Contains("MINIMAL FLOOD HAZARD")
			|| subtype.Contains("0.2 PCT") || subtype.Contains("MINIMAL FLOOD HAZARD"))
		{
			name = "X";
		}
		else
		{
			return null;
		}
		return FindByName(lookups, LookupTypes.FirmZone, name);
	}

	private static LookupRecord FindByName(ReferenceData lookups, int lookupTypeId, string name)
	{
		return lookups.OfType(lookupTypeId).FirstOrDefault(l => string.Equals(l.LookupFieldName, name, StringComparison.OrdinalIgnoreCase));
	}
}
