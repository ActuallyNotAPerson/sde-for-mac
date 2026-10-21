using System.Globalization;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Tests.Core;

/// <summary>The one rule behind the Structures tiles, the GIS files and the event summary.</summary>
public class DeterminationTests
{
	[Theory]
	[InlineData("39.99", DeterminationClass.NotSubstantial)]
	[InlineData("40", DeterminationClass.Close)]
	[InlineData("49.99", DeterminationClass.Close)]
	[InlineData("50", DeterminationClass.Substantial)]
	[InlineData("0", DeterminationClass.NotSubstantial)]
	[InlineData("120", DeterminationClass.Substantial)]
	public void Percent_BoundariesAreInclusiveAtTheLowerEdge(string percent, DeterminationClass expected)
	{
		decimal pct = decimal.Parse(percent, CultureInfo.InvariantCulture);
		AssessmentRecord a = new AssessmentRecord
		{
			PercentDamage = pct,
			DamageMessage = pct >= 50m ? Determinations.SubstantialMessage : Determinations.NotSubstantialMessage,
		};
		Assert.Equal(expected, Determinations.Classify(a));
	}

	[Fact]
	public void NoAssessment_NullMessageAndIncompleteMessage_AreIncomplete()
	{
		Assert.Equal(DeterminationClass.Incomplete, Determinations.Classify((AssessmentRecord)null));
		Assert.Equal(DeterminationClass.Incomplete, Determinations.Classify(new AssessmentRecord { PercentDamage = 80m }));
		Assert.Equal(DeterminationClass.Incomplete, Determinations.Classify(new AssessmentRecord { DamageMessage = Determinations.IncompleteMessage, PercentDamage = 80m }));
	}

	[Fact]
	public void AnyOtherMessage_IsUndetermined()
	{
		AssessmentRecord a = new AssessmentRecord { DamageMessage = "Damage Undetermined: Structure Inaccessible", PercentDamage = 0m };
		Assert.Equal(DeterminationClass.Undetermined, Determinations.Classify(a));
	}

	[Fact]
	public void CustomThresholds_AreUsed()
	{
		AssessmentRecord a = new AssessmentRecord { DamageMessage = Determinations.NotSubstantialMessage, PercentDamage = 35m };
		Assert.Equal(DeterminationClass.NotSubstantial, Determinations.Classify(a));
		Assert.Equal(DeterminationClass.Close, Determinations.Classify(a, 50m, 30m));
		Assert.Equal(DeterminationClass.Substantial, Determinations.Classify(a, 35m, 30m));
	}

	[Fact]
	public void Labels_AreDistinct()
	{
		string[] labels = Enum.GetValues<DeterminationClass>().Select(Determinations.Label).ToArray();
		Assert.Equal(labels.Length, labels.Distinct().Count());
	}
}
