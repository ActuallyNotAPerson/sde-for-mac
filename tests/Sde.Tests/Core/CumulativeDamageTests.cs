using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Tests.Core;

/// <summary>Cumulative damage: off by default, one line per event, look-back, both methods, assessments never changed.</summary>
public class CumulativeDamageTests
{
	private static readonly Guid Property = Guid.NewGuid();

	private static readonly CumulativeSettings On = new CumulativeSettings { Enabled = true };

	/// <summary>A synthetic assessment with computed damages and computed ACV (value 100,000 unless given).</summary>
	private static AssessmentRecord Event(DateTime? damage, decimal? percent, DateTime? inspected = null, decimal value = 100000m)
	{
		string message = percent is decimal p ? (p >= 50m ? "Substantially Damaged" : "Not Substantially Damaged") : "Data Entry Incomplete";
		return new AssessmentRecord
		{
			AssessmentID = Guid.NewGuid(),
			PropertyID = Property,
			Active = true,
			DateDamageOccurred = damage,
			DateofInspection = inspected ?? damage?.AddDays(7),
			PercentDamage = percent,
			DamageMessage = message,
			ValueOfBuildingID = LookupIds.ValueComputedAcv,
			CostOfRepairsID = LookupIds.RepairComputedDamages,
			ComputedActualCashValue = value,
			ComputedDamages = percent is decimal q ? value * q / 100m : null,
		};
	}

	[Fact]
	public void OffByDefault_IsNotApplicable()
	{
		CumulativeResult r = CumulativeDamage.Evaluate(new[] { Event(new DateTime(2026, 9, 1), 40m) }, new CumulativeSettings());

		Assert.Equal(CumulativeStatus.NotApplicable, r.Status);
		Assert.Empty(r.Events);
		Assert.False(r.MeetsThreshold);
		Assert.Null(r.TotalPercent);
		Assert.Equal(CumulativeStatus.NotApplicable, CumulativeDamage.Evaluate(Array.Empty<AssessmentRecord>(), null).Status);
	}

	[Fact]
	public void SumOfPercents_AddsEventsInTheLookBack()
	{
		AssessmentRecord current = Event(new DateTime(2026, 9, 1), 30m);
		AssessmentRecord earlier = Event(new DateTime(2019, 8, 15), 25m);
		AssessmentRecord tooOld = Event(new DateTime(2016, 8, 31), 40m);

		CumulativeResult r = CumulativeDamage.Evaluate(new[] { tooOld, current, earlier }, On);

		Assert.Equal(CumulativeStatus.Evaluated, r.Status);
		Assert.Equal(55.0m, r.TotalPercent);
		Assert.True(r.MeetsThreshold);
		Assert.Equal(new DateTime(2016, 9, 1), r.WindowStart);
		Assert.Equal(new[] { current.AssessmentID, earlier.AssessmentID, tooOld.AssessmentID }, r.Events.Select(e => e.AssessmentID));
		Assert.True(r.Events[0].IsCurrent);
		Assert.False(r.Events[2].Counted);
		Assert.Contains("before the look-back period", r.Events[2].Note);
		Assert.Contains(r.Explanation, l => l.Contains("2 of 3"));
	}

	[Fact]
	public void LookBackZero_CountsEveryEvent_AndThresholdIsConfigurable()
	{
		AssessmentRecord[] history = { Event(new DateTime(2026, 9, 1), 20m), Event(new DateTime(1990, 1, 1), 20m) };

		CumulativeResult all = CumulativeDamage.Evaluate(history, new CumulativeSettings { Enabled = true, LookbackYears = 0, ThresholdPercent = 40m });
		CumulativeResult higher = CumulativeDamage.Evaluate(history, new CumulativeSettings { Enabled = true, LookbackYears = 0, ThresholdPercent = 41m });

		Assert.Null(all.WindowStart);
		Assert.Equal(40.0m, all.TotalPercent);
		Assert.True(all.MeetsThreshold);
		Assert.False(higher.MeetsThreshold);
	}

	[Fact]
	public void SameDateOfDamage_CountsOnce_UsingTheLatestInspection()
	{
		DateTime storm = new DateTime(2026, 9, 1);
		AssessmentRecord first = Event(storm, 45m, inspected: new DateTime(2026, 9, 5));
		AssessmentRecord reinspection = Event(storm.AddHours(10), 30m, inspected: new DateTime(2026, 9, 20));
		AssessmentRecord earlier = Event(new DateTime(2024, 6, 1), 20m);

		CumulativeResult r = CumulativeDamage.Evaluate(new[] { first, reinspection, earlier }, On);

		Assert.Equal(2, r.Events.Count);
		Assert.Equal(reinspection.AssessmentID, r.Events[0].AssessmentID);
		Assert.Equal(50.0m, r.TotalPercent);
		Assert.True(r.MeetsThreshold);
		Assert.Single(r.Skipped);
		Assert.Contains("replaced by the later inspection of 09/20/2026", r.Skipped[0]);
	}

