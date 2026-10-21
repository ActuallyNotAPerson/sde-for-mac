using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Tests.Core;

/// <summary>Substantial improvement: thresholds, look-back windows, and missing market values.</summary>
public class ImprovementCalculatorTests
{
	private static readonly Guid Property = Guid.NewGuid();

	private static ImprovementProjectRecord Project(decimal? cost, DateTime? date, string description = "Work", bool included = true, string exclusion = null) => new()
	{
		ProjectID = Guid.NewGuid(),
		PropertyID = Property,
		Description = description,
		ProjectDate = date,
		Cost = cost,
		Included = included,
		ExclusionReason = exclusion,
	};

	[Theory]
	[InlineData(50000, 100000, true, 50.0)]
	[InlineData(49999, 100000, false, 49.9)]
	[InlineData(49999, 99998, true, 50.0)]
	[InlineData(49990, 99990, false, 49.9)]   // 49.995% never prints as 50.0%
	[InlineData(60000, 100000, true, 60.0)]
	[InlineData(0, 100000, false, 0.0)]
	public void Threshold_IsEqualOrExceeds(int costDollars, int valueDollars, bool si, double percentShown)
	{
		decimal cost = costDollars;
		decimal percent = (decimal)percentShown;
		SiResult r = ImprovementCalculator.Evaluate(Project(cost, new DateTime(2026, 5, 1)), null, valueDollars);

		Assert.True(r.CanDetermine);
		Assert.Equal(si, r.IsSubstantialImprovement);
		Assert.Equal(percent, r.Percent);
		Assert.Equal(cost, r.CostCounted);
		Assert.Equal(si ? SiResult.SubstantialImprovement : SiResult.NotSubstantialImprovement, r.ResultText);
	}

	[Fact]
	public void CustomThreshold_IsUsed()
	{
		SiResult r = ImprovementCalculator.Evaluate(Project(40000m, new DateTime(2026, 5, 1)), null, 100000m, new SiSettings { ThresholdPercent = 40m });

		Assert.True(r.IsSubstantialImprovement);
		Assert.Contains(r.Explanation, l => l.Contains("40% threshold"));
	}

	[Fact]
	public void DefaultSettings_CountOnlyThisProject()
	{
		ImprovementProjectRecord current = Project(30000m, new DateTime(2026, 5, 1), "Addition");
		ImprovementProjectRecord earlier = Project(30000m, new DateTime(2025, 5, 1), "Roof");

		SiResult r = ImprovementCalculator.Evaluate(current, new[] { earlier }, 100000m);

		Assert.False(r.IsSubstantialImprovement);
		Assert.Equal(30000m, r.CostCounted);
		Assert.Equal(new[] { current }, r.CountedProjects);
		Assert.Equal(0, r.LookbackYears);
		Assert.Null(r.WindowStart);
		Assert.Contains("only this project", r.Projects[1].Reason);
		Assert.Contains(r.Explanation, l => l.Contains("Only this project is counted"));
	}

	[Fact]
	public void LookBack_CountsEarlierProjectsInsideTheWindowOnly()
	{
		ImprovementProjectRecord current = Project(20000m, new DateTime(2026, 5, 1), "Addition");
		ImprovementProjectRecord inside = Project(15000m, new DateTime(2022, 6, 1), "Roof");
		ImprovementProjectRecord onStart = Project(10000m, new DateTime(2021, 5, 1), "Windows");
		ImprovementProjectRecord before = Project(90000m, new DateTime(2021, 4, 30), "Old remodel");
		ImprovementProjectRecord after = Project(90000m, new DateTime(2026, 8, 1), "Later work");
		ImprovementProjectRecord excluded = Project(5000m, new DateTime(2024, 1, 1), "Repainting", included: false, exclusion: "maintenance");
		ImprovementProjectRecord undated = Project(5000m, null, "Fence");
		ImprovementProjectRecord noCost = Project(null, new DateTime(2024, 2, 1), "Deck");

		SiResult r = ImprovementCalculator.Evaluate(current, new[] { inside, onStart, before, after, excluded, undated, noCost, current }, 100000m,
			new SiSettings { LookbackYears = 5 });

		Assert.Equal(45000m, r.CostCounted);
		Assert.Equal(45.0m, r.Percent);
		Assert.False(r.IsSubstantialImprovement);
		Assert.Equal(new DateTime(2021, 5, 1), r.WindowStart);
		Assert.Equal(new[] { current, inside, onStart }, r.CountedProjects);
		Assert.Equal(8, r.Projects.Count);   // the current project listed once
		Assert.Contains("before the look-back period", r.Projects.Single(p => p.Project == before).Reason);
		Assert.Contains("after this project", r.Projects.Single(p => p.Project == after).Reason);
		Assert.Equal("Excluded: maintenance", r.Projects.Single(p => p.Project == excluded).Reason);
		Assert.Contains("no project date", r.Projects.Single(p => p.Project == undated).Reason);
		Assert.Contains("no cost", r.Projects.Single(p => p.Project == noCost).Reason);
		Assert.Contains(r.Explanation, l => l.Contains("2 earlier projects are counted"));
	}

	[Fact]
	public void LookBack_CanTipTheDetermination()
	{
		ImprovementProjectRecord current = Project(30000m, new DateTime(2026, 5, 1));
		ImprovementProjectRecord earlier = Project(25000m, new DateTime(2020, 7, 1));

		Assert.False(ImprovementCalculator.Evaluate(current, new[] { earlier }, 100000m, new SiSettings { LookbackYears = 5 }).IsSubstantialImprovement);
		Assert.True(ImprovementCalculator.Evaluate(current, new[] { earlier }, 100000m, new SiSettings { LookbackYears = 10 }).IsSubstantialImprovement);
		Assert.False(ImprovementCalculator.Evaluate(current, new[] { earlier }, 100000m, new SiSettings { LookbackYears = 10, IncludePriorProjects = false }).IsSubstantialImprovement);
	}

