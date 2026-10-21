using System.Data;
using Dapper;
using Sde.Core.Events;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>A disaster (a flood, a storm) that assessments belong to (Mac-only Event table, never exported).</summary>
[Table("Event")]
public sealed class EventRecord
{
	[Key] public Guid EventID { get; set; }

	public string Name { get; set; }

	/// <summary>Flood, Hurricane, Tornado, Fire or Other (see <see cref="EventKinds"/>).</summary>
	public string Kind { get; set; }

	/// <summary>A disaster declaration number, e.g. DR-4999.</summary>
	public string DRNumber { get; set; }

	public DateTime? StartDate { get; set; }

	public DateTime? EndDate { get; set; }

	public string Notes { get; set; }

	/// <summary>Added to high-water mark elevations recorded in another vertical datum, or null.</summary>
	public decimal? DatumOffsetFt { get; set; }

	public DateTime? CreatedOn { get; set; }

	public string CreatedBy { get; set; }

	/// <summary>"September flood (DR-4999)".</summary>
	[NotMapped] public string Label => string.IsNullOrWhiteSpace(DRNumber) ? Name : $"{Name} ({DRNumber.Trim()})";

	/// <summary>The details the event summary and its PDF show.</summary>
	public EventDetails ToDetails() => new EventDetails(Name, Kind, DRNumber, StartDate, EndDate);
}

/// <summary>An assessment's membership of an event (Mac-only EventAssessment table).</summary>
[Table("EventAssessment")]
public sealed class EventAssessmentRecord
{
	[Key] public Guid AssessmentID { get; set; }

	public Guid EventID { get; set; }

	public DateTime? AddedOn { get; set; }
}

/// <summary>The kinds of event offered when one is created (stored as text, so others can be added).</summary>
public static class EventKinds
{
	public static readonly IReadOnlyList<string> All = new[] { "Flood", "Hurricane", "Tornado", "Fire", "Other" };
}

/// <summary>
/// Events: the disasters assessments are made after. An assessment belongs to at most one event. Membership is Mac-only
/// data, so it never changes an assessment's own rows, versions or LastUpdate; joining or leaving an event is recorded
/// in the assessment's history. The FEMA cumulative-damage grouping doesn't use events (that would change findings).
/// </summary>
public sealed partial class SdeStore
{
	/// <summary>Every event, newest first (by start date, then name).</summary>
	public List<EventRecord> GetEvents() =>
		Database.Read(c => TableMap<EventRecord>.Select(c, orderBy: "COALESCE(StartDate, CreatedOn) DESC, Name").ToList());

	public EventRecord GetEvent(Guid eventId) => Database.Read(c => TableMap<EventRecord>.Get(c, new { EventID = eventId }));

	/// <summary>Adds an event (EventID empty) or saves changes to one. The name is required.</summary>
	public EventRecord SaveEvent(EventRecord e, SaveContext context = null)
	{
		if (string.IsNullOrWhiteSpace(e.Name))
		{
			throw new ArgumentException("Give the event a name.");
		}
		if (e.StartDate is DateTime start && e.EndDate is DateTime end && end < start)
		{
			throw new ArgumentException("The event can't end before it starts.");
		}
		e.Name = e.Name.Trim();
		e.DRNumber = string.IsNullOrWhiteSpace(e.DRNumber) ? null : e.DRNumber.Trim();
		if (e.EventID == Guid.Empty)
		{
			e.EventID = Guid.NewGuid();
			e.CreatedOn = Clock();
			e.CreatedBy = (context ?? SaveContext.App).User ?? UserName;
		}
		Database.InTransaction((c, tx) => TableMap<EventRecord>.Upsert(c, e, tx));
		return e;
	}

	/// <summary>Deletes an event. Its assessments stay; they just no longer belong to it.</summary>
	public void DeleteEvent(Guid eventId) =>
		Database.InTransaction((c, tx) => TableMap<EventRecord>.Delete(c, "EventID = @eventId", new { eventId }, tx));

