using System.Globalization;
using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>
/// Applies the "Enter Default Data" values to new records.
/// Ports: PropertySelect "Preload using default values" (decomp/wpfSDE/wpfSDE.Assessment/PropertySelect.xaml.cs:94-115)
/// and BLL PropertyService.CreateNewInspection (decomp/BLL/BLL.Service/PropertyService.cs:177-204).
/// Fixed on the way: the geographic adjustment default was truncated to a whole number (Convert.ToInt32),
/// and unparseable default text crashed the original; here it is skipped.
/// </summary>
public static class DefaultValues
{
	public static void ApplyToProperty(DefaultValuesRecord d, PropertyRecord p)
	{
		p.City = d.City;
		p.ZipCode = d.Zip?.Replace(".", "");
		p.YearOfConstruction = Int(d.YearOfConstruction);
		p.NFIPCommunityName = d.NFIPCommunityName;
		p.NFIPCID = d.NFIPCommunityID ?? "";
		p.BFE = Dec(d.BFE);
		p.FIRMPanelNumber = d.FirmPanelNumber;
		p.StateID = d.State;
		p.CountyID = d.County;
		p.FIRMZone = d.FirmZone;
		p.PanelSuffix = d.Suffix;
		p.RegulatoryFloodway = d.RegulatoryFloodway;
		p.CommunityInformation = d.CommunityUse;
		if (Date(d.DateOfFirmPanel) is DateTime firm)
		{
			p.FIRMPanelDate = firm;
		}
	}

	public static void ApplyToAssessment(DefaultValuesRecord d, AssessmentFile file)
	{
		AssessmentRecord a = file.Assessment;
		a.DateDamageOccurred = Date(d.DateDamagedOccurred);
		a.CauseofDamageID = Int(d.CauseOfDamage);
		a.DurationOfFlood = Int(d.DurationOfFlood);
		a.DurationOfFloodUnitsID = Int(d.DurationOfFloodUnit);
		a.GeographicAdjustment = Dec(d.LocalMultiplier);
		a.CostDataDate = Date(d.CostDataDate);
		a.InspectorName = d.InspectedBy;
		a.InspectorPhoneNumber = d.InspectedPhone;
		a.DateofInspection = Date(d.DateOfInspection);
		a.CostData = d.CostDataRef;
		a.BaseCost = d.BaseCost;
		file.Property.Datum = d.Datum;
	}

	private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;

	private static decimal? Dec(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : null;

	private static DateTime? Date(string s) =>
		DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d
		: DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out d) ? d : null;
}
