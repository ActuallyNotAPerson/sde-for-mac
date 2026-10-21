using System.Globalization;
using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>How damage from several events is added up.</summary>
public enum CumulativeMethod
{
	/// <summary>Adds each event's percent damaged (Assessment.PercentDamage).</summary>
	SumOfPercents,

	/// <summary>Adds each event's repair cost and divides by the structure's value in the most recent event.</summary>
	TotalDamageOverCurrentValue,
}

/// <summary>
/// A community's cumulative damage rule (kept in Settings). Off by default: the NFIP minimum looks at each event
/// on its own, and only communities whose ordinance says so count damage cumulatively.
/// </summary>
public sealed class CumulativeSettings
{
	public bool Enabled { get; init; }

	public CumulativeMethod Method { get; init; } = CumulativeMethod.SumOfPercents;

	/// <summary>
	/// How many years before the most recent event are counted (events on or after that date count). 0 counts every
	/// event on record.
	/// </summary>
	public int LookbackYears { get; init; } = 10;

	public decimal ThresholdPercent { get; init; } = 50m;
}

/// <summary>Whether <see cref="CumulativeDamage.Evaluate"/> produced a total.</summary>
public enum CumulativeStatus
{
	/// <summary>Cumulative damage is turned off in Settings.</summary>
	NotApplicable,

	/// <summary>No assessment has a date of damage, so there are no events to add up.</summary>
	NoEvents,

	/// <summary>A total was worked out (it may still be null when the current value is missing).</summary>
	Evaluated,
}

/// <summary>One damage event: the assessment that stands for it, and whether it was counted.</summary>
public sealed class CumulativeEvent
{
	public Guid AssessmentID { get; init; }

	public DateTime DateDamageOccurred { get; init; }

	public DateTime? DateofInspection { get; init; }

	/// <summary>The assessment's percent damaged, as stored (never changed here).</summary>
	public decimal? PercentDamage { get; init; }

	/// <summary>The repair cost on the assessment's chosen basis (computed damages, contractor's or community's estimate).</summary>
	public decimal? RepairCost { get; init; }

	/// <summary>The structure value on the assessment's chosen basis (computed ACV, appraisal or adjusted tax value).</summary>
	public decimal? BuildingValue { get; init; }

	/// <summary>True for the most recent event (the one being determined).</summary>
	public bool IsCurrent { get; init; }

	public bool Counted { get; init; }

	/// <summary>Why it was or was not counted, in plain English.</summary>
	public string Note { get; init; }
}

/// <summary>The result of <see cref="CumulativeDamage.Evaluate"/>.</summary>
public sealed class CumulativeResult
{
	public CumulativeStatus Status { get; init; }

	public CumulativeMethod Method { get; init; }

	public decimal ThresholdPercent { get; init; }

	public int LookbackYears { get; init; }

	/// <summary>The first date counted; null when every event counts (look-back of 0) or nothing was evaluated.</summary>
	public DateTime? WindowStart { get; init; }

	/// <summary>One line per event (date of damage), newest first.</summary>
	public IReadOnlyList<CumulativeEvent> Events { get; init; } = Array.Empty<CumulativeEvent>();

	/// <summary>
	/// The cumulative percentage (one decimal): the sum of percents, or total repair cost over current value cut to
	/// one decimal. Null when it could not be worked out.
	/// </summary>
	public decimal? TotalPercent { get; init; }

	/// <summary>The repair costs of the counted events added up (shown for both methods).</summary>
	public decimal TotalRepairCost { get; init; }

	/// <summary>The structure value used for <see cref="CumulativeMethod.TotalDamageOverCurrentValue"/> (from the most recent event).</summary>
	public decimal? CurrentValue { get; init; }

	/// <summary>True when the total equals or exceeds the threshold.</summary>
	public bool MeetsThreshold { get; init; }

	/// <summary>Assessments that did not stand for an event (no date of damage, or replaced by a later inspection of the same event).</summary>
	public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

