using System.Text.Json;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Mcp;

namespace Sde.Mcp.Tests;

/// <summary>The edit tools: the switch, the field checks, one batch per call, reverting, locks and the backup.</summary>
public sealed class EditToolTests
{
	private const string AiUser = McpSession.ClientName + " (AI)";

	private static object Changes(string id, params (string Key, string Value)[] changes) =>
		new { assessmentId = id, changes = changes.Select(c => new { key = c.Key, value = c.Value }).ToArray() };

	private static int BackupsBeforeAiEdits(SdeStore store) =>
		new BackupService(store.Database).List().Count(b => b.Reason == "before AI edits");

	[Fact]
	public async Task Edits_are_refused_while_the_switch_is_off()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: false);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();
		int history = s.Store.GetHistory(ranch.Id()).Count;

		string refused = await s.CallErrorAsync("update_fields", Changes(id, ("Assessment.InspectorName", "Someone Else")));
		Assert.Contains("turned off", refused);
		Assert.Contains("Settings › AI assistants", refused);
		Assert.Contains("Settings › AI assistants", await s.CallErrorAsync("set_status", new { assessmentId = id, status = "Reviewed" }));

		Assert.Equal("Inspector A", s.Store.LoadAssessment(ranch.Id()).Assessment.InspectorName);
		Assert.Equal(AssessmentStatus.Draft, s.Store.GetStatus(ranch.Id()).Value);
		Assert.Equal(history, s.Store.GetHistory(ranch.Id()).Count);
		Assert.Equal(0, BackupsBeforeAiEdits(s.Store));
		Assert.Contains("turned off", s.Client.ServerInstructions);
	}

	[Fact]
	public async Task The_switch_is_read_again_on_every_call()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: false);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();
		await s.CallErrorAsync("update_fields", Changes(id, ("Assessment.InspectorName", "B")));

		AiAssistantSettings.SetAllowEdits(s.Store, true);
		Assert.True(AiAssistantSettings.AllowEdits(s.Store));
		await s.CallOkAsync("update_fields", Changes(id, ("Assessment.InspectorName", "B")));
		Assert.Equal("B", s.Store.LoadAssessment(ranch.Id()).Assessment.InspectorName);

		AiAssistantSettings.SetAllowEdits(s.Store, false);
		await s.CallErrorAsync("update_fields", Changes(id, ("Assessment.InspectorName", "C")));
		Assert.Equal("B", s.Store.LoadAssessment(ranch.Id()).Assessment.InspectorName);
	}

	[Fact]
	public async Task Update_fields_is_one_batch_that_revert_puts_back()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();
		AssessmentFile before = s.Store.LoadAssessment(ranch.Id());

		JsonElement r = await s.CallOkAsync("update_fields", Changes(id,
			("Assessment.InspectorName", "Inspector B"),
			("Property.FoundationTypeID", "basement"),
			("Assessment.BaseCost", "150"),
			("Assessment.DateofInspection", "2026-09-20")));
		Assert.Equal(s.Db.Database.FilePath, r.Str("database"));
		string batch = r.Str("batchId");
		Assert.NotNull(batch);
		List<JsonElement> changed = r.List("changed");
		Assert.Contains(changed, c => c.Str("key") == "Property.FoundationTypeID" && c.Str("from") == "Crawlspace" && c.Str("to") == "Basement");
		Assert.Contains(changed, c => c.Str("key") == "Assessment.InspectorName" && c.Str("to") == "Inspector B");
		decimal newPercent = r.GetProperty("result").Dec("percentDamaged").Value;
		Assert.NotEqual(75.0m, newPercent);

		AssessmentFile after = s.Store.LoadAssessment(ranch.Id());
		Assert.Equal("Inspector B", after.Assessment.InspectorName);
		Assert.Equal(47, after.Property.FoundationTypeID);
		Assert.Equal(150m, after.Assessment.BaseCost);
		Assert.Equal(new DateTime(2026, 9, 20), after.Assessment.DateofInspection);
		Assert.Equal(newPercent, after.Assessment.PercentDamage);

		// One call, one batch: every row is the AI assistant's, under the client's name.
		List<EditHistoryRecord> rows = s.Store.GetBatch(batch);
		Assert.Equal(4, rows.Count(h => h.Field != null));
		Assert.All(rows, h => Assert.Equal(SaveVia.AiAssistant, h.Via));
		Assert.All(rows, h => Assert.Equal(AiUser, h.ChangedBy));
		Assert.Single(rows.Select(h => h.BatchId).Distinct());

		// Settings › AI assistants lists it.
		AiChangeBatch listed = Assert.Single(AiAssistantSettings.RecentChanges(s.Store));
		Assert.Equal(batch, listed.BatchId);
		Assert.Equal(AiUser, listed.ChangedBy);
		Assert.Equal(new[] { ranch.Id() }, listed.AssessmentIds);
		Assert.StartsWith("123 River Street: ", listed.Summary);
		Assert.Contains("Inspector", listed.Summary);

		// "Revert AI changes" restores every field.
		BulkResult undone = s.Store.RevertBatch(batch);
		Assert.Equal(0, undone.ChangedSince);
		AssessmentFile reverted = s.Store.LoadAssessment(ranch.Id());
		Assert.Equal(before.Assessment.InspectorName, reverted.Assessment.InspectorName);
		Assert.Equal(before.Property.FoundationTypeID, reverted.Property.FoundationTypeID);
		Assert.Equal(before.Assessment.BaseCost, reverted.Assessment.BaseCost);
		Assert.Equal(before.Assessment.DateofInspection, reverted.Assessment.DateofInspection);
		Assert.Equal(75.0m, reverted.Assessment.PercentDamage);
	}

	[Fact]
	public async Task A_refused_value_changes_nothing_and_says_which_change()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();
		int history = s.Store.GetHistory(ranch.Id()).Count;

		string bad = await s.CallErrorAsync("update_fields", Changes(id, ("Assessment.InspectorName", "Changed"), ("Assessment.DateofInspection", "next Tuesday")));
		Assert.Contains("Change 2 (Assessment.DateofInspection)", bad);
		Assert.Contains("Nothing was changed", bad);
		Assert.Contains("choose one of the choices", await s.CallErrorAsync("update_fields", Changes(id, ("Property.FoundationTypeID", "Floating"))));
		Assert.Contains("There's no field called", await s.CallErrorAsync("update_fields", Changes(id, ("Owner", "x"))));
		Assert.Contains("doesn't apply", await s.CallErrorAsync("update_fields", Changes(id, ("Property.StructureUseID", "1"))));
		Assert.Contains("at least one change", await s.CallErrorAsync("update_fields", new { assessmentId = id, changes = Array.Empty<object>() }));

		Assert.Equal("Inspector A", s.Store.LoadAssessment(ranch.Id()).Assessment.InspectorName);
		Assert.Equal(history, s.Store.GetHistory(ranch.Id()).Count);
		Assert.Equal(0, BackupsBeforeAiEdits(s.Store));
	}

	[Fact]
	public async Task Dependent_fields_reset_as_in_the_editor()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();
		// A county by name follows the state set before it in the same call.
		JsonElement r = await s.CallOkAsync("update_fields", Changes(id, ("Property.StateID", "Indiana"), ("Property.CountyID", "Franklin")));
		PropertyRecord p = s.Store.GetProperty(ranch.Property.PropertyID);
		Assert.Equal("Indiana", s.Store.Reference.StateName(p.StateID));
		Assert.Equal("Franklin", s.Store.Reference.CountyName(p.CountyID));
		Assert.Contains(s.Store.Reference.CountiesIn(p.StateID), c => c.CountyID == p.CountyID);
		Assert.Equal(2, r.List("changed").Count);
	}

	[Fact]
	public async Task Final_and_letter_sent_assessments_are_refused()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();
		s.Store.SetStatus(ranch.Id(), AssessmentStatus.Final);

		string refused = await s.CallErrorAsync("update_fields", Changes(id, ("Assessment.InspectorName", "Someone")));
		Assert.Contains("Final", refused);
		Assert.Contains("Only a person can reopen it", refused);
		Assert.Contains("Only a person can reopen it", await s.CallErrorAsync("set_status", new { assessmentId = id, status = "Draft" }));
		Assert.Equal(AssessmentStatus.Final, s.Store.GetStatus(ranch.Id()).Value);
		Assert.Equal("Inspector A", s.Store.LoadAssessment(ranch.Id()).Assessment.InspectorName);

		s.Store.SetStatus(ranch.Id(), AssessmentStatus.LetterSent);
		Assert.Contains("Letter sent", await s.CallErrorAsync("update_fields", Changes(id, ("Assessment.InspectorName", "Someone"))));
		Assert.DoesNotContain(s.Store.GetHistory(ranch.Id()), h => h.Via == SaveVia.AiAssistant);
	}

	[Fact]
	public async Task Set_status_allows_draft_reviewed_and_appeal_only()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();

		Assert.Contains("Only a person can make an assessment Final", await s.CallErrorAsync("set_status", new { assessmentId = id, status = "Final" }));
		Assert.Contains("Only a person", await s.CallErrorAsync("set_status", new { assessmentId = id, status = "Letter sent" }));
		Assert.Contains("Draft, Reviewed or Appeal", await s.CallErrorAsync("set_status", new { assessmentId = id, status = "Done" }));
		Assert.Equal(AssessmentStatus.Draft, s.Store.GetStatus(ranch.Id()).Value);

		JsonElement r = await s.CallOkAsync("set_status", new { assessmentId = id, status = "reviewed" });
		Assert.Equal("Draft", r.Str("from"));
		Assert.Equal("Reviewed", r.Str("to"));
		Assert.Equal(AssessmentStatus.Reviewed, s.Store.GetStatus(ranch.Id()).Value);
		EditHistoryRecord row = Assert.Single(s.Store.GetBatch(r.Str("batchId")));
		Assert.Equal(SaveVia.AiAssistant, row.Via);
		Assert.Equal(AiUser, row.ChangedBy);

		JsonElement again = await s.CallOkAsync("set_status", new { assessmentId = id, status = "Reviewed" });
		Assert.Null(again.Str("batchId"));
		Assert.Contains("already Reviewed", again.Str("note"));

		// Reverting the AI's change puts the status back.
		BulkResult undone = AiAssistantSettings.Revert(s.Store, r.Str("batchId"), "Tester");
		Assert.Equal(1, undone.Changed);
		Assert.Equal(AssessmentStatus.Draft, s.Store.GetStatus(ranch.Id()).Value);
		Assert.Contains("Status: Reviewed", AiAssistantSettings.RecentChanges(s.Store).Single().Summary);
	}

	[Fact]
	public async Task Revert_leaves_values_someone_changed_since()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: true);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();
		JsonElement r = await s.CallOkAsync("update_fields", Changes(id, ("Assessment.InspectorName", "AI value"), ("Assessment.InspectorPhoneNumber", "555-0100")));
		s.Store.Apply(ranch.Id(), f => f.Assessment.InspectorName = "Person's value");

		BulkResult undone = AiAssistantSettings.Revert(s.Store, r.Str("batchId"), "Tester");
		Assert.Equal(1, undone.ChangedSince);
		AssessmentFile now = s.Store.LoadAssessment(ranch.Id());
		Assert.Equal("Person's value", now.Assessment.InspectorName);
		Assert.Null(now.Assessment.InspectorPhoneNumber);
	}

	[Fact]
	public async Task One_backup_is_taken_per_session_before_the_first_edit()
	{
		using TempDatabase db = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(db.Store);
		string id = ranch.Id().ToString();

		McpSession first = await McpSession.StartAsync(db, allowEdits: true);
		await first.CallOkAsync("find_structures");
		Assert.Equal(0, BackupsBeforeAiEdits(db.Store));
		await first.CallOkAsync("update_fields", Changes(id, ("Assessment.InspectorName", "One")));
		Assert.Equal(1, BackupsBeforeAiEdits(db.Store));
		Assert.NotNull(first.Context.Backup);
		await first.CallOkAsync("update_fields", Changes(id, ("Assessment.InspectorName", "Two")));
		await first.CallOkAsync("set_status", new { assessmentId = id, status = "Appeal" });
		Assert.Equal(1, BackupsBeforeAiEdits(db.Store));

		// The backup holds the database as it was before the first edit.
		string copy = Path.Combine(first.Context.Backup.Folder, "sde.sqlite");
		Assert.Equal("Inspector A", new SdeStore(SdeDatabase.Open(copy)).LoadAssessment(ranch.Id()).Assessment.InspectorName);
		await first.DisposeAsync();

		McpSession second = await McpSession.StartAsync(db, allowEdits: true);
		await second.CallOkAsync("set_status", new { assessmentId = id, status = "Reviewed" });
		Assert.Equal(2, BackupsBeforeAiEdits(db.Store));
		await second.DisposeAsync();
	}
}
