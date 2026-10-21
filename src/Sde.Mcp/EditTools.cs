using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>
/// The edit tools. They work only while the database's "Mcp.AllowEdits" switch is on (read again on every call), check
/// values exactly as the phone form does (<see cref="FieldInput"/>), save through <see cref="SdeStore.Apply"/>, and
/// record each call as one batch in edit history under the AI assistant's name, so SDE can revert it. Final and
/// Letter sent assessments are refused; nothing can be deleted.
/// </summary>
[McpServerToolType]
public sealed class EditTools(SdeMcpContext sde, ILogger<EditTools> log)
{
	/// <summary>The most field changes one update_fields call takes.</summary>
	public const int MaxChanges = 50;

	private const string Switch = " Works only while AI edits are turned on in SDE (Settings › AI assistants). Each call is recorded in the assessment's history as an AI assistant's change and can be reverted in SDE. Final and Letter sent assessments can't be changed.";

	[McpServerTool(Name = "update_fields", Title = "Change fields on an assessment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Changes fields on one assessment (and its structure). Values are checked exactly as SDE's phone form checks them, the changes are applied in order, and fields that depend on a changed field reset as they do in SDE's editor. All changes are saved together, or none if one is refused. Returns what changed and the new result." + Switch)]
	public UpdateFieldsResult UpdateFields(
		McpServer server,
		[Description("The assessment's ID (find_structures lists them).")] string assessmentId,
		[Description("The changes, in order: each a field key and its new value as text.")] FieldChangeInput[] changes)
	{
		sde.RequireEditsAllowed();
		if (changes == null || changes.Length == 0)
		{
			throw new McpException("Give at least one change: a field key and its new value.");
		}
		if (changes.Length > MaxChanges)
		{
			throw new McpException($"One call can change at most {MaxChanges} fields.");
		}
		SdeStore store = sde.Writer;
		AssessmentFile check = sde.LoadAssessment(store, assessmentId);
		Guid id = check.Assessment.AssessmentID;
		RefuseLocked(store, id);

		// Check every value on a copy first, so a refused value changes nothing and takes no backup.
		List<FieldInputChange> resolved = Resolve(check, changes, store);
		sde.EnsureBackup();
		SaveContext ctx = Context(server);
		AssessmentFile saved;
		try
		{
			saved = store.Apply(id, file => FieldInput.Apply(file, resolved, store.Reference, store.Calculator), ctx);
		}
		catch (FieldInputException ex)
		{
			throw new McpException(ex.Message + " Nothing was changed.");
		}
		catch (AssessmentLockedException ex)
		{
			throw Locked(ex.Status);
		}
		ReferenceData r = store.Reference;
		List<FieldChanged> changed = store.GetBatch(ctx.BatchId).Where(h => h.Field != null).Select(h => new FieldChanged(h.Field, h.Label,
			Describe.History(h.Field, h.OldValue, r), Describe.History(h.Field, h.NewValue, r))).ToList();
		log.LogInformation("update_fields on {Assessment} by {User}: {Count} field(s) changed (batch {Batch})", id, ctx.User, changed.Count, ctx.BatchId);
		return new UpdateFieldsResult(sde.DatabasePath, id.ToString(), Describe.Address(saved.Property, r), changed.Count == 0 ? null : ctx.BatchId,
			changed, Describe.Result(saved.Assessment), changed.Count == 0 ? "Nothing changed: the fields already had these values." : null);
	}

