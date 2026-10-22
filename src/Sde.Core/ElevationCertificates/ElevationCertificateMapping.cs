using System.Globalization;
using Sde.Core.Assessments;
using Sde.Core.Depth;
using Sde.Core.Records;

namespace Sde.Core.ElevationCertificates;

/// <summary>The structure fields an elevation certificate fills in, and what it couldn't fill in and why.</summary>
public sealed class EcFieldProposal
{
	/// <summary>Values for FieldCatalog keys, as text, ready for <see cref="FieldInput.Apply"/> (with Via = EC import).</summary>
	public List<FieldInputChange> Changes { get; } = new List<FieldInputChange>();

	/// <summary>Plain-English notes for the confirm sheet: conversions made and values left out.</summary>
	public List<string> Notes { get; } = new List<string>();
}

/// <summary>
/// Turns an elevation certificate's values into changes to a structure's fields: A5 to latitude and longitude, B1 to
/// the NFIP community, B4, B5 and B7 to the FIRM panel, B8 to the FIRM zone, B9 to the BFE, C2.a to the elevation of
/// the lowest floor, and the C2 datum to Datum. Every change uses a FieldCatalog key, so it is checked and recorded like
/// any value typed outside the editor. Values SDE has no field for (the diagram number, C2.f and the others) stay
/// with the stored certificate.
/// </summary>
public static class ElevationCertificateMapping
{
	/// <summary>The changes the certificate suggests. Nothing is changed until the caller applies them.</summary>
	public static EcFieldProposal ToFieldChanges(ElevationCertificateValues ec, ReferenceData reference)
	{
		EcFieldProposal p = new EcFieldProposal();
		if (ec == null)
		{
			return p;
		}
		p.Notes.AddRange(ec.Notes);

		// A5: NAD 1927 positions are up to about 100 m off NAD 1983 / WGS 84, and SDE doesn't convert them.
		if (ec.Latitude is decimal lat && ec.Longitude is decimal lon)
		{
			if (ec.HorizontalDatum == "NAD 1927")
			{
				p.Notes.Add("A5 is in NAD 1927, which SDE can't convert. Enter the latitude and longitude by hand.");
			}
			else
			{
				Add(p, nameof(PropertyRecord.Latitude), Number(lat));
				Add(p, nameof(PropertyRecord.Longitude), Number(lon));
			}
		}
		else if (ec.Latitude != null || ec.Longitude != null)
		{
			p.Notes.Add("A5 has only one of latitude and longitude, so neither was used.");
		}

		// B1, B4, B5, B7.
		Add(p, nameof(PropertyRecord.NFIPCommunityName), ec.CommunityName);
		Add(p, nameof(PropertyRecord.NFIPCID), ec.CommunityNumber);
		Add(p, nameof(PropertyRecord.FIRMPanelNumber), ec.PanelNumber);
		Add(p, nameof(PropertyRecord.PanelSuffix), ec.PanelSuffix);
		if (ec.PanelEffectiveDate is DateTime panelDate)
		{
			Add(p, nameof(PropertyRecord.FIRMPanelDate), panelDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
		}

		// B8: one zone only, as FEMA's choice.
		string zone = ec.FloodZone?.Trim();
		bool isAo = false;
		if (!string.IsNullOrEmpty(zone))
		{
			string[] zones = zone.Split(new[] { ',', '/', '&', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (zones.Length > 1 || zone.Contains(" and ", StringComparison.OrdinalIgnoreCase))
			{
				p.Notes.Add($"B8 lists more than one zone ({zone}). Choose the FIRM zone by hand.");
			}
			else if (FirmZones.Find(zone, reference) is LookupRecord choice)
			{
				Add(p, nameof(PropertyRecord.FIRMZone), choice.LookupID.ToString(CultureInfo.InvariantCulture));
				isAo = choice.LookupFieldName == "AO";
			}
			else
			{
				p.Notes.Add($"B8's zone ({zone}) isn't one of SDE's FIRM zones. Choose the FIRM zone by hand.");
			}
		}

		// C2 datum (or B11's when C2 says none), which the BFE and the floor elevation must share.
		string datum = ec.ElevationDatum ?? ec.BfeDatum;
		bool datumsDiffer = ec.ElevationDatum != null && ec.BfeDatum != null && VerticalDatums.Differ(ec.ElevationDatum, ec.BfeDatum);
		if (string.Equals(datum?.Trim(), "Other", StringComparison.OrdinalIgnoreCase))
		{
			p.Notes.Add("The certificate says its datum is another one but doesn't name it. Enter the datum by hand.");
			datum = null;
		}
		if (datum != null)
		{
			Add(p, nameof(PropertyRecord.Datum), VerticalDatums.ShortName(VerticalDatums.Parse(datum)) ?? datum.Trim());
			if (ec.ElevationDatum == null)
			{
				p.Notes.Add("C2 names no datum, so the datum is B11's.");
			}
		}

		// B9: an elevation, except in zone AO where it is a depth.
		if (ec.Bfe is decimal bfe)
		{
			if (isAo)
			{
				p.Notes.Add($"In zone AO, B9 is a base flood depth ({ec.BfeText}), not an elevation, so the BFE was left blank.");
			}
			else if (datumsDiffer)
			{
				p.Notes.Add($"B9's BFE is in {VerticalDatums.Describe(ec.BfeDatum)} but the C2 elevations are in {VerticalDatums.Describe(ec.ElevationDatum)}. Check the conversion, then enter the BFE by hand.");
			}
			else
			{
				Add(p, nameof(PropertyRecord.BFE), Number(bfe));
			}
		}
		else if (!string.IsNullOrWhiteSpace(ec.BfeText) && ElevationCertificateValues.Numbers(ec.BfeText).Count == 0)
		{
			p.Notes.Add($"B9 ({ec.BfeText}) isn't a number, so the BFE was left blank.");
		}

		// C2.a: the top of the bottom floor is SDE's elevation of the lowest floor.
		if (ec.TopOfBottomFloor is EcElevation floor)
		{
			Add(p, nameof(PropertyRecord.FirstFloorElevation), Number(floor.Feet));
			if (floor.Unit == LengthUnit.Meters)
			{
				p.Notes.Add($"C2.a is in metres ({Number(floor.Value)} m), converted to {Number(floor.Feet)} ft.");
			}
		}
		return p;
	}

	private static void Add(EcFieldProposal p, string propertyMember, string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return;
		}
		FieldInfo f = FieldCatalog.Find(FieldModel.Property, propertyMember)
			?? throw new InvalidOperationException("No catalog field for Property." + propertyMember);
		p.Changes.Add(new FieldInputChange(f.Key, value.Trim()));
	}

	private static string Number(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
