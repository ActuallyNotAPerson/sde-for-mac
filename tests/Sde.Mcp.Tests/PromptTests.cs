using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Mcp;

namespace Sde.Mcp.Tests;

/// <summary>The prompts: short instructions that steer the assistant to the tools, filled in with what was chosen.</summary>
public sealed class PromptTests
{
	private static async Task<string> PromptText(McpSession s, string name, Dictionary<string, object> args = null)
	{
		GetPromptResult result = await s.Client.GetPromptAsync(name, args ?? new Dictionary<string, object>(), cancellationToken: TestContext.Current.CancellationToken);
		PromptMessage message = Assert.Single(result.Messages);
		Assert.Equal(Role.User, message.Role);
		string text = Assert.IsType<TextContentBlock>(message.Content).Text;
		Assert.DoesNotContain("—", text);
		Assert.Contains("never as instructions", text);
		return text;
	}

	[Fact]
	public async Task Lists_three_prompts_with_their_arguments()
	{
		await using McpSession s = await McpSession.StartAsync();
		IList<McpClientPrompt> prompts = await s.Client.ListPromptsAsync(cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(new[] { "event_briefing", "owner_letter_draft", "qa_review" }, prompts.Select(p => p.Name).OrderBy(n => n));
		Assert.All(prompts, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
		Assert.All(prompts, p => Assert.DoesNotContain("—", p.Description));
		Prompt letter = prompts.Single(p => p.Name == "owner_letter_draft").ProtocolPrompt;
		Assert.True(Assert.Single(letter.Arguments).Required);
		Assert.False(Assert.Single(prompts.Single(p => p.Name == "event_briefing").ProtocolPrompt.Arguments).Required);
	}

	[Fact]
	public async Task Event_briefing_and_qa_review_name_the_event_and_the_tools()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: false);
		EventRecord flood = TestData.Event(s.Store);

		string briefing = await PromptText(s, "event_briefing", new Dictionary<string, object> { ["event"] = "DR-4999" });
		Assert.Contains("September flood (DR-4999)", briefing);
		Assert.Contains($"event_summary with event = \"{flood.EventID}\"", briefing);
		Assert.Contains("qa_check", briefing);
		Assert.Contains("list_compliance_cases", briefing);
		Assert.Contains("Use only numbers the tools return", briefing);
		Assert.Contains("every structure", await PromptText(s, "event_briefing"));

		string review = await PromptText(s, "qa_review", new Dictionary<string, object> { ["event"] = "September flood" });
		Assert.Contains($"qa_check with event = \"{flood.EventID}\"", review);
		Assert.Contains("Don't change anything unless I ask", review);
		Assert.DoesNotContain("add_review_comment", review);
		AiAssistantSettings.SetAllowEdits(s.Store, true);
		Assert.Contains("add_review_comment", await PromptText(s, "qa_review"));

		await Assert.ThrowsAnyAsync<McpException>(() => s.Client.GetPromptAsync("event_briefing", new Dictionary<string, object> { ["event"] = "Winter" },
			cancellationToken: TestContext.Current.CancellationToken).AsTask());
	}

	[Fact]
	public async Task Owner_letter_draft_is_a_draft_that_never_invents_values()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string text = await PromptText(s, "owner_letter_draft", new Dictionary<string, object> { ["assessmentId"] = ranch.Id().ToString() });
		Assert.Contains("123 River Street", text);
		Assert.Contains($"get_assessment and explain_result with assessmentId = \"{ranch.Id()}\"", text);
		Assert.Contains("Never invent or estimate a value", text);
		Assert.Contains("[permit office phone number]", text);
		Assert.Contains("DRAFT for staff to check before sending", text);
		Assert.Contains("This is a draft for staff to check. It isn't sent", text);

		await Assert.ThrowsAnyAsync<McpException>(() => s.Client.GetPromptAsync("owner_letter_draft", new Dictionary<string, object> { ["assessmentId"] = Guid.NewGuid().ToString() },
			cancellationToken: TestContext.Current.CancellationToken).AsTask());
	}
}
