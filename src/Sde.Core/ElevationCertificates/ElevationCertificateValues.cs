using System.Globalization;
using System.Text.RegularExpressions;

namespace Sde.Core.ElevationCertificates;

/// <summary>Feet or metres, as an elevation certificate's tick boxes say.</summary>
public enum LengthUnit
{
	Feet,
	Meters,
}

/// <summary>One elevation from an elevation certificate, in the unit the surveyor used.</summary>
public sealed record EcElevation(decimal Value, LengthUnit Unit)
{
	/// <summary>Feet per metre (international foot).</summary>
	public const decimal FeetPerMeter = 1m / 0.3048m;

	/// <summary>The elevation in feet (metres converted and rounded to 0.01 ft).</summary>
	public decimal Feet => Unit == LengthUnit.Meters ? Math.Round(Value * FeetPerMeter, 2, MidpointRounding.AwayFromZero) : Value;
}

/// <summary>
/// The values on an elevation certificate (FEMA Form FF-206-FY-22-152) that SDE can use, typed, with every field read
/// kept as text in <see cref="Fields"/>. Filled from a fillable PDF by the reader in Sde.Interop, or typed into the
/// entry sheet when the PDF is a scan or a print. Item numbers (A5, C2.a) are the form's.
/// </summary>
public sealed class ElevationCertificateValues
{
	// ───────────────────────── Section A: property ─────────────────────────

	/// <summary>A2: the building's street address.</summary>
	public string StreetAddress { get; set; }

	public string City { get; set; }

	/// <summary>Two-letter state code.</summary>
	public string State { get; set; }

	public string ZipCode { get; set; }

	/// <summary>A5: latitude in decimal degrees.</summary>
	public decimal? Latitude { get; set; }

	/// <summary>A5: longitude in decimal degrees (negative west).</summary>
	public decimal? Longitude { get; set; }

	/// <summary>A5: the horizontal datum ticked: "NAD 1927", "NAD 1983" or "WGS 84".</summary>
	public string HorizontalDatum { get; set; }

	/// <summary>A7: the building diagram number, e.g. "1B" or "8".</summary>
	public string BuildingDiagram { get; set; }

	// ───────────────────────── Section B: flood map ─────────────────────────

	/// <summary>B1.a: NFIP community name.</summary>
	public string CommunityName { get; set; }

	/// <summary>B1.b: NFIP community identification number.</summary>
	public string CommunityNumber { get; set; }

	/// <summary>B2: county name.</summary>
	public string County { get; set; }

	/// <summary>B4: FIRM map/panel number.</summary>
	public string PanelNumber { get; set; }

	/// <summary>B5: panel suffix.</summary>
	public string PanelSuffix { get; set; }

	/// <summary>B6: FIRM index date.</summary>
	public DateTime? FirmIndexDate { get; set; }

	/// <summary>B7: FIRM panel effective or revised date.</summary>
	public DateTime? PanelEffectiveDate { get; set; }

	/// <summary>B8: flood zone(s) as written, e.g. "AE".</summary>
	public string FloodZone { get; set; }

	/// <summary>B9: base flood elevation(s) as written (for zone AO, the base flood depth).</summary>
	public string BfeText { get; set; }

	/// <summary>B9 as a number, when it holds exactly one.</summary>
	public decimal? Bfe { get; set; }

	/// <summary>B11: the datum of the BFE: "NGVD 1929", "NAVD 1988", or the other datum written.</summary>
	public string BfeDatum { get; set; }

	// ───────────────────────── Section C: building elevations ─────────────────────────

	/// <summary>C2: the datum of the building elevations: "NGVD 1929", "NAVD 1988", or the other datum written.</summary>
	public string ElevationDatum { get; set; }

	/// <summary>C2: the benchmark used.</summary>
	public string Benchmark { get; set; }

	/// <summary>C2.a: top of bottom floor (including basement, crawlspace or enclosure floor).</summary>
	public EcElevation TopOfBottomFloor { get; set; }

	/// <summary>C2.b: top of the next higher floor.</summary>
	public EcElevation TopOfNextHigherFloor { get; set; }

