using Dapper;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>One call by an AI assistant that changed the database (one batch in edit history).</summary>
/// <param name="BatchId">The batch, for <see cref="AiAssistantSettings.Revert"/>.</param>
/// <param name="ChangedOn">When the change was made.</param>
/// <param name="ChangedBy">Who made it, such as "claude-code (AI)".</param>
/// <param name="Changes">How many things the call changed (one per field, status or structure; 1 for a new event or comment).</param>
/// <param name="AssessmentIds">The assessments it changed (or commented on).</param>
/// <param name="Summary">What changed in a line, such as "123 River Rd: Inspector, Flood elevation" or "Created event September flood".</param>
public sealed record AiChangeBatch(string BatchId, DateTime ChangedOn, string ChangedBy, int Changes, IReadOnlyList<Guid> AssessmentIds, string Summary)
{
	/// <summary>What kind of change it was: one of the <see cref="AiChangeKinds"/>.</summary>
	public string Kind { get; init; } = AiChangeKinds.Fields;

	/// <summary>What Revert does with this change, in a sentence for the person.</summary>
	public string RevertNote { get; init; }
}

/// <summary>The kinds of change an AI assistant can make (<see cref="AiChangeBatch.Kind"/>).</summary>
public static class AiChangeKinds
{
	/// <summary>update_fields: field values.</summary>
	public const string Fields = "Fields";

	/// <summary>set_status.</summary>
	public const string Status = "Status";

	/// <summary>create_structure: a new structure and its first assessment.</summary>
	public const string Structure = "Structure";

	/// <summary>create_event.</summary>
	public const string Event = "Event";

	/// <summary>add_review_comment.</summary>
	public const string Comment = "Comment";

	/// <summary>assign_to_team.</summary>
	public const string Assignment = "Assignment";

	/// <summary>What Revert does with a change of this kind.</summary>
	public static string RevertNote(string kind) => kind switch
	{
		Status => "Revert sets the status back, unless it has changed again since.",
		Structure => "Revert moves the new structure to Recently deleted, unless a person has worked on it since.",
		Event => "Revert removes the event while nothing has been put in it.",
		Comment => "Revert removes the comment while nobody has answered or resolved it.",
		Assignment => "Revert gives the structures back to the team they had before, or takes them off the team (and puts FEMA's Team name back, if it was set), unless they have been visited or reassigned since.",
		_ => "Revert puts back the values it changed, unless someone has changed them again since.",
	};
}

/// <summary>
/// What one AI assistant call did outside FEMA's fields, kept as JSON in its batch's record row (see
/// <see cref="SdeStore.WriteBatchRecord"/>), so <see cref="AiAssistantSettings.RecentChanges"/> can name it and
/// <see cref="AiAssistantSettings.Revert"/> can take it back.
/// </summary>
internal sealed class AiChangeRecord
{
	public string Kind { get; set; }

	public Guid? PropertyId { get; set; }

	public Guid? AssessmentId { get; set; }

	public Guid? EventId { get; set; }

	public Guid? CommentId { get; set; }

	public Guid? TeamId { get; set; }

	/// <summary>For an assignment: each structure the call gave to the team, with the team it had before for that event.</summary>
	public List<AiTeamMove> Moves { get; set; }

	/// <summary>For an assignment: true when FEMA's Team name field was set on the structures too.</summary>
	public bool FemaTeamName { get; set; }
}

/// <summary>A structure an AI assistant gave to a team, and what it had before for that event.</summary>
internal sealed class AiTeamMove
{
	public Guid PropertyId { get; set; }

	/// <summary>The team it had before for that event; null for none.</summary>
	public Guid? PreviousTeamId { get; set; }

	/// <summary>
	/// FEMA's Team name field before the call set it. Kept here because that field isn't in edit history (it isn't one
	/// of the fields SDE's editor shows), so a field revert can't put it back.
	/// </summary>
	public string PreviousFemaTeamName { get; set; }
}

/// <summary>
/// What SDE's Settings › AI assistants panel needs: the per-database switch that allows edits through the AI assistant
/// server, the AI assistants' recent changes, and reverting one of them.
/// </summary>
public static class AiAssistantSettings
{
	/// <summary>The per-database setting (MacSetting, JSON true or false) that lets AI assistants edit. Off when missing.</summary>
	public const string AllowEditsKey = "Mcp.AllowEdits";

