using System.Globalization;
using ModelContextProtocol;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>
/// Finds what a tool's arguments name: an event or a team by ID or name, the assessments of an event, the structures
/// behind a list of IDs. Each throws a tool error that says where the right values come from.
/// </summary>
internal static class Lookups
{
	/// <summary>
	/// The event with this ID, name, label ("September flood (DR-4999)") or disaster number, ignoring case. A tool error
	/// when there is none, or when a name fits several events.
	/// </summary>
	public static EventRecord RequireEvent(SdeStore store, string idOrName)
	{
		string wanted = idOrName?.Trim();
		if (string.IsNullOrEmpty(wanted))
		{
			throw new McpException("Name the event (its name or ID); list_events lists them.");
		}
		if (Guid.TryParse(wanted, out Guid id))
		{
			return store.GetEvent(id) ?? throw new McpException($"There's no event {id} in this database. list_events lists the events.");
		}
		List<EventRecord> events = store.GetEvents();
		List<EventRecord> matches = events.Where(e => Same(e.Name, wanted) || Same(e.Label, wanted)).ToList();
		if (matches.Count == 0)
		{
			matches = events.Where(e => Same(e.DRNumber, wanted)).ToList();
		}
		return matches.Count switch
		{
			1 => matches[0],
			0 => throw new McpException($"There's no event called \"{Describe.Shorten(wanted, 80)}\". list_events lists the events and their IDs."),
			_ => throw new McpException($"{matches.Count} events are called \"{Describe.Shorten(wanted, 80)}\". Give the event's ID instead: "
				+ string.Join(", ", matches.Select(e => $"{e.Label} ({e.EventID})")) + "."),
		};
	}

	/// <summary>The event named by <paramref name="idOrName"/>, or null when it is left out.</summary>
	public static EventRecord OptionalEvent(SdeStore store, string idOrName) =>
		string.IsNullOrWhiteSpace(idOrName) ? null : RequireEvent(store, idOrName);

	/// <summary>
	/// What the Dashboard counts: the latest assessment of each structure, in the event or (null) the whole database.
	/// Structures and assessments in Recently deleted are left out.
	/// </summary>
	public static List<Guid> LatestAssessments(SdeStore store, EventRecord e)
	{
		if (e == null)
		{
			return store.ListMapPoints().Where(r => r.AssessmentID != null).Select(r => r.AssessmentID.Value).ToList();
		}
		HashSet<Guid> members = store.AssessmentsInEvent(e.EventID);
		return store.ListMapPoints(members.Contains).Select(r => r.AssessmentID.Value).ToList();
	}

	/// <summary>
	/// The assessments named by <paramref name="ids"/> that are in use, in the order given (repeats dropped), and the
	/// IDs left out because they aren't assessments in use. A tool error for text that isn't an ID, or for too many.
	/// </summary>
	public static (List<Guid> Found, List<string> Skipped) ActiveAssessments(SdeStore store, IReadOnlyList<string> ids, int max)
	{
		if (ids.Count > max)
		{
			throw new McpException($"One call takes at most {max.ToString("N0", CultureInfo.InvariantCulture)} assessments.");
		}
		List<Guid> parsed = ids.Select(SdeMcpContext.ParseAssessmentId).Distinct().ToList();
		HashSet<Guid> inUse = ActiveIds(store);
		List<Guid> found = parsed.Where(inUse.Contains).ToList();
		List<string> skipped = parsed.Where(id => !inUse.Contains(id)).Select(id => id.ToString()).ToList();
		return (found, skipped);
	}

	/// <summary>Every assessment in use: active, of an active structure.</summary>
	public static HashSet<Guid> ActiveIds(SdeStore store) => store.Database.Read(c => Dapper.SqlMapper.Query<Guid>(c,
		"SELECT a.AssessmentID FROM Assessment a JOIN Property p ON p.PropertyID = a.PropertyID WHERE a.Active = 1 AND p.Active = 1").ToHashSet());

	/// <summary>
	/// The assessments a tool works on: the IDs given, else the event's (the latest of each structure), else every
	/// structure's latest. Returns them with the IDs left out and a few words for what was chosen.
	/// </summary>
	public static (List<Guid> Ids, List<string> Skipped, string Scope) Choose(SdeStore store, IReadOnlyList<string> ids, EventRecord e, int max)
	{
		if (ids != null && ids.Count > 0)
		{
			(List<Guid> found, List<string> skipped) = ActiveAssessments(store, ids, max);
			if (e != null)
			{
				HashSet<Guid> members = store.AssessmentsInEvent(e.EventID);
				skipped.AddRange(found.Where(id => !members.Contains(id)).Select(id => id.ToString()));
				found = found.Where(members.Contains).ToList();
			}
			return (found, skipped, found.Count == 1 ? "1 chosen assessment" : $"{found.Count} chosen assessments");
		}
		List<Guid> latest = LatestAssessments(store, e);
		if (latest.Count > max)
		{
			throw new McpException($"That's {latest.Count.ToString("N0", CultureInfo.InvariantCulture)} assessments; one call takes at most {max.ToString("N0", CultureInfo.InvariantCulture)}. Choose assessments with assessmentIds (find_structures lists them).");
		}
		return (latest, new List<string>(), e == null ? "every structure in the database (each one's latest assessment)" : $"the event {e.Label} (each structure's latest assessment in it)");
	}

	/// <summary>An active team by ID or name (ignoring case); a tool error naming the teams there are.</summary>
	public static TeamRecord RequireTeam(SdeStore store, string idOrName)
	{
		string wanted = idOrName?.Trim();
		List<TeamRecord> teams = store.GetTeams();
		string known = teams.Count == 0
			? "This database has no teams yet; someone can add them in SDE (Assignments)."
			: "The teams are: " + string.Join(", ", teams.Select(t => t.Name)) + ".";
		if (string.IsNullOrEmpty(wanted))
		{
			throw new McpException("Name the team (its name or ID). " + known);
		}
		TeamRecord team = Guid.TryParse(wanted, out Guid id)
			? teams.FirstOrDefault(t => t.TeamID == id)
			: teams.FirstOrDefault(t => Same(t.Name, wanted));
		return team ?? throw new McpException($"There's no team \"{Describe.Shorten(wanted, 80)}\". {known}");
	}

	/// <summary>An event as list_events shows it.</summary>
	public static EventItem Item(EventRecord e, int assessments) => new EventItem(e.EventID.ToString(), e.Name, e.Label, e.Kind,
		e.DRNumber, Describe.Date(e.StartDate), Describe.Date(e.EndDate), e.DatumOffsetFt, Describe.Shorten(e.Notes),
		assessments, Describe.Date(e.CreatedOn), e.CreatedBy);

	/// <summary>A date typed as yyyy-MM-dd, or null when left out; a tool error naming the argument otherwise.</summary>
	public static DateTime? ParseDate(string text, string argument)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		return DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)
			? d
			: throw new McpException($"{argument} must be a date written as yyyy-MM-dd, such as 2026-09-14.");
	}

	private static bool Same(string a, string b) => a != null && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);
}
