using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Analysis;
using Sde.Gis.Features;
using Sde.Gis.Formats;
using Point = Avalonia.Point;

namespace Sde.App.Tests;

/// <summary>Select by location, and copying a layer of areas' values onto structures (with the preview, locks and history).</summary>
public class MapAnalysisTests
{
	private static Polygon Box(double south, double west, double north, double east) => GisSetup.Factory.CreatePolygon(new[]
	{
		new Coordinate(west, south), new Coordinate(east, south), new Coordinate(east, north), new Coordinate(west, north), new Coordinate(west, south),
	});

	/// <summary>
	/// Made-up flood zones around the sample structures: an AE zone around River (39.1031, -84.5120), and around Mill
	/// Creek (39.1100, -84.5000) a small X zone inside a larger shaded X zone.
	/// </summary>
	internal static async Task<MapLayerViewModel> AddZones(UiSession ui)
	{
		FeatureLayer zones = new FeatureLayer("flood zones", GeometryKind.Polygon);
		zones.AddField("FLD_ZONE", FieldType.Text);
		zones.AddField("CID", FieldType.Text);
		zones.AddField("COMM_NAME", FieldType.Text);
		zones.AddField("PIN", FieldType.Text);
		zones.AddField("STATIC_BFE", FieldType.Real);
		Feature ae = zones.AddFeature(Box(39.1026, -84.5126, 39.1036, -84.5114), 1);
		ae["FLD_ZONE"] = "AE";
		ae["CID"] = "390999";
		ae["COMM_NAME"] = "Exampleville, City of";
		ae["PIN"] = "012-345-678";
		ae["STATIC_BFE"] = 491.5;
		Feature x = zones.AddFeature(Box(39.1098, -84.5003, 39.1102, -84.4997), 2);
		x["FLD_ZONE"] = "X";
		x["CID"] = "390999";
		x["PIN"] = "012-999-001";
		Feature shaded = zones.AddFeature(Box(39.1090, -84.5010, 39.1110, -84.4990), 3);
		shaded["FLD_ZONE"] = "0.2 PCT ANNUAL CHANCE FLOOD HAZARD";
		shaded["CID"] = "390999";
		string path = Path.Combine(ui.Folder, "flood-zones.geojson");
		GeoJsonFormat.Write(path, zones);
		ui.Dialogs.Answers.Enqueue(new[] { path });
		await ui.Main.Map.AddFileLayerCommand.ExecuteAsync(null);
		ui.Pump();
		return ui.Main.Map.Layers.Single(l => l.Name == "flood-zones" || l.Name == "flood zones");
	}

	private static MapPointViewModel Structure(MapViewModel map, string street) => map.Visible.Single(p => p.Address.Contains(street, StringComparison.Ordinal));

	[AvaloniaFact]
	public async Task SelectByLocation_FindsStructuresInsideOrNearTheSelectedZones()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		MapLayerViewModel zones = await AddZones(ui);
		map.SetModeCommand.Execute(MapMode.Select);
		map.SelectFeatures(zones.Key, zones.Data.Features.Where(f => (string)f["FLD_ZONE"] == "AE").ToList(), add: false);

