using System.ComponentModel;
using ModelContextProtocol.Server;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>The read tools for events (the disasters assessments belong to) and their summaries.</summary>
public sealed partial class ReadTools
{
	/// <summary>The most rows a breakdown (by street, community or inspector) returns.</summary>
	public const int MaxGroupRows = 10;

	[McpServerTool(Name = "list_events", Title = "List events", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Lists the events (floods, storms and other disasters) that assessments belong to, newest first, with each one's ID, kind, disaster number, dates and how many assessments it has. Other tools take an event's name or ID to work on that event only." + Untrusted)]
	public EventList ListEvents()
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		Dictionary<Guid, int> counts = store.EventCounts();
		List<EventItem> items = store.GetEvents().Select(e => Lookups.Item(e, counts.GetValueOrDefault(e.EventID))).ToList();
		return new EventList(sde.DatabasePath, items.Count, items);
	}

	[McpServerTool(Name = "event_summary", Title = "Summarize an event", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("The numbers SDE's Dashboard shows, for one event or the whole database: structures assessed, how many are substantially damaged, close to the threshold or not, total building value and repair cost, damage and inspection dates, review status, the largest streets, communities and inspectors, and field teams' progress. Each structure counts once, by its latest assessment." + Untrusted)]
	public EventSummaryResult EventSummary(
		[Description("The event's name or ID (list_events lists them). Leave out for the whole database.")] string @event = null)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		EventRecord e = Lookups.OptionalEvent(store, @event);
		Core.Events.EventSummary s = BuildSummary(store, e, out List<Guid> ids);
		Dictionary<AssessmentStatus, int> statuses = Enum.GetValues<AssessmentStatus>().ToDictionary(v => v, v => 0);
		Dictionary<Guid, AssessmentStatusRecord> stored = store.GetStatuses();
		foreach (Guid id in ids)
		{
			statuses[stored.TryGetValue(id, out AssessmentStatusRecord r) ? r.Value : AssessmentStatus.Draft]++;
		}
		List<StatusCount> byStatus = statuses.Where(kv => kv.Value > 0).Select(kv => new StatusCount(AssessmentStatuses.Label(kv.Key), kv.Value)).ToList();
		List<TeamProgressItem> teams = store.GetTeamProgress(e?.EventID).Where(t => t.Assigned > 0)
			.Select(t => new TeamProgressItem(t.TeamID.ToString(), t.Name, t.Assigned, t.Assessed, t.Done, t.Skipped)).ToList();
		EventItem item = e == null ? null : Lookups.Item(e, store.EventCounts().GetValueOrDefault(e.EventID));
		string note = s.AssessmentCount == 0
			? (e == null ? "There are no assessments in this database yet." : "No assessments are in this event yet.")
			: null;
		return new EventSummaryResult(sde.DatabasePath, item,
			e == null ? "Every structure in the database, by its latest assessment." : $"The structures in {e.Label}, each by its latest assessment in the event.",
			s.AssessmentCount, s.AssessedStructures, e == null ? s.NotYetAssessed : null, s.TotalStructures,
			new ResultCounts(s.Count(DeterminationClass.Substantial), s.Count(DeterminationClass.Close), s.Count(DeterminationClass.NotSubstantial),
				s.Count(DeterminationClass.Undetermined), s.Count(DeterminationClass.Incomplete)),
			s.TotalBuildingValue, s.TotalRepairCost, Describe.Date(s.DamageStart), Describe.Date(s.DamageEnd), Describe.Date(s.InspectionStart), Describe.Date(s.InspectionEnd),
			byStatus, Groups(s.ByStreet), Groups(s.ByCommunity), Groups(s.ByInspector), teams, note);
	}

	/// <summary>
	/// The Dashboard's summary for an event (null: the whole database, counting structures not yet assessed too), with
	/// the event's details. <paramref name="ids"/> gets the assessments counted.
	/// </summary>
	internal static Core.Events.EventSummary BuildSummary(SdeStore store, EventRecord e, out List<Guid> ids)
	{
		ids = Lookups.LatestAssessments(store, e);
		List<AssessmentFile> files = store.LoadAssessments(ids);
		return Core.Events.EventSummary.Build(files, e == null ? store.GetProperties() : null, e?.Label, store.Reference, details: e?.ToDetails());
	}

	private static List<SummaryGroup> Groups(IReadOnlyList<EventGroup> groups) => groups.Take(MaxGroupRows)
		.Select(g => new SummaryGroup(Describe.Shorten(g.Label, 120), g.Count, g.Substantial, g.Close, g.NotSubstantial, g.Other, g.BuildingValue, g.RepairCost))
		.ToList();
}
