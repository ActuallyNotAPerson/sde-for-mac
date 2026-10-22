using System.Text;
using System.Text.Json;
using SkiaSharp;
using Sde.Core.Assessments;
using Sde.Core.Photos;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Mcp.Tests;

/// <summary>make_report, export_geojson and make_web_map: always allowed, and only ever writing into "AI outputs".</summary>
public sealed class OutputToolTests
{
	private static string Outputs(McpSession s) => Path.Combine(s.Db.Folder, "AI outputs");

	/// <summary>A plain photo (generated, no real content) attached to the assessment, with an arrow drawn on it.</summary>
	private static void AddMarkedUpPhoto(SdeStore store, AssessmentFile file)
	{
		Directory.CreateDirectory(store.AttachmentsFolder);
		using (SKBitmap bitmap = new SKBitmap(64, 48))
		{
			bitmap.Erase(SKColors.LightGray);
			using SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
			File.WriteAllBytes(Path.Combine(store.AttachmentsFolder, "front.jpg"), data.ToArray());
		}
		AssessmentFile f = store.LoadAssessment(file.Id());
		f.Attachments.Add(new AttachmentRecord { FileName = "front.jpg", Active = true, DefaultImage = true, AttachmentType = "Photo" });
		store.SaveAssessment(f);
		store.SavePhotoMarkup("front.jpg", new PhotoMarkup { Shapes = { new MarkupShape { Kind = MarkupKinds.Arrow, X1 = 0.1, Y1 = 0.1, X2 = 0.5, Y2 = 0.5 } } }.ToJson());
	}

