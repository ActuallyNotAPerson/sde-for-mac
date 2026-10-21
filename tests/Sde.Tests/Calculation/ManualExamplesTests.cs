using System.Globalization;
using Newtonsoft.Json;
using Sde.Calculation;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Calculation;

/// <summary>
/// FEMA's own numbers, independent of the decompiled code: screenshots of SDE 3.0 running, and rules stated in the
/// SDE User Manual and Field Workbook (FEMA P-784, August 2017; installer/sde_3.0_user_manual_field_workbook_0.pdf,
/// text in docs/reference/sde-manual.txt). Figure numbers are the manual's.
///
/// The manual's residential screenshots predate the element and depreciation tables shipped with SDE 3.0.0
/// (April 2018): Fig. 3-37's twelve element shares are not a row of the shipped Residential.json, and Fig. 3-34
/// shows "4 - Average Condition" with 0.0 % where the shipped DepreciationValue table gives 24.2 % (FEMA's own
/// LookupService.GetDepreciationValues, decomp/DAL/DAL.Services/LookupService.cs:141, reads the same row). So those
/// examples are replayed with the values the screenshots display: element shares written to a table in FEMA's JSON
/// format, and the depreciation percentage the record held.
/// </summary>
public class ManualExamplesTests : IDisposable
{
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	private SdeStore Store => _db.Store;

	/// <summary>Element shares shown in Fig. 3-37 (Residential Element Percentages tab).</summary>
	private static readonly (string Element, decimal Share)[] Fig337Shares =
	{
		("Foundation", 8m), ("Superstructure", 15.9m), ("Roof Covering", 4.3m), ("Exterior Finish", 7m),
		("Doors and Windows", 15.8m), ("Cabinets and Countertops", 4.5m), ("Floor Finish", 7.9m), ("Plumbing", 8.6m),
		("Electrical", 4.9m), ("Appliances", 4.2m), ("Interior Finish", 13.2m), ("Hvac", 5.7m),
	};

	/// <summary>
	/// The one-story slab-on-grade house of Fig. 3-33 (Single Family, stud-framed, asphalt shingles, siding/stucco,
	/// Average quality), with an HVAC system as in Fig. 3-37.
	/// </summary>
	private PropertyRecord House(decimal squareFeet) => Store.SaveProperty(new PropertyRecord
	{
		StructureTypeID = 1, StoryTypeID = 104, ResidenceTypeID = 92, FoundationTypeID = 46, SuperStructureTypeID = 130,
		RoofCoveringTypeID = 95, ExteriorFinishTypeID = 25, HVACTypeID = 49, StructureQualityID = 53, SquareFootage = squareFeet,
	});

	/// <summary>FEMA-format JSON tables (Lookup.json copied from FEMA) whose only residential row is Fig. 3-37's shares for <see cref="House"/>.</summary>
	private ElementTables ManualTables()
	{
		ElementTables fema = ElementTables.Default;
		int Old(int type, int id) => fema.FindLookup(type, id).OldID ?? 0;
		string dir = Path.Combine(_db.Folder, "manual-json");
		Directory.CreateDirectory(dir);
		File.Copy(Path.Combine(TestPaths.FemaJson, "Lookup.json"), Path.Combine(dir, "Lookup.json"));
		File.WriteAllText(Path.Combine(dir, "Commercial.json"), "[]");
		File.WriteAllText(Path.Combine(dir, "Residential.json"), JsonConvert.SerializeObject(Fig337Shares.Select(s => new
		{
			ResidenceID = Old(11, 92), FoundationID = Old(7, 46), SuperStructureID = Old(16, 130), RoofCoveringID = Old(12, 95),
			ExteriorFinishID = Old(4, 25), HvacID = Old(8, 49), StoryID = Old(14, 104),
			Element = s.Element, Percentage = s.Share.ToString(CultureInfo.InvariantCulture),
		})));
		return ElementTables.LoadFemaJson(dir);
	}

	private AssessmentFile NewAssessment(PropertyRecord p, AssessmentCalculator calc, decimal baseCost)
	{
		AssessmentFile f = Store.CreateAssessment(p.PropertyID, applyDefaults: false);
		f.Assessment.BaseCost = baseCost;
		f.Assessment.GeographicAdjustment = 1.00m;
		calc.NormalizeRows(f);
		return f;
	}

	private static void Damage(AssessmentFile f, string element, decimal percent) =>
		f.ElementPercentages.Single(e => e.ElementName == element).UserDamage = percent;

