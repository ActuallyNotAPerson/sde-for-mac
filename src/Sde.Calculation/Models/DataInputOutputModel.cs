using System.Collections.Generic;

namespace Sde.Calculation.Models;

public class DataInputOutputModel
{
	public string PropertyID { get; set; }

	public string AssessmentID { get; set; }

	public int StructureTypeID { get; set; }

	public decimal? BaseCostPerSqFt { get; set; }

	public decimal? SquareFeet { get; set; }

	public decimal? GeographicAdjustment { get; set; }

	public int? DepreciationRating { get; set; }

	public decimal? DepreciationPercentage { get; set; }

	public int? ValueOfBuildingID { get; set; }

	public int? CostOfRepairsID { get; set; }

	public decimal? ProMarketAppraisal { get; set; }

	public decimal? TaxAssessedValue { get; set; }

	public decimal? FactorAdjustment { get; set; }

	public decimal? ContractorEstimate { get; set; }

	public decimal? CommunityEstimate { get; set; }

	public decimal? PercentOfExistingImprovements { get; set; }

	public ResidentialData ResidentialData { get; set; }

	public CommercialData CommercialData { get; set; }

	public decimal? Cost { get; set; }

	public decimal? TotalAdjustments { get; set; }

	public decimal? TotalReplacementCost { get; set; }

	public decimal? ReplacementCostPerSqFt { get; set; }

	public decimal? ComputedActualCashValue { get; set; }

	public decimal? ComputedDamages { get; set; }

	public decimal? PercentDamaged { get; set; }

	public decimal? AdjTaxAssessedValue { get; set; }

	public decimal? RepairReconPercentage { get; set; }

	public string FinalAssessment { get; set; }

	public string DamageUndeterminedMessage { get; set; }

	public decimal? ElementTotalEstimatedDamage { get; set; }

	public List<CostAdjustment> CostAdjustments { get; set; }

	public List<AdditionalAdjustment> AdditionalAdjustments { get; set; }

	public List<ElementPercentage> ElementPercentages { get; set; }
}
