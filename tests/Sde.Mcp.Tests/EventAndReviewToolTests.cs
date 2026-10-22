using System.Text.Json;
using Sde.Core.Assessments;
using Sde.Core.Quality;
using Sde.Data;

namespace Sde.Mcp.Tests;

/// <summary>The read tools for events, quality checks, review comments and compliance cases.</summary>
public sealed class EventAndReviewToolTests
{
	[Fact]
	public async Task Events_are_listed_and_summarised_as_the_dashboard_counts_them()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = TestData.PlacedRanch(s.Store);
		AssessmentFile a = TestData.Structure(s.Store, "201", latitude: 39.71m, longitude: -84.21m);
		AssessmentFile b = TestData.Structure(s.Store, "203", latitude: 39.72m, longitude: -84.22m);
		AssessmentFile outside = TestData.Structure(s.Store, "9", "Other");
		s.Store.SaveProperty(new Core.Records.PropertyRecord { StructureTypeID = 1, StreetNumber = "1", StreetName = "Unvisited" });
		EventRecord flood = TestData.Event(s.Store);
		EventRecord empty = TestData.Event(s.Store, "Spring storm", null);
		s.Store.SetEvent(new[] { ranch.Id(), a.Id(), b.Id() }, flood.EventID);
		s.Store.SetStatus(ranch.Id(), AssessmentStatus.Reviewed);
		TeamRecord team = TestData.Team(s.Store, "Team North");
		s.Store.AssignToTeam(new[] { a.Property.PropertyID, b.Property.PropertyID }, team.TeamID, flood.EventID);

		JsonElement events = await s.CallOkAsync("list_events");
		Assert.Equal(s.Db.Database.FilePath, events.Str("database"));
		Assert.Equal(2, events.Int("total"));
		JsonElement listed = events.List("events").Single(e => e.Str("eventId") == flood.EventID.ToString());
		Assert.Equal("September flood (DR-4999)", listed.Str("label"));
		Assert.Equal("2026-09-14", listed.Str("start"));
		Assert.Equal(3, listed.Int("assessments"));
		Assert.Equal(0, events.List("events").Single(e => e.Str("name") == "Spring storm").Int("assessments"));

		// By name, label, disaster number or ID.
		foreach (string name in new[] { "september flood", "September flood (DR-4999)", "DR-4999", flood.EventID.ToString() })
		{
			JsonElement summary = await s.CallOkAsync("event_summary", new { @event = name });
			Assert.Equal(3, summary.Int("assessments"));
		}
		JsonElement inEvent = await s.CallOkAsync("event_summary", new { @event = "September flood" });
		Assert.Equal(flood.EventID.ToString(), inEvent.GetProperty("event").Str("eventId"));
		Assert.Equal(1, inEvent.GetProperty("byResult").Int("substantial"));
		Assert.Equal(2, inEvent.GetProperty("byResult").Int("incomplete"));
		Assert.False(inEvent.TryGetProperty("notYetAssessed", out _));
		Assert.Contains(inEvent.List("byStatus"), x => x.Str("status") == "Reviewed" && x.Int("assessments") == 1);
		Assert.Contains(inEvent.List("byStatus"), x => x.Str("status") == "Draft" && x.Int("assessments") == 2);
		Assert.Equal("Sample", Assert.Single(inEvent.List("byStreet"), g => g.Int("assessments") == 2).Str("label"));
		JsonElement progress = Assert.Single(inEvent.List("teams"));
		Assert.Equal("Team North", progress.Str("name"));
		Assert.Equal(2, progress.Int("assigned"));
		Assert.Equal("2026-09-14", inEvent.Str("damageFrom"));

		JsonElement whole = await s.CallOkAsync("event_summary");
		Assert.False(whole.TryGetProperty("event", out _));
		Assert.Equal(4, whole.Int("assessments"));
		Assert.Equal(1, whole.Int("notYetAssessed"));
		Assert.Equal(5, whole.Int("totalStructures"));

		JsonElement none = await s.CallOkAsync("event_summary", new { @event = "Spring storm" });
		Assert.Equal(0, none.Int("assessments"));
		Assert.Contains("No assessments", none.Str("note"));

		// find_structures takes the event too.
		Assert.Equal(3, (await s.CallOkAsync("find_structures", new { @event = "September flood" })).Int("total"));
		Assert.Equal(0, (await s.CallOkAsync("find_structures", new { @event = "Spring storm" })).Int("total"));
		Assert.Contains("list_events", await s.CallErrorAsync("event_summary", new { @event = "Winter flood" }));
		Assert.Contains("list_events", await s.CallErrorAsync("find_structures", new { @event = Guid.NewGuid().ToString() }));

