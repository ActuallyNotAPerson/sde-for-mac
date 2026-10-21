using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Core.Events;

/// <summary>One row of a breakdown (a street, a community or an inspector).</summary>
public sealed class EventGroup
{
	/// <summary>What the row is called in a table.</summary>
	public string Label { get; init; }

	/// <summary>Assessments in the group.</summary>
	public int Count { get; init; }

	public int Substantial { get; init; }
	public int Close { get; init; }
	public int NotSubstantial { get; init; }

	/// <summary>Incomplete and undetermined assessments together.</summary>
	public int Other { get; init; }

	/// <summary>Sum of the building values that were provided.</summary>
	public decimal BuildingValue { get; init; }

	/// <summary>Sum of the repair costs that were provided.</summary>
	public decimal RepairCost { get; init; }
}

/// <summary>A structure location and its result, for maps and dot plots.</summary>
public readonly record struct EventPoint(decimal Latitude, decimal Longitude, DeterminationClass Class);

/// <summary>
/// The numbers for one disaster event: how many structures fall in each result class, the dollars involved,
/// and breakdowns by street, community and inspector. Built from the assessments the caller picked, so the
/// caller decides what "the event" is (for example one date of damage, or everything in a community).
/// Counts are per assessment; pass the latest assessment of each structure to count structures.
/// </summary>
public sealed class EventSummary
{
	/// <summary>Shown for a street, community or inspector that was left blank.</summary>
	public const string NotRecorded = "(not recorded)";

	/// <summary>The event name the caller gave, or null.</summary>
	public string EventName { get; init; }

	public decimal SubstantialPercent { get; init; }
	public decimal ClosePercent { get; init; }

	/// <summary>Assessments in the summary.</summary>
	public int AssessmentCount { get; init; }

	/// <summary>Structures with at least one assessment.</summary>
	public int AssessedStructures { get; init; }

	/// <summary>Structures that were listed but have no assessment yet.</summary>
	public int NotYetAssessed { get; init; }

	/// <summary>Assessed plus not yet assessed.</summary>
	public int TotalStructures => AssessedStructures + NotYetAssessed;

	/// <summary>Assessments per result class; every class is present, with 0 when empty.</summary>
	public IReadOnlyDictionary<DeterminationClass, int> ClassCounts { get; init; }

	/// <summary>Building values as the reports show them; assessments without one add nothing.</summary>
	public decimal TotalBuildingValue { get; init; }

	/// <summary>Repair costs as the reports show them; assessments without one add nothing.</summary>
	public decimal TotalRepairCost { get; init; }

	/// <summary>Earliest and latest Date of Damage, or null when none was entered.</summary>
	public DateTime? DamageStart { get; init; }
	public DateTime? DamageEnd { get; init; }

	/// <summary>Earliest and latest inspection date, or null when none was entered.</summary>
	public DateTime? InspectionStart { get; init; }
	public DateTime? InspectionEnd { get; init; }

	/// <summary>Largest groups first (then most substantial, then name).</summary>
	public IReadOnlyList<EventGroup> ByStreet { get; init; }
	public IReadOnlyList<EventGroup> ByCommunity { get; init; }
	public IReadOnlyList<EventGroup> ByInspector { get; init; }

	/// <summary>Locations of assessed structures that have coordinates.</summary>
	public IReadOnlyList<EventPoint> Points { get; init; }

	public int Count(DeterminationClass c) => ClassCounts[c];

