using Sde.Calculation.Models;

namespace Sde.Calculation;

/// <summary>
/// FEMA SDE 3.0 damage calculation, ported from decomp/CalculationEngine/CalculationEngine/Calculations.cs.
///
/// Every arithmetic step below is the original code, unchanged, including its quirks
/// (see docs/PORTING-NOTES.md §Calculation). The only changes:
///   • data comes from <see cref="ElementTables"/> (embedded, indexed) instead of JSON\*.json beside the .exe;
///   • a lookup ID missing from Lookup.json throws a descriptive exception instead of NullReferenceException.
/// tests/Sde.Tests/CalculationParityTests.cs compares this class with the untouched original on every
/// building configuration FEMA's tables define.
/// </summary>
public class Calculations
{
	private readonly ElementTables _tables;

	private DataInputOutputModel _DataInputOutputModel;

	public Calculations()
		: this(ElementTables.Default)
	{
	}

	public Calculations(ElementTables tables)
	{
		_tables = tables ?? throw new ArgumentNullException(nameof(tables));
	}

	/// <summary>Runs the full calculation. Mutates and returns <paramref name="data"/>, like the original.</summary>
	public DataInputOutputModel Run(DataInputOutputModel data) => RunCalulations(data);

	/// <summary>Original entry point name (sic), kept for traceability.</summary>
	public DataInputOutputModel RunCalulations(DataInputOutputModel data)
	{
		_DataInputOutputModel = data;
		CostCalculation();
		TotalAdjustmentsCalculations();
		TotalReplacementCost();
		ReplacementCostPerSquareFoot();
		RunElementPercentCalcs();
		ComputedActualCashValue();
		AdjustedTaxAssessedValue();
		RepairReconstructionPercentage();
		PercentDamaged();
		return data;
	}

	private void CostCalculation()
	{
		_DataInputOutputModel.Cost = _DataInputOutputModel.BaseCostPerSqFt * _DataInputOutputModel.SquareFeet * _DataInputOutputModel.GeographicAdjustment;
	}

	private void TotalAdjustmentsCalculations()
	{
		foreach (CostAdjustment costAdjustment in _DataInputOutputModel.CostAdjustments)
		{
			costAdjustment.TotalCost = costAdjustment.Quantity * costAdjustment.UnitCost;
		}
		_DataInputOutputModel.TotalAdjustments = _DataInputOutputModel.CostAdjustments.Select((CostAdjustment x) => x.Quantity * x.UnitCost).Sum();
		foreach (AdditionalAdjustment additionalAdjustment in _DataInputOutputModel.AdditionalAdjustments)
		{
			additionalAdjustment.TotalCost = additionalAdjustment.Quantity * additionalAdjustment.UnitCost;
		}
		DataInputOutputModel dataInputOutputModel = _DataInputOutputModel;
		dataInputOutputModel.TotalAdjustments += _DataInputOutputModel.AdditionalAdjustments.Select((AdditionalAdjustment x) => x.Quantity * x.UnitCost).Sum();
	}

	private void TotalReplacementCost()
	{
		_DataInputOutputModel.TotalReplacementCost = _DataInputOutputModel.Cost + _DataInputOutputModel.TotalAdjustments;
	}

	private void ReplacementCostPerSquareFoot()
	{
		decimal? squareFeet = _DataInputOutputModel.SquareFeet;
		if (squareFeet.GetValueOrDefault() > 0m && squareFeet.HasValue)
		{
			_DataInputOutputModel.ReplacementCostPerSqFt = _DataInputOutputModel.TotalReplacementCost / _DataInputOutputModel.SquareFeet;
		}
	}

	private void ComputedActualCashValue()
	{
		DataInputOutputModel dataInputOutputModel = _DataInputOutputModel;
		decimal? totalReplacementCost = _DataInputOutputModel.TotalReplacementCost;
		decimal value = 1;
		decimal? num = _DataInputOutputModel.DepreciationPercentage / (decimal?)100;
		dataInputOutputModel.ComputedActualCashValue = totalReplacementCost * ((decimal?)value - num);
	}

	private void AdjustedTaxAssessedValue()
	{
		_DataInputOutputModel.AdjTaxAssessedValue = _DataInputOutputModel.TaxAssessedValue * _DataInputOutputModel.FactorAdjustment;
	}

