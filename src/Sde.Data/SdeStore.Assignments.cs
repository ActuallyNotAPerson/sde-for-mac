using System.Security.Cryptography;
using Dapper;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>A field team (Mac-only Team table).</summary>
[Table("Team")]
public sealed class TeamRecord
{
	[Key] public Guid TeamID { get; set; }

	public string Name { get; set; }

	/// <summary>Free text: names, phone numbers.</summary>
	public string Members { get; set; }

	/// <summary>"#RRGGBB" for the map, or null.</summary>
	public string Color { get; set; }

	/// <summary>False once the team is removed; its past assignments stay.</summary>
	public bool Active { get; set; } = true;

	public DateTime? CreatedOn { get; set; }
}

/// <summary>A structure given to a team to visit (Mac-only Assignment table).</summary>
[Table("Assignment")]
public sealed class AssignmentRecord
{
	[Key] public Guid AssignmentID { get; set; }

	public Guid PropertyID { get; set; }

	public Guid TeamID { get; set; }

	public Guid? EventID { get; set; }

	public DateTime? AssignedOn { get; set; }

	public string AssignedBy { get; set; }

	public DateTime? DueDate { get; set; }

	/// <summary>The stop's place in the team's route (1 first), or null before a route is made.</summary>
	public int? RouteOrder { get; set; }

	/// <summary>See <see cref="AssignmentStatuses"/>.</summary>
	public string Status { get; set; } = AssignmentStatuses.Assigned;

	/// <summary>The code printed on the field worksheet (and in its QR code); the phone form's search accepts it.</summary>
	public string ShortCode { get; set; }

	public string Notes { get; set; }
}

/// <summary>Where an assignment is: Assigned, Visited, Done or Skipped.</summary>
public static class AssignmentStatuses
{
	public const string Assigned = "Assigned";
	public const string Visited = "Visited";
	public const string Done = "Done";
	public const string Skipped = "Skipped";

	public static readonly IReadOnlyList<string> All = new[] { Assigned, Visited, Done, Skipped };
}

/// <summary>One team's progress: how many structures it has, and how many it has assessed since they were assigned.</summary>
public sealed record TeamProgress(Guid TeamID, string Name, int Assigned, int Assessed, int Done, int Skipped);

public sealed partial class SdeStore
{
	/// <summary>Letters and digits that can't be mistaken for each other on paper (no 0/O, 1/I/L).</summary>
	private const string ShortCodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

	public List<TeamRecord> GetTeams(bool activeOnly = true) =>
		Database.Read(c => TableMap<TeamRecord>.Select(c, activeOnly ? "Active = 1" : null, orderBy: "Name").ToList());