	/// <summary>C2.c: bottom of the lowest horizontal structural member (V zones).</summary>
	public EcElevation BottomOfLowestMember { get; set; }

	/// <summary>C2.f: lowest adjacent grade (LAG) next to the building.</summary>
	public EcElevation LowestAdjacentGrade { get; set; }

	/// <summary>C2.g: highest adjacent grade (HAG) next to the building.</summary>
	public EcElevation HighestAdjacentGrade { get; set; }

	// ───────────────────────── Everything read ─────────────────────────

	/// <summary>Every field that had a value, by the form's field name, as text (kept with the stored PDF).</summary>
	public Dictionary<string, string> Fields { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>How values were read where the form was ambiguous, in plain English.</summary>
	public List<string> Notes { get; } = new List<string>();

	/// <summary>True when any value SDE can use was found.</summary>
	public bool HasAnyValue =>
		Latitude != null || Longitude != null || !string.IsNullOrWhiteSpace(BuildingDiagram)
		|| !string.IsNullOrWhiteSpace(CommunityName) || !string.IsNullOrWhiteSpace(CommunityNumber)
		|| !string.IsNullOrWhiteSpace(PanelNumber) || PanelEffectiveDate != null || !string.IsNullOrWhiteSpace(FloodZone)
		|| !string.IsNullOrWhiteSpace(BfeText) || TopOfBottomFloor != null || LowestAdjacentGrade != null
		|| !string.IsNullOrWhiteSpace(ElevationDatum);

	// ───────────────────────── The form's field names ─────────────────────────

	/// <summary>
	/// The field names of FEMA's fillable form FF-206-FY-22-152 (its XFA template, read 2026-10-21) that SDE reads.
	/// Check boxes hold "1" when ticked; the state lists hold an index into <see cref="StateCodes"/>.
	/// </summary>
	public static class Names
	{
		public const string StreetAddress = "headerBuildingStreetAddress";
		public const string City = "headerCity";
		public const string State = "headerState";
		public const string Zip = "headerZIP";
		public const string Latitude = "A5Latitude";
		public const string Longitude = "A5Longitude";
		public const string Nad1927 = "A5_1";
		public const string Nad1983 = "A5_2";
		public const string Wgs84 = "A5_3";
		public const string BuildingDiagram = "A7_BuildingDiagramNumber";
		public const string CommunityName = "B1.a_Community_Name";
		public const string CommunityNumber = "B1.b_Community_Number";
		public const string County = "B2_County_Name";
		public const string CommunityState = "B3_State";
		public const string PanelNumber = "B4_Map-Panel_Number";
		public const string PanelSuffix = "B5_Suffix";
		public const string FirmIndexDate = "B6_FIRM_Index_date";
		public const string PanelDate = "B7_FIRM_Effective-Revised_date";
		public const string FloodZone = "B8_Flood_Zones";
		public const string Bfe = "B9_BFE";
		public const string BfeNgvd29 = "b11_1";
		public const string BfeNavd88 = "b11_2";
		public const string BfeOther = "b11_3";
		public const string BfeOtherText = "b11_3_source";
		public const string Benchmark = "C2_Benchmark";
		public const string DatumText = "C2_Vert_Datum";
		public const string Ngvd29 = "c2_1";
		public const string Navd88 = "c2_2";
		public const string OtherDatum = "c2_3";
		public const string OtherDatumText = "c2_3_source";
		public const string C2a = "c2.a_measurement";
		public const string C2aFeet = "c2a_Feet";
		public const string C2aMeters = "c2a_Meters";
		public const string C2b = "c2.b_measurement";
		public const string C2bFeet = "c2b_Feet";
		public const string C2bMeters = "c2b_Meters";
		public const string C2c = "c2.c_measurement";
		public const string C2cFeet = "c2c_Feet";
		public const string C2cMeters = "c2c_Meters";
		public const string C2f = "c2.f_measurement";
		public const string C2fFeet = "c2f_Feet";
		public const string C2fMeters = "c2f_Meters";
		public const string C2g = "c2.g_measurement";
		public const string C2gFeet = "c2g_Feet";
		public const string C2gMeters = "c2g_Meters";

		/// <summary>Every name above.</summary>
		public static readonly IReadOnlyList<string> All = typeof(Names).GetFields()
			.Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()).ToList();

		/// <summary>Names only FEMA's elevation certificate uses: finding a few of them in a form means it is one.</summary>
		public static readonly IReadOnlyList<string> Distinctive = new[]
		{
			BuildingDiagram, CommunityName, CommunityNumber, PanelNumber, FloodZone, Bfe, C2a, C2f, Latitude, Longitude,
		};
	}