	[Fact]
	public void TotalDamageOverCurrentValue_DividesByTheMostRecentValue()
	{
		// 2026: 30,000 of 150,000 (20%); 2022: 40,000 of 100,000 (40%). Sum of percents 60%, but over current value 46.6%.
		AssessmentRecord current = Event(new DateTime(2026, 9, 1), 20m, value: 150000m);
		AssessmentRecord earlier = Event(new DateTime(2022, 9, 1), 40m, value: 100000m);
		CumulativeSettings settings = new CumulativeSettings { Enabled = true, Method = CumulativeMethod.TotalDamageOverCurrentValue };

		CumulativeResult r = CumulativeDamage.Evaluate(new[] { earlier, current }, settings);

		Assert.Equal(70000m, r.TotalRepairCost);
		Assert.Equal(150000m, r.CurrentValue);
		Assert.Equal(46.6m, r.TotalPercent);
		Assert.False(r.MeetsThreshold);
		Assert.Equal(60.0m, CumulativeDamage.Evaluate(new[] { earlier, current }, On).TotalPercent);
	}

	[Fact]
	public void TotalDamageOverCurrentValue_WithoutCurrentValue_HasNoTotal()
	{
		AssessmentRecord current = Event(new DateTime(2026, 9, 1), 20m);
		current.ComputedActualCashValue = null;
		CumulativeSettings settings = new CumulativeSettings { Enabled = true, Method = CumulativeMethod.TotalDamageOverCurrentValue };

		CumulativeResult r = CumulativeDamage.Evaluate(new[] { current }, settings);

		Assert.Equal(CumulativeStatus.Evaluated, r.Status);
		Assert.Null(r.TotalPercent);
		Assert.False(r.MeetsThreshold);
		Assert.Contains(r.Explanation, l => l.Contains("no structure value"));
	}

	[Fact]
	public void IncompleteInactiveAndUndatedAssessments_AreLeftOut()
	{
		AssessmentRecord current = Event(new DateTime(2026, 9, 1), 30m);
		AssessmentRecord incomplete = Event(new DateTime(2025, 9, 1), null);
		AssessmentRecord trashed = Event(new DateTime(2024, 9, 1), 40m);
		trashed.Active = false;
		AssessmentRecord undated = Event(null, 40m, inspected: new DateTime(2023, 3, 3));
		AssessmentRecord undetermined = Event(new DateTime(2022, 9, 1), 40m);
		undetermined.PhysicalDamage = true;

		CumulativeResult r = CumulativeDamage.Evaluate(new[] { current, incomplete, trashed, undated, undetermined }, On);

		Assert.Equal(30.0m, r.TotalPercent);
		Assert.Equal(3, r.Events.Count);
		Assert.Contains("data entry incomplete", r.Events[1].Note);
		Assert.Contains("damage undetermined", r.Events[2].Note);
		Assert.Contains(r.Skipped, s => s.Contains("03/03/2023") && s.Contains("no date of damage"));
	}

	[Fact]
	public void NoDatedEvents_IsNoEvents()
	{
		CumulativeResult r = CumulativeDamage.Evaluate(new[] { Event(null, 60m) }, On);

		Assert.Equal(CumulativeStatus.NoEvents, r.Status);
		Assert.False(r.MeetsThreshold);
		Assert.Single(r.Skipped);
	}

	[Fact]
	public void BasesFollowTheAssessmentsChoices()
	{
		AssessmentRecord a = new AssessmentRecord
		{
			CostOfRepairsID = LookupIds.RepairContractorEstimate, ContractorEstimate = 1m, CommunityEstimate = 2m, ComputedDamages = 3m,
			ValueOfBuildingID = LookupIds.ValueAdjustedTax, TaxAssessedValue = 1000m, FactorAdjustment = 1.5m, MarketAppraisal = 7m, ComputedActualCashValue = 8m,
		};
		Assert.Equal(1m, CumulativeDamage.RepairCost(a));
		Assert.Equal(1500m, CumulativeDamage.BuildingValue(a));
		a.CostOfRepairsID = LookupIds.RepairCommunityEstimate;
		a.ValueOfBuildingID = LookupIds.ValueMarketAppraisal;
		Assert.Equal(2m, CumulativeDamage.RepairCost(a));
		Assert.Equal(7m, CumulativeDamage.BuildingValue(a));
		a.CostOfRepairsID = null;
		a.ValueOfBuildingID = null;
		Assert.Equal(3m, CumulativeDamage.RepairCost(a));
		Assert.Equal(8m, CumulativeDamage.BuildingValue(a));
	}

	[Fact]
	public void NeverChangesTheAssessments()
	{
		AssessmentRecord current = Event(new DateTime(2026, 9, 1), 30m);
		AssessmentRecord earlier = Event(new DateTime(2024, 9, 1), 30m);

		CumulativeResult r = CumulativeDamage.Evaluate(new[] { current, earlier }, On);

		Assert.True(r.MeetsThreshold);
		Assert.Equal(30m, current.PercentDamage);
		Assert.Equal("Not Substantially Damaged", current.DamageMessage);
		Assert.Equal(30m, earlier.PercentDamage);
		Assert.Equal("Not Substantially Damaged", earlier.DamageMessage);
		Assert.Contains(r.Explanation, l => l.Contains("keeps its own percent damaged"));
	}

	[Fact]
	public void SeveralStructures_AreRefused()
	{
		AssessmentRecord other = Event(new DateTime(2026, 9, 1), 30m);
		other.PropertyID = Guid.NewGuid();
		Assert.Throws<ArgumentException>(() => CumulativeDamage.Evaluate(new[] { Event(new DateTime(2025, 1, 1), 1m), other }, On));
	}
}
