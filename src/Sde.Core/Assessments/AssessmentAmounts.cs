using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>
/// The two dollar amounts behind an assessment's percentage, as the printed reports choose them. Null means
/// "Not Provided" on the reports.
/// </summary>
public static class AssessmentAmounts
{
	/// <summary>
	/// The building value the percentage was computed against. Port of BLL ReportsService.AddBuildingValueToDataSet:
	/// null when the percent is missing or 0, otherwise the amount for the chosen basis (adjusted tax value is
	/// tax assessed value times factor adjustment, a missing part counting as 0).
	/// </summary>
	public static decimal? BuildingValue(AssessmentRecord a)
	{
		if (a == null || a.PercentDamage is not decimal pct || pct == 0m)
		{
			return null;
		}
		return a.ValueOfBuildingID switch
		{
			LookupIds.ValueAdjustedTax => (a.TaxAssessedValue ?? 0m) * (a.FactorAdjustment ?? 0m),
			LookupIds.ValueComputedAcv => a.ComputedActualCashValue,
			LookupIds.ValueMarketAppraisal => a.MarketAppraisal,
			_ => null,
		};
	}

	/// <summary>
	/// The repair cost behind the percentage. Port of BLL ReportsService.AddRepairCostToDataSet: null when the percent
	/// is missing, 0 when the percent is 0, otherwise the amount for the chosen basis.
	/// </summary>
	public static decimal? RepairCost(AssessmentRecord a)
	{
		if (a == null || a.PercentDamage is not decimal pct)
		{
			return null;
		}
		if (pct == 0m)
		{
			return 0m;
		}
		return a.CostOfRepairsID switch
		{
			LookupIds.RepairContractorEstimate => a.ContractorEstimate,
			LookupIds.RepairCommunityEstimate => a.CommunityEstimate,
			LookupIds.RepairComputedDamages => a.ComputedDamages,
			_ => null,
		};
	}
}