	/// <summary>The form's state list, in order: a state field holds the index of its choice ("35" is OH).</summary>
	public static readonly IReadOnlyList<string> StateCodes = new[]
	{
		"AK", "AL", "AZ", "AR", "CA", "CO", "CT", "DC", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME",
		"MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI",
		"SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "--", "AS", "GU", "MP", "PR", "VI",
	};

	/// <summary>
	/// Reads the values from the form's fields (name to text, as the PDF holds them). Blank fields are left out of
	/// <see cref="Fields"/>, and anything ambiguous is explained in <see cref="Notes"/>.
	/// </summary>
	public static ElevationCertificateValues FromFields(IReadOnlyDictionary<string, string> fields)
	{
		ElevationCertificateValues v = new ElevationCertificateValues();
		foreach (KeyValuePair<string, string> f in fields ?? new Dictionary<string, string>())
		{
			string text = f.Value?.Trim();
			if (!string.IsNullOrEmpty(text) && f.Key != null)
			{
				v.Fields[f.Key] = text;
			}
		}
		string Get(string name) => v.Fields.TryGetValue(name, out string s) ? s : null;
		bool Ticked(string name) => IsTicked(Get(name));

		v.StreetAddress = Get(Names.StreetAddress);
		v.City = Get(Names.City);
		v.State = StateCode(Get(Names.State)) ?? StateCode(Get(Names.CommunityState));
		v.ZipCode = Get(Names.Zip);

		v.HorizontalDatum = Ticked(Names.Nad1927) ? "NAD 1927" : Ticked(Names.Nad1983) ? "NAD 1983" : Ticked(Names.Wgs84) ? "WGS 84" : null;
		v.Latitude = Coordinate(Get(Names.Latitude), isLatitude: true, v.State, v.Notes);
		v.Longitude = Coordinate(Get(Names.Longitude), isLatitude: false, v.State, v.Notes);
		v.BuildingDiagram = Get(Names.BuildingDiagram);

		v.CommunityName = Get(Names.CommunityName);
		v.CommunityNumber = Get(Names.CommunityNumber);
		v.County = Get(Names.County);
		v.PanelNumber = Get(Names.PanelNumber);
		v.PanelSuffix = Get(Names.PanelSuffix);
		v.FirmIndexDate = Date(Get(Names.FirmIndexDate));
		v.PanelEffectiveDate = Date(Get(Names.PanelDate));
		v.FloodZone = Get(Names.FloodZone);
		v.BfeText = Get(Names.Bfe);
		List<decimal> bfes = Numbers(v.BfeText);
		v.Bfe = bfes.Count == 1 ? bfes[0] : null;
		if (bfes.Count > 1)
		{
			v.Notes.Add($"B9 holds more than one number ({v.BfeText}).");
		}
		v.BfeDatum = Ticked(Names.BfeNgvd29) ? "NGVD 1929" : Ticked(Names.BfeNavd88) ? "NAVD 1988" : Ticked(Names.BfeOther) ? Get(Names.BfeOtherText) ?? "Other" : null;

		v.ElevationDatum = Ticked(Names.Ngvd29) ? "NGVD 1929" : Ticked(Names.Navd88) ? "NAVD 1988" : Ticked(Names.OtherDatum) ? Get(Names.OtherDatumText) ?? "Other" : Get(Names.DatumText);
		v.Benchmark = Get(Names.Benchmark);
		v.TopOfBottomFloor = Elevation(Get(Names.C2a), Ticked(Names.C2aMeters), "C2.a", v.Notes);
		v.TopOfNextHigherFloor = Elevation(Get(Names.C2b), Ticked(Names.C2bMeters), "C2.b", v.Notes);
		v.BottomOfLowestMember = Elevation(Get(Names.C2c), Ticked(Names.C2cMeters), "C2.c", v.Notes);
		v.LowestAdjacentGrade = Elevation(Get(Names.C2f), Ticked(Names.C2fMeters), "C2.f", v.Notes);
		v.HighestAdjacentGrade = Elevation(Get(Names.C2g), Ticked(Names.C2gMeters), "C2.g", v.Notes);
		return v;
	}

