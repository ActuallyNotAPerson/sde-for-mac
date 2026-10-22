using Sde.Calculation.Models;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>
/// explain_result: how an assessment's percent damaged was worked out, in plain English. Every number comes from FEMA's
/// engine through <see cref="AssessmentCalculator"/> (run on a copy that is never saved), so nothing here repeats the
/// calculation; it only puts the engine's own figures in order and says what they mean.
/// </summary>
internal static class ResultExplainer
{
	public static ResultExplanation Explain(SdeStore store, AssessmentFile stored, string database, string status)
	{
		ReferenceData r = store.Reference;
		AssessmentRecord before = stored.Assessment;
		decimal? storedPercent = before.PercentDamage;
		string storedMessage = before.DamageMessage;

		// A fresh calculation on the loaded copy (not saved). It fills in the same rounded values a save stores.
		AssessmentFile file = stored;
		DataInputOutputModel raw = store.Calculator.Recalculate(file);
		AssessmentRecord a = file.Assessment;
		PropertyRecord p = file.Property;

		List<AdjustmentLine> adjustments = file.CostAdjustments
			.Where(c => (c.Quantity ?? 0m) != 0m || (c.UnitCost ?? 0m) != 0m)
			.Select(c => new AdjustmentLine(r.LookupName(c.CostAdjustmentLookupID) ?? "Adjustment", c.Quantity, c.UnitCost, c.TotalCost))
			.Concat(file.AdditionalAdjustments
				.Where(x => (x.Quantity ?? 0m) != 0m || (x.UnitCost ?? 0m) != 0m)
				.Select(x => new AdjustmentLine(string.IsNullOrWhiteSpace(x.AdjustmentName) ? "Other adjustment" : x.AdjustmentName.Trim(), x.Quantity, x.UnitCost, x.TotalCost)))
			.ToList();
		decimal? baseCost = AssessmentCalculator.Money(raw.Cost);
		ReplacementCostParts replacement = new ReplacementCostParts(a.BaseCost, p.SquareFootage, a.GeographicAdjustment, baseCost, adjustments,
			a.TotalAdjustments, a.TotalReplacementCost, a.ReplacementCostSqFt);
		DepreciationParts depreciation = new DepreciationParts(r.LookupName(a.DepreciationRatingID), a.DepreciationValue,
			a.DepreciationRatingID == LookupIds.DepreciationOther ? Describe.Shorten(a.DepreciationExplanation) : null);
		List<ElementDamage> elements = Describe.Elements(file);
		decimal? adjustedTax = AssessmentCalculator.Money(raw.AdjTaxAssessedValue);
		Amount building = new Amount(r.LookupName(a.ValueOfBuildingID) ?? "No building value basis chosen", a.ValueOfBuildingID switch
		{
			LookupIds.ValueComputedAcv => a.ComputedActualCashValue,
			LookupIds.ValueMarketAppraisal => a.MarketAppraisal,
			LookupIds.ValueAdjustedTax => adjustedTax,
			_ => null,
		});
		Amount repair = new Amount(r.LookupName(a.CostOfRepairsID) ?? "No repair cost basis chosen", a.CostOfRepairsID switch
		{
			LookupIds.RepairComputedDamages => a.ComputedDamages,
			LookupIds.RepairContractorEstimate => a.ContractorEstimate,
			LookupIds.RepairCommunityEstimate => a.CommunityEstimate,
			_ => null,
		});
		ResultSummary result = Describe.Result(a);
		DeterminationClass state = Determinations.Classify(a);

		List<string> steps = new List<string>();
		// 1. Replacement cost.
		if (baseCost is decimal bc && bc != 0m)
		{
			steps.Add($"Base replacement cost: {Describe.Money(a.BaseCost)} per sq ft × {Describe.Number(p.SquareFootage)} sq ft × geographic adjustment {Describe.Number(a.GeographicAdjustment, "0.###")} = {Describe.Money(baseCost)}.");
		}
		else
		{
			steps.Add($"Base replacement cost is {Describe.Money(baseCost ?? 0m)}: the base cost per sq ft ({Describe.Money(a.BaseCost)}), the square footage ({Describe.Number(p.SquareFootage)}) or the geographic adjustment ({Describe.Number(a.GeographicAdjustment, "0.###")}) is missing or zero.");
		}
		if (adjustments.Count > 0)
		{
			string lines = string.Join("; ", adjustments.Select(x => $"{Describe.Shorten(x.Name, 60)} {Describe.Number(x.Quantity)} × {Describe.Money(x.UnitCost)} = {Describe.Money(x.Total)}"));
			steps.Add($"Adjustments: {lines}. Total adjustments: {Describe.Money(a.TotalAdjustments)}.");
		}
		steps.Add($"Total replacement cost: {Describe.Money(baseCost ?? 0m)} + {Describe.Money(a.TotalAdjustments ?? 0m)} = {Describe.Money(a.TotalReplacementCost)}.");

		// 2. Depreciation and actual cash value.
		if (a.DepreciationValue is decimal dep && dep != 0m)
		{
			string why = a.DepreciationRatingID == LookupIds.DepreciationOther
				? $"the inspector entered {Describe.Percent(dep)} (\"Other\" condition)"
				: $"condition rating \"{depreciation.Rating}\" gives {Describe.Percent(dep)} (FEMA's depreciation table)";
			steps.Add($"Depreciation: {why}, so the actual cash value is {Describe.Money(a.TotalReplacementCost)} × (100% - {Describe.Percent(dep)}) = {Describe.Money(a.ComputedActualCashValue)}.");
		}
		else
		{
			steps.Add($"No depreciation is entered, so the actual cash value equals the replacement cost: {Describe.Money(a.ComputedActualCashValue)}.");
		}

		// 3. Damage by element.
		int damaged = elements.Count(e => e.PercentDamaged != 0m);
		steps.Add($"Damage by element: each element is a fixed share of the replacement cost (FEMA's tables for this kind of building), times the % damaged the inspector entered. {damaged} of {elements.Count} elements have damage; total estimated damage: {Describe.Money(a.TotalEstimatedDamages)}.");

		// 4. The two amounts and the percentage.
		steps.Add(building.Value is decimal
			? $"Building value basis: {building.Basis}, {Describe.Money(building.Value)}" + (a.ValueOfBuildingID == LookupIds.ValueAdjustedTax
				? $" (tax assessed value {Describe.Money(a.TaxAssessedValue)} × factor {Describe.Number(a.FactorAdjustment, "0.##")})."
				: ".")
			: $"Building value basis: {building.Basis}, with no amount entered.");
		steps.Add(repair.Value is decimal
			? $"Repair cost basis: {repair.Basis}, {Describe.Money(repair.Value)}."
			: $"Repair cost basis: {repair.Basis}, with no amount entered.");

		switch (state)
		{
			case DeterminationClass.Undetermined:
				steps.Add($"Damage is marked undetermined (\"{Describe.Shorten(a.DamageMessage, 120)}\"), so SDE reports that reason and 0% instead of a percentage.");
				break;
			case DeterminationClass.Incomplete:
				steps.Add("There's no percentage yet: the building value for the chosen basis is missing or zero, so FEMA's calculation stops at \"Data Entry Incomplete\".");
				break;
			default:
				bool capped = raw.PercentDamaged == 1m && building.Value is decimal bv && repair.Value is decimal rv && rv > bv;
				steps.Add($"Percent damaged = repair cost ÷ building value = {Describe.Money(repair.Value)} ÷ {Describe.Money(building.Value)} = {Describe.Percent(a.PercentDamage)}" + (capped ? " (capped at 100%)." : "."));
				string threshold = Determinations.DefaultSubstantialPercent.ToString("0.##", Describe.Us) + "%";
				steps.Add(state == DeterminationClass.Substantial
					? $"{Describe.Percent(a.PercentDamage)} is at or above the {threshold} threshold, so the structure is Substantially Damaged under NFIP rules."
					: $"{Describe.Percent(a.PercentDamage)} is below the {threshold} threshold, so the structure is Not Substantially Damaged."
						+ (state == DeterminationClass.Close ? $" It is within 10 points of the threshold, so SDE shows it as Close: worth double-checking the inputs." : ""));
				break;
		}

		string note = null;
		if (storedPercent != a.PercentDamage || storedMessage != a.DamageMessage)
		{
			note = $"The stored result ({Describe.Percent(storedPercent)}, {storedMessage ?? Determinations.IncompleteMessage}) differs from a fresh calculation, which this explanation uses. Opening the assessment in SDE and saving it updates the stored result.";
		}

		return new ResultExplanation(database, a.AssessmentID.ToString(), Describe.Address(p, r), status, result,
			state == DeterminationClass.Substantial, building, repair, replacement, depreciation, a.ComputedActualCashValue,
			elements, a.TotalEstimatedDamages, steps, note);
	}
}