	/// <summary>
	/// Builds the summary.
	/// </summary>
	/// <param name="assessments">The assessments to count.</param>
	/// <param name="allProperties">Every structure that should have been assessed; those without an assessment in
	/// <paramref name="assessments"/> count as not yet assessed. Null when unknown.</param>
	/// <param name="eventName">A name for the event, kept as given.</param>
	/// <param name="reference">Used to add the street suffix ("River Street"); streets are grouped by name alone without it.</param>
	public static EventSummary Build(
		IEnumerable<AssessmentFile> assessments,
		IEnumerable<PropertyRecord> allProperties = null,
		string eventName = null,
		ReferenceData reference = null,
		decimal substantialPercent = Determinations.DefaultSubstantialPercent,
		decimal closePercent = Determinations.DefaultClosePercent)
	{
		List<AssessmentFile> files = (assessments ?? Enumerable.Empty<AssessmentFile>()).Where(f => f?.Assessment != null).ToList();
		Dictionary<DeterminationClass, int> counts = Enum.GetValues<DeterminationClass>().ToDictionary(c => c, c => 0);
		List<Row> rows = new();
		HashSet<Guid> assessedProperties = new();
		List<EventPoint> points = new();
		decimal value = 0m;
		decimal cost = 0m;
		foreach (AssessmentFile f in files)
		{
			AssessmentRecord a = f.Assessment;
			PropertyRecord p = f.Property ?? new PropertyRecord();
			DeterminationClass c = Determinations.Classify(a, substantialPercent, closePercent);
			counts[c]++;
			decimal v = AssessmentAmounts.BuildingValue(a) ?? 0m;
			decimal r = AssessmentAmounts.RepairCost(a) ?? 0m;
			value += v;
			cost += r;
			assessedProperties.Add(a.PropertyID != Guid.Empty ? a.PropertyID : p.PropertyID);
			rows.Add(new Row(c, v, r, Street(p, reference), Community(p), Clean(a.InspectorName)));
			if (p.Latitude is decimal lat && p.Longitude is decimal lon && !(lat == 0m && lon == 0m))
			{
				points.Add(new EventPoint(lat, lon, c));
			}
		}

		int notYet = (allProperties ?? Enumerable.Empty<PropertyRecord>())
			.Where(p => p != null)
			.Select(p => p.PropertyID)
			.Distinct()
			.Count(id => !assessedProperties.Contains(id));

		List<DateTime> damage = files.Where(f => f.Assessment.DateDamageOccurred != null).Select(f => f.Assessment.DateDamageOccurred.Value).ToList();
		List<DateTime> inspected = files.Where(f => f.Assessment.DateofInspection != null).Select(f => f.Assessment.DateofInspection.Value).ToList();

		return new EventSummary
		{
			EventName = eventName,
			SubstantialPercent = substantialPercent,
			ClosePercent = closePercent,
			AssessmentCount = files.Count,
			AssessedStructures = assessedProperties.Count,
			NotYetAssessed = notYet,
			ClassCounts = counts,
			TotalBuildingValue = value,
			TotalRepairCost = cost,
			DamageStart = damage.Count == 0 ? null : damage.Min(),
			DamageEnd = damage.Count == 0 ? null : damage.Max(),
			InspectionStart = inspected.Count == 0 ? null : inspected.Min(),
			InspectionEnd = inspected.Count == 0 ? null : inspected.Max(),
			ByStreet = Group(rows, r => r.Street),
			ByCommunity = Group(rows, r => r.Community),
			ByInspector = Group(rows, r => r.Inspector),
			Points = points,
		};
	}

	private sealed record Row(DeterminationClass Class, decimal Value, decimal Cost, string Street, string Community, string Inspector);

	private static IReadOnlyList<EventGroup> Group(List<Row> rows, Func<Row, string> key) => rows
		.GroupBy(key, StringComparer.OrdinalIgnoreCase)
		.Select(g => new EventGroup
		{
			Label = g.Key,
			Count = g.Count(),
			Substantial = g.Count(r => r.Class == DeterminationClass.Substantial),
			Close = g.Count(r => r.Class == DeterminationClass.Close),
			NotSubstantial = g.Count(r => r.Class == DeterminationClass.NotSubstantial),
			Other = g.Count(r => r.Class is DeterminationClass.Incomplete or DeterminationClass.Undetermined),
			BuildingValue = g.Sum(r => r.Value),
			RepairCost = g.Sum(r => r.Cost),
		})
		.OrderByDescending(g => g.Count)
		.ThenByDescending(g => g.Substantial)
		.ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
		.ToList();

	private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? NotRecorded : string.Join(" ", s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

	/// <summary>The street name without the house number, with the suffix when the reference data can name it.</summary>
	private static string Street(PropertyRecord p, ReferenceData reference)
	{
		string name = (p.StreetName ?? "").Trim();
		if (name.Length == 0)
		{
			return NotRecorded;
		}
		string suffix = reference?.LookupName(p.StreetSuffixID);
		return Clean(string.IsNullOrWhiteSpace(suffix) ? name : name + " " + suffix.Trim());
	}

	/// <summary>The NFIP community name, or its ID when there is no name.</summary>
	private static string Community(PropertyRecord p)
	{
		if (!string.IsNullOrWhiteSpace(p.NFIPCommunityName))
		{
			return Clean(p.NFIPCommunityName);
		}
		return string.IsNullOrWhiteSpace(p.NFIPCID) ? NotRecorded : Clean(p.NFIPCID);
	}
}