	/// <summary>The history field a status change is recorded under (see <see cref="SdeStore.SetStatus(Guid, AssessmentStatus, string, SaveContext)"/>).</summary>
	internal const string StatusField = "Status";

	/// <summary>True when AI assistants may edit this database. The server reads it again on every edit call.</summary>
	public static bool AllowEdits(SdeStore store) => store.GetSetting(AllowEditsKey, false);

	/// <summary>Turns AI edits on or off for this database.</summary>
	public static void SetAllowEdits(SdeStore store, bool allow) => store.SetSetting(AllowEditsKey, allow);

	/// <summary>The history field of a batch's record row: what an AI assistant call did outside FEMA's fields.</summary>
	internal const string RecordField = "AI change";

	/// <summary>
	/// The AI assistants' most recent changes, newest first: one per call that changed something (edit history rows
	/// with Via = "AI assistant", grouped by batch).
	/// </summary>
	public static List<AiChangeBatch> RecentChanges(SdeStore store, int count = 20)
	{
		List<EditHistoryRecord> rows = store.Database.Read(c => c.Query<EditHistoryRecord>("""
			SELECT * FROM EditHistory
			WHERE BatchId IN (
				SELECT BatchId FROM EditHistory WHERE Via = @via AND BatchId IS NOT NULL
				GROUP BY BatchId ORDER BY MAX(ChangedOn) DESC, MAX(HistoryID) DESC LIMIT @count)
			ORDER BY HistoryID
			""", new { via = SaveVia.AiAssistant, count }).ToList());
		Dictionary<Guid, string> addresses = new Dictionary<Guid, string>();
		List<(AiChangeBatch Batch, long Last)> batches = new List<(AiChangeBatch, long)>();
		foreach (IGrouping<string, EditHistoryRecord> batch in rows.GroupBy(r => r.BatchId))
		{
			List<EditHistoryRecord> all = batch.ToList();
			EditHistoryRecord recordRow = RecordRow(all);
			AiChangeRecord record = Read(recordRow);
			List<EditHistoryRecord> list = all.Where(r => r != recordRow).ToList();
			List<Guid> assessments = list.Where(r => r.AssessmentID != null).Select(r => r.AssessmentID.Value)
				.Concat(record?.AssessmentId is Guid a ? new[] { a } : Array.Empty<Guid>()).Distinct().ToList();
			string summary;
			string kind;
			if (recordRow != null)
			{
				summary = recordRow.Label;
				kind = record?.Kind ?? AiChangeKinds.Fields;
			}
			else
			{
				List<string> labels = list.Select(r => r.Field == StatusField ? "Status: " + r.NewValue : r.Label).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList();
				string what = labels.Count <= 4 ? string.Join(", ", labels) : string.Join(", ", labels.Take(3)) + $" and {labels.Count - 3} more";
				string where = assessments.Count > 1
					? $"{assessments.Count} assessments"
					: list[0].PropertyID is Guid pid ? Address(store, pid, addresses) : "";
				summary = string.IsNullOrEmpty(where) ? what : where + ": " + what;
				kind = list.All(r => r.Field == StatusField) ? AiChangeKinds.Status : AiChangeKinds.Fields;
			}
			batches.Add((new AiChangeBatch(batch.Key, all.Max(r => r.ChangedOn), all[0].ChangedBy, Math.Max(1, list.Count), assessments, summary)
			{
				Kind = kind,
				RevertNote = AiChangeKinds.RevertNote(kind),
			}, all.Max(r => r.HistoryID)));
		}
		// Calls made within the same moment keep the order they were made in.
		return batches.OrderByDescending(b => b.Batch.ChangedOn).ThenByDescending(b => b.Last).Select(b => b.Batch).ToList();
	}

	/// <summary>Writes a call's record row: what it did outside FEMA's fields, in its batch.</summary>
	internal static void Record(SdeStore store, SaveContext ctx, string summary, AiChangeRecord record) =>
		store.WriteBatchRecord(RecordField, summary, System.Text.Json.JsonSerializer.Serialize(record), ctx);

	/// <summary>A batch's record row (no structure, no assessment, <see cref="RecordField"/>), or null.</summary>
	private static EditHistoryRecord RecordRow(IEnumerable<EditHistoryRecord> rows) =>
		rows.FirstOrDefault(r => r.Field == RecordField && r.PropertyID == null && r.AssessmentID == null);

