using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Records;

namespace Sde.Tests.Core;

public class EventSummaryTests
{
	/// <summary>A synthetic assessed structure. Value and cost use the Computed ACV and Computed Damages bases.</summary>
	private static AssessmentFile Make(string street, string community, string inspector, decimal? percent, string message, decimal value = 100000m, decimal cost = 0m, decimal? lat = null, decimal? lon = null, DateTime? damaged = null, DateTime? inspected = null)
	{
		PropertyRecord p = new PropertyRecord
		{
			PropertyID = Guid.NewGuid(), StreetNumber = "10", StreetName = street, NFIPCommunityName = community, NFIPCID = "000001",
			Latitude = lat, Longitude = lon,
		};
		AssessmentRecord a = new AssessmentRecord
		{
			AssessmentID = Guid.NewGuid(), PropertyID = p.PropertyID, InspectorName = inspector, PercentDamage = percent, DamageMessage = message,
			ValueOfBuildingID = LookupIds.ValueComputedAcv, CostOfRepairsID = LookupIds.RepairComputedDamages,
			ComputedActualCashValue = value, ComputedDamages = cost, DateDamageOccurred = damaged, DateofInspection = inspected,
		};
		return new AssessmentFile { Property = p, Assessment = a };
	}

	[Fact]
	public void Totals_Classes_Groups_AndDates()
	{
		List<AssessmentFile> files = new()
		{
			Make("Oak", "Sample Town", "Ann", 75m, Determinations.SubstantialMessage, 200000m, 150000m, 40.0m, -90.0m, new DateTime(2026, 5, 1), new DateTime(2026, 5, 4)),
			Make("oak", "Sample Town", "Ann", 45m, Determinations.NotSubstantialMessage, 100000m, 45000m, 40.001m, -90.001m, new DateTime(2026, 5, 2), new DateTime(2026, 5, 6)),
			Make("Elm", "Other Village", "Bob", 10m, Determinations.NotSubstantialMessage, 100000m, 10000m),
			Make("Elm", "Other Village", null, null, Determinations.IncompleteMessage),
			Make("Elm", "Other Village", "Bob", 0m, "Damage Undetermined: Structure Inaccessible"),
		};
		List<PropertyRecord> all = files.Select(f => f.Property).ToList();
		all.Add(new PropertyRecord { PropertyID = Guid.NewGuid() });
		all.Add(new PropertyRecord { PropertyID = Guid.NewGuid() });

		EventSummary s = EventSummary.Build(files, all, "Spring flood");

		Assert.Equal("Spring flood", s.EventName);
		Assert.Equal(5, s.AssessmentCount);
		Assert.Equal(5, s.AssessedStructures);
		Assert.Equal(2, s.NotYetAssessed);
		Assert.Equal(7, s.TotalStructures);
		Assert.Equal(1, s.Count(DeterminationClass.Substantial));
		Assert.Equal(1, s.Count(DeterminationClass.Close));
		Assert.Equal(1, s.Count(DeterminationClass.NotSubstantial));
		Assert.Equal(1, s.Count(DeterminationClass.Incomplete));
		Assert.Equal(1, s.Count(DeterminationClass.Undetermined));
		// Value counts only where there is a nonzero percent: 200k + 100k + 100k. Cost: 150k + 45k + 10k + 0 (percent 0).
		Assert.Equal(400000m, s.TotalBuildingValue);
		Assert.Equal(205000m, s.TotalRepairCost);

		Assert.Equal(new DateTime(2026, 5, 1), s.DamageStart);
		Assert.Equal(new DateTime(2026, 5, 2), s.DamageEnd);
		Assert.Equal(new DateTime(2026, 5, 4), s.InspectionStart);
		Assert.Equal(new DateTime(2026, 5, 6), s.InspectionEnd);

		// Street grouping ignores case; Elm (3) before Oak (2).
		Assert.Equal(new[] { "Elm", "Oak" }, s.ByStreet.Select(g => g.Label).ToArray());
		Assert.Equal(2, s.ByStreet[1].Count);
		Assert.Equal(1, s.ByStreet[1].Substantial);
		Assert.Equal(1, s.ByStreet[1].Close);
		Assert.Equal(2, s.ByStreet[0].Other);
		Assert.Equal(new[] { "Other Village", "Sample Town" }, s.ByCommunity.Select(g => g.Label).ToArray());
		Assert.Contains(s.ByInspector, g => g.Label == EventSummary.NotRecorded && g.Count == 1);
		Assert.Equal(3, s.ByInspector.Count);
		Assert.Equal(2, s.Points.Count);
	}

	[Fact]
	public void Empty_HasNoDatesAndZeroCounts()
	{
		EventSummary s = EventSummary.Build(new List<AssessmentFile>());
		Assert.Equal(0, s.TotalStructures);
		Assert.Null(s.DamageStart);
		Assert.All(Enum.GetValues<DeterminationClass>(), c => Assert.Equal(0, s.Count(c)));
		Assert.Empty(s.ByStreet);
	}

	[Fact]
	public void AssessmentAmounts_MatchTheReportRules()
	{
		AssessmentRecord a = new AssessmentRecord { PercentDamage = 50m, ValueOfBuildingID = LookupIds.ValueAdjustedTax, TaxAssessedValue = 80000m, FactorAdjustment = 1.25m, CostOfRepairsID = LookupIds.RepairContractorEstimate };
		Assert.Equal(100000m, AssessmentAmounts.BuildingValue(a));
		Assert.Null(AssessmentAmounts.RepairCost(a));
		a.ContractorEstimate = 60000m;
		Assert.Equal(60000m, AssessmentAmounts.RepairCost(a));
		a.PercentDamage = 0m;
		Assert.Null(AssessmentAmounts.BuildingValue(a));
		Assert.Equal(0m, AssessmentAmounts.RepairCost(a));
		a.PercentDamage = null;
		Assert.Null(AssessmentAmounts.RepairCost(a));
	}
}
