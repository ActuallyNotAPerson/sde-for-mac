using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>
/// How a structure's result is grouped on the Structures tiles, the editor's result panel, the GIS export and the
/// event summary. The order runs from "no answer yet" to "substantially damaged".
/// </summary>
public enum DeterminationClass
{
	/// <summary>No assessment, or the calculator is still waiting for data ("Data Entry Incomplete").</summary>
	Incomplete,
	/// <summary>The inspector recorded a reason the damage could not be determined.</summary>
	Undetermined,
	/// <summary>Below the "close" percentage.</summary>
	NotSubstantial,
	/// <summary>Not substantially damaged, but within reach of the threshold, so worth a second look.</summary>
	Close,
	/// <summary>At or above the substantial-damage percentage.</summary>
	Substantial,
}

/// <summary>The single rule for turning an assessment's result into a <see cref="DeterminationClass"/>.</summary>
public static class Determinations
{
	/// <summary>The damage message the calculator stores when it has no answer yet.</summary>
	public const string IncompleteMessage = "Data Entry Incomplete";

	public const string SubstantialMessage = "Substantially Damaged";

	public const string NotSubstantialMessage = "Not Substantially Damaged";

	/// <summary>The NFIP substantial-damage threshold, in percent.</summary>
	public const decimal DefaultSubstantialPercent = 50m;

	/// <summary>The "close" band starts here, in percent.</summary>
	public const decimal DefaultClosePercent = 40m;

	/// <summary>
	/// Classifies an assessment. A missing assessment is Incomplete. Otherwise the calculator's damage message decides
	/// whether there is a percentage to look at at all: no message (null) or "Data Entry Incomplete" is Incomplete, any message
	/// other than the two determinations (an undetermined-damage reason) is Undetermined, and for the two determinations
	/// the percentage is compared with the thresholds (at or above is in the class, so 40 is Close and 50 is Substantial).
	/// </summary>
	public static DeterminationClass Classify(AssessmentRecord assessment, decimal substantialPercent = DefaultSubstantialPercent, decimal closePercent = DefaultClosePercent)
	{
		if (assessment == null)
		{
			return DeterminationClass.Incomplete;
		}
		return Classify(assessment.DamageMessage, assessment.PercentDamage, substantialPercent, closePercent);
	}

	/// <summary>The same rule for callers that only have the message and percentage (for example a list row).</summary>
	public static DeterminationClass Classify(string damageMessage, decimal? percentDamage, decimal substantialPercent = DefaultSubstantialPercent, decimal closePercent = DefaultClosePercent)
	{
		if (damageMessage == null || damageMessage == IncompleteMessage)
		{
			return DeterminationClass.Incomplete;
		}
		if (damageMessage != SubstantialMessage && damageMessage != NotSubstantialMessage)
		{
			return DeterminationClass.Undetermined;
		}
		if (percentDamage >= substantialPercent)
		{
			return DeterminationClass.Substantial;
		}
		return percentDamage >= closePercent ? DeterminationClass.Close : DeterminationClass.NotSubstantial;
	}

	/// <summary>Short plain-English name for lists, tables and GIS attributes.</summary>
	public static string Label(DeterminationClass c) => c switch
	{
		DeterminationClass.Incomplete => "Incomplete",
		DeterminationClass.Undetermined => "Undetermined",
		DeterminationClass.NotSubstantial => "Not substantial",
		DeterminationClass.Close => "Close",
		DeterminationClass.Substantial => "Substantial",
		_ => throw new ArgumentOutOfRangeException(nameof(c)),
	};
}
