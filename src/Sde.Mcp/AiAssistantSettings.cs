using Dapper;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>One call by an AI assistant that changed the database (one batch in edit history).</summary>
/// <param name="BatchId">The batch, for <see cref="AiAssistantSettings.Revert"/>.</param>
/// <param name="ChangedOn">When the change was made.</param>
/// <param name="ChangedBy">Who made it, such as "claude-code (AI)".</param>
/// <param name="Changes">How many history rows the call wrote (one per changed field or status).</param>
/// <param name="AssessmentIds">The assessments it changed.</param>
/// <param name="Summary">What changed in a line, such as "123 River Rd: Inspector, Flood elevation".</param>
public sealed record AiChangeBatch(string BatchId, DateTime ChangedOn, string ChangedBy, int Changes, IReadOnlyList<Guid> AssessmentIds, string Summary);

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
		List<AiChangeBatch> batches = new List<AiChangeBatch>();
		foreach (IGrouping<string, EditHistoryRecord> batch in rows.GroupBy(r => r.BatchId))
		{
			List<EditHistoryRecord> list = batch.ToList();
			List<Guid> assessments = list.Where(r => r.AssessmentID != null).Select(r => r.AssessmentID.Value).Distinct().ToList();
			List<string> labels = list.Select(r => r.Field == StatusField ? "Status: " + r.NewValue : r.Label).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList();
			string what = labels.Count <= 4 ? string.Join(", ", labels) : string.Join(", ", labels.Take(3)) + $" and {labels.Count - 3} more";
			string where = assessments.Count > 1
				? $"{assessments.Count} assessments"
				: list[0].PropertyID is Guid pid ? Address(store, pid, addresses) : "";
			batches.Add(new AiChangeBatch(batch.Key, list.Max(r => r.ChangedOn), list[0].ChangedBy, list.Count, assessments,
				string.IsNullOrEmpty(where) ? what : where + ": " + what));
		}
		return batches.OrderByDescending(b => b.ChangedOn).ToList();
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
	/// <see cref="SdeStore.RevertBatch"/>, and a status change by setting the status back. Anything someone changed again
	/// since is left alone and counted in <see cref="BulkResult.ChangedSince"/>; locked assessments are skipped.
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
		int statusChanged = 0;
		int statusSkipped = 0;
		foreach (EditHistoryRecord row in store.GetBatch(batchId).Where(r => r.Field == StatusField && r.AssessmentID != null).Reverse())
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
		return result;
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