	private void RepairReconstructionPercentage()
	{
		decimal? computedActualCashValue = _DataInputOutputModel.ComputedActualCashValue;
		if (computedActualCashValue.GetValueOrDefault() > 0m && computedActualCashValue.HasValue)
		{
			_DataInputOutputModel.RepairReconPercentage = _DataInputOutputModel.ComputedDamages / _DataInputOutputModel.ComputedActualCashValue;
		}
	}

	private void PercentDamaged()
	{
		decimal? num = 0m;
		decimal? num2 = 0m;
		switch (_DataInputOutputModel.ValueOfBuildingID)
		{
		case 390:
			num2 = _DataInputOutputModel.ComputedActualCashValue;
			break;
		case 391:
			num2 = _DataInputOutputModel.ProMarketAppraisal;
			break;
		case 392:
			num2 = _DataInputOutputModel.AdjTaxAssessedValue;
			break;
		}
		switch (_DataInputOutputModel.CostOfRepairsID)
		{
		case 393:
			num = _DataInputOutputModel.ComputedDamages;
			break;
		case 394:
			num = _DataInputOutputModel.ContractorEstimate;
			break;
		case 395:
			num = _DataInputOutputModel.CommunityEstimate;
			break;
		}
		decimal? num3 = num2;
		if (num3.GetValueOrDefault() > 0m && num3.HasValue)
		{
			_DataInputOutputModel.PercentDamaged = num / num2;
		}
		else
		{
			_DataInputOutputModel.PercentDamaged = null;
		}
		_DataInputOutputModel.FinalAssessment = "Data Entry Incomplete";
		decimal? num4;
		decimal num5;
		if (_DataInputOutputModel.PercentDamaged.HasValue)
		{
			decimal value = 100;
			num3 = _DataInputOutputModel.PercentDamaged;
			num4 = (decimal?)value * num3;
			num5 = 50;
			if (num4.GetValueOrDefault() >= num5 && num4.HasValue)
			{
				_DataInputOutputModel.FinalAssessment = "Substantially Damaged";
			}
			value = 100;
			num3 = _DataInputOutputModel.PercentDamaged;
			num4 = (decimal?)value * num3;
			num5 = 50;
			if (num4.GetValueOrDefault() < num5 && num4.HasValue)
			{
				_DataInputOutputModel.FinalAssessment = "Not Substantially Damaged";
			}
		}
		num4 = _DataInputOutputModel.PercentDamaged;
		num5 = 1;
		if (num4.GetValueOrDefault() > num5 && num4.HasValue)
		{
			_DataInputOutputModel.PercentDamaged = 1;
		}
		if (!string.IsNullOrEmpty(_DataInputOutputModel.DamageUndeterminedMessage))
		{
			_DataInputOutputModel.FinalAssessment = _DataInputOutputModel.DamageUndeterminedMessage;
			_DataInputOutputModel.PercentDamaged = 0m;
		}
	}

	private void RunElementPercentCalcs()
	{
		List<ElementPercentage> elementList = GetElementList();
		if (_DataInputOutputModel.ElementPercentages.Count() == 0)
		{
			// Original quirk (Calculations.cs:195-208): it builds a default row per element but never
			// adds it to the list, so ElementPercentages stays empty and ComputedDamages is 0.
			// Callers (Sde.Core) always pass the element rows, as the original UI did.
			_DataInputOutputModel.ElementPercentages = new List<ElementPercentage>();
			_DataInputOutputModel.ElementTotalEstimatedDamage = 0m;
		}
		else
		{
			foreach (ElementPercentage item in _DataInputOutputModel.ElementPercentages)
			{
				ElementPercentage elementPercentage = elementList.FirstOrDefault((ElementPercentage x) => x.ElementName.ToLower() == item.ElementName.ToLower()) ?? new ElementPercentage();
				item.ElementName = elementPercentage.ElementName ?? "";
				item.Percentage = elementPercentage.Percentage;
				item.ItemCost = CalculateElementItemCost(Convert.ToDecimal(item.Percentage));
				item.DamagedValue = CalculateElemenDamageValue(Convert.ToDecimal(item.ItemCost), Convert.ToDecimal(item.PercentDamaged));
			}
		}
		_DataInputOutputModel.ElementTotalEstimatedDamage = _DataInputOutputModel.ElementPercentages.Select((ElementPercentage x) => x.DamagedValue).Sum();
		_DataInputOutputModel.ComputedDamages = _DataInputOutputModel.ElementTotalEstimatedDamage;
	}