		Task asking = map.SelectByLocationCommand.ExecuteAsync(null);
		ui.Pump();
		SelectByLocationSheetViewModel sheet = Assert.IsType<SelectByLocationSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(sheet.Target.IsStructures);
		Assert.Equal("the selected feature of " + zones.Name, sheet.Source.Label);
		Assert.Equal(new[] { "the selected feature of " + zones.Name, "an area you draw on the map" }, sheet.Sources.Select(s => s.Label));
		Assert.Equal("1 of 2 structures match.", sheet.CountText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// Within a distance: 100 ft reaches only River; a mile reaches Mill Creek too.
		sheet.Relation = sheet.Relations.Single(r => r.Relation == SpatialRelation.WithinDistance);
		Assert.True(sheet.NeedsDistance);
		Assert.Equal("1 of 2 structures match.", sheet.CountText);
		sheet.DistanceText = "1";
		sheet.UnitIndex = 1;
		Assert.Equal("2 of 2 structures match.", sheet.CountText);
		sheet.DistanceText = "lots";
		Assert.Equal("Type the distance as a number, such as 100.", sheet.Problem);
		sheet.SelectCommand.Execute(null);
		Assert.Same(sheet, ui.Main.Sheets.Current);
		sheet.DistanceText = "1";
		sheet.SelectCommand.Execute(null);
		await asking;
		Assert.Equal(2, ui.Main.Structures.CheckedCount);
		Assert.Equal("2 structures are selected.", map.Message);

		// Within the selection: only those that touch the AE zone stay.
		asking = map.SelectByLocationCommand.ExecuteAsync(null);
		ui.Pump();
		sheet = Assert.IsType<SelectByLocationSheetViewModel>(ui.Main.Sheets.Current);
		sheet.Source = sheet.Sources.Single(s => s.Layer == zones);
		sheet.MethodIndex = 2;
		Assert.Equal("1 of 2 structures match.", sheet.CountText);
		sheet.SelectCommand.Execute(null);
		await asking;
		Assert.Equal("River", ui.Store.GetProperty(Assert.Single(map.SelectedStructures())).StreetName);

		// Zones that contain the selected structure.
		map.SelectArea(map.Visible.Select(p => p.Id).ToList(), add: false);
		map.ClearSelectionCommand.Execute(null);
		asking = map.SelectByLocationCommand.ExecuteAsync(null);
		ui.Pump();
		sheet = Assert.IsType<SelectByLocationSheetViewModel>(ui.Main.Sheets.Current);
		sheet.Target = zones;
		sheet.Source = sheet.Sources.Single(s => s.Layer?.IsStructures == true);
		Assert.Equal("the 2 selected structures", sheet.Source.Label);
		sheet.Relation = sheet.Relations.Single(r => r.Relation == SpatialRelation.Contains);
		Assert.Equal($"3 of 3 {zones.Name} match.", sheet.CountText);
		sheet.SelectCommand.Execute(null);
		await asking;
		Assert.Equal(3, zones.SelectedCount);
	}

	[AvaloniaFact]
	public async Task SelectByLocation_WithADrawnArea_WaitsForTheArea()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		await AddZones(ui);
		view.Map.SetView(39.1065, -84.5060, 15);
		ui.Pump();
		Task asking = map.SelectByLocationCommand.ExecuteAsync(null);
		ui.Pump();
		SelectByLocationSheetViewModel sheet = Assert.IsType<SelectByLocationSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(sheet.Source.IsDrawnArea);
		sheet.Relation = sheet.Relations.Single(r => r.Relation == SpatialRelation.WithinDistance);
		sheet.DistanceText = "200";
		Assert.StartsWith("Next, draw the area on the map", sheet.CountText);
		sheet.SelectCommand.Execute(null);
		await asking;
		Assert.True(map.IsSelect);
		Assert.True(map.IsPolygonSelect);
		Assert.Equal("Click the corners of the area; double-click or press Return to select the structures that are within 200 ft of it.", map.Hint);