	[McpServerTool(Name = "set_status", Title = "Set an assessment's status", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Moves an assessment to Draft, Reviewed or Appeal. Making an assessment Final or marking its letter sent stays a person's decision in SDE, and so does reopening a Final one." + Switch)]
	public SetStatusResult SetStatus(
		McpServer server,
		[Description("The assessment's ID (find_structures lists them).")] string assessmentId,
		[Description("The new status: Draft, Reviewed or Appeal.")] string status)
	{
		sde.RequireEditsAllowed();
		AssessmentStatus target = AiAssistantSettings.ParseStatus(status) switch
		{
			AssessmentStatus s when s is AssessmentStatus.Draft or AssessmentStatus.Reviewed or AssessmentStatus.Appeal => s,
			AssessmentStatus.Final or AssessmentStatus.LetterSent => throw new McpException("Only a person can make an assessment Final or mark its letter sent, in SDE. An AI assistant can set Draft, Reviewed or Appeal."),
			_ => throw new McpException("status must be Draft, Reviewed or Appeal."),
		};
		SdeStore store = sde.Writer;
		AssessmentFile file = sde.LoadAssessment(store, assessmentId);
		Guid id = file.Assessment.AssessmentID;
		AssessmentStatusRecord current = RefuseLocked(store, id);
		string address = Describe.Address(file.Property, store.Reference);
		if (current.Value == target)
		{
			return new SetStatusResult(sde.DatabasePath, id.ToString(), address, AssessmentStatuses.Label(current.Value), AssessmentStatuses.Label(target), null,
				$"Nothing changed: the assessment is already {AssessmentStatuses.Label(target)}.");
		}
		sde.EnsureBackup();
		SaveContext ctx = Context(server);
		try
		{
			// No reason is passed, so this can never reopen an assessment someone made Final in the meantime: the store
			// refuses to reopen without one.
			store.SetStatus(id, target, reason: null, ctx);
		}
		catch (ArgumentException)
		{
			throw Locked(store.GetStatus(id).Value);
		}
		catch (InvalidOperationException ex)
		{
			throw new McpException(ex.Message);
		}
		log.LogInformation("set_status on {Assessment} by {User}: {From} to {To} (batch {Batch})", id, ctx.User, current.Value, target, ctx.BatchId);
		return new SetStatusResult(sde.DatabasePath, id.ToString(), address, AssessmentStatuses.Label(current.Value), AssessmentStatuses.Label(target), ctx.BatchId, null);
	}

	/// <summary>
	/// Applies the changes in order to a copy, mapping a choice given by name to its ID (the choices offered at that
	/// point, so a county follows the state set before it). Returns the changes as <see cref="FieldInput"/> takes them;
	/// throws a tool error naming the first refused change.
	/// </summary>
	private static List<FieldInputChange> Resolve(AssessmentFile copy, IReadOnlyList<FieldChangeInput> changes, SdeStore store)
	{
		List<FieldInputChange> resolved = new List<FieldInputChange>();
		for (int i = 0; i < changes.Count; i++)
		{
			FieldChangeInput change = changes[i];
			FieldInfo f = ReadTools.RequireField(change?.Key);
			string value = change.Value;
			if (Describe.IsChoice(f.Kind) && !string.IsNullOrWhiteSpace(value) && !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
			{
				(int Id, string Name) match = FieldCatalog.Options(f, copy, store.Reference)
					.FirstOrDefault(o => string.Equals(o.Name?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));
				if (match.Name != null)
				{
					value = match.Id.ToString(CultureInfo.InvariantCulture);
				}
			}
			FieldInputChange next = new FieldInputChange(f.Key, value);
			try
			{
				FieldInput.Apply(copy, new[] { next }, store.Reference, store.Calculator);
			}
			catch (FieldInputException ex)
			{
				string hint = Describe.IsChoice(f.Kind) ? " get_field_options lists the choices." : "";
				throw new McpException($"Change {i + 1} ({f.Key}): {ex.Message}{hint} Nothing was changed.");
			}
			resolved.Add(next);
		}
		return resolved;
	}

	/// <summary>The assessment's status; a tool error when it is Final or Letter sent.</summary>
	private static AssessmentStatusRecord RefuseLocked(SdeStore store, Guid id)
	{
		AssessmentStatusRecord status = store.GetStatus(id);
		if (status.IsLocked)
		{
			throw Locked(status.Value);
		}
		return status;
	}

	private static McpException Locked(AssessmentStatus status) => new McpException(
		$"This assessment is {AssessmentStatuses.Label(status)}, so SDE won't let an AI assistant change it, and nothing was changed. Only a person can reopen it, in SDE (Status, then Reopen, with a reason).");

	/// <summary>One call, one batch in edit history, under the connected assistant's name.</summary>
	private static SaveContext Context(McpServer server) => new SaveContext
	{
		Via = SaveVia.AiAssistant,
		BatchId = Guid.NewGuid().ToString("N"),
		User = ClientName(server?.ClientInfo?.Name) + " (AI)",
	};

	/// <summary>The assistant's name as it introduced itself ("claude-code"), kept short and plain.</summary>
	internal static string ClientName(string name)
	{
		string clean = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
		if (clean.Length > 40)
		{
			clean = clean[..40].Trim();
		}
		return clean.Length == 0 ? "AI assistant" : clean;
	}
}