	/// <summary>The event an assessment belongs to, or null.</summary>
	public Guid? GetEventOf(Guid assessmentId) =>
		Database.Read(c => c.ExecuteScalar<Guid?>("SELECT EventID FROM EventAssessment WHERE AssessmentID = @assessmentId", new { assessmentId }));

	/// <summary>Every membership: assessment ID → event ID.</summary>
	public Dictionary<Guid, Guid> GetEventMemberships() =>
		Database.Read(c => TableMap<EventAssessmentRecord>.Select(c).ToDictionary(r => r.AssessmentID, r => r.EventID));

	/// <summary>The assessments in an event (active or not).</summary>
	public HashSet<Guid> AssessmentsInEvent(Guid eventId) =>
		Database.Read(c => c.Query<Guid>("SELECT AssessmentID FROM EventAssessment WHERE EventID = @eventId", new { eventId }).ToHashSet());

	/// <summary>How many active assessments of active structures each event has.</summary>
	public Dictionary<Guid, int> EventCounts() => Database.Read(c => c.Query<EventCountRow>("""
		SELECT e.EventID, COUNT(*) AS Count FROM EventAssessment e
		JOIN Assessment a ON a.AssessmentID = e.AssessmentID AND a.Active = 1
		JOIN Property p ON p.PropertyID = a.PropertyID AND p.Active = 1
		GROUP BY e.EventID
		""").ToDictionary(x => x.EventID, x => x.Count));

	private sealed class EventCountRow
	{
		public Guid EventID { get; set; }

		public int Count { get; set; }
	}

	/// <summary>
	/// Puts assessments in an event (or takes them out of any, with <paramref name="eventId"/> null), recording it in
	/// each one's history as one batch. Returns how many changed.
	/// </summary>
	public int SetEvent(IReadOnlyCollection<Guid> assessmentIds, Guid? eventId, SaveContext context = null)
	{
		SaveContext ctx = (context ?? SaveContext.Batch(SaveVia.App)).AsBatch();
		return Database.InTransaction((c, tx) =>
		{
			EventRecord target = eventId is Guid id ? TableMap<EventRecord>.Get(c, new { EventID = id }, tx) ?? throw new ArgumentException("That event no longer exists.") : null;
			int changed = 0;
			foreach (Guid aid in assessmentIds.Distinct())
			{
				Guid? propertyId = c.ExecuteScalar<Guid?>("SELECT PropertyID FROM Assessment WHERE AssessmentID = @aid", new { aid }, tx);
				if (propertyId == null)
				{
					continue;
				}
				Guid? current = c.ExecuteScalar<Guid?>("SELECT EventID FROM EventAssessment WHERE AssessmentID = @aid", new { aid }, tx);
				if (current == eventId)
				{
					continue;
				}
				string before = current is Guid was ? TableMap<EventRecord>.Get(c, new { EventID = was }, tx)?.Label : null;
				TableMap<EventAssessmentRecord>.Delete(c, "AssessmentID = @aid", new { aid }, tx);
				if (target != null)
				{
					TableMap<EventAssessmentRecord>.Insert(c, new EventAssessmentRecord { AssessmentID = aid, EventID = target.EventID, AddedOn = Clock() }, tx);
				}
				WriteEvent(c, tx, propertyId.Value, aid, target == null ? "Taken out of event " + before : "Added to event " + target.Label, ctx,
					"Event", before, target?.Label);
				changed++;
			}
			return changed;
		});
	}

	/// <summary>
	/// The event a damage date falls in (between its start and end; an event with no end is open), the one that started
	/// last when several do; null when none does. New assessments are offered this event.
	/// </summary>
	public EventRecord SuggestEvent(DateTime? damageDate)
	{
		if (damageDate is not DateTime d)
		{
			return null;
		}
		DateTime day = d.Date;
		return GetEvents()
			.Where(e => e.StartDate is DateTime s && s.Date <= day && (e.EndDate == null || e.EndDate.Value.Date >= day))
			.OrderByDescending(e => e.StartDate)
			.FirstOrDefault();
	}
}