	[Fact]
	public async Task Make_report_writes_a_pdf_into_ai_outputs_even_with_edits_off()
	{
		await using McpSession s = await McpSession.StartAsync(allowEdits: false);
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		AddMarkedUpPhoto(s.Store, ranch);
		AssessmentFile other = TestData.Structure(s.Store, "7");
		DateTime? lastUpdate = s.Store.LoadAssessment(ranch.Id()).Assessment.LastUpdate;
		int history = s.Store.GetHistory(ranch.Id()).Count;

		JsonElement r = await s.CallOkAsync("make_report", new { assessmentIds = new[] { ranch.Id().ToString() } });
		Assert.Equal(s.Db.Database.FilePath, r.Str("database"));
		string path = r.Str("path");
		Assert.Equal(Path.Combine(Outputs(s), "Summary report for 123 River Street.pdf"), path);
		Assert.Equal("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
		Assert.Equal(1, r.Int("assessments"));
		Assert.True(r.GetProperty("photos").GetBoolean());

		// A second report never replaces the first.
		JsonElement again = await s.CallOkAsync("make_report", new { assessmentIds = new[] { ranch.Id().ToString() } });
		Assert.Equal(Path.Combine(Outputs(s), "Summary report for 123 River Street 2.pdf"), again.Str("path"));

		// A detailed report of two, one ID unknown; a file name can't lead out of the folder.
		string unknown = Guid.NewGuid().ToString();
		JsonElement detailed = await s.CallOkAsync("make_report", new
		{
			assessmentIds = new[] { ranch.Id().ToString(), other.Id().ToString(), unknown }, kind = "detailed", includePhotos = false, fileName = "../../Outside/escape.pdf",
		});
		Assert.Equal(Outputs(s), Path.GetDirectoryName(detailed.Str("path")));
		Assert.EndsWith("escape.pdf", detailed.Str("path"));
		Assert.Equal("Substantial Damage Detail Report", detailed.Str("report"));
		Assert.Equal(2, detailed.Int("assessments"));
		Assert.Equal(new[] { unknown }, detailed.List("skipped").Select(x => x.GetString()));
		Assert.False(Directory.Exists(Path.Combine(s.Db.Folder, "..", "Outside")));

		// An event's structures.
		EventRecord flood = TestData.Event(s.Store);
		s.Store.SetEvent(new[] { other.Id() }, flood.EventID);
		JsonElement byEvent = await s.CallOkAsync("make_report", new { @event = "September flood" });
		Assert.Equal(1, byEvent.Int("assessments"));
		Assert.Equal("Summary report for September flood (DR-4999).pdf", Path.GetFileName(byEvent.Str("path")));

		Assert.Contains("summary", await s.CallErrorAsync("make_report", new { assessmentIds = new[] { ranch.Id().ToString() }, kind = "full" }));
		Assert.Contains("Choose the assessments", await s.CallErrorAsync("make_report"));
		Assert.Contains("can be printed", await s.CallErrorAsync("make_report", new { assessmentIds = new[] { unknown } }));

		// Nothing in the database changed, and no backup was needed.
		Assert.Equal(lastUpdate, s.Store.LoadAssessment(ranch.Id()).Assessment.LastUpdate);
		Assert.Equal(history, s.Store.GetHistory(ranch.Id()).Count);
		Assert.Null(s.Context.Backup);
	}

	[Fact]
	public async Task Export_geojson_writes_points_with_chosen_fields_and_leaves_owners_out()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = TestData.PlacedRanch(s.Store);
		AssessmentFile placed = TestData.Structure(s.Store, "201", latitude: 39.71m, longitude: -84.21m, owner: "Sample");
		TestData.Structure(s.Store, "5", "Nowhere");

		JsonElement r = await s.CallOkAsync("export_geojson", new { fields = new[] { "Property.FoundationTypeID", "Assessment.InspectorName" } });
		Assert.Equal(Path.Combine(Outputs(s), "SDE structures.geojson"), r.Str("path"));
		Assert.Equal(2, r.Int("features"));
		Assert.Equal(1, r.Int("skippedNoCoordinates"));
		Assert.False(r.GetProperty("ownerNames").GetBoolean());
		Assert.Contains("Property.FoundationTypeID", r.List("properties").Select(p => p.GetString()));
		using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(r.Str("path"))))
		{
			Assert.Equal("FeatureCollection", doc.RootElement.Str("type"));
			JsonElement feature = doc.RootElement.List("features").Single(f => f.GetProperty("properties").Str("assessment_id") == ranch.Id().ToString());
			Assert.Equal(-84.20m, feature.GetProperty("geometry").GetProperty("coordinates")[0].GetDecimal());
			Assert.Equal(39.70m, feature.GetProperty("geometry").GetProperty("coordinates")[1].GetDecimal());
			JsonElement p = feature.GetProperty("properties");
			Assert.Equal("123 River Street", p.Str("address"));
			Assert.Equal("Substantial", p.Str("determination"));
			Assert.Equal(75.0m, p.Dec("damage_pct"));
			Assert.Equal("Crawlspace", p.Str("Property.FoundationTypeID"));
			Assert.Equal("Inspector A", p.Str("Assessment.InspectorName"));
			Assert.False(p.TryGetProperty("owner_name", out _));
		}
		Assert.DoesNotContain("Pat Example", File.ReadAllText(r.Str("path")));

		// Owner details only when asked for.
		string refused = await s.CallErrorAsync("export_geojson", new { fields = new[] { "Property.OwnerLastName" } });
		Assert.Contains("includeOwnerNames", refused);
		JsonElement owners = await s.CallOkAsync("export_geojson", new { fields = new[] { "Property.OwnerLastName" }, includeOwnerNames = true, fileName = "With owners" });
		Assert.Equal("With owners.geojson", Path.GetFileName(owners.Str("path")));
		string text = File.ReadAllText(owners.Str("path"));
		Assert.Contains("\"owner_name\": \"Pat Example\"", text);
		Assert.Contains("\"Property.OwnerLastName\": \"Example\"", text);

		// One event.
		EventRecord flood = TestData.Event(s.Store);
		s.Store.SetEvent(new[] { placed.Id() }, flood.EventID);
		JsonElement inEvent = await s.CallOkAsync("export_geojson", new { @event = "September flood" });
		Assert.Equal(1, inEvent.Int("features"));
		Assert.Equal("September flood (DR-4999) structures.geojson", Path.GetFileName(inEvent.Str("path")));
		Assert.Contains("There's no field called", await s.CallErrorAsync("export_geojson", new { fields = new[] { "Owner" } }));
	}

	[Fact]
	public async Task Make_web_map_keeps_public_maps_private()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = TestData.PlacedRanch(s.Store);
		TestData.Structure(s.Store, "5", "Nowhere");

		JsonElement internalMap = await s.CallOkAsync("make_web_map", new { popupFields = new[] { "Assessment.InspectorName" } });
		Assert.Equal(Path.Combine(Outputs(s), "Damage assessments web map"), internalMap.Str("folder"));
		Assert.Equal(Path.Combine(internalMap.Str("folder"), "index.html"), internalMap.Str("indexPath"));
		Assert.Equal("Internal", internalMap.Str("privacy"));
		Assert.Equal(1, internalMap.Int("features"));
		Assert.Equal(1, internalMap.Int("skippedNoCoordinates"));
		string page = File.ReadAllText(internalMap.Str("indexPath"));
		Assert.Contains("123 River Street", page);
		Assert.Contains("Inspector A", page);

		JsonElement publicMap = await s.CallOkAsync("make_web_map", new { title = "Flood damage", privacy = "public", popupFields = new[] { "Assessment.InspectorName" }, basemap = "none" });
		Assert.Equal("Public", publicMap.Str("privacy"));
		Assert.Contains("popupFields was not used", publicMap.Str("note"));
		string shared = File.ReadAllText(publicMap.Str("indexPath")) + File.ReadAllText(Path.Combine(publicMap.Str("folder"), "structures.geojson"));
		Assert.DoesNotContain("123 River", shared);
		Assert.DoesNotContain("Pat Example", shared);
		Assert.DoesNotContain("Inspector A", shared);
		Assert.Contains("100 block of River Street", shared);

		EventRecord flood = TestData.Event(s.Store);
		s.Store.SetEvent(new[] { ranch.Id() }, flood.EventID);
		JsonElement eventMap = await s.CallOkAsync("make_web_map", new { @event = flood.EventID.ToString() });
		Assert.True(eventMap.GetProperty("eventSummary").GetBoolean());
		Assert.Equal("September flood (DR-4999) web map", Path.GetFileName(eventMap.Str("folder")));
		Assert.Contains("class=\"summary\"", File.ReadAllText(eventMap.Str("indexPath")));

		Assert.Contains("internal", await s.CallErrorAsync("make_web_map", new { privacy = "secret" }));
		Assert.Contains("osm", await s.CallErrorAsync("make_web_map", new { basemap = "satellite" }));
	}
}
