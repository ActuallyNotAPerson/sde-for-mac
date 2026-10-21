using System.Globalization;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Reports.Internal;

namespace Sde.Reports.Letters;

/// <summary>
/// Things a letter needs that are not in the structure or assessment: who is writing, the date, the review status,
/// and (for a substantial improvement letter) the project and its SI calculation. Kept in Settings by the app.
/// </summary>
public sealed class LetterContext
{
	/// <summary>The community's name as it should appear in letters (default: the structure's NFIP community name).</summary>
	public string CommunityName { get; init; }

	/// <summary>The department sending the letter, e.g. "Floodplain Management Office".</summary>
	public string OfficeName { get; init; }

	/// <summary>The office's mailing address (may have several lines).</summary>
	public string OfficeAddress { get; init; }

	public string OfficePhone { get; init; }

	public string OfficeEmail { get; init; }

	/// <summary>The floodplain administrator or other official who signs the letter.</summary>
	public string OfficialName { get; init; }

	public string OfficialTitle { get; init; }

	/// <summary>The letter date (default: today).</summary>
	public DateTime? Today { get; init; }

	/// <summary>The assessment's review status, e.g. "Final".</summary>
	public string Status { get; init; }

	/// <summary>The improvement project, for a substantial improvement letter.</summary>
	public ImprovementProjectRecord Project { get; init; }

	/// <summary>The SI calculation, for a substantial improvement letter.</summary>
	public SiResult Improvement { get; init; }

	/// <summary>More fields, or replacements for built-in ones (for a community's own templates).</summary>
	public IReadOnlyDictionary<string, string> Extra { get; init; }
}

