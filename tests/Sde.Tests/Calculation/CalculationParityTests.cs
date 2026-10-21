using System.Globalization;
using Sde.Calculation;
using Sde.Calculation.Models;
using static Sde.Tests.Calculation.ScenarioGenerator;

namespace Sde.Tests.Calculation;

/// <summary>
/// The answer-key tests: the Mac port must produce byte-identical results (every decimal, including
/// its scale, every string) to FEMA's original CalculationEngine, for every building configuration
/// the screens can select.
/// </summary>
public class CalculationParityTests
{
	[Fact]
	public void EmbeddedTables_AreIdenticalTo_FemaJson()
	{
		ElementTables fema = ElementTables.LoadFemaJson(TestPaths.FemaJson);
		ElementTables embedded = ElementTables.Default;

		Assert.Equal(23625, fema.ResidentialKeyCount);
		Assert.Equal(fema.ResidentialKeyCount, embedded.ResidentialKeyCount);
		Assert.Equal(fema.CommercialKeyCount, embedded.CommercialKeyCount);
		foreach (var k in fema.ResidentialKeys)
		{
			AssertSame(fema.Residential(k.Residence, k.Foundation, k.Superstructure, k.Roof, k.Exterior, k.Hvac, k.Story),
				embedded.Residential(k.Residence, k.Foundation, k.Superstructure, k.Roof, k.Exterior, k.Hvac, k.Story));
		}
		foreach (var k in fema.CommercialKeys)
		{
			AssertSame(fema.Commercial(k.Story, k.Use, k.Sprinkler, k.Conveyance), embedded.Commercial(k.Story, k.Use, k.Sprinkler, k.Conveyance));
		}
		Assert.Equal(fema.Lookups.Count, embedded.Lookups.Count);
	}

	private static void AssertSame(IReadOnlyList<ElementShare> a, IReadOnlyList<ElementShare> b)
	{
		Assert.Equal(a.Count, b.Count);
		for (int i = 0; i < a.Count; i++)
		{
			Assert.Equal(a[i].Element, b[i].Element);
			// Compare text too, so 13.0 vs 13 (decimal scale) would fail.
			Assert.Equal(a[i].Percentage.ToString(CultureInfo.InvariantCulture), b[i].Percentage.ToString(CultureInfo.InvariantCulture));
		}
	}

	/// <summary>Every residential selection the screens allow: 4×7×5×5×5×3×3 = 31,500 inputs covering all 23,625 table rows.</summary>
	[Fact]
	[Trait("Category", "Slow")]
	public void Residential_AllConfigurations_MatchOriginal()
	{
		OriginalEngine.EnsureReady();
		int n = 0;
		List<string> failures = new();
		foreach (int r in Residence)
		foreach (int f in Foundation)
		foreach (int s in Superstructure)
		foreach (int roof in Roof)
		foreach (int e in Exterior)
		foreach (int h in Hvac)
		foreach (int st in ResStory)
		{
			DataInputOutputModel input = new ScenarioGenerator(n++).Residential(r, f, s, roof, e, h, st);
			Compare(input, failures, $"res r={r} f={f} s={s} roof={roof} e={e} h={h} st={st}");
		}
		Assert.Equal(31500, n);
		Assert.True(failures.Count == 0, $"{failures.Count} mismatches, first: {failures.FirstOrDefault()}");
	}

	/// <summary>Every non-residential selection: 6×24×3×3 = 1,296 inputs, 8 random scenarios each.</summary>
	[Fact]
	public void NonResidential_AllConfigurations_MatchOriginal()
	{
		OriginalEngine.EnsureReady();
		int n = 0;
		List<string> failures = new();
		foreach (int st in NonResStory)
		foreach (int use in StructureUse)
		foreach (int spr in Sprinkler)
		foreach (int conv in Conveyance)
		for (int k = 0; k < 8; k++)
		{
			DataInputOutputModel input = new ScenarioGenerator(1_000_000 + n++).NonResidential(st, use, spr, conv);
			Compare(input, failures, $"nonres st={st} use={use} spr={spr} conv={conv} k={k}");
		}
		Assert.Equal(10368, n);
		Assert.True(failures.Count == 0, $"{failures.Count} mismatches, first: {failures.FirstOrDefault()}");
	}