		// A small area next to Mill Creek (about 30 m away): within 200 ft, so it is selected.
		foreach ((double lat, double lon) in new[] { (39.1103, -84.5004), (39.1106, -84.5004), (39.1106, -84.5001), (39.1103, -84.5001) })
		{
			view.Map.Click(view.Map.ToScreen(lat, lon));
		}
		view.Map.Focus();
		ui.Window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
		ui.Pump();
		Assert.Equal("Mill Creek", ui.Store.GetProperty(Assert.Single(map.SelectedStructures())).StreetName);
		Assert.False(map.IsPolygonSelect);
		Assert.Equal("1 structure is selected.", map.Message);
	}

	[AvaloniaFact]
	public async Task CopyValues_PreviewsEachStructure_SkipsLockedOnes_AndIsOneBatchInHistory()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		MapLayerViewModel zones = await AddZones(ui);
		Assert.True(zones.IsAreaLayer);
		MapPointViewModel river = Structure(map, "River");
		MapPointViewModel mill = Structure(map, "Mill Creek");
		FieldInfo firm = FieldCatalog.Get("Property.FIRMZone");

		Task copying = map.CopyValuesCommand.ExecuteAsync(zones);
		ui.Pump();
		CopyValuesSheetViewModel sheet = Assert.IsType<CopyValuesSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Same(zones, sheet.Layer);
		Assert.Equal("FLD_ZONE", sheet.SourceField.Key);
		Assert.Equal(firm, sheet.Target.Field);
		Assert.Equal(new[] { "Parcel number", "FIRM zone", "NFIP community ID", "Community name" }, sheet.Targets.Take(4).Select(t => t.Label));
		Assert.False(sheet.HasSelection);
		Assert.True(sheet.AllStructures);
		Assert.Equal("All 2 structures on the map", sheet.AllText);
		Assert.Equal(2, sheet.Rows.Count);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		CopyValueRowViewModel riverRow = sheet.Rows.Single(r => r.PropertyId == river.Id);
		CopyValueRowViewModel millRow = sheet.Rows.Single(r => r.PropertyId == mill.Id);
		Assert.Equal("AE", riverRow.New);
		Assert.Equal("Inside one area", riverRow.How);
		Assert.Equal("X", millRow.New);
		Assert.Equal("Inside 2 areas: the smallest is used", millRow.How);

		// Parcel numbers: the field is picked from the name, and Mill Creek's smaller zone has its own.
		sheet.SourceField = sheet.Fields.Single(f => f.Key == "PIN");
		Assert.Equal("Parcel number", sheet.Target.Label);
		Assert.Equal("012-345-678", sheet.Rows.Single(r => r.PropertyId == river.Id).New);

		// Back to flood zones, for the selected structure only.
		sheet.SourceField = sheet.Fields.Single(f => f.Key == "FLD_ZONE");
		sheet.Cancel();
		await copying;
		map.SelectArea(new[] { river.Id, mill.Id }, add: false);
		ui.Store.SetStatus(mill.AssessmentID!.Value, AssessmentStatus.Final);
		ui.Services.RaiseDataChanged();
		copying = map.CopyValuesCommand.ExecuteAsync(null);
		ui.Pump();
		sheet = Assert.IsType<CopyValuesSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(sheet.OnlySelected);
		Assert.Equal("The 2 selected structures", sheet.SelectedText);
		millRow = sheet.Rows.Single(r => r.PropertyId == mill.Id);
		Assert.False(millRow.CanCopy);
		Assert.False(millRow.IsTicked);
		Assert.Equal("Locked (Final)", millRow.How);
		Assert.Equal(1, sheet.TickedCount);
		Assert.Equal("Copy 1 value", sheet.CopyText);
		Assert.StartsWith("1 to change", sheet.SummaryText);
		Assert.Contains("1 can't be changed", sheet.SummaryText);
		sheet.CopyCommand.Execute(null);
		await copying;
		ui.Pump();

		AssessmentFile riverFile = ui.Store.LoadAssessment(river.AssessmentID!.Value);
		Assert.Equal("AE", FieldCatalog.Display(firm, FieldCatalog.GetValue(riverFile, firm), ui.Store.Reference));
		Assert.StartsWith($"Copied FLD_ZONE from {zones.Name} into FIRM zone on 1 structure.", map.Message);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Values copied:", StringComparison.Ordinal));
		EditHistoryRecord row = ui.Store.GetHistory(river.AssessmentID.Value).First(h => h.Field == firm.Key);
		Assert.Equal(SaveVia.Map, row.Via);
		Assert.NotNull(row.BatchId);
		Assert.All(ui.Store.GetBatch(row.BatchId), h => Assert.Equal(SaveVia.Map, h.Via));
		Assert.DoesNotContain(ui.Store.GetBatch(row.BatchId), h => h.AssessmentID == mill.AssessmentID);

		// The batch can be reverted, as from History.
		ui.Store.RevertBatch(row.BatchId);
		AssessmentFile reverted = ui.Store.LoadAssessment(river.AssessmentID.Value);
		Assert.Equal("", FieldCatalog.Display(firm, FieldCatalog.GetValue(reverted, firm), ui.Store.Reference));
	}

	[Fact]
	public void LayerFieldNames_SuggestTheStructureField()
	{
		Assert.Equal("Property.FIRMZone", CopyValuesSheetViewModel.Suggest("FLD_ZONE"));
		Assert.Equal("Property.FIRMZone", CopyValuesSheetViewModel.Suggest("flood_zone"));
		Assert.Equal("Property.ParcelNumber", CopyValuesSheetViewModel.Suggest("PIN"));
		Assert.Equal("Property.ParcelNumber", CopyValuesSheetViewModel.Suggest("Parcel ID"));
		Assert.Equal("Property.NFIPCID", CopyValuesSheetViewModel.Suggest("CID"));
		Assert.Equal("Property.NFIPCommunityName", CopyValuesSheetViewModel.Suggest("COMM_NAME"));
		Assert.Null(CopyValuesSheetViewModel.Suggest("land_value"));
	}

	[AvaloniaFact]
	public async Task ScreenshotsOfSelectByLocationAndCopyValues_LightAndDark()
	{
		foreach ((ThemeVariant theme, string suffix) in new[] { (ThemeVariant.Light, ""), (ThemeVariant.Dark, "-dark") })
		{
			using UiSession ui = new UiSession(theme);
			MapTestSupport.SetUpPlaces(ui, Basemaps.UsgsTopo);
			MapView view = MapTestSupport.ShowMap(ui);
			MapViewModel map = ui.Main.Map;
			MapLayerViewModel zones = await AddZones(ui);
			map.ApplyStyle(zones, new LayerStyleSheetViewModel.Result(Sde.Gis.Styles.LayerStyle.Categorized(zones.Data, "FLD_ZONE"), null, null, null));
			view.Map.FitTo(new MapExtent(-84.514, 39.101, -84.498, 39.112));
			map.SetModeCommand.Execute(MapMode.Select);
			map.SelectTarget = zones;
			map.SelectFeatures(zones.Key, zones.Data.Features.Where(f => (string)f["FLD_ZONE"] == "AE").ToList(), add: false);
			Task asking = map.SelectByLocationCommand.ExecuteAsync(null);
			ui.Pump();
			SelectByLocationSheetViewModel sheet = Assert.IsType<SelectByLocationSheetViewModel>(ui.Main.Sheets.Current);
			sheet.Target = sheet.Targets.Single(t => t.IsStructures);
			sheet.Source = sheet.Sources.Single(s => s.Layer == zones);
			sheet.Relation = sheet.Relations.Single(r => r.Relation == SpatialRelation.WithinDistance);
			sheet.DistanceText = "500";
			Assert.Equal("1 of 2 structures match.", sheet.CountText);
			MapTestSupport.Settle(ui, view);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			ui.Snapshot("56h-map-select-by-location" + suffix);
			sheet.Cancel();
			await asking;

			// River is in zone A now (the zones layer says AE); Mill Creek already has X.
			foreach ((string street, string zone) in new[] { ("River", "A"), ("Mill Creek", "X") })
			{
				Sde.Core.Records.PropertyRecord p = ui.Store.GetProperties().Single(x => x.StreetName == street);
				p.FIRMZone = Sde.Core.FirmZones.Find(zone, ui.Store.Reference).LookupID;
				ui.Store.SaveProperty(p);
			}
			ui.Services.RaiseDataChanged();
			Task copying = map.CopyValuesCommand.ExecuteAsync(zones);
			ui.Pump();
			CopyValuesSheetViewModel copy = Assert.IsType<CopyValuesSheetViewModel>(ui.Main.Sheets.Current);
			Assert.Equal("1 to change, 1 already the same.", copy.SummaryText);
			MapTestSupport.Settle(ui, view);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			ui.Snapshot("56i-map-copy-values" + suffix);
			ui.Main.Sheets.Current.Cancel();
			await copying;
		}
	}
}
