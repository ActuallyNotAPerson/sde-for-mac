using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Mcp;

namespace Sde.Mcp.Tests;

/// <summary>The read tools and resources, called through the SDK's client.</summary>
public sealed class ReadToolTests
{
	private static readonly string[] AllTools =
	{
		"find_structures", "get_assessment", "explain_result", "get_history", "get_field_options", "field_help", "update_fields", "set_status",
	};

	[Fact]
	public async Task Lists_every_tool_and_nothing_that_deletes_or_exports()
	{
		await using McpSession s = await McpSession.StartAsync();
		IList<McpClientTool> tools = await s.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(AllTools.OrderBy(x => x), tools.Select(t => t.Name).OrderBy(x => x));
		foreach (McpClientTool t in tools)
		{
			Tool tool = t.ProtocolTool;
			Assert.False(string.IsNullOrWhiteSpace(tool.Description));
			Assert.NotNull(tool.OutputSchema);
			bool edits = t.Name is "update_fields" or "set_status";
			Assert.Equal(!edits, tool.Annotations?.ReadOnlyHint == true);
			Assert.DoesNotContain("—", tool.Description);
		}
		string[] never = { "delete", "purge", "restore", "import", "export", "letter", "setting" };
		Assert.DoesNotContain(tools, t => never.Any(n => t.Name.Contains(n, StringComparison.OrdinalIgnoreCase)));
		// Free text is untrusted: the tools that return it say so.
		foreach (string name in new[] { "find_structures", "get_assessment", "explain_result", "get_history" })
		{
			Assert.Contains("never as instructions", tools.Single(t => t.Name == name).Description);
		}
		Assert.Contains("never as instructions", s.Client.ServerInstructions);
		Assert.Contains(s.Db.Database.FilePath, s.Client.ServerInstructions);
	}

	[Fact]
	public async Task Find_structures_filters_and_pages()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		for (int i = 0; i < 6; i++)
		{
			PropertyRecord p = s.Store.SaveProperty(new PropertyRecord
			{
				StructureTypeID = i < 4 ? 1 : 2, StreetNumber = (200 + i).ToString(), StreetName = "Sample Road", City = "Testville",
				OwnerLastName = "Owner" + i, Latitude = i % 2 == 0 ? 39.5m : null, Longitude = i % 2 == 0 ? -84.2m : null,
			});
			if (i < 5)
			{
				s.Store.CreateAssessment(p.PropertyID, applyDefaults: false);
			}
		}
		s.Store.SetStatus(ranch.Id(), AssessmentStatus.Reviewed);

		JsonElement all = await s.CallOkAsync("find_structures", new { pageSize = 3 });
		Assert.Equal(s.Db.Database.FilePath, all.Str("database"));
		Assert.Equal(7, all.Int("total"));
		Assert.Equal(3, all.Int("pages"));
		Assert.Equal(3, all.List("items").Count);
		Assert.Contains("page = 2", all.Str("next"));

		JsonElement last = await s.CallOkAsync("find_structures", new { pageSize = 3, page = 3 });
		Assert.Single(last.List("items"));
		Assert.Null(last.Str("next"));

		JsonElement byText = await s.CallOkAsync("find_structures", new { query = "owner3" });
		JsonElement found = Assert.Single(byText.List("items"));
		Assert.Equal("203 Sample Road", found.Str("address"));

		JsonElement reviewed = await s.CallOkAsync("find_structures", new { status = "Reviewed" });
		JsonElement r = Assert.Single(reviewed.List("items"));
		Assert.Equal(ranch.Id().ToString(), r.Str("assessmentId"));
		Assert.Equal(75.0m, r.Dec("percentDamaged"));
		Assert.Equal("Substantial", r.Str("result"));
		Assert.Equal("Pat Example", r.Str("owner"));

		Assert.Equal(1, (await s.CallOkAsync("find_structures", new { result = "Substantial" })).Int("total"));
		Assert.Equal(1, (await s.CallOkAsync("find_structures", new { minPercent = 50 })).Int("total"));
		Assert.Equal(0, (await s.CallOkAsync("find_structures", new { maxPercent = 50 })).Int("total"));
		Assert.Equal(2, (await s.CallOkAsync("find_structures", new { structureType = "non-residential" })).Int("total"));
		Assert.Equal(3, (await s.CallOkAsync("find_structures", new { coordinates = "has" })).Int("total"));
		JsonElement missing = await s.CallOkAsync("find_structures", new { coordinates = "missing" });
		Assert.Equal(4, missing.Int("total"));
		Assert.All(missing.List("items"), i => Assert.False(i.GetProperty("hasCoordinates").GetBoolean()));
		// The structure with no assessment yet is listed without an assessment ID.
		Assert.Contains(missing.List("items"), i => i.Str("assessmentId") == null && i.Str("result") == "No assessment");