	/// <summary>Adds (TeamID empty) or saves a team. Names must be unique among active teams.</summary>
	public TeamRecord SaveTeam(TeamRecord team)
	{
		if (string.IsNullOrWhiteSpace(team.Name))
		{
			throw new ArgumentException("Give the team a name.");
		}
		team.Name = team.Name.Trim();
		Database.InTransaction((c, tx) =>
		{
			if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Team WHERE Active = 1 AND Name = @Name COLLATE NOCASE AND TeamID <> @TeamID",
				new { team.Name, team.TeamID }, tx) > 0)
			{
				throw new ArgumentException($"There's already a team called {team.Name}.");
			}
			if (team.TeamID == Guid.Empty)
			{
				team.TeamID = Guid.NewGuid();
				team.CreatedOn = Clock();
			}
			TableMap<TeamRecord>.Upsert(c, team, tx);
		});
		return team;
	}

	/// <summary>Removes a team from the lists; what it was assigned stays in its history.</summary>
	public void RemoveTeam(Guid teamId) =>
		Database.InTransaction((c, tx) => c.Execute("UPDATE Team SET Active = 0 WHERE TeamID = @teamId", new { teamId }, tx));

	/// <summary>Assignments, optionally of one team and/or one event (null event = every event), in route order.</summary>
	public List<AssignmentRecord> GetAssignments(Guid? teamId = null, Guid? eventId = null) => Database.Read(c => TableMap<AssignmentRecord>.Select(c,
		"(@teamId IS NULL OR TeamID = @teamId) AND (@eventId IS NULL OR EventID = @eventId)", new { teamId, eventId },
		orderBy: "TeamID, COALESCE(RouteOrder, 1000000), AssignedOn").ToList());

	public AssignmentRecord GetAssignment(Guid assignmentId) =>
		Database.Read(c => TableMap<AssignmentRecord>.Get(c, new { AssignmentID = assignmentId }));

	/// <summary>The assignment with a worksheet's short code (ignoring case and spaces), or null.</summary>
	public AssignmentRecord FindAssignmentByCode(string code)
	{
		string clean = new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
		return clean.Length == 0 ? null : Database.Read(c => TableMap<AssignmentRecord>.Select(c, "ShortCode = @clean", new { clean }).FirstOrDefault());
	}

	/// <summary>
	/// Gives structures to a team for an event (null: no event). A structure already assigned for that event moves to
	/// the new team (its route place is cleared). With <paramref name="alsoSetFemaTeamName"/>, FEMA's own "Team name"
	/// on the structure is set too; that field is exported. One batch in edit history. Returns the assignments.
	/// </summary>
	public List<AssignmentRecord> AssignToTeam(IReadOnlyCollection<Guid> propertyIds, Guid teamId, Guid? eventId = null, DateTime? dueDate = null,
		bool alsoSetFemaTeamName = false, SaveContext context = null)
	{
		SaveContext ctx = (context ?? SaveContext.Batch(SaveVia.App)).AsBatch();
		return Database.InTransaction((c, tx) =>
		{
			TeamRecord team = TableMap<TeamRecord>.Get(c, new { TeamID = teamId }, tx) ?? throw new ArgumentException("That team no longer exists.");
			List<AssignmentRecord> result = new List<AssignmentRecord>();
			foreach (Guid pid in propertyIds.Distinct())
			{
				PropertyRecord p = TableMap<PropertyRecord>.Get(c, new { PropertyID = pid }, tx);
				if (p == null)
				{
					continue;
				}
				AssignmentRecord a = TableMap<AssignmentRecord>.Select(c, "PropertyID = @pid AND EventID IS @eventId", new { pid, eventId }, tx).FirstOrDefault();
				if (a != null && a.TeamID == teamId)
				{
					result.Add(a);
					continue;
				}
				a ??= new AssignmentRecord { AssignmentID = Guid.NewGuid(), PropertyID = pid, EventID = eventId, ShortCode = NewShortCode(c, tx) };
				a.TeamID = teamId;
				a.AssignedOn = Clock();
				a.AssignedBy = ctx.User ?? UserName;
				a.DueDate = dueDate ?? a.DueDate;
				a.RouteOrder = null;
				a.Status = AssignmentStatuses.Assigned;
				TableMap<AssignmentRecord>.Upsert(c, a, tx);
				WriteEvent(c, tx, pid, null, "Assigned to " + team.Name, ctx);
				if (alsoSetFemaTeamName && p.TeamName != team.Name)
				{
					p.TeamName = team.Name;
					SavePropertyTracked(c, tx, p, ctx);
				}
				result.Add(a);
			}
			return result;
		});
	}

	/// <summary>Takes structures off whatever team has them for an event (null: no event).</summary>
	public int Unassign(IReadOnlyCollection<Guid> propertyIds, Guid? eventId = null, SaveContext context = null)
	{
		SaveContext ctx = (context ?? SaveContext.Batch(SaveVia.App)).AsBatch();
		return Database.InTransaction((c, tx) =>
		{
			int removed = 0;
			foreach (Guid pid in propertyIds.Distinct())
			{
				if (c.Execute("DELETE FROM Assignment WHERE PropertyID = @pid AND EventID IS @eventId", new { pid, eventId }, tx) > 0)
				{
					WriteEvent(c, tx, pid, null, "Taken off its team", ctx);
					removed++;
				}
			}
			return removed;
		});
	}

	/// <summary>Marks an assignment Assigned, Visited, Done or Skipped.</summary>
	public void SetAssignmentStatus(Guid assignmentId, string status)
	{
		if (!AssignmentStatuses.All.Contains(status))
		{
			throw new ArgumentException("Unknown assignment status: " + status);
		}
		Database.InTransaction((c, tx) => c.Execute("UPDATE Assignment SET Status = @status WHERE AssignmentID = @assignmentId", new { status, assignmentId }, tx));
	}

	/// <summary>Saves a team's route: the assignments in visiting order (others keep no place).</summary>
	public void SaveRouteOrder(IReadOnlyList<Guid> assignmentIds) => Database.InTransaction((c, tx) =>
	{
		for (int i = 0; i < assignmentIds.Count; i++)
		{
			c.Execute("UPDATE Assignment SET RouteOrder = @order WHERE AssignmentID = @id", new { order = i + 1, id = assignmentIds[i] }, tx);
		}
	});

	/// <summary>
	/// Each active team's progress for an event (null: every event): structures assigned, how many have an active
	/// assessment inspected on or after the day they were assigned, and how many are marked Done or Skipped.
	/// </summary>
	public List<TeamProgress> GetTeamProgress(Guid? eventId = null) => Database.Read(c => c.Query<TeamProgressRow>("""
		SELECT t.TeamID, t.Name,
		       COUNT(a.AssignmentID) AS Assigned,
		       SUM(CASE WHEN EXISTS (SELECT 1 FROM Assessment s WHERE s.PropertyID = a.PropertyID AND s.Active = 1
		                             AND date(s.DateofInspection) >= date(a.AssignedOn)) THEN 1 ELSE 0 END) AS Assessed,
		       SUM(CASE WHEN a.Status = 'Done' THEN 1 ELSE 0 END) AS Done,
		       SUM(CASE WHEN a.Status = 'Skipped' THEN 1 ELSE 0 END) AS Skipped
		FROM Team t
		LEFT JOIN Assignment a ON a.TeamID = t.TeamID AND (@eventId IS NULL OR a.EventID = @eventId)
		LEFT JOIN Property p ON p.PropertyID = a.PropertyID
		WHERE t.Active = 1 AND (a.AssignmentID IS NULL OR p.Active = 1)
		GROUP BY t.TeamID, t.Name
		ORDER BY t.Name
		""", new { eventId }).Select(r => new TeamProgress(r.TeamID, r.Name, r.Assigned, r.Assessed, r.Done, r.Skipped)).ToList());

	private sealed class TeamProgressRow
	{
		public Guid TeamID { get; set; }

		public string Name { get; set; }

		public int Assigned { get; set; }

		public int Assessed { get; set; }

		public int Done { get; set; }

		public int Skipped { get; set; }
	}

	/// <summary>A new six-character worksheet code no other assignment uses.</summary>
	private static string NewShortCode(System.Data.IDbConnection c, System.Data.IDbTransaction tx)
	{
		while (true)
		{
			char[] code = new char[6];
			for (int i = 0; i < code.Length; i++)
			{
				code[i] = ShortCodeAlphabet[RandomNumberGenerator.GetInt32(ShortCodeAlphabet.Length)];
			}
			string text = new string(code);
			if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Assignment WHERE ShortCode = @text", new { text }, tx) == 0)
			{
				return text;
			}
		}
	}
}
