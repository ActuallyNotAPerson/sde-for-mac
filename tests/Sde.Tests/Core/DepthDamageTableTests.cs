using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Tests.Core;

/// <summary>Community depth-damage tables: straight-line interpolation between rows, the end rows' values beyond them.</summary>
public class DepthDamageTableTests
{
	private static DepthDamageTable Table() => new DepthDamageTable
	{
		Name = "Synthetic",
		BuildingType = DepthTableBuildingType.Residential,
		Rows =
		{
			new DepthDamageRow { DepthFt = 0m, Percents = { ["Floor Finish"] = 20m, ["Plumbing"] = null } },
			new DepthDamageRow { DepthFt = 2m, Percents = { ["Floor Finish"] = 60m, ["Plumbing"] = 30m } },
			new DepthDamageRow { DepthFt = 5m, Percents = { ["Floor Finish"] = 100m, ["Plumbing"] = 60m } },
		},
	};

	[Theory]
	[InlineData(-1, 20)]       // below the first row: the first row's value
	[InlineData(0, 20)]
	[InlineData(1, 40)]
	[InlineData(2, 60)]
	[InlineData(3.5, 80)]
	[InlineData(5, 100)]
	[InlineData(9, 100)]       // above the last row: the last row's value
	public void Interpolates_AndClampsAtTheEnds(double depth, double expected) =>
		Assert.Equal((decimal)expected, Table().Percent("Floor Finish", (decimal)depth));

	[Fact]
	public void EmptyCells_AreSkipped_AndMissingElementsGetNothing()
	{
		DepthDamageTable t = Table();
		Assert.Equal(30m, t.Percent("Plumbing", 0m));        // no value at 0 ft: the first row that has one
		Assert.Equal(45m, t.Percent("Plumbing", 3.5m));
		Assert.Null(t.Percent("HVAC", 2m));
		Dictionary<string, decimal> s = t.Suggest(new[] { "Floor Finish", "Plumbing", "HVAC" }, 1m);
		Assert.Equal(new[] { "Floor Finish", "Plumbing" }, s.Keys);
		Assert.Equal(40m, s["Floor Finish"]);
		Assert.Equal(30m, s["Plumbing"]);
	}

	[Fact]
	public void Values_AreRoundedToTwoDecimals_AndKeptInRange()
	{
		DepthDamageTable t = new DepthDamageTable
		{
			Rows =
			{
				new DepthDamageRow { DepthFt = 0m, Percents = { ["Foundation"] = 0m } },
				new DepthDamageRow { DepthFt = 3m, Percents = { ["Foundation"] = 10m } },
				new DepthDamageRow { DepthFt = 4m, Percents = { ["Foundation"] = 150m } },
			},
		};
		Assert.Equal(3.33m, t.Percent("Foundation", 1m));
		Assert.Equal(100m, t.Percent("Foundation", 4m));
	}

	[Fact]
	public void AppliesTo_TheBuildingType()
	{
		DepthDamageTable t = Table();
		Assert.True(t.AppliesTo(new PropertyRecord { StructureTypeID = 1 }));
		Assert.False(t.AppliesTo(new PropertyRecord { StructureTypeID = 2 }));
		Assert.False(t.AppliesTo(new PropertyRecord()));
	}
}