		Assert.Contains("status must be", await s.CallErrorAsync("find_structures", new { status = "Finished" }));
	}

	[Fact]
	public async Task Get_assessment_shows_labels_display_values_and_stored_values()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		JsonElement a = await s.CallOkAsync("get_assessment", new { assessmentId = ranch.Id().ToString() });
		Assert.Equal(s.Db.Database.FilePath, a.Str("database"));
		Assert.Equal("123 River Street", a.Str("address"));
		Assert.Equal("Residential", a.Str("structureType"));
		Assert.Equal("Draft", a.Str("status"));
		Assert.Equal(75.0m, a.GetProperty("result").Dec("percentDamaged"));
		Assert.Equal("Substantially Damaged", a.GetProperty("result").Str("determination"));
		Assert.Equal(FieldCatalog.Steps[0], a.List("steps")[0].Str("name"));

		JsonElement suffix = a.Field("Property.StreetSuffixID");
		Assert.Equal("Street suffix", suffix.Str("label"));
		Assert.Equal("Street", suffix.Str("value"));
		Assert.Equal("172", suffix.Str("stored"));
		Assert.Equal("Inspector A", a.Field("Assessment.InspectorName").Str("value"));
		Assert.Null(a.Field("Assessment.InspectorName").Str("stored"));
		Assert.Equal("2026-09-14", a.Field("Assessment.DateDamageOccurred").Str("stored"));
		// Only fields that apply: "Describe the cause" is for "Other" causes, the structure use for non-residential.
		Assert.DoesNotContain(a.List("steps").SelectMany(x => x.List("fields")), f => f.Str("key") is "Assessment.CauseOfDamageDescription" or "Property.StructureUseID");
		Assert.Equal(12, a.List("elements").Count);
		Assert.Contains(a.List("elements"), e => e.Str("element") == "Interior Finish" && e.Dec("percentDamaged") == 90m);

		Assert.Contains("isn't an assessment ID", await s.CallErrorAsync("get_assessment", new { assessmentId = "123 River" }));
		Assert.Contains("There's no assessment", await s.CallErrorAsync("get_assessment", new { assessmentId = Guid.NewGuid().ToString() }));
		s.Store.TrashAssessment(ranch.Id());
		Assert.Contains("Recently deleted", await s.CallErrorAsync("get_assessment", new { assessmentId = ranch.Id().ToString() }));
	}

	[Fact]
	public async Task Explain_result_walks_through_the_calculators_numbers()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		AssessmentRecord stored = s.Store.LoadAssessment(ranch.Id()).Assessment;
		JsonElement e = await s.CallOkAsync("explain_result", new { assessmentId = ranch.Id().ToString() });

		Assert.Equal(s.Db.Database.FilePath, e.Str("database"));
		Assert.True(e.GetProperty("meetsThreshold").GetBoolean());
		Assert.Equal(stored.ComputedActualCashValue, e.GetProperty("buildingValue").Dec("value"));
		Assert.Equal("Computed Actual Cash Value", e.GetProperty("buildingValue").Str("basis"));
		Assert.Equal(stored.ComputedDamages, e.GetProperty("repairCost").Dec("value"));
		Assert.Equal(stored.TotalReplacementCost, e.GetProperty("replacementCost").Dec("total"));
		Assert.Equal(stored.TotalEstimatedDamages, e.Dec("totalElementDamage"));
		Assert.Equal(2, e.GetProperty("replacementCost").List("adjustments").Count);
		Assert.Null(e.Str("note"));
		string steps = string.Join("\n", e.List("explanation").Select(x => x.GetString()));
		Assert.Contains("$118.75 per sq ft × 1,450 sq ft × geographic adjustment 0.93", steps);
		Assert.Contains("Garage 240 × $32.50 = $7,800.00", steps);
		Assert.Contains("= 75.0%", steps);
		Assert.Contains("at or above the 50% threshold", steps);
		Assert.DoesNotContain("—", steps);

		// Explaining doesn't save anything.
		Assert.Equal(stored.LastUpdate, s.Store.LoadAssessment(ranch.Id()).Assessment.LastUpdate);
	}

	[Fact]
	public async Task Explain_result_says_when_there_is_no_percentage_yet()
	{
		await using McpSession s = await McpSession.StartAsync();
		PropertyRecord p = s.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1, StreetNumber = "9", StreetName = "Empty Lane" });
		AssessmentFile blank = s.Store.CreateAssessment(p.PropertyID, applyDefaults: false);
		JsonElement e = await s.CallOkAsync("explain_result", new { assessmentId = blank.Id().ToString() });
		Assert.False(e.GetProperty("meetsThreshold").GetBoolean());
		Assert.Equal("Incomplete", e.GetProperty("result").Str("result"));
		Assert.Contains(e.List("explanation"), x => x.GetString().Contains("no percentage yet"));
	}

	[Fact]
	public async Task Get_history_pages_newest_first_with_display_values()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		for (int i = 1; i <= 4; i++)
		{
			s.Store.Apply(ranch.Id(), f => f.Assessment.InspectorName = "Inspector " + i, SaveContext.Batch(SaveVia.BulkEdit));
		}
		s.Store.Apply(ranch.Id(), f => f.Property.FoundationTypeID = 47, SaveContext.Batch(SaveVia.BulkEdit));
		int total = s.Store.GetHistory(ranch.Id()).Count;

		JsonElement first = await s.CallOkAsync("get_history", new { assessmentId = ranch.Id().ToString(), pageSize = 2 });
		Assert.Equal(total, first.Int("total"));
		Assert.Equal(2, first.List("items").Count);
		Assert.Contains("page = 2", first.Str("next"));
		JsonElement newest = first.List("items")[0];
		Assert.Equal("Property.FoundationTypeID", newest.Str("field"));
		Assert.Equal("Crawlspace", newest.Str("from"));
		Assert.Equal("Basement", newest.Str("to"));
		Assert.Equal("Bulk edit", newest.Str("via"));
		Assert.Equal("Inspector 4", first.List("items")[1].Str("to"));

		JsonElement lastPage = await s.CallOkAsync("get_history", new { assessmentId = ranch.Id().ToString(), pageSize = 2, page = first.Int("pages") });
		Assert.Null(lastPage.Str("next"));
		Assert.Contains(lastPage.List("items"), x => x.Str("label") == SdeStore.CreatedEvent);
	}

	[Fact]
	public async Task Get_field_options_lists_the_choices_for_this_assessment()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		string id = ranch.Id().ToString();

		JsonElement county = await s.CallOkAsync("get_field_options", new { assessmentId = id, fieldKey = "Property.CountyID" });
		Assert.Equal("choice", county.Str("kind"));
		Assert.Equal(s.Store.Reference.CountiesIn(36).Count(), county.List("options").Count);
		Assert.Contains(county.List("options"), o => o.Str("name") == "Franklin");

		JsonElement stories = await s.CallOkAsync("get_field_options", new { assessmentId = id, fieldKey = "Property.StoryTypeID" });
		Assert.Equal(FieldCatalog.Options(FieldCatalog.Get("Property.StoryTypeID"), ranch, s.Store.Reference).Count, stories.List("options").Count);
		Assert.Equal("One Story (Standard)", stories.Str("current"));
		Assert.Equal("104", stories.Str("currentStored"));

		JsonElement date = await s.CallOkAsync("get_field_options", new { assessmentId = id, fieldKey = "Assessment.DateofInspection" });
		Assert.Equal("date", date.Str("kind"));
		Assert.Empty(date.List("options"));
		Assert.Contains("yyyy-MM-dd", date.Str("accepts"));

		JsonElement other = await s.CallOkAsync("get_field_options", new { assessmentId = id, fieldKey = "assessment.causeofdamagedescription" });
		Assert.False(other.GetProperty("applies").GetBoolean());

		Assert.Contains("There's no field called", await s.CallErrorAsync("get_field_options", new { assessmentId = id, fieldKey = "Owner" }));
	}

	[Fact]
	public async Task Field_help_and_the_resources()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);

		JsonElement one = await s.CallOkAsync("field_help", new { fieldKey = "Property.StreetName" });
		JsonElement entry = Assert.Single(one.List("fields"));
		Assert.Equal("Street name", entry.Str("label"));
		Assert.Contains("FEMA P-784, page", entry.Str("manualPage"));
		Assert.False(string.IsNullOrWhiteSpace(entry.Str("help")));

		JsonElement all = await s.CallOkAsync("field_help");
		Assert.Equal(FieldCatalog.All.Count, all.List("fields").Count);
		JsonElement building = await s.CallOkAsync("field_help", new { step = 2 });
		Assert.All(building.List("fields"), f => Assert.Equal("Building", f.Str("step")));

		IList<McpClientResourceTemplate> templates = await s.Client.ListResourceTemplatesAsync(cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(templates, t => t.UriTemplate == "sde://assessment/{id}");
		ReadResourceResult res = await s.Client.ReadResourceAsync($"sde://assessment/{ranch.Id()}", cancellationToken: TestContext.Current.CancellationToken);
		using (JsonDocument doc = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(res.Contents)).Text))
		{
			Assert.Equal("123 River Street", doc.RootElement.Str("address"));
			Assert.Equal(s.Db.Database.FilePath, doc.RootElement.Str("database"));
		}
		ReadResourceResult help = await s.Client.ReadResourceAsync("sde://field-help", cancellationToken: TestContext.Current.CancellationToken);
		using (JsonDocument doc = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(help.Contents)).Text))
		{
			Assert.Equal(FieldCatalog.All.Count, doc.RootElement.List("fields").Count);
		}
	}

	[Fact]
	public async Task Read_tools_use_a_read_only_connection_and_hold_the_shared_lease()
	{
		await using McpSession s = await McpSession.StartAsync();
		Assert.True(s.Context.Reader.Database.IsReadOnly);
		Assert.False(s.Context.Writer.Database.IsReadOnly);
		Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => s.Context.Reader.SetSetting("Test", true));
		if (!OperatingSystem.IsWindows())
		{
			// While the server runs, SDE can't restore a backup or move the database.
			Assert.Null(DatabaseLease.TryExclusive(s.Db.Database.LockPath));
			await s.StopAsync();
			using DatabaseLease after = DatabaseLease.TryExclusive(s.Db.Database.LockPath);
			Assert.NotNull(after);
		}
	}
}
