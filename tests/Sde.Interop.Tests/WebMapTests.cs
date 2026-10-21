using System.Text.Json;
using System.Text.RegularExpressions;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.WebMap;

namespace Sde.Interop.Tests;

/// <summary>Share a web map (C9): a folder that opens in any browser, with nothing private on a public map.</summary>
public class WebMapTests
{
	private static List<AssessmentFile> Placed(TempDatabase t)
	{
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		ranch.Property.Latitude = 39.701234m;
		ranch.Property.Longitude = -84.181234m;
		ranch.Property.OwnerLastName = "<script>alert(1)</script>";
		t.Store.SaveAssessment(ranch);
		AssessmentFile nowhere = t.Store.CreateAssessment(t.Store.SaveProperty(new PropertyRecord { StreetNumber = "9", StreetName = "Elm", StructureTypeID = 1 }).PropertyID, applyDefaults: false);
		return t.Store.LoadAllAssessments();
	}

	private static JsonDocument Data(string folder) => JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "structures.geojson")));

	[Fact]
	public void AnInternalMap_HasADotPerStructure_WithEscapedPopups_AndNeedsNothingOnline()
	{
		using TempDatabase t = new TempDatabase();
		string folder = Path.Combine(t.Folder, "web map");
		WebMapResult r = WebMapExport.Write(Placed(t), t.Store.Reference, new WebMapOptions
		{
			Title = "September flood",
			PopupFields = new[] { "Property.OwnerLastName", "Assessment.InspectorName" },
			Summary = EventSummary.Build(Array.Empty<AssessmentFile>(), details: new EventDetails("September flood", "Flood", "DR-4999", new DateTime(2026, 9, 14), null)),
		}, folder);

		Assert.Equal(1, r.Features);
		Assert.Equal(1, r.SkippedNoCoordinates);
		using JsonDocument data = Data(folder);
		JsonElement feature = data.RootElement.GetProperty("features")[0];
		Assert.Equal(-84.181234, feature.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble());
		string popup = feature.GetProperty("properties").GetProperty("popup").GetString();
		Assert.Contains("&lt;script&gt;", popup);
		Assert.Contains("Inspector A", popup);

		string html = File.ReadAllText(r.IndexPath);
		Assert.DoesNotContain("<script>alert", html);
		Assert.Contains("L.map(", html);
		Assert.Contains("From Sep 14, 2026", html);
		// No stylesheet or script is fetched from elsewhere: only the background tiles need the internet.
		Assert.Empty(Regex.Matches(html, "<(script|link)[^>]+(src|href)=", RegexOptions.IgnoreCase));
		Assert.True(File.Exists(Path.Combine(folder, "LICENSE-Leaflet.txt")));
	}

	[Fact]
	public void APublicMap_CountsStructuresInSquares_WithoutOwnersOrExactPlaces()
	{
		using TempDatabase t = new TempDatabase();
		List<AssessmentFile> files = Placed(t);
		AssessmentFile neighbour = SampleCases.RanchFlood(t.Store);
		neighbour.Property.StreetNumber = "131";
		neighbour.Property.Latitude = 39.701500m;
		neighbour.Property.Longitude = -84.181000m;
		t.Store.SaveAssessment(neighbour);
		files = t.Store.LoadAllAssessments();
		string folder = Path.Combine(t.Folder, "public");

		WebMapResult r = WebMapExport.Write(files, t.Store.Reference, new WebMapOptions
		{
			Privacy = WebMapPrivacy.Public,
			PopupFields = new[] { "Property.OwnerLastName" },
		}, folder);

		Assert.Equal(1, r.Features);
		using JsonDocument data = Data(folder);
		JsonElement feature = data.RootElement.GetProperty("features")[0];
		Assert.Equal(2, feature.GetProperty("properties").GetProperty("count").GetInt32());
		double lon = feature.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble();
		Assert.NotEqual(-84.181234, lon);
		string everything = File.ReadAllText(r.IndexPath) + File.ReadAllText(Path.Combine(folder, "structures.geojson"));
		Assert.DoesNotContain("Example", everything);
		Assert.DoesNotContain("script&gt;", everything);
		Assert.DoesNotContain("123 River", everything);
		Assert.Contains("100 block of River", everything);
	}

	[Theory]
	[InlineData("123", "River", "100 block of River")]
	[InlineData("7", "Elm", "0 block of Elm")]
	[InlineData("12B", "Oak", "0 block of Oak")]
	[InlineData(null, "Oak", "Oak")]
	public void Block_CutsTheNumberToItsHundred(string number, string street, string expected)
	{
		Assert.Equal(expected, WebMapExport.Block(new PropertyRecord { StreetNumber = number, StreetName = street }, null));
	}
}
