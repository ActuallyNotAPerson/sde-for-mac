using System.Globalization;
using Sde.Calculation;
using Sde.Calculation.Models;
using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>
/// Connects stored assessments to the FEMA calculation engine.
///
/// In the original, this logic lived in the WPF screens: four tabs each pushed inputs into a shared
/// DataInputOutputModel (ucStructureDamage.RunCalcs, ucCost.RunCalcs, ucElementPercentages.RunCalcs,
/// ucOutputSummary.RunCalcs), the engine ran, results were shown in formatted labels, and Save parsed the
/// labels back into the database. That round trip through "$1,234.57" / "52.3 %" text is what rounded the
/// stored results, so <see cref="ApplyResults"/> applies exactly that rounding (see PORTING-NOTES.md §Save).
/// </summary>
public sealed class AssessmentCalculator
{
	/// <summary>Element rows SDE shows for residential structures, in save order (CalcID = Lookup ID of type 2).</summary>
	public static readonly int[] ResidentialElements = { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

	/// <summary>Manufactured houses show "Skirting / Forms Piers" (16) instead of Foundation (4).</summary>
	public static readonly int[] ManufacturedElements = { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

	/// <summary>Element rows for non-residential structures (ucElementPercentages.ShowHideRows).</summary>
	public static readonly int[] NonResidentialElements = { 4, 5, 6, 12, 13, 15, 17 };

	/// <summary>The original always had 7 additional-adjustment rows on the Cost tab.</summary>
	public const int AdditionalAdjustmentRows = 7;

	private readonly ReferenceData _ref;
	private readonly ElementTables _tables;

	public AssessmentCalculator(ReferenceData referenceData, ElementTables tables = null)
	{
		_ref = referenceData;
		_tables = tables ?? ElementTables.Default;
	}

	/// <summary>The element CalcIDs shown for this structure, in the order the original saved them.</summary>
	public static int[] VisibleElements(PropertyRecord p)
	{
		if (p.StructureTypeID == 1)
		{
			return p.ResidenceTypeID == LookupIds.ManufacturedHouse ? ManufacturedElements : ResidentialElements;
		}
		return NonResidentialElements;
	}

	/// <summary>
	/// Makes the child rows match what the original screens would show and save: one element row per
	/// visible element, one cost-adjustment row per applicable adjustment type, and 7 additional-adjustment rows.
	/// Existing user entries are kept (matched by CalcID / lookup ID / zOrder).
	/// </summary>
	public void NormalizeRows(AssessmentFile file)
	{
		Guid aid = file.Assessment.AssessmentID;

		Dictionary<int, ElementPercentageRecord> existing = file.ElementPercentages
			.GroupBy(e => e.CalcID).ToDictionary(g => g.Key, g => g.First());
		file.ElementPercentages = VisibleElements(file.Property).Select(calcId =>
		{
			if (!existing.TryGetValue(calcId, out ElementPercentageRecord row))
			{
				row = new ElementPercentageRecord { ElementID = Guid.NewGuid(), CalcID = calcId, UserDamage = 0m };
			}
			row.AssessmentID = aid;
			row.ElementName = _ref.LookupName(calcId);
			return row;
		}).ToList();

		Dictionary<int, CostAdjustmentResultRecord> costs = file.CostAdjustments
			.GroupBy(c => c.CostAdjustmentLookupID).ToDictionary(g => g.Key, g => g.First());
		file.CostAdjustments = _ref.CostAdjustmentsFor(file.Property.StructureTypeID, file.Property.ResidenceTypeID).Select(type =>
		{
			if (!costs.TryGetValue(type.LookupID, out CostAdjustmentResultRecord row))
			{
				row = new CostAdjustmentResultRecord { ResultID = Guid.NewGuid(), CostAdjustmentLookupID = type.LookupID };
			}
			row.AssessmentID = aid;
			row.Units = type.LookupValue1;
			return row;
		}).ToList();

		Dictionary<int, AdditionalAdjustmentRecord> extras = file.AdditionalAdjustments
			.Where(a => a.zOrder is >= 1 and <= AdditionalAdjustmentRows)
			.GroupBy(a => a.zOrder.Value).ToDictionary(g => g.Key, g => g.First());
		file.AdditionalAdjustments = Enumerable.Range(1, AdditionalAdjustmentRows).Select(z =>
		{
			if (!extras.TryGetValue(z, out AdditionalAdjustmentRecord row))
			{
				row = new AdditionalAdjustmentRecord { AdditionalAdjustmentID = Guid.NewGuid(), zOrder = z };
			}
			row.AssessmentID = aid;
			return row;
		}).ToList();
	}

	/// <summary>Builds the engine input exactly as the original screens did.</summary>
	public DataInputOutputModel BuildInput(AssessmentFile file)
	{
		PropertyRecord p = file.Property;
		AssessmentRecord a = file.Assessment;
		DataInputOutputModel m = new DataInputOutputModel
		{
			PropertyID = p.PropertyID.ToString(),
			AssessmentID = a.AssessmentID.ToString(),
			// ucStructureDamage.RunCalcs: combo box LookupIDs; an empty combo is LookupID 0.
			StructureTypeID = p.StructureTypeID ?? 0,
			ResidentialData = new ResidentialData
			{
				ResidenceID = p.ResidenceTypeID ?? 0,
				FoundationID = p.FoundationTypeID ?? 0,
				SuperstructureID = p.SuperStructureTypeID ?? 0,
				RoofCoveringID = p.RoofCoveringTypeID ?? 0,
				ExteriorFinishID = p.ExteriorFinishTypeID ?? 0,
				HvacID = p.HVACTypeID ?? 0,
				StoryID = p.StoryTypeID ?? 0,
			},
			CommercialData = new CommercialData
			{
				StoryID = p.StoryTypeID ?? 0,
				StructureUseID = p.StructureUseID ?? 0,
				SprinklerID = p.SprinklerSystem ?? 0,
				ConveyanceID = p.Conveyance ?? 0,
			},
			DamageUndeterminedMessage = a.PhysicalDamage ? _ref.LookupName(a.UndeterminedDamageTypeID) : "",
			// ucCost.RunCalcs: an empty text box is 0.
			BaseCostPerSqFt = a.BaseCost ?? 0m,
			SquareFeet = p.SquareFootage ?? 0m,
			GeographicAdjustment = a.GeographicAdjustment ?? 0m,
			DepreciationRating = a.DepreciationRatingID ?? 0,
			DepreciationPercentage = a.DepreciationValue ?? 0m,
			CostAdjustments = file.CostAdjustments.Select(c => new CostAdjustment
			{
				CostAdjustmentLookupID = c.CostAdjustmentLookupID,
				Quantity = c.Quantity ?? 0m,
				UnitCost = c.UnitCost ?? 0m,
			}).ToList(),
			// The 7 additional rows are always passed; empty quantity/cost stay null (TextEdit.EditValue == null).
			AdditionalAdjustments = file.AdditionalAdjustments.OrderBy(x => x.zOrder).Select(x => new AdditionalAdjustment
			{
				zOrder = x.zOrder ?? 0,
				Quantity = x.Quantity,
				UnitCost = x.UnitCost,
			}).ToList(),
			// ucElementPercentages.RunCalcs: the visible element rows with the user's % damaged.
			ElementPercentages = file.ElementPercentages.Select(e => new ElementPercentage
			{
				CalcID = e.CalcID,
				ElementName = e.ElementName,
				PercentDamaged = e.UserDamage,
				Percentage = e.ElementPercentage,
				ItemCost = ParseMoney(e.ItemCost),
				DamagedValue = ParseMoney(e.DamageValues),
			}).ToList(),
			// ucOutputSummary.RunCalcs: chosen bases; empty optional values are 0.
			ValueOfBuildingID = a.ValueOfBuildingID,
			CostOfRepairsID = a.CostOfRepairsID,
			ProMarketAppraisal = a.MarketAppraisal ?? 0m,
			TaxAssessedValue = a.TaxAssessedValue ?? 0m,
			FactorAdjustment = a.FactorAdjustment ?? 0m,
			ContractorEstimate = a.ContractorEstimate ?? 0m,
			CommunityEstimate = a.CommunityEstimate ?? 0m,
			PercentOfExistingImprovements = a.ExistingImprovementPercent ?? 0m,
		};
		return m;
	}

	/// <summary>
	/// Changing the condition rating, as ucCost.comboBoxDepreciation_SelectedIndexChanged + SetDepreciationValue did:
	/// the % is looked up from the DepreciationValue table (0 shows as blank → null). For rating 389 "Other" the
	/// box becomes editable and the inspector then types a % and an explanation; any other rating clears the explanation.
	/// </summary>
	public void ApplyDepreciationRating(AssessmentFile file, int? ratingLookupId)
	{
		PropertyRecord p = file.Property;
		file.Assessment.DepreciationRatingID = ratingLookupId is > 0 ? ratingLookupId : null;
		decimal pct = _ref.DepreciationPercent(p.StoryTypeID ?? 0, p.StructureUseID ?? 0, p.StructureTypeID ?? 0, file.Assessment.DepreciationRatingID ?? 0);
		file.Assessment.DepreciationValue = pct == 0m ? null : pct;
		if (ratingLookupId != LookupIds.DepreciationOther)
		{
			file.Assessment.DepreciationExplanation = "";
		}
	}

	/// <summary>Normalizes rows, runs FEMA's engine and stores the results. Returns the raw engine output.</summary>
	public DataInputOutputModel Recalculate(AssessmentFile file)
	{
		NormalizeRows(file);
		DataInputOutputModel result = new Calculations(_tables).RunCalulations(BuildInput(file));
		ApplyResults(file, result);
		return result;
	}

	/// <summary>
	/// Writes engine results into the records the way the original Save did: values went through display
	/// labels ("{0:C}" → 2 decimals, "{0:0.0} %" → 1 decimal, both rounding half away from zero) and were
	/// parsed back. Save order in the original was Cost → Output Summary → Elements, so later tabs win.
	/// </summary>
	public static void ApplyResults(AssessmentFile file, DataInputOutputModel r)
	{
		AssessmentRecord a = file.Assessment;

		// Cost tab (ucCost.SaveData + AssessmentService.SaveCostTab)
		a.TotalAdjustments = Money(r.TotalAdjustments);
		a.ReplacementCostSqFt = Money(r.ReplacementCostPerSqFt);
		a.TotalReplacementCost = Money(r.TotalReplacementCost) ?? 0m;
		a.ReplacementCost = Money(r.TotalReplacementCost) ?? 0m;
		foreach (CostAdjustmentResultRecord row in file.CostAdjustments)
		{
			CostAdjustment calc = r.CostAdjustments.FirstOrDefault(x => x.CostAdjustmentLookupID == row.CostAdjustmentLookupID);
			row.TotalCost = Money(calc?.TotalCost);
		}
		foreach (AdditionalAdjustmentRecord row in file.AdditionalAdjustments)
		{
			AdditionalAdjustment calc = r.AdditionalAdjustments.FirstOrDefault(x => x.zOrder == row.zOrder);
			row.TotalCost = Money(calc?.TotalCost);
		}

		// Output Summary tab (ucOutputSummary.SaveData + SaveOutputSummaryTab)
		a.ComputedActualCashValue = Money(r.ComputedActualCashValue);
		a.PercentDamage = Percent(r.PercentDamaged) ?? 0m;
		a.DamageMessage = r.FinalAssessment;
		a.RepairReconstructPercent = Percent(r.RepairReconPercentage);

		// Element Percentages tab, saved last (ucElementPercentages.SaveData + SaveElementsTab)
		a.TotalReplacementCost = Money(r.TotalReplacementCost) ?? 0m;
		a.TotalEstimatedDamages = Money(r.ElementTotalEstimatedDamage) ?? 0m;
		a.ComputedDamages = a.TotalEstimatedDamages;
		foreach (ElementPercentageRecord row in file.ElementPercentages)
		{
			ElementPercentage calc = r.ElementPercentages.FirstOrDefault(x => x.CalcID == row.CalcID);
			if (calc == null)
			{
				continue;
			}
			row.ElementPercentage = calc.Percentage ?? 0m;
			row.ItemCost = MoneyText(calc.ItemCost);
			row.DamageValues = MoneyText(calc.DamagedValue);
		}
	}

	/// <summary>"{0:C}" then parse: 2 decimals, midpoint away from zero; null stays null.</summary>
	public static decimal? Money(decimal? v) => v is decimal d ? Math.Round(d, 2, MidpointRounding.AwayFromZero) : null;

	/// <summary>"{0*100:0.0} %" then parse: a percentage with 1 decimal; null stays null.</summary>
	public static decimal? Percent(decimal? fraction) => fraction is decimal d ? Math.Round(d * 100m, 1, MidpointRounding.AwayFromZero) : null;

	private static string MoneyText(decimal? v) => (Money(v) ?? 0m).ToString("0.00", CultureInfo.InvariantCulture);

	private static decimal ParseMoney(string s) =>
		decimal.TryParse(s?.Replace("$", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : 0m;
}
