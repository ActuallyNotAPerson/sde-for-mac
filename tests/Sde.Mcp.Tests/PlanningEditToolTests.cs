using System.Text.Json;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Mcp;

namespace Sde.Mcp.Tests;

/// <summary>
/// create_structure, add_review_comment, assign_to_team and create_event: the switch, the refusals, one batch per call
/// listed in Settings › AI assistants, and what Revert does with each.
/// </summary>
public sealed class PlanningEditToolTests
{
	private const string AiUser = McpSession.ClientName + " (AI)";

	private static int BackupsBeforeAiEdits(SdeStore store) =>
		new BackupService(store.Database).List().Count(b => b.Reason == "before AI edits");

	private static object NewStructure(string number, string street, string type = "Residential", object[] fields = null, string @event = null) =>
		new { streetNumber = number, streetName = street, structureType = type, fields, @event };

	private static object Field(string key, string value) => new { key, value };

	[Fact]
	public async Task The_new_edit_tools_are_refused_while_the_switch_is_off()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: false);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		TestData.Team(s.Store, "North");
		int structures = s.Store.GetProperties().Count;

		Assert.Contains("turned off", await s.CallErrorAsync("create_structure", NewStructure("1", "Oak")));
		Assert.Contains("turned off", await s.CallErrorAsync("add_review_comment", new { assessmentId = ranch.Id().ToString(), comment = "Check the depth." }));
		Assert.Contains("turned off", await s.CallErrorAsync("assign_to_team", new { team = "North", structures = new[] { ranch.Id().ToString() } }));
		Assert.Contains("turned off", await s.CallErrorAsync("create_event", new { name = "October storm" }));

		Assert.Equal(structures, s.Store.GetProperties().Count);
		Assert.Empty(s.Store.GetReviewComments(ranch.Id()));
		Assert.Empty(s.Store.GetAssignments());
		Assert.Empty(s.Store.GetEvents());
		Assert.Empty(AiAssistantSettings.RecentChanges(s.Store));
		Assert.Equal(0, BackupsBeforeAiEdits(s.Store));
	}

	[Fact]
	public async Task Create_structure_adds_a_draft_that_revert_moves_to_recently_deleted()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		EventRecord flood = TestData.Event(s.Store);

		// Refused values add nothing and take no backup.
		Assert.Contains("Residential or Non-residential", await s.CallErrorAsync("create_structure", NewStructure("45", "Oak", "Barn")));
		string bad = await s.CallErrorAsync("create_structure", NewStructure("45", "Oak", fields: new[] { Field("Property.City", "Testville"), Field("Property.FoundationTypeID", "Floating") }));
		Assert.Contains("Field 2 (Property.FoundationTypeID)", bad);
		Assert.Contains("Nothing was changed", bad);
		Assert.Contains("street name", await s.CallErrorAsync("create_structure", NewStructure("45", " ")));
		Assert.Contains("list_events", await s.CallErrorAsync("create_structure", NewStructure("45", "Oak", @event: "Winter")));
		Assert.Empty(s.Store.GetProperties());
		Assert.Equal(0, BackupsBeforeAiEdits(s.Store));

		JsonElement r = await s.CallOkAsync("create_structure", NewStructure("45", "Oak", "residential",
			new[] { Field("Property.StreetSuffixID", "Street"), Field("Property.City", "Testville"), Field("Assessment.InspectorName", "Inspector B") }, "September flood"));
		Assert.Equal(s.Db.Database.FilePath, r.Str("database"));
		Assert.Equal("45 Oak Street", r.Str("address"));
		Assert.Equal("Residential", r.Str("structureType"));
		Assert.Equal("September flood (DR-4999)", r.Str("event"));
		Assert.Contains(r.List("fields"), f => f.Str("key") == "Property.StreetSuffixID" && f.Str("value") == "Street");
		Assert.Contains(r.List("fields"), f => f.Str("key") == "Assessment.InspectorName" && f.Str("value") == "Inspector B");
		Assert.Null(r.Str("note"));
		Guid id = Guid.Parse(r.Str("assessmentId"));
		string batch = r.Str("batchId");
		AssessmentFile created = s.Store.LoadAssessment(id);
		Assert.Equal("Testville", created.Property.City);
		Assert.Equal(AssessmentStatus.Draft, s.Store.GetStatus(id).Value);
		Assert.Equal(flood.EventID, s.Store.GetEventOf(id));
		Assert.Equal(1, BackupsBeforeAiEdits(s.Store));
		List<EditHistoryRecord> rows = s.Store.GetBatch(batch);
		Assert.All(rows, h => Assert.Equal(SaveVia.AiAssistant, h.Via));
		Assert.All(rows, h => Assert.Equal(AiUser, h.ChangedBy));
		Assert.Contains(s.Store.GetHistory(id), h => h.Label == SdeStore.CreatedEvent && h.ChangedBy == AiUser);
		Assert.Equal(1, (await s.CallOkAsync("find_structures", new { query = "oak" })).Int("total"));

		AiChangeBatch listed = Assert.Single(AiAssistantSettings.RecentChanges(s.Store));
		Assert.Equal(batch, listed.BatchId);
		Assert.Equal(AiChangeKinds.Structure, listed.Kind);
		Assert.Equal("Added 45 Oak Street (Residential, September flood (DR-4999))", listed.Summary);
		Assert.Equal(new[] { id }, listed.AssessmentIds);
		Assert.Contains("Recently deleted", listed.RevertNote);

		// The same address again is allowed, with a warning.
		JsonElement twin = await s.CallOkAsync("create_structure", NewStructure("45", "oak", fields: new[] { Field("Property.StreetSuffixID", "Street") }));
		Assert.Contains("same address", twin.Str("note"));
		Assert.Contains(created.Property.PropertyID.ToString(), twin.Str("note"));

		// Revert moves the new structure to Recently deleted (it can be restored there).
		BulkResult undone = AiAssistantSettings.Revert(s.Store, batch, "Tester");
		Assert.Equal(1, undone.Changed);
		Assert.Equal(0, undone.ChangedSince);
		Assert.False(s.Store.GetProperty(created.Property.PropertyID).Active);
		Assert.Contains(s.Store.GetDeletedItems(), d => d.PropertyID == created.Property.PropertyID);

		// A structure a person has worked on since stays.
		Guid twinId = Guid.Parse(twin.Str("assessmentId"));
		s.Store.Apply(twinId, f => f.Assessment.InspectorName = "Person's value");
		BulkResult kept = AiAssistantSettings.Revert(s.Store, twin.Str("batchId"), "Tester");
		Assert.Equal(0, kept.Changed);
		Assert.Equal(1, kept.ChangedSince);
		Assert.True(s.Store.GetProperty(s.Store.LoadAssessment(twinId).Property.PropertyID).Active);
	}

	[Fact]
	public async Task Add_review_comment_works_on_final_assessments_but_not_checked_out_ones()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		s.Store.SetStatus(ranch.Id(), AssessmentStatus.Final);
		DateTime? lastUpdate = s.Store.LoadAssessment(ranch.Id()).Assessment.LastUpdate;
		string id = ranch.Id().ToString();

		JsonElement r = await s.CallOkAsync("add_review_comment", new { assessmentId = id, comment = "The flood depth looks high for this street.", fieldKey = "assessment.depthofflood" });
		Assert.Equal("123 River Street", r.Str("address"));
		Assert.Equal("Assessment.DepthOfFlood", r.Str("fieldKey"));
		Assert.Equal(AiUser, r.Str("author"));
		ReviewCommentRecord stored = Assert.Single(s.Store.GetReviewComments(ranch.Id()));
		Assert.Equal(SaveVia.AiAssistant, stored.Via);
		Assert.Equal(AiUser, stored.Author);
		Assert.Equal(r.Str("commentId"), stored.CommentID.ToString());

		JsonElement reply = await s.CallOkAsync("add_review_comment", new { assessmentId = id, comment = "The neighbours' depths are similar.", replyTo = stored.CommentID.ToString() });
		Assert.Equal(stored.CommentID.ToString(), reply.Str("replyTo"));
		JsonElement threads = await s.CallOkAsync("get_review_comments", new { assessmentId = id });
		JsonElement thread = Assert.Single(threads.List("threads"));
		Assert.Equal("AI assistant", thread.Str("via"));
		Assert.Single(thread.List("replies"));

		Assert.Contains("get_review_comments", await s.CallErrorAsync("add_review_comment", new { assessmentId = id, comment = "x", replyTo = Guid.NewGuid().ToString() }));
		Assert.Contains("Write the comment", await s.CallErrorAsync("add_review_comment", new { assessmentId = id, comment = " " }));
		Assert.Contains("4,000", await s.CallErrorAsync("add_review_comment", new { assessmentId = id, comment = new string('x', 4001) }));
		Assert.Contains("There's no field called", await s.CallErrorAsync("add_review_comment", new { assessmentId = id, comment = "x", fieldKey = "Depth" }));

		// The assessment itself is untouched and stays Final.
		Assert.Equal(AssessmentStatus.Final, s.Store.GetStatus(ranch.Id()).Value);
		Assert.Equal(lastUpdate, s.Store.LoadAssessment(ranch.Id()).Assessment.LastUpdate);

		List<AiChangeBatch> recent = AiAssistantSettings.RecentChanges(s.Store);
		Assert.Equal(2, recent.Count);
		Assert.All(recent, b => Assert.Equal(AiChangeKinds.Comment, b.Kind));
		Assert.Equal("Commented on 123 River Street: \"The flood depth looks high for this street.\"", recent[1].Summary);
		Assert.StartsWith("Replied to a comment on 123 River Street", recent[0].Summary);
		Assert.Equal(new[] { ranch.Id() }, recent[1].AssessmentIds);

		// Revert removes a comment nobody has answered, and leaves one that has replies.
		Assert.Equal(1, AiAssistantSettings.Revert(s.Store, reply.Str("batchId"), "Tester").Changed);
		s.Store.AddReviewComment(ranch.Id(), "Agreed, recheck it.", parentId: stored.CommentID);
		BulkResult kept = AiAssistantSettings.Revert(s.Store, r.Str("batchId"), "Tester");
		Assert.Equal(1, kept.ChangedSince);
		Assert.Equal(2, s.Store.GetReviewComments(ranch.Id()).Count);

		// Checking a work package back in would replace the comments, so a checked-out assessment is refused.
		AssessmentFile away = SampleCases.RanchFlood(s.Store);
		WorkPackages.CheckOut(s.Store, new[] { away.Id() }, Path.Combine(s.Db.Folder, "out" + WorkPackages.Extension), "Team 1");
		Assert.Contains("checked out to Team 1", await s.CallErrorAsync("add_review_comment", new { assessmentId = away.Id().ToString(), comment = "Check it." }));
		Assert.Empty(s.Store.GetReviewComments(away.Id()));
	}

	[Fact]
	public async Task Assign_to_team_moves_structures_and_revert_gives_them_back()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		TeamRecord north = TestData.Team(s.Store, "North");
		TeamRecord south = TestData.Team(s.Store, "South");
		EventRecord flood = TestData.Event(s.Store);
		AssessmentFile a = TestData.Structure(s.Store, "1");
		AssessmentFile b = TestData.Structure(s.Store, "2");
		AssessmentFile c = TestData.Structure(s.Store, "3");
		s.Store.AssignToTeam(new[] { a.Property.PropertyID }, south.TeamID, flood.EventID);

		Assert.Contains("North, South", await s.CallErrorAsync("assign_to_team", new { team = "East", structures = new[] { a.Id().ToString() } }));
		Assert.Contains("yyyy-MM-dd", await s.CallErrorAsync("assign_to_team", new { team = "North", structures = new[] { a.Id().ToString() }, dueDate = "Friday" }));
		Assert.Contains("find_structures", await s.CallErrorAsync("assign_to_team", new { team = "North", structures = new[] { Guid.NewGuid().ToString() } }));
		Assert.Contains("isn't a structure or assessment ID", await s.CallErrorAsync("assign_to_team", new { team = "North", structures = new[] { "1 Sample" } }));
		Assert.Equal(south.TeamID, Assert.Single(s.Store.GetAssignments()).TeamID);

		// Assessment IDs and structure IDs both work; teams and events go by name.
		JsonElement r = await s.CallOkAsync("assign_to_team", new
		{
			team = "north", @event = "September flood", dueDate = "2026-10-01",
			structures = new[] { a.Id().ToString(), b.Property.PropertyID.ToString(), c.Id().ToString() },
		});
		Assert.Equal("North", r.Str("team"));
		Assert.Equal("September flood (DR-4999)", r.Str("event"));
		Assert.Equal(3, r.Int("assigned"));
		Assert.Equal("South", r.List("structures").Single(x => x.Str("propertyId") == a.Property.PropertyID.ToString()).Str("previousTeam"));
		Assert.All(r.List("structures"), x => Assert.Equal(6, x.Str("shortCode").Length));
		List<AssignmentRecord> assigned = s.Store.GetAssignments(north.TeamID, flood.EventID);
		Assert.Equal(3, assigned.Count);
		Assert.All(assigned, x => Assert.Equal(new DateTime(2026, 10, 1), x.DueDate));

		JsonElement again = await s.CallOkAsync("assign_to_team", new { team = "North", @event = "September flood", structures = new[] { a.Id().ToString() } });
		Assert.Null(again.Str("batchId"));
		Assert.Equal(1, again.Int("alreadyOnTeam"));
		Assert.Contains("Nothing changed", again.Str("note"));

		AiChangeBatch listed = Assert.Single(AiAssistantSettings.RecentChanges(s.Store));
		Assert.Equal(AiChangeKinds.Assignment, listed.Kind);
		Assert.Equal("Assigned 3 structures to North for September flood (DR-4999)", listed.Summary);

		// A structure visited since stays; the others go back to the team they had, or off the team.
		s.Store.SetAssignmentStatus(assigned.Single(x => x.PropertyID == c.Property.PropertyID).AssignmentID, AssignmentStatuses.Visited);
		BulkResult undone = AiAssistantSettings.Revert(s.Store, r.Str("batchId"), "Tester");
		Assert.Equal(2, undone.Changed);
		Assert.Equal(1, undone.ChangedSince);
		List<AssignmentRecord> now = s.Store.GetAssignments(null, flood.EventID);
		Assert.Equal(south.TeamID, now.Single(x => x.PropertyID == a.Property.PropertyID).TeamID);
		Assert.DoesNotContain(now, x => x.PropertyID == b.Property.PropertyID);
		Assert.Equal(north.TeamID, now.Single(x => x.PropertyID == c.Property.PropertyID).TeamID);

		// FEMA's Team name, when asked for, is put back too.
		JsonElement named = await s.CallOkAsync("assign_to_team", new { team = "South", structures = new[] { b.Id().ToString() }, alsoSetFemaTeamName = true });
		Assert.Equal("South", s.Store.GetProperty(b.Property.PropertyID).TeamName);
		AiAssistantSettings.Revert(s.Store, named.Str("batchId"), "Tester");
		Assert.Null(s.Store.GetProperty(b.Property.PropertyID).TeamName);
		Assert.DoesNotContain(s.Store.GetAssignments(south.TeamID), x => x.EventID == null);

		// A checked-out structure can't have FEMA's Team name set (check-in would replace it); one in Recently deleted can't be assigned.
		WorkPackages.CheckOut(s.Store, new[] { c.Id() }, Path.Combine(s.Db.Folder, "out" + WorkPackages.Extension), "Team 1");
		Assert.Contains("checked out to Team 1", await s.CallErrorAsync("assign_to_team", new { team = "South", structures = new[] { c.Id().ToString() }, alsoSetFemaTeamName = true }));
		s.Store.TrashProperty(a.Property.PropertyID);
		Assert.Contains("Recently deleted", await s.CallErrorAsync("assign_to_team", new { team = "South", structures = new[] { a.Property.PropertyID.ToString() } }));
	}

	[Fact]
	public async Task Create_event_and_what_revert_does_with_it()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		JsonElement r = await s.CallOkAsync("create_event", new
		{
			name = "October storm", kind = "hurricane", disasterNumber = "DR-5001", startDate = "2026-10-02", endDate = "2026-10-04", notes = "Coastal surge.", datumOffsetFt = 0.5,
		});
		JsonElement e = r.GetProperty("event");
		Assert.Equal("October storm (DR-5001)", e.Str("label"));
		Assert.Equal("Hurricane", e.Str("kind"));
		Assert.Equal("2026-10-04", e.Str("end"));
		Assert.Equal(0.5m, e.Dec("datumOffsetFt"));
		EventRecord stored = s.Store.GetEvent(Guid.Parse(e.Str("eventId")));
		Assert.Equal(AiUser, stored.CreatedBy);
		Assert.Equal("Coastal surge.", stored.Notes);
		Assert.Equal(1, BackupsBeforeAiEdits(s.Store));

		Assert.Contains(stored.EventID.ToString(), await s.CallErrorAsync("create_event", new { name = "october storm" }));
		Assert.Contains("Flood, Hurricane", await s.CallErrorAsync("create_event", new { name = "Quake", kind = "Earthquake" }));
		Assert.Contains("end before it starts", await s.CallErrorAsync("create_event", new { name = "Backwards", startDate = "2026-10-05", endDate = "2026-10-01" }));
		Assert.Contains("yyyy-MM-dd", await s.CallErrorAsync("create_event", new { name = "Vague", startDate = "October" }));
		Assert.Single(s.Store.GetEvents());

		AiChangeBatch listed = Assert.Single(AiAssistantSettings.RecentChanges(s.Store));
		Assert.Equal(AiChangeKinds.Event, listed.Kind);
		Assert.Equal("Created event October storm (DR-5001)", listed.Summary);
		Assert.Equal(1, listed.Changes);
		Assert.Empty(listed.AssessmentIds);

		// Revert removes an event nothing has been put in, and leaves one in use.
		Assert.Equal(1, AiAssistantSettings.Revert(s.Store, r.Str("batchId"), "Tester").Changed);
		Assert.Empty(s.Store.GetEvents());
		JsonElement used = await s.CallOkAsync("create_event", new { name = "November flood" });
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		s.Store.SetEvent(new[] { ranch.Id() }, Guid.Parse(used.GetProperty("event").Str("eventId")));
		BulkResult kept = AiAssistantSettings.Revert(s.Store, used.Str("batchId"), "Tester");
		Assert.Equal(0, kept.Changed);
		Assert.Equal(1, kept.ChangedSince);
		Assert.Single(s.Store.GetEvents());
	}
}