	private static AiChangeRecord Read(EditHistoryRecord row)
	{
		if (string.IsNullOrWhiteSpace(row?.NewValue))
		{
			return null;
		}
		try
		{
			return System.Text.Json.JsonSerializer.Deserialize<AiChangeRecord>(row.NewValue);
		}
		catch (System.Text.Json.JsonException)
		{
			return null;
		}
	}

	private static string Address(SdeStore store, Guid propertyId, Dictionary<Guid, string> cache)
	{
		if (!cache.TryGetValue(propertyId, out string address))
		{
			address = Describe.Address(store.GetProperty(propertyId), store.Reference);
			cache[propertyId] = address;
		}
		return address;
	}

	/// <summary>
	/// Puts back what one AI assistant call changed, as a new batch: field values through
	/// <see cref="SdeStore.RevertBatch"/>, a status change by setting the status back, and what the call did outside
	/// FEMA's fields as <see cref="AiChangeKinds.RevertNote"/> says: a new structure goes to Recently deleted, a new
	/// event or comment is removed, structures go back to the team they had. Anything someone changed again since is
	/// left alone and counted in <see cref="BulkResult.ChangedSince"/>; locked assessments are skipped.
	/// </summary>
	public static BulkResult Revert(SdeStore store, string batchId, string user = null)
	{
		SaveContext ctx = new SaveContext
		{
			Via = SaveVia.Undo,
			BatchId = Guid.NewGuid().ToString("N"),
			User = user,
			Event = "Reverted an AI assistant's change",
		};
		List<EditHistoryRecord> rows = store.GetBatch(batchId);
		int statusChanged = 0;
		int statusSkipped = 0;
		foreach (EditHistoryRecord row in rows.Where(r => r.Field == StatusField && r.AssessmentID != null).Reverse())
		{
			Guid id = row.AssessmentID.Value;
			AssessmentStatus now = store.GetStatus(id).Value;
			AssessmentStatus? before = ParseStatus(row.OldValue);
			if (before == null || AssessmentStatuses.Label(now) != row.NewValue || AssessmentStatuses.IsLocked(now) || AssessmentStatuses.IsLocked(before.Value))
			{
				statusSkipped++;
				continue;
			}
			store.SetStatus(id, before.Value, reason: null, ctx);
			statusChanged++;
		}
		BulkResult result = store.RevertBatch(batchId, ctx);
		result.Changed += statusChanged;
		result.ChangedSince += statusSkipped;
		if (Read(RecordRow(rows)) is AiChangeRecord record)
		{
			Undo(store, batchId, record, ctx, result);
		}
		return result;
	}