	private decimal CalculateElementItemCost(decimal elementPercentage)
	{
		decimal result = 0m;
		try
		{
			result = elementPercentage / 100m * Convert.ToDecimal(_DataInputOutputModel.TotalReplacementCost);
			return result;
		}
		catch (Exception)
		{
		}
		return result;
	}

	private decimal CalculateElemenDamageValue(decimal itemCost, decimal percentDamage)
	{
		decimal result = 0m;
		try
		{
			result = itemCost * (Convert.ToDecimal(percentDamage) / 100m);
			return result;
		}
		catch (Exception)
		{
		}
		return result;
	}

	/// <summary>
	/// The element list (name + % of replacement cost) for the model's building configuration.
	/// Requires <see cref="RunCalulations"/> to have set the model, or use <see cref="GetElementList(DataInputOutputModel)"/>.
	/// </summary>
	public List<ElementPercentage> GetElementList()
	{
		int ResidenceID = ((_DataInputOutputModel.ResidentialData.ResidenceID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.ResidentialData.ResidenceID, 11));
		int FoundationID = ((_DataInputOutputModel.ResidentialData.FoundationID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.ResidentialData.FoundationID, 7));
		int SuperstructureID = ((_DataInputOutputModel.ResidentialData.SuperstructureID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.ResidentialData.SuperstructureID, 16));
		int RoofCoveringID = ((_DataInputOutputModel.ResidentialData.RoofCoveringID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.ResidentialData.RoofCoveringID, 12));
		int ExteriorFinishID = ((_DataInputOutputModel.ResidentialData.ExteriorFinishID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.ResidentialData.ExteriorFinishID, 4));
		int HvacID = ((_DataInputOutputModel.ResidentialData.HvacID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.ResidentialData.HvacID, 8));
		int StoryID = ((_DataInputOutputModel.ResidentialData.StoryID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.ResidentialData.StoryID, 14));
		int NonResStoryID = ((_DataInputOutputModel.CommercialData.StoryID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.CommercialData.StoryID, 14));
		int StructureUseID = ((_DataInputOutputModel.CommercialData.StructureUseID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.CommercialData.StructureUseID, 15));
		int SprinklerID = ((_DataInputOutputModel.CommercialData.SprinklerID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.CommercialData.SprinklerID, 13));
		int ConveyanceID = ((_DataInputOutputModel.CommercialData.ConveyanceID == 0) ? 1 : GetOldLookUpID(_DataInputOutputModel.CommercialData.ConveyanceID, 1));
		if (_DataInputOutputModel.StructureTypeID == 1)
		{
			return _tables.Residential(ResidenceID, FoundationID, SuperstructureID, RoofCoveringID, ExteriorFinishID, HvacID, StoryID)
				.Select(e => new ElementPercentage
				{
					ElementName = e.Element,
					Percentage = e.Percentage
				}).ToList();
		}
		return _tables.Commercial(NonResStoryID, StructureUseID, SprinklerID, ConveyanceID)
			.Select(e => new ElementPercentage
			{
				ElementName = e.Element,
				Percentage = e.Percentage
			}).ToList();
	}

	/// <summary>Element list for a model without running the calculation (used to build input rows).</summary>
	public List<ElementPercentage> GetElementList(DataInputOutputModel model)
	{
		_DataInputOutputModel = model;
		return GetElementList();
	}

	public int GetOldLookUpID(int id, int LoookupTypeID)
	{
		if (id == 0)
		{
			return 0;
		}
		LookupRow row = _tables.FindLookup(LoookupTypeID, id)
			?? throw new InvalidOperationException($"Lookup ID {id} (lookup type {LoookupTypeID}) is not in FEMA's Lookup.json; the original SDE would crash here too.");
		return row.OldID ?? 0;
	}
}
