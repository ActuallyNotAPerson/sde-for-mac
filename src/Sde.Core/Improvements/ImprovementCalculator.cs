using System.Globalization;

namespace Sde.Core.Improvements;

/// <summary>How a community counts improvements toward substantial improvement (SI). Kept in Settings.</summary>
public sealed class SiSettings
{
	/// <summary>The SI threshold as a percentage of market value. NFIP minimum: 50.</summary>
	public decimal ThresholdPercent { get; init; } = 50m;

	/// <summary>
	/// How many years of earlier projects count together with this one (cumulative SI). 0 (the NFIP minimum) means
	/// only this project is counted.
	/// </summary>
	public int LookbackYears { get; init; }

	/// <summary>Whether earlier projects in the look-back period are added to this one. Has no effect when <see cref="LookbackYears"/> is 0.</summary>
	public bool IncludePriorProjects { get; init; } = true;
}

/// <summary>One project the calculation looked at, and whether its cost was counted.</summary>
public sealed class SiProjectLine
{
	public ImprovementProjectRecord Project { get; init; }

	/// <summary>True for the project being determined.</summary>
	public bool IsCurrent { get; init; }

	public bool Counted { get; init; }

	/// <summary>The cost counted (0 when not counted or no cost was entered).</summary>
	public decimal CostCounted { get; init; }

	/// <summary>Why the project was or was not counted, in plain English.</summary>
	public string Reason { get; init; }
}

/// <summary>The result of <see cref="ImprovementCalculator.Evaluate"/>.</summary>
public sealed class SiResult
{
	public const string SubstantialImprovement = "Substantial Improvement";
	public const string NotSubstantialImprovement = "Not Substantial Improvement";
	public const string CannotDetermine = "Cannot be determined";

	/// <summary>The total improvement cost counted.</summary>
	public decimal CostCounted { get; init; }

	public decimal? MarketValue { get; init; }

	/// <summary>
	/// Cost as a percentage of market value, cut (not rounded) to one decimal so the printed figure never reads 50.0%
	/// for a cost that is just under the threshold. Null when there is no usable market value.
	/// </summary>
	public decimal? Percent { get; init; }

	public decimal ThresholdPercent { get; init; }

	public int LookbackYears { get; init; }

	/// <summary>The earliest project date counted (the start of the look-back period); null when only this project counts.</summary>
	public DateTime? WindowStart { get; init; }

	/// <summary>False when there is no market value (or it is zero or negative), so no percentage can be worked out.</summary>
	public bool CanDetermine { get; init; }

	/// <summary>True when the cost counted equals or exceeds the threshold percentage of market value.</summary>
	public bool IsSubstantialImprovement { get; init; }

	/// <summary>Every project considered: the current one first, then earlier ones newest first.</summary>
	public IReadOnlyList<SiProjectLine> Projects { get; init; } = Array.Empty<SiProjectLine>();

	/// <summary>The projects whose cost was counted.</summary>
	public IReadOnlyList<ImprovementProjectRecord> CountedProjects => Projects.Where(p => p.Counted).Select(p => p.Project).ToList();

	/// <summary>Plain-English lines explaining the result, for the screen and the determination report.</summary>
	public IReadOnlyList<string> Explanation { get; init; } = Array.Empty<string>();

	/// <summary><see cref="SubstantialImprovement"/>, <see cref="NotSubstantialImprovement"/> or <see cref="CannotDetermine"/>.</summary>
	public string ResultText => !CanDetermine ? CannotDetermine : IsSubstantialImprovement ? SubstantialImprovement : NotSubstantialImprovement;

	/// <summary>A determination row for this result (a new DeterminationID each call).</summary>
	public SIDeterminationRecord ToRecord(Guid propertyId, DateTime determinedOn, string marketValueBasis, string determinedBy, string notes) => new()
	{
		DeterminationID = Guid.NewGuid(),
		PropertyID = propertyId,
		DeterminedOn = determinedOn,
		MarketValue = MarketValue,
		MarketValueBasis = marketValueBasis,
		LookbackYears = LookbackYears,
		ThresholdPercent = ThresholdPercent,
		CumulativeCost = CostCounted,
		Percent = Percent,
		Result = ResultText,
		DeterminedBy = determinedBy,
		Notes = notes,
	};
}

