using Avalonia.Headless.XUnit;
using Sde.App.ViewModels.Sheets;
using Sde.App.ViewModels.Shell;
using Sde.Data;
using Sde.Interop.WebMap;

namespace Sde.App.Tests;

/// <summary>
/// Share a web map (v2.5, C9): Structures, Export ▾, Web map… for the chosen event, with popup fields and the event's
/// summary for the office, or grid squares with no addresses for the public. Writes the sheet's screenshot.
/// </summary>
public class WebMapUiTests
{
	[AvaloniaFact]
	public async Task WebMap_FromStructures_ForTheOfficeAndThePublic()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		ui.Pump();
		Guid river = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value;
		Guid mill = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("48", StringComparison.Ordinal)).AssessmentID!.Value;
		EventRecord e = ui.Store.SaveEvent(new EventRecord { Name = "September flood", Kind = "Flood", StartDate = new DateTime(2026, 9, 1) });
		ui.Store.SetEvent(new[] { river, mill }, e.EventID);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		ui.Main.Events.ChooseCommand.Execute(ui.Main.Events.Events.Single(c => c.Id == e.EventID));
		ui.Pump();
		string parent = Path.Combine(ui.Folder, "web");
		Directory.CreateDirectory(parent);

		// For the office: the event's name and summary, a dot per structure with the chosen popup fields.
		Task making = ui.Main.Structures.WebMapCommand.ExecuteAsync(null);
		ui.Pump();
		WebMapSheetViewModel sheet = Assert.IsType<WebMapSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("Web map of 2 structures", sheet.Title);
		Assert.Equal(e.Label, sheet.MapTitle);
		Assert.True(sheet.IncludeSummary);
		Assert.Contains("September flood", sheet.SummaryLabel);
		sheet.PopupFields.Single(p => p.Key.EndsWith(".InspectorName", StringComparison.Ordinal)).IsChecked = true;
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("56e-web-map-sheet");
		ui.Dialogs.Answers.Enqueue(parent);
		await sheet.CreateCommand.ExecuteAsync(null);
		await making;
		string office = Assert.Single(Directory.GetDirectories(parent));
		string geo = File.ReadAllText(Path.Combine(office, "structures.geojson"));
		Assert.Contains("River", geo);
		Assert.Contains("Inspector A", geo);
		Assert.Contains("September flood", File.ReadAllText(Path.Combine(office, "index.html")));
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Web map made: Made the web map in September flood web map."));

		// For the public: grid squares, no addresses or owners, no background map.
		making = ui.Main.Structures.WebMapCommand.ExecuteAsync(null);
		ui.Pump();
		sheet = Assert.IsType<WebMapSheetViewModel>(ui.Main.Sheets.Current);
		sheet.IsPublic = true;
		sheet.ShowBlocks = false;
		sheet.CellSizeIndex = 2;
		sheet.BasemapIndex = 2;
		WebMapOptions options = sheet.Options(null);
		Assert.Equal(WebMapPrivacy.Public, options.Privacy);
		Assert.Equal(PublicAddressDetail.Hidden, options.AddressDetail);
		Assert.Equal(500, options.CellMeters);
		Assert.Equal(WebMapBasemap.None, options.Basemap);
		ui.Dialogs.Answers.Enqueue(parent);
		await sheet.CreateCommand.ExecuteAsync(null);
		await making;
		string published = Directory.GetDirectories(parent).Single(d => d != office);
		string publicGeo = File.ReadAllText(Path.Combine(published, "structures.geojson"));
		Assert.DoesNotContain("River", publicGeo);
		Assert.DoesNotContain("Mill Creek", publicGeo);
		Assert.DoesNotContain("Example", publicGeo);
		Assert.DoesNotContain("Inspector A", publicGeo);
	}
}