	[Fact]
	public void UndatedCurrentProject_UsesAsOfForTheWindow()
	{
		ImprovementProjectRecord current = Project(30000m, null);
		ImprovementProjectRecord earlier = Project(25000m, new DateTime(2024, 1, 1));

		SiResult r = ImprovementCalculator.Evaluate(current, new[] { earlier }, 100000m, new SiSettings { LookbackYears = 3 }, asOf: new DateTime(2026, 10, 1));

		Assert.Equal(new DateTime(2023, 10, 1), r.WindowStart);
		Assert.True(r.IsSubstantialImprovement);
	}

	[Theory]
	[InlineData(null)]
	[InlineData(0)]
	[InlineData(-5)]
	public void MissingOrZeroMarketValue_CannotBeDetermined(int? value)
	{
		SiResult r = ImprovementCalculator.Evaluate(Project(80000m, new DateTime(2026, 5, 1)), null, (decimal?)value);

		Assert.False(r.CanDetermine);
		Assert.False(r.IsSubstantialImprovement);
		Assert.Null(r.Percent);
		Assert.Equal(80000m, r.CostCounted);
		Assert.Equal(SiResult.CannotDetermine, r.ResultText);
		Assert.Contains(r.Explanation, l => l.Contains("market value"));
	}

	[Fact]
	public void MissingCurrentCost_CountsAsZero()
	{
		SiResult r = ImprovementCalculator.Evaluate(Project(null, new DateTime(2026, 5, 1)), null, 100000m);

		Assert.Equal(0m, r.CostCounted);
		Assert.Equal(0m, r.Percent);
		Assert.Contains(r.Explanation, l => l.Contains("No cost has been entered"));
	}

	[Fact]
	public void BadSettings_AreRefused()
	{
		ImprovementProjectRecord p = Project(1m, DateTime.Today);
		Assert.Throws<ArgumentException>(() => ImprovementCalculator.Evaluate(p, null, 1m, new SiSettings { ThresholdPercent = 0m }));
		Assert.Throws<ArgumentException>(() => ImprovementCalculator.Evaluate(p, null, 1m, new SiSettings { LookbackYears = -1 }));
		Assert.Throws<ArgumentNullException>(() => ImprovementCalculator.Evaluate(null, null, 1m));
	}

	[Fact]
	public void ToRecord_CarriesTheResult()
	{
		ImprovementProjectRecord current = Project(60000m, new DateTime(2026, 5, 1));
		SiResult r = ImprovementCalculator.Evaluate(current, null, 100000m, new SiSettings { LookbackYears = 0 });

		SIDeterminationRecord d = r.ToRecord(Property, new DateTime(2026, 10, 20), "Professional appraisal", "A. Official", "Synthetic");

		Assert.Equal(Property, d.PropertyID);
		Assert.Equal(60000m, d.CumulativeCost);
		Assert.Equal(60.0m, d.Percent);
		Assert.Equal(50m, d.ThresholdPercent);
		Assert.Equal(0, d.LookbackYears);
		Assert.Equal(SiResult.SubstantialImprovement, d.Result);
		Assert.Equal("Professional appraisal", d.MarketValueBasis);
	}

	[Theory]
	[InlineData(typeof(ImprovementProjectRecord), "ImprovementProject")]
	[InlineData(typeof(SIDeterminationRecord), "SIDetermination")]
	public void Records_MatchTheSchemaColumns(Type record, string table)
	{
		using TempDatabase db = new TempDatabase();
		using SqliteConnection c = db.Database.Connect();
		List<string> columns = c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").ToList();
		List<string> properties = record.GetProperties().Select(p => p.Name).ToList();

		Assert.NotEmpty(columns);
		Assert.Equal(columns, properties);
	}

	[Fact]
	public void Records_RoundTripThroughTheDatabase()
	{
		using TempDatabase db = new TempDatabase();
		PropertyRecord p = db.Store.SaveProperty(new PropertyRecord { StreetNumber = "1", StreetName = "Sample" });
		ImprovementProjectRecord project = new ImprovementProjectRecord
		{
			ProjectID = Guid.NewGuid(), PropertyID = p.PropertyID, PermitNumber = "P-0001", Description = "Addition",
			ProjectDate = new DateTime(2026, 5, 1), Cost = 1234.56m, CostBasis = "Contractor's estimate", Included = false,
			ExclusionReason = "Test", CreatedOn = new DateTime(2026, 5, 2, 9, 30, 0),
		};
		SIDeterminationRecord determination = ImprovementCalculator.Evaluate(project, null, 10000m)
			.ToRecord(p.PropertyID, new DateTime(2026, 5, 3), "Appraisal", "Inspector", null);

		using SqliteConnection c = db.Database.Connect();
		TableMap<ImprovementProjectRecord>.Insert(c, project);
		TableMap<SIDeterminationRecord>.Insert(c, determination);
		ImprovementProjectRecord back = TableMap<ImprovementProjectRecord>.Get(c, new { project.ProjectID });
		SIDeterminationRecord dBack = TableMap<SIDeterminationRecord>.Get(c, new { determination.DeterminationID });

		Assert.Equal(project.Cost, back.Cost);
		Assert.Equal(project.ProjectDate, back.ProjectDate);
		Assert.False(back.Included);
		Assert.Equal("P-0001", back.PermitNumber);
		Assert.Equal(12.3m, dBack.Percent);
		Assert.Equal(SiResult.NotSubstantialImprovement, dBack.Result);
	}
}