/// <summary>
/// Substantial improvement: whether the cost of an improvement (plus, if the community counts them, earlier
/// improvements within a look-back period) equals or exceeds a percentage of the structure's market value
/// (FEMA P-758). This is separate from substantial damage and never changes an assessment's PercentDamage or
/// DamageMessage.
/// </summary>
public static class ImprovementCalculator
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>
	/// Works out the SI percentage and determination for <paramref name="currentProject"/>.
	/// <para>
	/// The current project always counts (its cost, or 0 when none is entered). An earlier project counts only when
	/// the settings include prior projects, the look-back period is more than 0 years, it is marked Included, it has a
	/// date and a cost, and its date falls on or after the start of the period (the current project's date, or
	/// <paramref name="asOf"/> or today when it has none, minus the look-back years) and not after the current project.
	/// </para>
	/// </summary>
	/// <param name="currentProject">The project being determined.</param>
	/// <param name="priorProjects">Other projects on the same structure (may include the current one, which is skipped).</param>
	/// <param name="marketValue">Market value of the structure only, before the improvement. Null or 0 means the result cannot be determined.</param>
	/// <param name="settings">Threshold and look-back; null uses the defaults (50 percent, this project only).</param>
	/// <param name="asOf">The date the look-back period ends when the current project has no date (default: today).</param>
	public static SiResult Evaluate(ImprovementProjectRecord currentProject, IReadOnlyList<ImprovementProjectRecord> priorProjects, decimal? marketValue,
		SiSettings settings = null, DateTime? asOf = null)
	{
		ArgumentNullException.ThrowIfNull(currentProject);
		settings ??= new SiSettings();
		if (settings.ThresholdPercent <= 0m)
		{
			throw new ArgumentException("The SI threshold must be more than 0 percent.", nameof(settings));
		}
		if (settings.LookbackYears < 0)
		{
			throw new ArgumentException("The look-back period cannot be negative.", nameof(settings));
		}

		List<SiProjectLine> lines = new();
		List<string> explanation = new();
		decimal total = 0m;

		decimal currentCost = currentProject.Cost ?? 0m;
		total += currentCost;
		lines.Add(new SiProjectLine
		{
			Project = currentProject,
			IsCurrent = true,
			Counted = true,
			CostCounted = currentCost,
			Reason = currentProject.Cost is null ? "This project (no cost entered, counted as $0.00)" : "This project",
		});
		if (currentProject.Cost is null)
		{
			explanation.Add("No cost has been entered for this project, so it is counted as $0.00.");
		}

		bool countPrior = settings.IncludePriorProjects && settings.LookbackYears > 0;
		DateTime end = (currentProject.ProjectDate ?? asOf ?? DateTime.Today).Date;
		DateTime? windowStart = countPrior ? end.AddYears(-settings.LookbackYears) : null;

		IEnumerable<ImprovementProjectRecord> others = (priorProjects ?? Array.Empty<ImprovementProjectRecord>())
			.Where(p => p != null && p != currentProject && p.ProjectID != currentProject.ProjectID)
			.OrderByDescending(p => p.ProjectDate ?? DateTime.MinValue);
		foreach (ImprovementProjectRecord p in others)
		{
			string reason = null;
			if (!countPrior)
			{
				reason = settings.LookbackYears == 0 ? "Not counted: only this project is counted" : "Not counted: earlier projects are not added";
			}
			else if (!p.Included)
			{
				reason = string.IsNullOrWhiteSpace(p.ExclusionReason) ? "Excluded" : "Excluded: " + p.ExclusionReason.Trim();
			}
			else if (p.ProjectDate is not DateTime date)
			{
				reason = "Not counted: no project date";
			}
			else if (date.Date < windowStart.Value)
			{
				reason = $"Not counted: before the look-back period (starts {windowStart.Value.ToString("MM/dd/yyyy", Us)})";
			}
			else if (date.Date > end)
			{
				reason = "Not counted: dated after this project";
			}
			else if (p.Cost is null)
			{
				reason = "Not counted: no cost entered";
			}

			bool counted = reason == null;
			decimal cost = counted ? p.Cost.Value : 0m;
			total += cost;
			lines.Add(new SiProjectLine { Project = p, Counted = counted, CostCounted = cost, Reason = reason ?? "Earlier project in the look-back period" });
		}

		int priorCounted = lines.Count(l => !l.IsCurrent && l.Counted);
		if (countPrior)
		{
			explanation.Add($"Improvements are counted over {Years(settings.LookbackYears)}: projects dated from {windowStart.Value.ToString("MM/dd/yyyy", Us)} to {end.ToString("MM/dd/yyyy", Us)}.");
			explanation.Add(priorCounted == 0
				? "No earlier project in that period is counted."
				: $"{priorCounted} earlier project{(priorCounted == 1 ? " is" : "s are")} counted together with this one.");
		}
		else
		{
			explanation.Add("Only this project is counted (no look-back period).");
		}
		explanation.Add($"Improvement cost counted: {Money(total)}.");

		bool canDetermine = marketValue is decimal mv && mv > 0m;
		decimal? percent = null;
		bool isSi = false;
		if (canDetermine)
		{
			decimal exact = total * 100m / marketValue.Value;
			percent = Math.Floor(exact * 10m) / 10m;
			isSi = exact >= settings.ThresholdPercent;
			explanation.Add($"Market value of the structure: {Money(marketValue.Value)}.");
			explanation.Add($"{Money(total)} is {percent.Value.ToString("#,##0.0", Us)}% of {Money(marketValue.Value)}.");
			explanation.Add(isSi
				? $"That equals or exceeds the {Pct(settings.ThresholdPercent)} threshold, so this is a substantial improvement."
				: $"That is below the {Pct(settings.ThresholdPercent)} threshold, so this is not a substantial improvement.");
		}
		else
		{
			explanation.Add(marketValue is null
				? "No market value has been entered for the structure, so the percentage cannot be worked out."
				: "The market value of the structure must be more than $0.00 to work out the percentage.");
		}

		return new SiResult
		{
			CostCounted = total,
			MarketValue = marketValue,
			Percent = percent,
			ThresholdPercent = settings.ThresholdPercent,
			LookbackYears = countPrior ? settings.LookbackYears : 0,
			WindowStart = windowStart,
			CanDetermine = canDetermine,
			IsSubstantialImprovement = isSi,
			Projects = lines,
			Explanation = explanation,
		};
	}

	private static string Money(decimal d) => d.ToString("C2", Us);

	private static string Pct(decimal d) => d.ToString("0.##", Us) + "%";

	private static string Years(int n) => n == 1 ? "1 year" : n + " years";
}