	private static decimal Money(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

	[Fact]
	public void Fig3_37_ElementCostsAndDamages()
	{
		AssessmentCalculator calc = new AssessmentCalculator(Store.Reference, ManualTables());
		AssessmentFile f = NewAssessment(House(2400m), calc, 135m); // any base cost × area giving the figure's $324,000
		Damage(f, "Roof Covering", 100);
		Damage(f, "Exterior Finish", 5);
		Damage(f, "Floor Finish", 10);
		Damage(f, "Interior Finish", 20);
		calc.Recalculate(f);

		Assert.Equal(324_000m, f.Assessment.TotalReplacementCost);
		// Element Cost and Damage Values columns exactly as displayed.
		Dictionary<string, (decimal Cost, decimal Damage)> shown = new()
		{
			["Foundation"] = (25_920.00m, 0m), ["Superstructure"] = (51_516.00m, 0m), ["Roof Covering"] = (13_932.00m, 13_932.00m),
			["Exterior Finish"] = (22_680.00m, 1_134.00m), ["Doors and Windows"] = (51_192.00m, 0m), ["Cabinets and Countertops"] = (14_580.00m, 0m),
			["Floor Finish"] = (25_596.00m, 2_559.60m), ["Plumbing"] = (27_864.00m, 0m), ["Electrical"] = (15_876.00m, 0m),
			["Appliances"] = (13_608.00m, 0m), ["Interior Finish"] = (42_768.00m, 8_553.60m), ["HVAC"] = (18_468.00m, 0m),
		};
		Assert.Equal(shown.Keys.Order(), f.ElementPercentages.Select(e => e.ElementName).Order());
		foreach (ElementPercentageRecord e in f.ElementPercentages)
		{
			Assert.Equal((e.ElementName, shown[e.ElementName].Cost, shown[e.ElementName].Damage), (e.ElementName, Money(e.ItemCost), Money(e.DamageValues)));
		}
		Assert.Equal(26_179.20m, f.Assessment.TotalEstimatedDamages); // "Computed Damages: $26,179.20"
	}

	/// <summary>Fig. 3-34 (Cost tab) and Fig. 3-38 (Output Summary tab, adjusted tax assessed value basis).</summary>
	[Fact]
	public void Fig3_34_and_3_38_CostTabAndOutputSummary()
	{
		AssessmentCalculator calc = new AssessmentCalculator(Store.Reference, ManualTables());
		AssessmentFile f = NewAssessment(House(2000m), calc, 135m);
		AssessmentRecord a = f.Assessment;
		a.DepreciationRatingID = 386;  // "4 - Average Condition"
		a.DepreciationValue = 0m;      // "Depreciation Percentage: 0.0 %" as displayed (see the class note)
		Damage(f, "Roof Covering", 5); // $580.50 = 5 % of the 4.3 % roof covering share of $270,000
		a.TaxAssessedValue = 137_268m;
		a.FactorAdjustment = 1.00m;
		a.ValueOfBuildingID = 392;     // Adj. Tax Assessed Value
		a.CostOfRepairsID = 393;       // Computed Damages
		calc.Recalculate(f);

		// Fig. 3-34: Base Cost $135.00 × 2,000.00 sq ft × 1.00 = Cost $270,000.00; Total Adjustments $0.00.
		Assert.Equal(270_000m, a.TotalReplacementCost);
		Assert.Equal(0m, a.TotalAdjustments ?? 0m);
		Assert.Equal(135m, a.ReplacementCostSqFt);
		Assert.Equal(270_000m, a.ComputedActualCashValue);
		// Fig. 3-38: Computed Damages $580.50 ÷ Adjusted Tax Assessed Value $137,268.00 = 0.4 %, Not Substantially Damaged;
		// Repair/Reconstruction Percentage 0.2 % (damages ÷ computed ACV).
		Assert.Equal(580.50m, a.TotalEstimatedDamages);
		Assert.Equal(0.4m, a.PercentDamage);
		Assert.Equal("Not Substantially Damaged", a.DamageMessage);
		Assert.Equal(0.2m, a.RepairReconstructPercent);
	}

	/// <summary>Fig. 3-43: Non-Residential Element Percentages tab for a 1-story Mini-Warehouse, with the shipped tables.</summary>
	[Fact]
	public void Fig3_43_MiniWarehouseElementShares()
	{
		PropertyRecord p = Store.SaveProperty(new PropertyRecord { StructureTypeID = 2, StoryTypeID = 101, StructureUseID = 118, StructureQualityID = 53 });
		AssessmentFile f = NewAssessment(p, Store.Calculator, 0m);
		Store.Calculator.Recalculate(f);
		Dictionary<string, decimal> shown = new()
		{
			["Foundation"] = 13.0m, ["Superstructure"] = 29.0m, ["Roof Covering"] = 8.0m, ["Plumbing"] = 11.0m,
			["Electrical"] = 6.0m, ["Interiors"] = 23.0m, ["HVAC"] = 10.0m,
		};
		Assert.Equal(shown, f.ElementPercentages.ToDictionary(e => e.ElementName, e => e.ElementPercentage!.Value));
	}

	/// <summary>
	/// Manual §3.11 troubleshooting tip: "Data Entry Incomplete" instead of a percentage means key data is missing,
	/// "most likely … the square footage, base cost, or the depreciation rate"; once entered, the percentage appears.
	/// In FEMA's program (ucCost.xaml.cs:440-444) a blank square footage or base cost is sent to the engine as 0, so
	/// the replacement cost, and with it the value of the building, is 0 and the engine reports Data Entry Incomplete
	/// (Calculations.PercentDamaged) — for a building without cost adjustments, as here.
	/// </summary>
	[Theory]
	[InlineData("square footage")]
	[InlineData("base cost")]
	public void DataEntryIncomplete_UntilKeyDataIsEntered(string missing)
	{
		AssessmentFile f = NewAssessment(House(1450m), Store.Calculator, 118.75m);
		Store.Calculator.ApplyDepreciationRating(f, 386);
		Damage(f, "Interior Finish", 60);
		Damage(f, "Floor Finish", 100);
		Store.Calculator.Recalculate(f);
		decimal percent = f.Assessment.PercentDamage!.Value;
		Assert.Equal("Not Substantially Damaged", f.Assessment.DamageMessage);

		Action restore = missing == "square footage"
			? Swap(() => f.Property.SquareFootage, v => f.Property.SquareFootage = v)
			: Swap(() => f.Assessment.BaseCost, v => f.Assessment.BaseCost = v);
		Store.Calculator.Recalculate(f);
		Assert.Equal("Data Entry Incomplete", f.Assessment.DamageMessage);
		Assert.Equal(0m, f.Assessment.PercentDamage);

		restore();
		Store.Calculator.Recalculate(f);
		Assert.Equal(percent, f.Assessment.PercentDamage);
		Assert.Equal("Not Substantially Damaged", f.Assessment.DamageMessage);
	}

	/// <summary>
	/// The tip's third item, the depreciation rate: FEMA's Cost tab sends a blank depreciation percentage to the engine
	/// as 0 % (ucCost.xaml.cs:444), so a percentage is still computed — on the undepreciated value, which understates
	/// the damage. What stops that from going unnoticed is the validator's required depreciation rule (a red pin).
	/// </summary>
	[Fact]
	public void BlankDepreciation_CountsAsZeroLikeFemasCostTab_AndIsReportedAsRequired()
	{
		AssessmentFile f = SampleCases.RanchFlood(Store);
		AssessmentValidator validator = new AssessmentValidator(Store.Reference);
		Assert.DoesNotContain(validator.Validate(f, false), i => i.Severity == IssueSeverity.Required && i.ControlName.Contains("Depreciation"));

		f.Assessment.DepreciationRatingID = null;
		f.Assessment.DepreciationValue = null;
		Store.Calculator.Recalculate(f);
		Assert.Equal(56.9m, f.Assessment.PercentDamage); // 75.0 % × (1 − 24.2 %)
		Assert.Equal("Substantially Damaged", f.Assessment.DamageMessage);
		Assert.Contains(validator.Validate(f, false), i => i.Severity == IssueSeverity.Required && i.ControlName.Contains("Depreciation"));
	}

	private static Action Swap(Func<decimal?> get, Action<decimal?> set)
	{
		decimal? old = get();
		set(null);
		return () => set(old);
	}

	/// <summary>
	/// Manual §3.11.2.2: "Damage Undetermined" (e.g. No Physical Damage Sustained) "reduces the amount of data required
	/// to save a valid assessment and the structure will be identified with 0 percent damaged in any SDE reports or exports."
	/// </summary>
	[Fact]
	public void DamageUndetermined_IsZeroPercent_AndNeedsLessData()
	{
		AssessmentFile f = SampleCases.RanchFlood(Store);
		f.Assessment.PhysicalDamage = true;
		f.Assessment.UndeterminedDamageTypeID = 360; // No Physical Damage Sustained
		Store.Calculator.Recalculate(f);
		Store.SaveAssessment(f);
		Assert.Equal(0m, f.Assessment.PercentDamage);
		Assert.Equal("No Physical Damage Sustained", f.Assessment.DamageMessage);
		AssessmentRecord exported = new ExchangeService(Store).ReadDataset(new[] { f.Assessment.AssessmentID }).Assessments.Single();
		Assert.Equal(0m, exported.PercentDamage);

		AssessmentValidator validator = new AssessmentValidator(Store.Reference);
		PropertyRecord bare = Store.SaveProperty(new PropertyRecord { StructureTypeID = 1, StreetNumber = "9", StreetName = "Dry" });
		AssessmentFile empty = Store.CreateAssessment(bare.PropertyID, applyDefaults: false);
		int requiredDamaged = validator.Validate(empty, false).Count(i => i.Severity == IssueSeverity.Required);
		empty.Assessment.PhysicalDamage = true;
		empty.Assessment.UndeterminedDamageTypeID = 360;
		int requiredUndetermined = validator.Validate(empty, false).Count(i => i.Severity == IssueSeverity.Required);
		Assert.True(requiredUndetermined < requiredDamaged, $"{requiredUndetermined} required with damage undetermined vs {requiredDamaged} without");
	}

	/// <summary>
	/// Manual §3.11.2.6: "to increase the assessed structure value by 43 percent, input a value of 1.43", and
	/// §3.11.2.6/Fig. 3-38: "If the structure has a Percent Damaged of 50 percent or higher" the Substantial Damage requirements apply.
	/// </summary>
	[Fact]
	public void TaxFactor_1_43_RaisesTheValueBy43Percent_AndExactly50PercentIsSubstantial()
	{
		AssessmentFile f = SampleCases.RanchFlood(Store);
		AssessmentRecord a = f.Assessment;
		a.TaxAssessedValue = 100_000m;
		a.FactorAdjustment = 1.43m;
		a.ValueOfBuildingID = 392;
		a.ContractorEstimate = 71_500m; // exactly half of $143,000
		a.CostOfRepairsID = 394;
		Store.Calculator.Recalculate(f);
		Assert.Equal(50.0m, a.PercentDamage);
		Assert.Equal("Substantially Damaged", a.DamageMessage);

		a.ContractorEstimate = 71_499m;
		Store.Calculator.Recalculate(f);
		Assert.Equal(50.0m, a.PercentDamage); // 49.999 % shows as 50.0 % …
		Assert.Equal("Not Substantially Damaged", a.DamageMessage); // … but the determination uses the unrounded ratio
	}

	/// <summary>
	/// Field Workbook §8.4 procedure for a professional appraisal plus a contractor's estimate: base cost $1, the
	/// actual square footage, depreciation rating "Other" with an explanation and a value of 1. The manual says the
	/// result is a valid assessment, so no required data may be reported missing.
	/// </summary>
	[Fact]
	public void AppraisalPlusContractorEstimate_ManualProcedure_IsAValidAssessment()
	{
		AssessmentFile f = SampleCases.RanchFlood(Store);
		AssessmentRecord a = f.Assessment;
		a.MarketAppraisal = 180_000m;
		a.ValueOfBuildingID = 391;
		a.ContractorEstimate = 95_000m;
		a.CostOfRepairsID = 394;
		a.BaseCost = 1m;
		Store.Calculator.ApplyDepreciationRating(f, 389);
		a.DepreciationExplanation = "Used Professional Market Appraisal value prepared by the ABC Appraisal Company on 10/01/26 instead of depreciation";
		a.DepreciationValue = 1m;
		Store.Calculator.Recalculate(f);

		Assert.Equal(52.8m, a.PercentDamage); // 95,000 ÷ 180,000
		Assert.Equal("Substantially Damaged", a.DamageMessage);
		List<ValidationIssue> required = new AssessmentValidator(Store.Reference).Validate(f, false).Where(i => i.Severity == IssueSeverity.Required).ToList();
		Assert.True(required.Count == 0, string.Join("; ", required.Select(i => i.Message)));
	}
}