	/// <summary>Takes back what a call did outside FEMA's fields (its record), counting what was done and what was left.</summary>
	private static void Undo(SdeStore store, string batchId, AiChangeRecord record, SaveContext ctx, BulkResult result)
	{
		switch (record.Kind)
		{
			case AiChangeKinds.Structure when record.PropertyId is Guid pid:
				PropertyRecord p = store.GetProperty(pid);
				// A person's work on the structure since (an edit, another assessment, a photo) leaves it where it is. Later AI
				// changes, and reverts of them, go with it.
				if (p == null || !p.Active || store.GetPropertyHistory(pid).Any(h => h.BatchId != batchId && h.Via != SaveVia.AiAssistant && h.Via != SaveVia.Undo))
				{
					result.ChangedSince++;
					return;
				}
				try
				{
					store.TrashProperty(pid, ctx);
					result.Changed++;
				}
				catch (AssessmentLockedException ex)
				{
					result.Locked.Add(ex.AssessmentID);
				}
				catch (AssessmentCheckedOutException)
				{
					result.ChangedSince++;
				}
				return;
			case AiChangeKinds.Event when record.EventId is Guid eid:
				if (store.GetEvent(eid) == null || store.AssessmentsInEvent(eid).Count > 0 || store.GetAssignments(null, eid).Count > 0)
				{
					result.ChangedSince++;
					return;
				}
				store.DeleteEvent(eid);
				result.Changed++;
				return;
			case AiChangeKinds.Comment when record.CommentId is Guid cid:
				if (store.RemoveReviewComment(cid))
				{
					result.Changed++;
				}
				else
				{
					result.ChangedSince++;
				}
				return;
			case AiChangeKinds.Assignment when record.TeamId is Guid team && record.Moves != null:
				Dictionary<Guid, AssignmentRecord> now = store.GetAssignments(null, record.EventId).Where(a => a.EventID == record.EventId)
					.GroupBy(a => a.PropertyID).ToDictionary(g => g.Key, g => g.First());
				HashSet<Guid> activeTeams = store.GetTeams().Select(t => t.TeamID).ToHashSet();
				string teamName = store.GetTeams(activeOnly: false).FirstOrDefault(t => t.TeamID == team)?.Name;
				List<Guid> takeOff = new List<Guid>();
				Dictionary<Guid, List<Guid>> giveBack = new Dictionary<Guid, List<Guid>>();
				foreach (AiTeamMove move in record.Moves)
				{
					// Only while the structure is still with that team and nobody has visited it or moved it since.
					if (!now.TryGetValue(move.PropertyId, out AssignmentRecord a) || a.TeamID != team || a.Status != AssignmentStatuses.Assigned
						|| (move.PreviousTeamId is Guid gone && !activeTeams.Contains(gone)))
					{
						result.ChangedSince++;
						continue;
					}
					if (record.FemaTeamName && store.GetProperty(move.PropertyId) is PropertyRecord structure && structure.TeamName == teamName
						&& structure.TeamName != move.PreviousFemaTeamName)
					{
						structure.TeamName = move.PreviousFemaTeamName;
						store.SaveProperty(structure, ctx);
					}
					if (move.PreviousTeamId is Guid previous)
					{
						if (!giveBack.TryGetValue(previous, out List<Guid> list))
						{
							giveBack[previous] = list = new List<Guid>();
						}
						list.Add(move.PropertyId);
					}
					else
					{
						takeOff.Add(move.PropertyId);
					}
				}
				if (takeOff.Count > 0)
				{
					result.Changed += store.Unassign(takeOff, record.EventId, ctx);
				}
				foreach (KeyValuePair<Guid, List<Guid>> back in giveBack)
				{
					store.AssignToTeam(back.Value, back.Key, record.EventId, context: ctx);
					result.Changed += back.Value.Count;
				}
				return;
		}
	}

	/// <summary>
	/// The Terminal command that connects Claude Code to this database:
	/// <c>claude mcp add sde -- "&lt;SDE&gt;" mcp --db "&lt;database&gt;"</c>.
	/// </summary>
	/// <param name="processPath">SDE's program inside the app (Environment.ProcessPath in the app).</param>
	/// <param name="databasePath">The database to serve.</param>
	public static string ClaudeCodeCommand(string processPath, string databasePath) =>
		$"claude mcp add {SdeMcpServer.ServerName} -- {ShellQuote(processPath)} {SdeMcpServer.Command} --db {ShellQuote(databasePath)}";

	/// <summary>The entry to add under "mcpServers" in Claude Desktop's claude_desktop_config.json, as JSON.</summary>
	public static string ClaudeDesktopConfig(string processPath, string databasePath)
	{
		Dictionary<string, object> config = new Dictionary<string, object>
		{
			["mcpServers"] = new Dictionary<string, object>
			{
				[SdeMcpServer.ServerName] = new Dictionary<string, object>
				{
					["command"] = processPath,
					["args"] = new[] { SdeMcpServer.Command, "--db", databasePath },
				},
			},
		};
		return System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		});
	}

	/// <summary>A path in double quotes for zsh or bash, with the characters that are special there escaped.</summary>
	private static string ShellQuote(string path)
	{
		System.Text.StringBuilder quoted = new System.Text.StringBuilder("\"");
		foreach (char c in path ?? "")
		{
			if (c is '"' or '\\' or '$' or '`')
			{
				quoted.Append('\\');
			}
			quoted.Append(c);
		}
		return quoted.Append('"').ToString();
	}

	/// <summary>A status from its label ("Draft", "Letter sent"…), or null.</summary>
	internal static AssessmentStatus? ParseStatus(string label)
	{
		foreach (AssessmentStatus s in Enum.GetValues<AssessmentStatus>())
		{
			if (string.Equals(AssessmentStatuses.Label(s), label?.Trim(), StringComparison.OrdinalIgnoreCase)
				|| string.Equals(s.ToString(), label?.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return s;
			}
		}
		return null;
	}
}
