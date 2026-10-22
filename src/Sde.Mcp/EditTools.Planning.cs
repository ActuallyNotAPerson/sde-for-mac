using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sde.Core;
using Sde.Core.Addresses;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>
/// The edit tools that add things rather than change fields: a structure, a review comment, a team assignment, an
/// event. Like the others they work only while AI edits are on, take the session's backup before the first change,
/// and record each call as one batch under the assistant's name. What a call adds outside FEMA's fields is kept in the
/// batch's record row, so SDE's Settings › AI assistants can list it and revert it.
/// </summary>
public sealed partial class EditTools
{
	/// <summary>The most structures one assign_to_team call takes.</summary>
	public const int MaxAssign = 500;

	private const string Recorded = " Works only while AI edits are turned on in SDE (Settings › AI assistants). Each call is recorded as an AI assistant's change that SDE can revert.";

	[McpServerTool(Name = "create_structure", Title = "Add a structure", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Adds a structure with its first assessment (a Draft), as SDE's New structure does: the street address and the structure type at least, then any other fields, checked exactly as SDE's phone form checks them. With an event, the new assessment joins it. Nothing is added if a value is refused. Returns the new assessment ID, and says when another structure already has the same address." + Recorded)]
	public CreateStructureResult CreateStructure(
		McpServer server,
		[Description("The street name, such as \"River\" (the suffix, such as Street, goes in Property.StreetSuffixID).")] string streetName,
		[Description("Residential or Non-residential.")] string structureType,
		[Description("The house number, such as \"123\".")] string streetNumber = null,
		[Description("Other fields, in order: each a field key and its value as text, as update_fields takes them (field_help lists the keys).")] FieldChangeInput[] fields = null,
		[Description("An event for the new assessment to join: its name or ID (list_events lists them).")] string @event = null)
	{
		sde.RequireEditsAllowed();
		if (string.IsNullOrWhiteSpace(streetName))
		{
			throw new McpException("Give the street name (and the house number, if it has one).");
		}
		if (string.IsNullOrWhiteSpace(structureType))
		{
			throw new McpException("Give the structure type: Residential or Non-residential.");
		}
		if (fields != null && fields.Length > MaxChanges)
		{
			throw new McpException($"One call can set at most {MaxChanges} other fields.");
		}
		SdeStore store = sde.Writer;
		EventRecord e = Lookups.OptionalEvent(store, @event);
		string type = new string(structureType.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() switch
		{
			"residential" or "1" => "1",
			"nonresidential" or "2" => "2",
			_ => throw new McpException("structureType must be Residential or Non-residential."),
		};

		// Everything is checked on the new, unsaved draft first, so a refused value adds nothing and takes no backup.
		AssessmentFile draft = store.NewDraft(new PropertyRecord(), store.GetPreferences().UseDefaults);
		Resolve(draft, new[]
		{
			new FieldChangeInput("Property.StructureTypeID", type),
			new FieldChangeInput("Property.StreetNumber", streetNumber),
			new FieldChangeInput("Property.StreetName", streetName),
		}, store, "Address or type");
		Resolve(draft, fields ?? Array.Empty<FieldChangeInput>(), store, "Field");

		ReferenceData r = store.Reference;
		AddressMatcher matcher = new AddressMatcher(r);
		AddressKey key = matcher.KeyFor(draft.Property);
		List<PropertyRecord> same = key.CanMatch ? store.GetProperties().Where(p => matcher.Same(matcher.KeyFor(p), key)).ToList() : new List<PropertyRecord>();

		sde.EnsureBackup();
		SaveContext ctx = Context(server);
		store.SaveAssessment(draft, ctx);
		Guid assessmentId = draft.Assessment.AssessmentID;
		Guid propertyId = draft.Property.PropertyID;
		if (e != null)
		{
			store.SetEvent(new[] { assessmentId }, e.EventID, ctx);
		}
		string address = Describe.Address(draft.Property, r);
		string kind = Describe.StructureType(draft.Property.StructureTypeID);
		AiAssistantSettings.Record(store, ctx, $"Added {address} ({kind}{(e == null ? "" : ", " + e.Label)})",
			new AiChangeRecord { Kind = AiChangeKinds.Structure, PropertyId = propertyId, AssessmentId = assessmentId, EventId = e?.EventID });
		log.LogInformation("create_structure by {User}: assessment {Assessment} (batch {Batch})", ctx.User, assessmentId, ctx.BatchId);

		AssessmentFile saved = store.LoadAssessment(assessmentId);
		List<FieldValue> set = new[] { "Property.StructureTypeID", "Property.StreetNumber", "Property.StreetName" }
			.Concat((fields ?? Array.Empty<FieldChangeInput>()).Select(f => ReadTools.RequireField(f.Key).Key))
			.Distinct()
			.Select(k =>
			{
				FieldInfo f = FieldCatalog.Get(k);
				object value = FieldCatalog.GetValue(saved, f);
				string shown = Describe.Display(f, value, r);
				string stored = FieldCatalog.Canonical(value);
				return new FieldValue(f.Key, f.Label, Describe.Shorten(shown), stored == shown ? null : stored);
			}).ToList();
		string note = same.Count == 0 ? null
			: $"{(same.Count == 1 ? "Another structure has" : $"{same.Count} other structures have")} the same address ({string.Join(", ", same.Take(5).Select(p => p.PropertyID))}). Check it isn't the same building.";
		return new CreateStructureResult(sde.DatabasePath, assessmentId.ToString(), propertyId.ToString(), address, kind, e?.Label, ctx.BatchId, set,
			Describe.Result(saved.Assessment), note);
	}

	[McpServerTool(Name = "add_review_comment", Title = "Comment on an assessment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Adds a review comment to an assessment (the Review page's comments), about the whole assessment or one field, or replies to a comment (get_review_comments lists them). A comment doesn't change the assessment, so Final ones can be commented on too; assessments checked out to another Mac can't. Write plainly, for the staff who review the assessment." + Recorded)]
	public CommentResult AddReviewComment(
		McpServer server,
		[Description("The assessment's ID (find_structures lists them).")] string assessmentId,
		[Description("The comment, at most 4,000 characters.")] string comment,
		[Description("The field it's about, such as \"Assessment.DepthOfFloodAboveFFE\" (get_assessment lists the keys). Leave out for the whole assessment.")] string fieldKey = null,
		[Description("To answer a comment: its ID (get_review_comments lists them).")] string replyTo = null)
	{
		sde.RequireEditsAllowed();
		SdeStore store = sde.Writer;
		AssessmentFile file = sde.LoadAssessment(store, assessmentId);
		Guid id = file.Assessment.AssessmentID;
		if (store.GetCheckOut(id) is CheckOutRecord checkOut)
		{
			// Checking the work package back in replaces the assessment's comments with the field Mac's.
			throw CheckedOut(new AssessmentCheckedOutException(id, checkOut));
		}
		if (string.IsNullOrWhiteSpace(comment))
		{
			throw new McpException("Write the comment.");
		}
		if (comment.Trim().Length > SdeStore.MaxCommentLength)
		{
			throw new McpException($"A comment can be at most {SdeStore.MaxCommentLength.ToString("N0", Describe.Us)} characters.");
		}
		string field = string.IsNullOrWhiteSpace(fieldKey) ? null : ReadTools.RequireField(fieldKey).Key;
		Guid? parent = null;
		if (!string.IsNullOrWhiteSpace(replyTo))
		{
			parent = Guid.TryParse(replyTo.Trim(), out Guid p) && store.GetReviewComments(id).Any(c => c.CommentID == p)
				? p
				: throw new McpException($"There's no comment {Describe.Shorten(replyTo, 60)} on this assessment. get_review_comments lists its comments.");
		}
		sde.EnsureBackup();
		SaveContext ctx = Context(server);
		ReviewCommentRecord added;
		try
		{
			added = store.AddReviewComment(id, comment, field, parent, ctx);
		}
		catch (ArgumentException ex)
		{
			throw new McpException(ex.Message + " Nothing was added.");
		}
		string address = Describe.Address(file.Property, store.Reference);
		AiAssistantSettings.Record(store, ctx, $"{(parent == null ? "Commented on" : "Replied to a comment on")} {address}: \"{Describe.Shorten(added.Body.ReplaceLineEndings(" "), 80)}\"",
			new AiChangeRecord { Kind = AiChangeKinds.Comment, PropertyId = file.Property.PropertyID, AssessmentId = id, CommentId = added.CommentID });
		log.LogInformation("add_review_comment on {Assessment} by {User} (batch {Batch})", id, ctx.User, ctx.BatchId);
		return new CommentResult(sde.DatabasePath, id.ToString(), address, added.CommentID.ToString(), parent?.ToString(), field, added.Author, ctx.BatchId);
	}

	[McpServerTool(Name = "assign_to_team", Title = "Assign structures to a team", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Gives structures to a field team to visit, for an event (or for no event), as SDE's Assign to team does. A structure another team had for that event moves to this team. With alsoSetFemaTeamName, FEMA's own Team name field on each structure is set too; that field is exported. Teams are named by name or ID; the error for an unknown team lists them." + Recorded)]
	public AssignResult AssignToTeam(
		McpServer server,
		[Description("The team's name or ID.")] string team,
		[Description("The structures: each an assessment ID or a structure (property) ID, as find_structures lists them. At most 500.")] string[] structures,
		[Description("The event the visits are for: its name or ID (list_events lists them). Leave out for no event.")] string @event = null,
		[Description("When the visits are due, as yyyy-MM-dd.")] string dueDate = null,
		[Description("Also set FEMA's Team name field on each structure (it is exported to FEMA's format).")] bool alsoSetFemaTeamName = false)
	{
		sde.RequireEditsAllowed();
		SdeStore store = sde.Writer;
		TeamRecord t = Lookups.RequireTeam(store, team);
		EventRecord e = Lookups.OptionalEvent(store, @event);
		DateTime? due = Lookups.ParseDate(dueDate, "dueDate");
		if (structures == null || structures.Length == 0)
		{
			throw new McpException("Give at least one structure: an assessment ID or a structure ID (find_structures lists them).");
		}
		if (structures.Length > MaxAssign)
		{
			throw new McpException($"One call can assign at most {MaxAssign} structures.");
		}
		List<Guid> propertyIds = new List<Guid>();
		foreach (string text in structures)
		{
			Guid id = Guid.TryParse(text?.Trim(), out Guid parsed)
				? parsed
				: throw new McpException($"\"{Describe.Shorten(text, 60)}\" isn't a structure or assessment ID; find_structures lists them. Nothing was assigned.");
			PropertyRecord p = store.GetProperty(id) ?? (store.LoadAssessment(id) is AssessmentFile f && f.Assessment.Active ? f.Property : null);
			if (p == null)
			{
				throw new McpException($"There's no structure or assessment {id} in this database. find_structures lists them. Nothing was assigned.");
			}
			if (!p.Active)
			{
				throw new McpException($"The structure at {Describe.Address(p, store.Reference)} is in Recently deleted, so it can't be assigned. Nothing was assigned.");
			}
			propertyIds.Add(p.PropertyID);
		}
		propertyIds = propertyIds.Distinct().ToList();
		if (alsoSetFemaTeamName)
		{
			// Checking a work package back in replaces the structure's FEMA fields, so the team name would be lost.
			foreach (Guid pid in propertyIds)
			{
				foreach (AssessmentRecord a in store.GetAssessmentsForProperty(pid))
				{
					if (store.GetCheckOut(a.AssessmentID) is CheckOutRecord checkOut)
					{
						throw new McpException($"{new AssessmentCheckedOutException(a.AssessmentID, checkOut).Message} FEMA's Team name can't be set on it, so nothing was assigned.");
					}
				}
			}
		}
		Dictionary<Guid, AssignmentRecord> before = store.GetAssignments(null, e?.EventID).Where(a => a.EventID == e?.EventID)
			.GroupBy(a => a.PropertyID).ToDictionary(g => g.Key, g => g.First());
		Dictionary<Guid, string> teamNames = store.GetTeams(activeOnly: false).ToDictionary(x => x.TeamID, x => x.Name);
		// Structures the team already has for this event are left as they are (SdeStore.AssignToTeam does the same).
		List<Guid> moving = propertyIds.Where(pid => !(before.TryGetValue(pid, out AssignmentRecord a) && a.TeamID == t.TeamID)).ToList();

		Dictionary<Guid, string> femaNames = alsoSetFemaTeamName ? moving.ToDictionary(pid => pid, pid => store.GetProperty(pid).TeamName) : null;
		string batchId = null;
		List<AssignmentRecord> assigned;
		if (moving.Count > 0)
		{
			sde.EnsureBackup();
			SaveContext ctx = Context(server);
			assigned = store.AssignToTeam(propertyIds, t.TeamID, e?.EventID, due, alsoSetFemaTeamName, ctx);
			AiAssistantSettings.Record(store, ctx,
				$"Assigned {(moving.Count == 1 ? Describe.Address(store.GetProperty(moving[0]), store.Reference) : $"{moving.Count} structures")} to {t.Name}{(e == null ? "" : " for " + e.Label)}",
				new AiChangeRecord
				{
					Kind = AiChangeKinds.Assignment,
					TeamId = t.TeamID,
					EventId = e?.EventID,
					FemaTeamName = alsoSetFemaTeamName,
					Moves = moving.Select(pid => new AiTeamMove
					{
						PropertyId = pid,
						PreviousTeamId = before.TryGetValue(pid, out AssignmentRecord a) ? a.TeamID : null,
						PreviousFemaTeamName = femaNames?.GetValueOrDefault(pid),
					}).ToList(),
				});
			batchId = ctx.BatchId;
			log.LogInformation("assign_to_team by {User}: {Count} structure(s) to {Team} (batch {Batch})", ctx.User, moving.Count, t.Name, ctx.BatchId);
		}
		else
		{
			assigned = store.GetAssignments(t.TeamID, e?.EventID).Where(a => a.EventID == e?.EventID && propertyIds.Contains(a.PropertyID)).ToList();
		}
		Dictionary<Guid, AssignmentRecord> now = assigned.GroupBy(a => a.PropertyID).ToDictionary(g => g.Key, g => g.First());
		List<AssignedStructure> rows = propertyIds.Select(pid => new AssignedStructure(pid.ToString(), Describe.Address(store.GetProperty(pid), store.Reference),
			now.TryGetValue(pid, out AssignmentRecord a) ? a.ShortCode : null,
			before.TryGetValue(pid, out AssignmentRecord b) && b.TeamID != t.TeamID ? teamNames.GetValueOrDefault(b.TeamID) : null)).ToList();
		string note = moving.Count == 0 ? $"Nothing changed: {t.Name} already had {(propertyIds.Count == 1 ? "it" : "them all")}." : null;
		return new AssignResult(sde.DatabasePath, t.Name, e?.Label, moving.Count, propertyIds.Count - moving.Count, rows, batchId, note);
	}

	[McpServerTool(Name = "create_event", Title = "Create an event", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Creates an event (a flood, a storm or another disaster) that assessments can belong to, as SDE's New event does. Refused when an event of that name already exists. Put assessments in it with create_structure, or in SDE." + Recorded)]
	public CreateEventResult CreateEvent(
		McpServer server,
		[Description("The event's name, such as \"September 2026 flood\".")] string name,
		[Description("Flood (the default), Hurricane, Tornado, Fire or Other.")] string kind = "Flood",
		[Description("The disaster declaration number, such as DR-4999.")] string disasterNumber = null,
		[Description("The first day, as yyyy-MM-dd.")] string startDate = null,
		[Description("The last day, as yyyy-MM-dd (leave out while it continues).")] string endDate = null,
		[Description("Notes for staff.")] string notes = null,
		[Description("Feet to add to high-water mark elevations recorded in another vertical datum.")] decimal? datumOffsetFt = null)
	{
		sde.RequireEditsAllowed();
		SdeStore store = sde.Writer;
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new McpException("Give the event a name.");
		}
		string chosenKind = string.IsNullOrWhiteSpace(kind) ? EventKinds.All[0]
			: EventKinds.All.FirstOrDefault(k => string.Equals(k, kind.Trim(), StringComparison.OrdinalIgnoreCase))
				?? throw new McpException("kind must be one of " + string.Join(", ", EventKinds.All) + ".");
		if (notes != null && notes.Length > FieldInput.MaxTextLength)
		{
			throw new McpException($"Notes can be at most {FieldInput.MaxTextLength.ToString("N0", Describe.Us)} characters.");
		}
		EventRecord existing = store.GetEvents().FirstOrDefault(x => string.Equals(x.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
		if (existing != null)
		{
			throw new McpException($"There's already an event called {existing.Label} (ID {existing.EventID}). Use it, or give the new event another name.");
		}
		EventRecord e = new EventRecord
		{
			Name = name.Trim(),
			Kind = chosenKind,
			DRNumber = disasterNumber,
			StartDate = Lookups.ParseDate(startDate, "startDate"),
			EndDate = Lookups.ParseDate(endDate, "endDate"),
			Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
			DatumOffsetFt = datumOffsetFt,
		};
		if (e.StartDate is DateTime start && e.EndDate is DateTime end && end < start)
		{
			throw new McpException("The event can't end before it starts.");
		}
		sde.EnsureBackup();
		SaveContext ctx = Context(server);
		try
		{
			store.SaveEvent(e, ctx);
		}
		catch (ArgumentException ex)
		{
			throw new McpException(ex.Message + " Nothing was created.");
		}
		AiAssistantSettings.Record(store, ctx, "Created event " + e.Label, new AiChangeRecord { Kind = AiChangeKinds.Event, EventId = e.EventID });
		log.LogInformation("create_event by {User}: {Event} (batch {Batch})", ctx.User, e.EventID, ctx.BatchId);
		return new CreateEventResult(sde.DatabasePath, Lookups.Item(e, 0), ctx.BatchId);
	}
}