	/// <summary>Hand-picked edge cases: nulls everywhere, zero square feet, >100% damage, undetermined damage, bad basis IDs.</summary>
	[Theory]
	[MemberData(nameof(EdgeCases))]
	public void EdgeCases_MatchOriginal(string name, DataInputOutputModel input)
	{
		List<string> failures = new();
		Compare(input, failures, name);
		Assert.Empty(failures);
	}

	public static IEnumerable<object[]> EdgeCases()
	{
		DataInputOutputModel Typical() => new()
		{
			StructureTypeID = 1,
			BaseCostPerSqFt = 125.50m, SquareFeet = 1800m, GeographicAdjustment = 1.05m, DepreciationPercentage = 22.1m,
			ValueOfBuildingID = 390, CostOfRepairsID = 393,
			ResidentialData = new ResidentialData { ResidenceID = 92, FoundationID = 45, SuperstructureID = 130, RoofCoveringID = 95, ExteriorFinishID = 25, HvacID = 49, StoryID = 104 },
			CommercialData = new CommercialData { StoryID = 104 },
			CostAdjustments = new() { new CostAdjustment { CostAdjustmentLookupID = 376, Quantity = 1, UnitCost = 12000m } },
			AdditionalAdjustments = new() { new AdditionalAdjustment { zOrder = 1, AdjustmentName = "Shed", Quantity = 1, UnitCost = 2500m } },
			ElementPercentages = new[] { "Foundation", "Superstructure", "Roof Covering", "Exterior Finish", "Interior Finish", "Doors and Windows", "Cabinets and Countertops", "Floor Finish", "Plumbing", "Electrical", "Appliances", "HVAC" }
				.Select((nm, i) => new ElementPercentage { CalcID = 4 + i, ElementName = nm, PercentDamaged = 60m }).ToList(),
		};
		yield return new object[] { "typical substantially damaged", Typical() };
		DataInputOutputModel m;
		m = Typical(); foreach (var e in m.ElementPercentages) e.PercentDamaged = 10m; yield return new object[] { "typical not substantial", m };
		m = Typical(); foreach (var e in m.ElementPercentages) e.PercentDamaged = 100m; m.DepreciationPercentage = 70m; yield return new object[] { "capped above 100%", m };
		m = Typical(); m.SquareFeet = 0m; yield return new object[] { "zero square feet", m };
		m = Typical(); m.SquareFeet = null; yield return new object[] { "null square feet", m };
		m = Typical(); m.DepreciationPercentage = null; yield return new object[] { "null depreciation", m };
		m = Typical(); m.DepreciationPercentage = 100m; yield return new object[] { "100% depreciation (ACV 0)", m };
		m = Typical(); m.DamageUndeterminedMessage = "No Physical Damage"; yield return new object[] { "undetermined damage", m };
		m = Typical(); m.ValueOfBuildingID = null; m.CostOfRepairsID = null; yield return new object[] { "no basis chosen", m };
		m = Typical(); m.ValueOfBuildingID = 391; m.ProMarketAppraisal = 0m; yield return new object[] { "appraisal basis with no appraisal", m };
		m = Typical(); m.ValueOfBuildingID = 392; m.TaxAssessedValue = 150000m; m.FactorAdjustment = 1.15m; m.CostOfRepairsID = 394; m.ContractorEstimate = 90000m; yield return new object[] { "tax basis + contractor estimate", m };
		m = Typical(); m.CostOfRepairsID = 395; m.CommunityEstimate = null; yield return new object[] { "community estimate null", m };
		m = Typical(); m.ElementPercentages = new(); yield return new object[] { "empty element list quirk", m };
		m = Typical(); m.ElementPercentages[0].ElementName = "Not An Element"; yield return new object[] { "unknown element name", m };
		m = Typical(); m.CostAdjustments[0].Quantity = null; m.AdditionalAdjustments[0].UnitCost = null; yield return new object[] { "null adjustment parts", m };
		m = Typical(); m.ResidentialData.ResidenceID = 94; m.ElementPercentages[0].ElementName = "Skirting / Forms Piers"; yield return new object[] { "manufactured home skirting", m };
		m = Typical(); m.GeographicAdjustment = 0.875m; m.BaseCostPerSqFt = 99.999m; m.SquareFeet = 1234.5m; yield return new object[] { "awkward decimals", m };
		// The 50% Substantial Damage threshold, approached from both sides.
		foreach (decimal repair in new[] { 50000m, 49999.99m, 49500m, 49000.01m, 50000.01m, 100000m, 100000.01m })
		{
			m = Typical(); m.ValueOfBuildingID = 391; m.ProMarketAppraisal = 100000m; m.CostOfRepairsID = 394; m.ContractorEstimate = repair;
			yield return new object[] { $"threshold: {repair} of 100000", m };
		}
		m = Typical(); m.StructureTypeID = 2; m.CommercialData = new CommercialData { StoryID = 102, StructureUseID = 115, SprinklerID = 99, ConveyanceID = 2 };
		m.ElementPercentages = new[] { "Foundation", "Superstructure", "Roof Covering", "Plumbing", "Electrical", "HVAC", "Interiors" }.Select(nm => new ElementPercentage { ElementName = nm, PercentDamaged = 45m }).ToList();
		yield return new object[] { "non-residential apartments", m };
	}

