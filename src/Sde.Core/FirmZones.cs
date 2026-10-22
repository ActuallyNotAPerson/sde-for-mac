using System.Text.RegularExpressions;
using Sde.Core.Records;

namespace Sde.Core;

/// <summary>
/// Turns a flood zone as a map or a form writes it ("AE", "A12", "X" with "0.2 PCT ANNUAL CHANCE FLOOD HAZARD", "VE")
/// into FEMA's FIRM zone choice (Lookup type 5). The NFHL lookup, the elevation certificate reader and the quality
/// checks all use it, so a zone means the same everywhere.
/// </summary>
public static class FirmZones
{
	private static readonly Regex AWithNumber = new Regex(@"^A\d{1,2}$", RegexOptions.Compiled);
	private static readonly Regex VWithNumber = new Regex(@"^V\d{1,2}$", RegexOptions.Compiled);

	/// <summary>
	/// The name of FEMA's FIRM zone choice for a zone code: A, AE, AH, AO, AR, VE, V and D as they are; A99 and the
	/// numbered A zones of older maps as "A1 thru A99"; numbered V zones as "V"; X, B and C (and the X subtypes) as "X".
	/// Null for anything else (blank, "AREA NOT INCLUDED", open water).
	/// </summary>
	public static string ChoiceName(string zone, string subtype = null)
	{
		string z = (zone ?? "").Trim().ToUpperInvariant();
		string s = (subtype ?? "").Trim().ToUpperInvariant();
		if (z.Length == 0)
		{
			return null;
		}
		if (z is "A" or "AE" or "AH" or "AO" or "AR" or "VE" or "V" or "D")
		{
			return z;
		}
		if (z == "A99" || AWithNumber.IsMatch(z))
		{
			return "A1 thru A99";
		}
		if (VWithNumber.IsMatch(z))
		{
			return "V";
		}
		if (z is "X" or "B" or "C"
			|| z.Contains("0.2 PCT") || z.Contains("MINIMAL FLOOD HAZARD")
			|| s.Contains("0.2 PCT") || s.Contains("MINIMAL FLOOD HAZARD"))
		{
			return "X";
		}
		return null;
	}

	/// <summary>FEMA's FIRM zone choice for a zone code (see <see cref="ChoiceName"/>), or null when there is none.</summary>
	public static LookupRecord Find(string zone, ReferenceData reference, string subtype = null)
	{
		string name = ChoiceName(zone, subtype);
		return name == null
			? null
			: reference.OfType(LookupTypes.FirmZone).FirstOrDefault(l => string.Equals(l.LookupFieldName, name, StringComparison.OrdinalIgnoreCase));
	}
}