	/// <summary>Plain-English lines explaining the result.</summary>
	public IReadOnlyList<string> Explanation { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Cumulative substantial damage: damage from several events within a look-back period added together, for
/// communities whose ordinance requires it. Off by default. This is an extra, separate finding: it reads
/// assessments and never changes their PercentDamage or DamageMessage, so FEMA's reports and the SDE export keep
/// FEMA's own per-event determination.
/// </summary>
public static class CumulativeDamage
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>
	/// Adds up the damage in <paramref name="history"/> (the assessments of one structure). Each event is counted
	/// once, by DateDamageOccurred; when several assessments share that date, the one inspected last stands for it
	/// (then the one saved last). Inactive assessments and those without a date of damage are left out. The most
	/// recent event is the current one, and the look-back period ends on its date.
	/// </summary>
	public static CumulativeResult Evaluate(IReadOnlyList<AssessmentRecord> history, CumulativeSettings settings)
	{
		settings ??= new CumulativeSettings();
		if (!settings.Enabled)
		{
			return new CumulativeResult
			{
				Status = CumulativeStatus.NotApplicable,
				Method = settings.Method,
				ThresholdPercent = settings.ThresholdPercent,
				LookbackYears = settings.LookbackYears,
				Explanation = new[] { "Cumulative damage is turned off in Settings." },
			};
		}
		if (settings.ThresholdPercent <= 0m)
		{
			throw new ArgumentException("The cumulative damage threshold must be more than 0 percent.", nameof(settings));
		}
		if (settings.LookbackYears < 0)
		{
			throw new ArgumentException("The look-back period cannot be negative.", nameof(settings));
		}

		List<AssessmentRecord> active = (history ?? Array.Empty<AssessmentRecord>()).Where(a => a != null && a.Active).ToList();
		if (active.Select(a => a.PropertyID).Distinct().Count() > 1)
		{
			throw new ArgumentException("Cumulative damage adds up the assessments of one structure; these belong to several.", nameof(history));
		}

		List<string> skipped = new();
		foreach (AssessmentRecord a in active.Where(a => a.DateDamageOccurred is null))
		{
			skipped.Add($"Inspection of {Date(a.DateofInspection)}: no date of damage, so it cannot be matched to an event.");
		}

		// One assessment per date of damage: the latest inspection (then the latest save).
		List<AssessmentRecord> events = new();
		foreach (IGrouping<DateTime, AssessmentRecord> g in active.Where(a => a.DateDamageOccurred is not null).GroupBy(a => a.DateDamageOccurred.Value.Date))
		{
			List<AssessmentRecord> ordered = g.OrderByDescending(a => a.DateofInspection ?? DateTime.MinValue).ThenByDescending(a => a.LastUpdate ?? DateTime.MinValue).ToList();
			events.Add(ordered[0]);
			foreach (AssessmentRecord older in ordered.Skip(1))
			{
				skipped.Add($"Inspection of {Date(older.DateofInspection)} for the event of {Date(g.Key)}: replaced by the later inspection of {Date(ordered[0].DateofInspection)}.");
			}
		}
		events = events.OrderByDescending(a => a.DateDamageOccurred.Value).ToList();

		if (events.Count == 0)
		{
			return new CumulativeResult
			{
				Status = CumulativeStatus.NoEvents,
				Method = settings.Method,
				ThresholdPercent = settings.ThresholdPercent,
				LookbackYears = settings.LookbackYears,
				Skipped = skipped,
				Explanation = new[] { "No assessment has a date of damage, so there are no events to add up." },
			};
		}

		AssessmentRecord current = events[0];
		DateTime currentDate = current.DateDamageOccurred.Value.Date;
		DateTime? windowStart = settings.LookbackYears > 0 ? currentDate.AddYears(-settings.LookbackYears) : null;
		bool byPercent = settings.Method == CumulativeMethod.SumOfPercents;

		List<CumulativeEvent> lines = new();
		decimal sumPercent = 0m;
		decimal sumCost = 0m;
		bool missing = false;
		foreach (AssessmentRecord a in events)
		{
			DateTime date = a.DateDamageOccurred.Value.Date;
			decimal? cost = RepairCost(a);
			string note = null;
			if (windowStart is DateTime start && date < start)
			{
				note = $"Not counted: before the look-back period (starts {Date(start)})";
			}
			else if (a.PhysicalDamage)
			{
				note = "Not counted: damage undetermined";
			}
			else if (byPercent && a.PercentDamage is null)
			{
				note = "Not counted: no percent damaged (data entry incomplete)";
			}
			else if (!byPercent && cost is null)
			{
				note = "Not counted: no repair cost";
			}
			bool counted = note == null;
			if (counted)
			{
				sumPercent += a.PercentDamage ?? 0m;
				sumCost += cost ?? 0m;
			}
			else if (a == current)
			{
				missing = true;
			}
			lines.Add(new CumulativeEvent
			{
				AssessmentID = a.AssessmentID,
				DateDamageOccurred = date,
				DateofInspection = a.DateofInspection,
				PercentDamage = a.PercentDamage,
				RepairCost = cost,
				BuildingValue = BuildingValue(a),
				IsCurrent = a == current,
				Counted = counted,
				Note = note ?? (a == current ? "Most recent event" : "Earlier event in the look-back period"),
			});
		}

		List<string> explanation = new();
		int countedEvents = lines.Count(l => l.Counted);
		explanation.Add(windowStart is DateTime ws
			? $"Events from {Date(ws)} to {Date(currentDate)} ({Years(settings.LookbackYears)}) are counted: {countedEvents} of {lines.Count}."
			: $"Every event on record is counted: {countedEvents} of {lines.Count}.");
		if (missing)
		{
			explanation.Add("The most recent event is not counted (see its line), so the total may be too low.");
		}

		decimal? total = null;
		decimal? currentValue = BuildingValue(current);
		if (byPercent)
		{
			total = sumPercent;
			explanation.Add($"The percents damaged of the counted events add up to {Pct1(total.Value)}.");
		}
		else if (currentValue is decimal v && v > 0m)
		{
			total = Math.Floor(sumCost * 1000m / v) / 10m;
			explanation.Add($"Repair costs of the counted events add up to {Money(sumCost)}, which is {Pct1(total.Value)} of the current value of {Money(v)}.");
		}
		else
		{
			explanation.Add($"Repair costs of the counted events add up to {Money(sumCost)}, but the most recent assessment has no structure value, so no percentage can be worked out.");
		}

		bool meets = total is decimal t && (byPercent ? t : sumCost * 100m / currentValue.Value) >= settings.ThresholdPercent;
		if (total is not null)
		{
			explanation.Add(meets
				? $"That equals or exceeds the {Pct(settings.ThresholdPercent)} cumulative threshold."
				: $"That is below the {Pct(settings.ThresholdPercent)} cumulative threshold.");
		}
		explanation.Add("This is a separate finding: each assessment keeps its own percent damaged and determination.");

		return new CumulativeResult
		{
			Status = CumulativeStatus.Evaluated,
			Method = settings.Method,
			ThresholdPercent = settings.ThresholdPercent,
			LookbackYears = settings.LookbackYears,
			WindowStart = windowStart,
			Events = lines,
			TotalPercent = total,
			TotalRepairCost = sumCost,
			CurrentValue = currentValue,
			MeetsThreshold = meets,
			Skipped = skipped,
			Explanation = explanation,
		};
	}

	/// <summary>
	/// The repair cost behind an assessment's percentage, on its chosen basis (CostOfRepairsID): 394 contractor's
	/// estimate, 395 community's estimate, otherwise computed damages (393). Null when that amount is missing.
	/// </summary>
	public static decimal? RepairCost(AssessmentRecord a)
	{
		ArgumentNullException.ThrowIfNull(a);
		return a.CostOfRepairsID switch
		{
			LookupIds.RepairContractorEstimate => a.ContractorEstimate,
			LookupIds.RepairCommunityEstimate => a.CommunityEstimate,
			_ => a.ComputedDamages,
		};
	}

	/// <summary>
	/// The structure value behind an assessment's percentage, on its chosen basis (ValueOfBuildingID): 391 professional
	/// market appraisal, 392 tax assessed value times the factor adjustment, otherwise computed actual cash value (390).
	/// Null when that amount is missing.
	/// </summary>
	public static decimal? BuildingValue(AssessmentRecord a)
	{
		ArgumentNullException.ThrowIfNull(a);
		return a.ValueOfBuildingID switch
		{
			LookupIds.ValueMarketAppraisal => a.MarketAppraisal,
			LookupIds.ValueAdjustedTax => a.TaxAssessedValue is decimal t && a.FactorAdjustment is decimal f ? t * f : null,
			_ => a.ComputedActualCashValue,
		};
	}

	private static string Date(DateTime? d) => d is DateTime x ? x.ToString("MM/dd/yyyy", Us) : "(no date)";

	private static string Money(decimal d) => d.ToString("C2", Us);

	private static string Pct(decimal d) => d.ToString("0.##", Us) + "%";

	private static string Pct1(decimal d) => d.ToString("#,##0.0", Us) + "%";

	private static string Years(int n) => n == 1 ? "1 year" : n + " years";
}