/// <summary>
/// The fields a letter template can use, and their values for one structure. Every field is always present (blank
/// when there is nothing to put in it), so a template only reports unknown placeholders for names that are not fields.
/// <list type="table">
/// <listheader><term>Field</term><description>Value</description></listheader>
/// <item><term>Today</term><description>The letter date, e.g. October 20, 2026.</description></item>
/// <item><term>CommunityName, NFIPCommunityID</term><description>The community (from Settings, else the structure's NFIP community).</description></item>
/// <item><term>OfficeName, OfficeAddress, OfficePhone, OfficeEmail, OfficialName, OfficialTitle</term><description>Who is writing (from Settings).</description></item>
/// <item><term>OwnerName, OwnerFirstName, OwnerLastName</term><description>The owner, "First Last".</description></item>
/// <item><term>MailingName, MailingCareOf, MailingStreet, MailingCityStateZip, MailingAddress</term><description>Where the letter goes; the structure's address when no mailing address is entered. MailingAddress has one line each for care-of, street, and city, state and ZIP.</description></item>
/// <item><term>PropertyAddress, PropertyCity, PropertyState, PropertyZip, PropertyCityStateZip, ParcelNumber</term><description>The damaged or improved structure.</description></item>
/// <item><term>FloodZone, FIRMPanel, BFE</term><description>Flood map information.</description></item>
/// <item><term>DamageDate, InspectionDate, InspectorName, CauseOfDamage</term><description>The assessment.</description></item>
/// <item><term>PercentDamaged, DamageDetermination, DeterminationWording</term><description>FEMA's result as stored, e.g. "52.3%", "Substantially Damaged", "is substantially damaged".</description></item>
/// <item><term>BuildingValue, BuildingValueBasis, RepairCost, RepairCostBasis</term><description>The amounts and bases behind the percentage.</description></item>
/// <item><term>Status</term><description>The review status.</description></item>
/// <item><term>ProjectDescription, PermitNumber, ProjectDate, ImprovementCost, MarketValue, ImprovementPercent, SIThreshold, LookbackPeriod, SIDetermination, SIDeterminationWording</term><description>Substantial improvement.</description></item>
/// </list>
/// docs/LETTERS.md lists the same fields for users.
/// </summary>
public static class LetterFields
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>Every built-in field name, in the order they are listed to users.</summary>
	public static readonly IReadOnlyList<string> Names = new[]
	{
		"Today", "CommunityName", "NFIPCommunityID",
		"OfficeName", "OfficeAddress", "OfficePhone", "OfficeEmail", "OfficialName", "OfficialTitle",
		"OwnerName", "OwnerFirstName", "OwnerLastName",
		"MailingName", "MailingCareOf", "MailingStreet", "MailingCityStateZip", "MailingAddress",
		"PropertyAddress", "PropertyCity", "PropertyState", "PropertyZip", "PropertyCityStateZip", "ParcelNumber",
		"FloodZone", "FIRMPanel", "BFE",
		"DamageDate", "InspectionDate", "InspectorName", "CauseOfDamage",
		"PercentDamaged", "DamageDetermination", "DeterminationWording",
		"BuildingValue", "BuildingValueBasis", "RepairCost", "RepairCostBasis",
		"Status",
		"ProjectDescription", "PermitNumber", "ProjectDate", "ImprovementCost", "MarketValue", "ImprovementPercent",
		"SIThreshold", "LookbackPeriod", "SIDetermination", "SIDeterminationWording",
	};

	/// <summary>The fields for an assessment (and its structure).</summary>
	public static IReadOnlyDictionary<string, string> For(AssessmentFile file, ReferenceData reference, LetterContext context = null)
	{
		ArgumentNullException.ThrowIfNull(file);
		return For(file.Property, file.Assessment, reference, context);
	}

	/// <summary>
	/// The fields for a structure. <paramref name="assessment"/> may be null (a substantial improvement letter); its
	/// fields are then blank. Only reads the records: nothing is changed.
	/// </summary>
	public static IReadOnlyDictionary<string, string> For(PropertyRecord property, AssessmentRecord assessment, ReferenceData reference, LetterContext context = null)
	{
		ArgumentNullException.ThrowIfNull(property);
		ArgumentNullException.ThrowIfNull(reference);
		context ??= new LetterContext();
		AssessmentRecord a = assessment ?? new AssessmentRecord();
		AssessmentView v = new AssessmentView(new AssessmentFile { Property = property, Assessment = a }, reference);
		PropertyRecord p = property;
		Dictionary<string, string> f = new(StringComparer.OrdinalIgnoreCase);

		f["Today"] = LongDate(context.Today ?? DateTime.Today);
		f["CommunityName"] = Clean(context.CommunityName).Length > 0 ? Clean(context.CommunityName) : Clean(p.NFIPCommunityName);
		f["NFIPCommunityID"] = Clean(p.NFIPCID);
		f["OfficeName"] = Clean(context.OfficeName);
		f["OfficeAddress"] = Clean(context.OfficeAddress);
		f["OfficePhone"] = Clean(context.OfficePhone);
		f["OfficeEmail"] = Clean(context.OfficeEmail);
		f["OfficialName"] = Clean(context.OfficialName);
		f["OfficialTitle"] = Clean(context.OfficialTitle);

		f["OwnerName"] = Fmt.Join(" ", p.OwnerFirstName, p.OwnerLastName);
		f["OwnerFirstName"] = Clean(p.OwnerFirstName);
		f["OwnerLastName"] = Clean(p.OwnerLastName);

		// The structure's own address when no mailing address was entered (the owner lives there).
		bool hasMailing = !string.IsNullOrWhiteSpace(p.MailingStreetName) || !string.IsNullOrWhiteSpace(p.MailingStreetNumber);
		string mailingName = Fmt.Join(" ", p.MailingOwnerFirstName, p.MailingOwnerLastName);
		f["MailingName"] = mailingName.Length > 0 ? mailingName : f["OwnerName"];
		f["MailingCareOf"] = Clean(p.MailingCareOf);
		f["MailingStreet"] = hasMailing ? v.MailingStreetAddress : v.StreetAddress;
		f["MailingCityStateZip"] = hasMailing
			? CityStateZip(p.MailingCity, reference.StateAbbreviation(p.MailingStateID) ?? v.MailingState, Fmt.Zip(p.MailingZipCode))
			: CityStateZip(p.City, reference.StateAbbreviation(p.StateID) ?? v.State, v.Zip);
		f["MailingAddress"] = string.Join("\n", new[] { f["MailingCareOf"], f["MailingStreet"], f["MailingCityStateZip"] }.Where(s => s.Length > 0));

		f["PropertyAddress"] = v.StreetAddress;
		f["PropertyCity"] = v.City.Trim();
		f["PropertyState"] = v.State;
		f["PropertyZip"] = v.Zip;
		f["PropertyCityStateZip"] = CityStateZip(p.City, reference.StateAbbreviation(p.StateID) ?? v.State, v.Zip);
		f["ParcelNumber"] = Clean(p.ParcelNumber);
		f["FloodZone"] = v.Name(p.FIRMZone);
		f["FIRMPanel"] = Fmt.Join("", p.FIRMPanelNumber, p.PanelSuffix);
		f["BFE"] = Fmt.Number2(p.BFE);

		f["DamageDate"] = LongDate(a.DateDamageOccurred);
		f["InspectionDate"] = LongDate(a.DateofInspection);
		f["InspectorName"] = Clean(a.InspectorName);
		f["CauseOfDamage"] = a.CauseofDamageID == LookupIds.CauseOther && !string.IsNullOrWhiteSpace(a.CauseOfDamageDescription)
			? a.CauseOfDamageDescription.Trim()
			: v.Name(a.CauseofDamageID);
		f["PercentDamaged"] = Fmt.Percent1(a.PercentDamage);
		f["DamageDetermination"] = v.DamageMessage;
		f["DeterminationWording"] = v.Determination switch
		{
			Determination.Substantial => "is substantially damaged",
			Determination.NotSubstantial => "is not substantially damaged",
			_ => "has not yet been determined to be substantially damaged or not",
		};
		f["BuildingValue"] = assessment == null ? "" : Fmt.Money(CumulativeDamage.BuildingValue(a));
		f["BuildingValueBasis"] = assessment == null ? "" : v.BasisValue;
		f["RepairCost"] = assessment == null ? "" : Fmt.Money(CumulativeDamage.RepairCost(a));
		f["RepairCostBasis"] = assessment == null ? "" : v.BasisCost;
		f["Status"] = Clean(context.Status);

		ImprovementProjectRecord project = context.Project ?? context.Improvement?.Projects.FirstOrDefault(x => x.IsCurrent)?.Project;
		SiResult si = context.Improvement;
		f["ProjectDescription"] = Clean(project?.Description);
		f["PermitNumber"] = Clean(project?.PermitNumber);
		f["ProjectDate"] = LongDate(project?.ProjectDate);
		f["ImprovementCost"] = si != null ? Fmt.Money(si.CostCounted) : Fmt.Money(project?.Cost);
		f["MarketValue"] = Fmt.Money(si?.MarketValue);
		f["ImprovementPercent"] = Fmt.Percent1(si?.Percent);
		f["SIThreshold"] = si != null ? si.ThresholdPercent.ToString("0.##", Us) + "%" : "";
		f["LookbackPeriod"] = si == null ? "" : si.LookbackYears switch
		{
			0 => "this project only",
			1 => "1 year",
			_ => si.LookbackYears + " years",
		};
		f["SIDetermination"] = si?.ResultText ?? "";
		f["SIDeterminationWording"] = si == null ? ""
			: !si.CanDetermine ? "cannot be determined until the market value of the structure is known"
			: si.IsSubstantialImprovement ? "is a substantial improvement"
			: "is not a substantial improvement";

		foreach (KeyValuePair<string, string> kv in context.Extra ?? new Dictionary<string, string>())
		{
			if (!string.IsNullOrWhiteSpace(kv.Key))
			{
				f[kv.Key.Trim()] = kv.Value ?? "";
			}
		}
		return f;
	}

	private static string Clean(string s) => s?.Trim() ?? "";

	private static string LongDate(DateTime? d) => d is DateTime x ? x.ToString("MMMM d, yyyy", Us) : "";

	/// <summary>"City, ST 12345-6789", leaving out what is missing.</summary>
	private static string CityStateZip(string city, string state, string zip) => Fmt.Join(" ", Fmt.Join(", ", city, state), zip);
}