	/// <summary>Guards the guard: the random scenarios must actually reach every outcome, not just "Data Entry Incomplete".</summary>
	[Fact]
	public void RandomScenarios_CoverEveryOutcome()
	{
		Dictionary<string, int> outcomes = new();
		int withDamages = 0;
		for (int n = 0; n < 4000; n++)
		{
			ScenarioGenerator g = new ScenarioGenerator(n);
			DataInputOutputModel m = n % 2 == 0
				? g.Residential(Residence[n % 4], Foundation[n % 7], Superstructure[n % 5], Roof[n % 5], Exterior[n % 5], Hvac[n % 3], ResStory[n % 3])
				: g.NonResidential(NonResStory[n % 6], StructureUse[n % 24], Sprinkler[n % 3], Conveyance[n % 3]);
			new Sde.Calculation.Calculations().RunCalulations(m);
			outcomes[m.FinalAssessment] = outcomes.GetValueOrDefault(m.FinalAssessment) + 1;
			if (m.ComputedDamages > 0)
			{
				withDamages++;
			}
		}
		Assert.True(outcomes.GetValueOrDefault("Substantially Damaged") > 400, Describe(outcomes));
		Assert.True(outcomes.GetValueOrDefault("Not Substantially Damaged") > 400, Describe(outcomes));
		Assert.True(outcomes.GetValueOrDefault("Data Entry Incomplete") > 100, Describe(outcomes));
		Assert.True(outcomes.GetValueOrDefault("No Physical Damage") > 50, Describe(outcomes));
		Assert.True(withDamages > 2000, $"only {withDamages} scenarios had computed damages");
	}

	private static string Describe(Dictionary<string, int> d) => string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value}"));

	[Fact]
	public void LookupIdMissingFromLookupJson_ThrowsInBoth()
	{
		DataInputOutputModel m = new ScenarioGenerator(7).Residential(92, 45, 130, 95, 25, 49, 104);
		m.ResidentialData.FoundationID = 12345;
		OriginalEngine.EnsureReady();
		Assert.ThrowsAny<Exception>(() => OriginalEngine.Run(m));
		Assert.Throws<InvalidOperationException>(() => OriginalEngine.RunPort(m));
	}

	private static void Compare(DataInputOutputModel input, List<string> failures, string label)
	{
		string expected = OriginalEngine.Run(input);
		string actual = OriginalEngine.RunPort(input);
		if (expected != actual)
		{
			failures.Add($"{label}\n  original: {expected}\n  port:     {actual}");
		}
	}
}