		// Two events of the same name are told apart by ID.
		TestData.Event(s.Store, "Spring storm", "DR-5000");
		Assert.Contains("Give the event's ID", await s.CallErrorAsync("event_summary", new { @event = "spring storm" }));
	}

	[Fact]
	public async Task Qa_check_groups_findings_by_check_and_pages()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		AssessmentFile twinA = TestData.Structure(s.Store, "10", latitude: 39.70m, longitude: -84.20m);
		AssessmentFile twinB = TestData.Structure(s.Store, "10", latitude: 39.75m, longitude: -84.25m);
		EventRecord flood = TestData.Event(s.Store);
		s.Store.SetEvent(new[] { twinA.Id(), twinB.Id() }, flood.EventID);

		JsonElement all = await s.CallOkAsync("qa_check", new { pageSize = 100 });
		Assert.Equal(s.Db.Database.FilePath, all.Str("database"));
		Assert.Equal(3, all.Int("checked"));
		Assert.Equal("the whole database", all.Str("scope"));
		JsonElement duplicates = all.List("rules").Single(r => r.Str("ruleId") == QaRuleIds.DuplicateAddress);
		Assert.Equal("Same address", duplicates.Str("title"));
		Assert.Equal("Warning", duplicates.Str("severity"));
		Assert.Equal(2, duplicates.Int("open"));
		JsonElement group = all.List("groups").Single(g => g.Str("ruleId") == QaRuleIds.DuplicateAddress);
		Assert.Equal(2, group.List("findings").Count);
		Assert.All(group.List("findings"), f => Assert.Equal("10 Sample", f.Str("about")));
		Assert.Contains(all.List("groups"), g => g.Str("ruleId") == QaRuleIds.MissingCoordinates
			&& g.List("findings").Any(f => f.Str("assessmentId") == ranch.Id().ToString() && f.Str("about") == "123 River Street" && f.Str("fieldKey") != null));
		// Errors first, then warnings, then information.
		List<int> order = all.List("groups").Select(g => g.Str("severity") switch { "Error" => 0, "Warning" => 1, _ => 2 }).ToList();
		Assert.Equal(order.OrderBy(x => x), order);
		Assert.Contains("flood map zone", all.Str("note"));

		// A finding set aside in SDE is left out unless asked for, with its reason when shown.
		QaFinding photos = QaRules.Check(s.Store.LoadQaInput()).First(f => f.RuleId == QaRuleIds.MissingPhotos && f.AssessmentId == ranch.Id());
		s.Store.DismissQaFinding(photos, "Photos are on the inspector's camera");
		JsonElement open = await s.CallOkAsync("qa_check", new { rule = "No photos" });
		Assert.Equal(1, open.Int("dismissedCount"));
		Assert.DoesNotContain(open.List("groups").SelectMany(g => g.List("findings")), f => f.Str("assessmentId") == ranch.Id().ToString());
		Assert.Contains("set aside", open.Str("note"));
		Assert.All(open.List("groups"), g => Assert.Equal(QaRuleIds.MissingPhotos, g.Str("ruleId")));
		JsonElement shown = await s.CallOkAsync("qa_check", new { rule = "MissingPhotos", includeDismissed = true });
		JsonElement set = shown.List("groups").SelectMany(g => g.List("findings")).Single(f => f.Str("assessmentId") == ranch.Id().ToString());
		Assert.True(set.GetProperty("dismissed").GetBoolean());
		Assert.Equal("Photos are on the inspector's camera", set.Str("dismissedReason"));

		// Paging, one event, chosen assessments.
		JsonElement first = await s.CallOkAsync("qa_check", new { pageSize = 2 });
		Assert.Equal(2, first.List("groups").Sum(g => g.List("findings").Count));
		Assert.Contains("page = 2", first.Str("next"));
		Assert.Equal(all.Int("total") - 1, first.Int("total"));
		JsonElement inEvent = await s.CallOkAsync("qa_check", new { @event = "September flood" });
		Assert.Equal(2, inEvent.Int("checked"));
		Assert.DoesNotContain(inEvent.List("groups").SelectMany(g => g.List("findings")), f => f.Str("assessmentId") == ranch.Id().ToString());
		JsonElement chosen = await s.CallOkAsync("qa_check", new { assessmentIds = new[] { ranch.Id().ToString() } });
		Assert.Equal(1, chosen.Int("checked"));
		Assert.DoesNotContain(chosen.List("rules"), r => r.Str("ruleId") == QaRuleIds.DuplicateAddress);

		Assert.Contains("MissingPhotos (No photos)", await s.CallErrorAsync("qa_check", new { rule = "Spelling" }));
		Assert.Contains("isn't an assessment ID", await s.CallErrorAsync("qa_check", new { assessmentIds = new[] { "10 Sample" } }));
	}

	[Fact]
	public async Task Review_comments_are_listed_in_threads()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		ReviewCommentRecord first = s.Store.AddReviewComment(ranch.Id(), "Is the inspection date right?", "Assessment.DateofInspection");
		s.Store.AddReviewComment(ranch.Id(), "Yes, checked with the inspector.", parentId: first.CommentID);
		ReviewCommentRecord done = s.Store.AddReviewComment(ranch.Id(), "Photos added.");
		s.Store.ResolveReviewComment(done.CommentID);

		JsonElement list = await s.CallOkAsync("get_review_comments", new { assessmentId = ranch.Id().ToString() });
		Assert.Equal("123 River Street", list.Str("address"));
		Assert.Equal(1, list.Int("openThreads"));
		Assert.Equal(2, list.List("threads").Count);
		JsonElement thread = list.List("threads")[0];
		Assert.Equal(first.CommentID.ToString(), thread.Str("commentId"));
		Assert.Equal("Assessment.DateofInspection", thread.Str("fieldKey"));
		Assert.Equal("Inspection date", thread.Str("fieldLabel"));
		Assert.Equal("Yes, checked with the inspector.", Assert.Single(thread.List("replies")).Str("body"));
		Assert.True(list.List("threads")[1].GetProperty("resolved").GetBoolean());
	}

	[Fact]
	public async Task Compliance_cases_filter_as_the_compliance_page_does()
	{
		await using McpSession s = await McpSession.StartAsync();
		DateTime today = new DateTime(2026, 10, 20);
		s.Context.Reader.Clock = () => today;
		AssessmentFile late = SampleCases.RanchFlood(s.Store);
		AssessmentFile closed = SampleCases.RanchFlood(s.Store);
		EventRecord flood = TestData.Event(s.Store);
		s.Store.SetEvent(new[] { late.Id() }, flood.EventID);
		ComplianceCaseRecord a = s.Store.OpenComplianceCase(late.Id());
		a.Path = CompliancePaths.Elevate;
		a.Deadline = today.AddDays(-5);
		a.IccStatus = IccStatuses.Filed;
		a.PermitNumber = "BP-0001";
		s.Store.SaveComplianceCase(a);
		ComplianceCaseRecord b = s.Store.OpenComplianceCase(closed.Id());
		b.Path = CompliancePaths.Demolish;
		b.Status = ComplianceStatuses.Closed;
		b.ClosedReason = "Demolished";
		s.Store.SaveComplianceCase(b);

		JsonElement all = await s.CallOkAsync("list_compliance_cases");
		Assert.Equal(2, all.Int("total"));
		Assert.Equal(1, all.Int("open"));
		Assert.Equal(1, all.Int("overdue"));
		Assert.Equal(1, all.Int("closed"));
		JsonElement first = all.List("items")[0];
		Assert.Equal(a.CaseID.ToString(), first.Str("caseId"));
		Assert.Equal("SD", first.Str("kind"));
		Assert.Equal("123 River Street", first.Str("address"));
		Assert.Equal("Pat Example", first.Str("owner"));
		Assert.Equal("Elevate", first.Str("path"));
		Assert.Equal("BP-0001", first.Str("permitNumber"));
		Assert.True(first.GetProperty("overdue").GetBoolean());
		Assert.Equal(5, first.Int("daysOverdue"));
		Assert.Equal("Filed", first.Str("iccStatus"));
		Assert.Equal("Closed", all.List("items")[1].Str("status"));

		Assert.Equal(1, (await s.CallOkAsync("list_compliance_cases", new { status = "open" })).Int("total"));
		Assert.Equal(1, (await s.CallOkAsync("list_compliance_cases", new { overdue = true })).Int("total"));
		Assert.Equal(b.CaseID.ToString(), (await s.CallOkAsync("list_compliance_cases", new { path = "demolish" })).List("items").Single().Str("caseId"));
		Assert.Equal(0, (await s.CallOkAsync("list_compliance_cases", new { path = "none" })).Int("total"));
		Assert.Equal(1, (await s.CallOkAsync("list_compliance_cases", new { iccStatus = "filed" })).Int("total"));
		Assert.Equal(1, (await s.CallOkAsync("list_compliance_cases", new { iccStatus = "Not filed" })).Int("total"));
		Assert.Equal(a.CaseID.ToString(), (await s.CallOkAsync("list_compliance_cases", new { @event = "DR-4999" })).List("items").Single().Str("caseId"));
		JsonElement paged = await s.CallOkAsync("list_compliance_cases", new { pageSize = 1 });
		Assert.Single(paged.List("items"));
		Assert.Contains("page = 2", paged.Str("next"));
		Assert.Contains("open", await s.CallErrorAsync("list_compliance_cases", new { status = "pending" }));
		Assert.Contains("Elevate", await s.CallErrorAsync("list_compliance_cases", new { path = "Raise" }));
	}
}
