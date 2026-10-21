using Sde.Calculation;
using Sde.Calculation.Models;

namespace Sde.Tests.Calculation;

/// <summary>
/// Deterministic random calculation inputs that look like what SDE's screens send
/// (see docs/SDE-ARCHITECTURE.md §2), plus the odd values the screens allow.
/// </summary>
internal sealed class ScenarioGenerator
{
	// DB LookupIDs the screens offer (Lookup.json types 11,7,16,12,4,8,14 / 14,15,13,1). 0 = nothing selected.
	public static readonly int[] Residence = { 0, 92, 93, 94 };
	public static readonly int[] Foundation = { 0, 43, 44, 45, 46, 47, 48 };
	public static readonly int[] Superstructure = { 0, 130, 131, 132, 133 };
	public static readonly int[] Roof = { 0, 95, 96, 97, 98 };
	public static readonly int[] Exterior = { 0, 25, 26, 27, 28 };
	public static readonly int[] Hvac = { 0, 49, 50 };
	public static readonly int[] ResStory = { 0, 104, 105 };
	public static readonly int[] NonResStory = { 0, 101, 102, 103, 104, 105 };
	public static readonly int[] StructureUse = new[] { 0 }.Concat(Enumerable.Range(106, 23)).ToArray();
	public static readonly int[] Sprinkler = { 0, 99, 100 };
	public static readonly int[] Conveyance = { 0, 1, 2 };

	private readonly Random _rng;

	public ScenarioGenerator(int seed) => _rng = new Random(seed);

	public DataInputOutputModel Residential(int residence, int foundation, int superstructure, int roof, int exterior, int hvac, int story)
	{
		DataInputOutputModel m = Base(1);
		m.ResidentialData = new ResidentialData
		{
			ResidenceID = residence, FoundationID = foundation, SuperstructureID = superstructure,
			RoofCoveringID = roof, ExteriorFinishID = exterior, HvacID = hvac, StoryID = story,
		};
		// The UI passes CommercialData too, with the same story combo value.
		m.CommercialData = new CommercialData { StoryID = story };
		m.ElementPercentages = Elements(m);
		return m;
	}

	public DataInputOutputModel NonResidential(int story, int use, int sprinkler, int conveyance)
	{
		DataInputOutputModel m = Base(2);
		m.ResidentialData = new ResidentialData { StoryID = story };
		m.CommercialData = new CommercialData { StoryID = story, StructureUseID = use, SprinklerID = sprinkler, ConveyanceID = conveyance };
		m.ElementPercentages = Elements(m);
		return m;
	}

	private DataInputOutputModel Base(int structureType)
	{
		return new DataInputOutputModel
		{
			PropertyID = Guid.NewGuid().ToString(),
			AssessmentID = Guid.NewGuid().ToString(),
			StructureTypeID = structureType,
			BaseCostPerSqFt = Pick(0.05, null, () => Money(40, 400)),
			SquareFeet = Pick(0.05, null, () => _rng.Next(4) == 0 ? Dec(_rng.Next(300, 9000) + 0.5) : _rng.Next(0, 9000)),
			GeographicAdjustment = Pick(0.05, null, () => Dec(Math.Round(0.7 + _rng.NextDouble() * 0.6, _rng.Next(1, 4)))),
			DepreciationRating = _rng.Next(383, 390),
			DepreciationPercentage = Pick(0.05, null, () => Dec(Math.Round(_rng.NextDouble() * 75, 1))),
			ValueOfBuildingID = OneOf<int?>(390, 390, 390, 391, 392, null, 0, 999),
			CostOfRepairsID = OneOf<int?>(393, 393, 393, 394, 395, null, 7),
			ProMarketAppraisal = Pick(0.3, 0m, () => Money(20000, 600000)),
			TaxAssessedValue = Pick(0.3, 0m, () => Money(10000, 500000)),
			FactorAdjustment = Pick(0.3, 0m, () => Dec(Math.Round(0.5 + _rng.NextDouble() * 2, 2))),
			ContractorEstimate = Pick(0.3, 0m, () => Money(0, 400000)),
			CommunityEstimate = Pick(0.3, 0m, () => Money(0, 400000)),
			PercentOfExistingImprovements = Pick(0.5, 0m, () => Dec(Math.Round(_rng.NextDouble() * 30, 2))),
			DamageUndeterminedMessage = _rng.Next(20) == 0 ? "No Physical Damage" : (_rng.Next(3) == 0 ? "" : null),
			CostAdjustments = Enumerable.Range(0, _rng.Next(0, 8)).Select(i => new CostAdjustment
			{
				CostAdjustmentLookupID = 365 + i,
				Quantity = Pick(0.1, null, () => _rng.Next(3) == 0 ? Dec(Math.Round(_rng.NextDouble() * 500, 2)) : _rng.Next(0, 6)),
				UnitCost = Pick(0.1, null, () => Money(0, 25000)),
			}).ToList(),
			AdditionalAdjustments = Enumerable.Range(1, _rng.Next(0, 8)).Select(i => new AdditionalAdjustment
			{
				zOrder = i,
				AdjustmentName = "Adj " + i,
				Quantity = Pick(0.2, null, () => _rng.Next(0, 4)),
				UnitCost = Pick(0.2, null, () => Money(-500, 15000)),
			}).ToList(),
			ElementPercentages = new List<ElementPercentage>(),
		};
	}

	/// <summary>Element rows like the UI sends: one per visible element, named from the Lookup table ("HVAC", not "Hvac").</summary>
	private List<ElementPercentage> Elements(DataInputOutputModel m)
	{
		if (_rng.Next(25) == 0)
		{
			return new List<ElementPercentage>(); // exercises the original's empty-list quirk
		}
		List<ElementPercentage> list = new Calculations().GetElementList(m)
			.Select((e, i) => new ElementPercentage
			{
				CalcID = 4 + i,
				ElementName = _rng.Next(3) == 0 ? e.ElementName.ToUpperInvariant() : e.ElementName,
				PercentDamaged = Pick(0.05, null, () => _rng.Next(4) switch
				{
					0 => 0m,
					1 => 100m,
					2 => _rng.Next(0, 101),
					_ => Dec(Math.Round(_rng.NextDouble() * 100, _rng.Next(1, 3))),
				}),
			}).ToList();
		if (_rng.Next(10) == 0)
		{
			list.Add(new ElementPercentage { CalcID = 99, ElementName = "Garage", PercentDamaged = 50m }); // name not in table
		}
		return list;
	}

	private decimal Money(int min, int max) => Dec(Math.Round(min + _rng.NextDouble() * (max - min), 2));

	private static decimal Dec(double d) => (decimal)d;

	private decimal? Pick(double chance, decimal? special, Func<decimal> value) => _rng.NextDouble() < chance ? special : value();

	private T OneOf<T>(params T[] options) => options[_rng.Next(options.Length)];
}