	/// <summary>True for a ticked check box: "1" in FEMA's XFA form; "Yes", "On", "True" or "X" in other forms.</summary>
	public static bool IsTicked(string value) =>
		value != null && (value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("x", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// A latitude or longitude in decimal degrees from what a surveyor typed: "39.712345", "-84.178901", "39.712345° N",
	/// "39°42'44.4\"N", "39 42 44.4 N" or "N 39-42-44.4". S and W (or a minus sign) make it negative. A US longitude
	/// typed without a minus sign or W is read as west (except in Guam and the Northern Mariana Islands, which are east),
	/// with a note. Null when it isn't a coordinate.
	/// </summary>
	public static decimal? Coordinate(string text, bool isLatitude, string state = null, List<string> notes = null)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		string t = text.Trim().ToUpperInvariant();
		bool negative = t.StartsWith('-') || Regex.IsMatch(t, @"(^|[^A-Z])[SW]([^A-Z]|$)");
		bool hemisphere = Regex.IsMatch(t, @"(^|[^A-Z])[NSEW]([^A-Z]|$)");
		List<decimal> parts = Regex.Matches(t, @"\d+(?:\.\d+)?")
			.Select(m => decimal.Parse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture)).ToList();
		if (parts.Count == 0 || parts.Count > 3)
		{
			return null;
		}
		decimal value = parts[0] + (parts.Count > 1 ? parts[1] / 60m : 0m) + (parts.Count > 2 ? parts[2] / 3600m : 0m);
		if (parts.Count > 1 && (parts[1] >= 60m || (parts.Count > 2 && parts[2] >= 60m)))
		{
			return null;
		}
		if (!isLatitude && !negative && !hemisphere && value > 0m && state is not ("GU" or "MP"))
		{
			negative = true;
			notes?.Add($"The longitude ({text.Trim()}) has no minus sign or W, so it was read as west.");
		}
		value = Math.Round(negative ? -value : value, 6, MidpointRounding.AwayFromZero);
		decimal limit = isLatitude ? 90m : 180m;
		return value >= -limit && value <= limit ? value : null;
	}

	/// <summary>Every number in a text ("742.5 ft" gives 742.5; "742, 743" gives two).</summary>
	public static List<decimal> Numbers(string text) =>
		string.IsNullOrWhiteSpace(text)
			? new List<decimal>()
			: Regex.Matches(text, @"-?\d+(?:,\d{3})*(?:\.\d+)?|-?\.\d+")
				.Select(m => decimal.Parse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture)).ToList();

	private static EcElevation Elevation(string text, bool meters, string item, List<string> notes)
	{
		List<decimal> n = Numbers(text);
		if (n.Count != 1)
		{
			if (n.Count > 1)
			{
				notes.Add($"{item} holds more than one number ({text}).");
			}
			return null;
		}
		return new EcElevation(n[0], meters ? LengthUnit.Meters : LengthUnit.Feet);
	}

	/// <summary>A state from the form's list index ("35"), or a two-letter code typed in another form; null for "--" or nothing.</summary>
	private static string StateCode(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string code = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
			? (i >= 0 && i < StateCodes.Count ? StateCodes[i] : null)
			: value.Trim().ToUpperInvariant();
		return code is { Length: 2 } && code != "--" ? code : null;
	}

	/// <summary>A date as XFA stores it (2011-01-06) or as typed in other forms (01/06/2011).</summary>
	private static DateTime? Date(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string[] formats = { "yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "M/d/yyyy", "MM-dd-yyyy", "M-d-yyyy" };
		return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d.Date : null;
	}
}
