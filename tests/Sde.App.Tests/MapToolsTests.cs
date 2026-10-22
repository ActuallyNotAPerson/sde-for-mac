using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Analysis;
using Sde.Gis.Features;
using Sde.Gis.Styles;
using Point = Avalonia.Point;

namespace Sde.App.Tests;

/// <summary>Selecting (click, box, area, attributes, in a chosen layer), what can be done with selected structures, measuring and bookmarks.</summary>
public class MapToolsTests
{
	/// <summary>Clicks the corners of a shape on the map, then finishes it with Return.</summary>
	private static void Draw(UiSession ui, MapView view, IEnumerable<(double Lat, double Lon)> corners)
	{
		foreach ((double lat, double lon) in corners)
		{
			view.Map.Click(view.Map.ToScreen(lat, lon));
		}
		view.Map.Focus();
		ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		ui.Pump();
	}

	private static IEnumerable<(double, double)> Box(double south, double west, double north, double east) =>
		new[] { (south, west), (north, west), (north, east), (south, east) };

	[AvaloniaFact]
	public void DrawingAnArea_SelectsTheStructuresInsideIt_AndTheShapeGoes()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		map.SetModeCommand.Execute(MapMode.Select);
		map.IsPolygonSelect = true;
		ui.Pump();
		Assert.Equal(MapSketch.Polygon, view.Map.Sketch);
		Assert.Contains("corners", map.Hint);

		// Around River (39.1031, -84.5120) but not Mill Creek (39.1100, -84.5000).
		Draw(ui, view, Box(39.100, -84.515, 39.106, -84.508));
		Assert.Equal(1, ui.Main.Structures.CheckedCount);
		Assert.Equal("River", ui.Store.GetProperty(Assert.Single(map.SelectedStructures())).StreetName);
		Assert.Empty(view.Map.SketchPoints);
		Assert.True(map.HasChecked);
		Assert.Equal("1 structure selected", map.SelectionText);

		// The selection bar is on the map, with every button named.
		TextBlock bar = ui.Window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "1 structure selected");
		Assert.True(bar.IsEffectivelyVisible);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		map.IsPolygonSelect = false;
		Assert.Equal(MapSketch.None, view.Map.Sketch);
		Assert.Equal(MapDrag.Box, view.Map.Drag);
	}

	[AvaloniaFact]
	public async Task SelectingInALayer_ByClickBoxAndArea_PicksItsFeatures()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await MapStylesTableTests.OpenWithParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.SetModeCommand.Execute(MapMode.Select);
			map.SelectTarget = parcels;
			ui.Pump();
			Assert.Equal(parcels.Key, view.Map.SelectLayer);
			Assert.Contains("parcels", map.Hint);
			view.Map.FitTo(new MapExtent(-84.051, 39.619, -84.044, 39.624));
			ui.Pump();

			// A box over part of the layer selects the features it touches.
			Point a = view.Map.ToScreen(39.6215, -84.0505);
			Point b = view.Map.ToScreen(39.6195, -84.0485);
			view.Map.SelectBox(new Rect(a, b), add: false);
			ui.Pump();
			Assert.True(parcels.SelectedCount > 0);
			Assert.True(map.HasLayerSelection);
			Assert.Equal($"{parcels.SelectedCount} selected in parcels", map.LayerSelectionText);
			Assert.Equal(0, ui.Main.Structures.CheckedCount);

			// An area drawn on the map selects what it touches, as the analysis library counts it.
			map.IsPolygonSelect = true;
			ui.Pump();
			(double, double)[] corners = Box(39.6199, -84.0499, 39.6203, -84.0493).ToArray();
			Draw(ui, view, corners);
			Polygon area = GisSetup.Factory.CreatePolygon(corners.Append(corners[0]).Select(c => new Coordinate(c.Item2, c.Item1)).ToArray());
			HashSet<long> expected = SpatialSelect.ByLocation(parcels.Data.Features, new[] { area }, SpatialRelation.Intersects).Select(f => f.Id).ToHashSet();
			Assert.NotEmpty(expected);
			Assert.Equal(expected.OrderBy(i => i), parcels.Selected.OrderBy(i => i));

			// Show in the table lists just those; Clear empties the selection.
			map.ShowSelectionInTableCommand.Execute(null);
			Assert.True(map.IsTableOpen);
			Assert.True(map.TableSelectedOnly);
			Assert.Equal(expected.Count, map.TableRows.Count);
			map.ClearSelectionCommand.Execute(null);
			Assert.Equal(0, parcels.SelectedCount);
			Assert.False(map.HasLayerSelection);
			Assert.Empty(map.TableRows);
		}
	}

	[AvaloniaFact]
	public async Task SelectByAttribute_CountsAsYouType_JoinsWithAndOr_AndSelects()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await MapStylesTableTests.OpenWithParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.SetModeCommand.Execute(MapMode.Select);
			map.SelectTarget = parcels;
			Task showing = map.SelectByAttributeCommand.ExecuteAsync(null);
			ui.Pump();
			SelectByAttributeSheetViewModel sheet = Assert.IsType<SelectByAttributeSheetViewModel>(ui.Main.Sheets.Current);
			Assert.Same(parcels, sheet.Layer);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

			AttributeConditionViewModel first = Assert.Single(sheet.Conditions);
			first.Field = first.Fields.Single(f => f.Key == "flood_zone");
			first.Value = "AE";
			Assert.Equal("12 of 30 match.", sheet.CountText);

			sheet.AddConditionCommand.Execute(null);
			AttributeConditionViewModel second = sheet.Conditions[1];
			second.Field = second.Fields.Single(f => f.Key == "land_value");
			second.Operator = OperatorChoice.All.Single(o => o.Operator == AttributeOperator.GreaterThan);
			second.Value = "abc";
			Assert.True(sheet.HasProblem);
			Assert.Null(sheet.CountText);
			second.Value = "30000";
			Assert.False(sheet.HasProblem);
			int both = parcels.Data.Features.Count(f => (string)f["flood_zone"] == "AE" && Convert.ToDouble(f["land_value"]) > 30000);
			int either = parcels.Data.Features.Count(f => (string)f["flood_zone"] == "AE" || Convert.ToDouble(f["land_value"]) > 30000);
			Assert.Equal($"{both} of 30 match.", sheet.CountText);
			sheet.MatchAny = true;
			Assert.Equal($"{either} of 30 match.", sheet.CountText);

			sheet.SelectCommand.Execute(null);
			await showing;
			Assert.Null(ui.Main.Sheets.Current);
			Assert.Equal(either, parcels.SelectedCount);

			// On the structures, the matches are checked in the Structures list.
			showing = map.SelectByAttributeCommand.ExecuteAsync(null);
			ui.Pump();
			sheet = Assert.IsType<SelectByAttributeSheetViewModel>(ui.Main.Sheets.Current);
			sheet.Layer = sheet.Layers.Single(l => l.IsStructures);
			AttributeConditionViewModel address = Assert.Single(sheet.Conditions);
			address.Field = address.Fields.Single(f => f.Key == "Address");
			address.Operator = OperatorChoice.All.Single(o => o.Operator == AttributeOperator.Contains);
			address.Value = "river";
			Assert.Equal("1 of 2 match.", sheet.CountText);
			sheet.SelectCommand.Execute(null);
			await showing;
			Assert.Equal("River", ui.Store.GetProperty(Assert.Single(map.SelectedStructures())).StreetName);
		}
	}

	[AvaloniaFact]
	public async Task SelectedStructures_CanBeAssignedToATeam_GivenAField_PrintedAndPutOnAWebMap()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		map.SelectArea(map.Visible.Select(p => p.Id).ToList(), add: false);
		Assert.Equal("2 structures selected", map.SelectionText);
		List<Guid> structures = map.SelectedStructures().ToList();

		// Assign to team, making the team on the way.
		Task assigning = map.AssignSelectionCommand.ExecuteAsync(null);
		ui.Pump();
		AssignTeamSheetViewModel team = Assert.IsType<AssignTeamSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("Assign 2 structures to a team", team.Title);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		team.Team = team.Teams.Single(t => t.IsNew);
		team.AssignCommand.Execute(null);
		Assert.Equal("Give the team a name.", team.Problem);
		team.NewTeamName = "North crew";
		team.AlsoSetTeamName = true;
		team.AssignCommand.Execute(null);
		await assigning;
		Assert.Equal("Assigned 2 structures to North crew.", map.Message);
		TeamRecord north = Assert.Single(ui.Store.GetTeams());
		Assert.Equal(structures.OrderBy(g => g), ui.Store.GetAssignments(north.TeamID).Select(a => a.PropertyID).OrderBy(g => g));
		Assert.All(structures, id => Assert.Equal("North crew", ui.Store.GetProperty(id).TeamName));
		ui.Pump();
		int team2 = map.StructureFeatures.Fields.IndexOf("Team");
		Assert.All(map.StructureFeatures.Features, f => Assert.Equal("North crew", f[team2]));

		// Set a field opens bulk edit on the two assessments.
		Task setting = map.SetFieldOnSelectionCommand.ExecuteAsync(null);
		ui.Pump();
		BulkEditSheetViewModel bulk = Assert.IsType<BulkEditSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("Set a field on 2 assessments", bulk.Title);
		bulk.Cancel();
		await setting;

		// Reports print the selected structures.
		string pdf = Path.Combine(ui.Folder, "selected.pdf");
		ui.Dialogs.Answers.Enqueue(pdf);
		await map.PrintSelectionCommand.ExecuteAsync("PercentDamaged");
		Assert.Contains(pdf, ui.Dialogs.Opened);

		// Add to web map writes a folder with index.html and opens it.
		Task webMap = map.WebMapOfSelectionCommand.ExecuteAsync(null);
		ui.Pump();
		WebMapSheetViewModel sheet = Assert.IsType<WebMapSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.MapTitle = "Mill Creek: flood";
		string parent = Path.Combine(ui.Folder, "web");
		Directory.CreateDirectory(parent);
		ui.Dialogs.Answers.Enqueue(parent);
		await sheet.CreateCommand.ExecuteAsync(null);
		await webMap;
		string index = Path.Combine(parent, "Mill Creek- flood web map", "index.html");
		Assert.True(File.Exists(index));
		Assert.Contains(index, ui.Dialogs.Opened);
		Assert.Equal("Made the web map in Mill Creek- flood web map.", map.Message);
		Assert.Contains("Mill Creek: flood", File.ReadAllText(index));
	}

	[AvaloniaFact]
	public void Measure_ShowsDistanceInFeetAndAreaInAcres_AlongTheEarth()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		map.SetModeCommand.Execute(MapMode.Measure);
		ui.Pump();
		Assert.Equal(MapSketch.Line, view.Map.Sketch);
		view.Map.SetView(39.1060, -84.5060, 16);
		ui.Pump();

		view.Map.Click(view.Map.ToScreen(39.1040, -84.5100));
		Assert.Equal("Click the next point.", map.Measurement);
		view.Map.Click(view.Map.ToScreen(39.1040, -84.5020));
		LatLon[] line = view.Map.SketchPoints.ToArray();
		double metres = Measure.Distance(line[0].Longitude, line[0].Latitude, line[1].Longitude, line[1].Latitude);
		Assert.Equal("Distance: " + MapViewModel.Length(metres), map.Measurement);
		Assert.InRange(Measure.MetresToFeet(metres), 2260, 2280); // 0.008 degrees of longitude at 39.1 degrees north
		Assert.Matches(@"^Distance: 0\.43 mi \(2,2\d\d ft\)$", map.Measurement);

		// Delete takes off the last point; a third point adds on.
		view.Map.Focus();
		ui.Window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
		Assert.Equal("Click the next point.", map.Measurement);

		// Area: the shape closes, and its area and perimeter are given.
		map.MeasureKindIndex = 1;
		ui.Pump();
		Assert.Equal(MapSketch.Polygon, view.Map.Sketch);
		Assert.Empty(view.Map.SketchPoints);
		Assert.Null(map.Measurement);
		Draw(ui, view, Box(39.1040, -84.5100, 39.1080, -84.5020));
		LatLon[] corners = view.Map.SketchPoints.ToArray();
		Assert.Equal(4, corners.Length);
		Polygon square = GisSetup.Factory.CreatePolygon(corners.Append(corners[0]).Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray());
		Assert.StartsWith("Area: " + MapViewModel.Area(Measure.Area(square)), map.Measurement);
		Assert.Contains("Perimeter: ", map.Measurement);
		Assert.InRange(Measure.SquareMetresToAcres(Measure.Area(square)), 75, 77); // about 445 m by 691 m
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		MapTestSupport.Settle(ui, view);
		ui.Snapshot("56c-map-measure");

		// The next click starts a new shape; Clear forgets it.
		view.Map.Click(view.Map.ToScreen(39.1050, -84.5050));
		Assert.Single(view.Map.SketchPoints);
		map.ClearMeasurementCommand.Execute(null);
		Assert.Empty(view.Map.SketchPoints);
		Assert.Null(map.Measurement);

		// Leaving the tool clears it.
		map.SetModeCommand.Execute(MapMode.Browse);
		ui.Pump();
		Assert.Equal(MapSketch.None, view.Map.Sketch);
	}

	[Fact]
	public void LengthsAndAreas_ReadAsAnAssessorWouldSayThem()
	{
		Assert.Equal("328 ft", MapViewModel.Length(100));
		Assert.Equal("1.00 mi (5,280 ft)", MapViewModel.Length(1609.344));
		Assert.Equal("1.00 acre (43,560 sq ft)", MapViewModel.Area(4046.8564224));
		Assert.Equal("0.25 acres (10,890 sq ft)", MapViewModel.Area(4046.8564224 / 4));
		Assert.Equal("12.5 acres (544,500 sq ft)", MapViewModel.Area(4046.8564224 * 12.5));
	}

	[AvaloniaFact]
	public void Bookmarks_SaveTheView_GoBackToIt_AndStayWithTheDatabase()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		Assert.False(map.HasBookmarks);
		view.Map.SetView(39.1065, -84.5060, 16.5);
		ui.Pump();
		MapExtent saved = map.ViewExtent;
		map.NewBookmarkName = "Mill Creek";
		map.AddBookmarkCommand.Execute(null);
		map.AddBookmarkCommand.Execute(null);
		Assert.Equal(new[] { "Mill Creek", "View 2" }, map.Bookmarks.Select(b => b.Name));
		Assert.Equal(new[] { "Mill Creek", "View 2" }, ui.Store.GetMapBookmarks().Select(b => b.Name));
		Assert.Null(map.NewBookmarkName);

		view.Map.SetView(38.5, -96, 5);
		ui.Pump();
		map.Bookmarks[0].GoCommand.Execute(null);
		ui.Pump();
		Assert.Equal(16.5, view.Map.Zoom, 3);
		Assert.Equal(39.1065, view.Map.CenterLatitude, 5);
		Assert.Equal(-84.5060, view.Map.CenterLongitude, 5);
		Assert.Equal(saved.MinLongitude, map.ViewExtent.MinLongitude, 5);

		map.Bookmarks[1].RemoveCommand.Execute(null);
		Assert.Equal("Mill Creek", Assert.Single(ui.Store.GetMapBookmarks()).Name);

		// The database opened again: the bookmark is read back.
		ui.Services.Open(ui.Store.Database.FilePath);
		ui.Main.ShowCommand.Execute(Sde.App.ViewModels.Page.Map);
		ui.Pump();
		Assert.Equal("Mill Creek", Assert.Single(ui.Main.Map.Bookmarks).Name);

		// The flyout lists them, with every button named.
		DropDownButton button = ui.Window.GetVisualDescendants().OfType<DropDownButton>().Single(b => b.Name == "BookmarksButton");
		Flyout flyout = Assert.IsType<Flyout>(button.Flyout);
		flyout.ShowAt(button);
		ui.Pump();
		Control panel = Assert.IsAssignableFrom<Control>(flyout.Content);
		List<Button> buttons = panel.GetVisualDescendants().OfType<Button>().ToList();
		Assert.Contains(buttons, b => b.Content as string == "Mill Creek");
		Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(b).GetName())));

		// Return in the name box saves the view as well as Add.
		TextBox name = panel.GetVisualDescendants().OfType<TextBox>().Single();
		name.Focus();
		name.Text = "Ferry";
		Assert.Equal("Ferry", ui.Main.Map.NewBookmarkName);
		Assert.True(name.IsFocused);
		TopLevel.GetTopLevel(name)!.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(new[] { "Mill Creek", "Ferry" }, ui.Main.Map.Bookmarks.Select(b => b.Name));
		flyout.Hide();
	}

	/// <summary>
	/// The engine draws a big layer as tiles made in the background, so moving the map stays quick: 50,000 parcel-sized
	/// polygons, panned 40 frames at parcel scale in a 1440 by 900 window, with a median frame under 50 ms.
	/// </summary>
	[AvaloniaFact]
	public void FiftyThousandPolygons_PanQuickly()
	{
		using UiSession ui = new UiSession();
		MapView view = MapTestSupport.ShowMap(ui);
		FeatureLayer big = new FeatureLayer("Parcels", GeometryKind.Polygon);
		big.AddField("parcel_id", FieldType.Text);
		const double south = 39.60;
		const double west = -84.10;
		const double dLat = 0.000135; // about 15 m
		const double dLon = 0.000175;
		long id = 0;
		for (int row = 0; row < 200; row++)
		{
			for (int col = 0; col < 250; col++)
			{
				double y = south + row * dLat;
				double x = west + col * dLon;
				Feature f = big.AddFeature(GisSetup.Factory.CreatePolygon(new[]
				{
					new Coordinate(x, y), new Coordinate(x + dLon * 0.8, y), new Coordinate(x + dLon * 0.8, y + dLat * 0.8),
					new Coordinate(x, y + dLat * 0.8), new Coordinate(x, y),
				}), ++id);
				f["parcel_id"] = "P-" + id;
			}
		}
		Assert.Equal(50_000, big.Features.Count);
		Stopwatch build = Stopwatch.StartNew();
		view.Map.ShowLayers(new object[] { new MapFeatureLayer { Key = "big", Name = "Parcels", Data = big, Revision = 1 } });
		build.Stop();
		Assert.True(view.Map.IsRasterized("big"));

		double midLat = south + 100 * dLat;
		double midLon = west + 125 * dLon;
		view.Map.SetView(midLat, midLon, 17);
		Stopwatch settle = Stopwatch.StartNew();
		MapTestSupport.Settle(ui, view, 30000);
		settle.Stop();

		List<double> frames = new List<double>();
		for (int i = 0; i < 48; i++)
		{
			Stopwatch frame = Stopwatch.StartNew();
			view.Map.SetView(midLat + i * dLat * 0.6, midLon + i * dLon * 0.8, 17);
			using WriteableBitmap shot = MapTestSupport.Capture(ui);
			frame.Stop();
			if (i >= 8)
			{
				// The first frames warm the renderer up.
				frames.Add(frame.Elapsed.TotalMilliseconds);
			}
		}
		frames.Sort();
		double median = frames[frames.Count / 2];
		double p95 = frames[(int)(frames.Count * 0.95)];
		Console.WriteLine($"[frame-times] 50,000 polygons: layer built in {build.ElapsedMilliseconds} ms, first view settled in {settle.ElapsedMilliseconds} ms; pan frames median {median:0.0} ms, 95th percentile {p95:0.0} ms, slowest {frames[^1]:0.0} ms");
		File.WriteAllText(Path.Combine(Path.GetTempPath(), "sde-map-frame-times.txt"),
			$"build {build.ElapsedMilliseconds} ms; settle {settle.ElapsedMilliseconds} ms; median {median:0.0} ms; p95 {p95:0.0} ms; max {frames[^1]:0.0} ms");
		Assert.True(median < 50, $"Median pan frame took {median:0.0} ms.");

		// Clicking still finds a parcel.
		Point at = view.Map.ToScreen(midLat + 20.4 * dLat, midLon + 30.4 * dLon);
		Assert.NotNull(view.Map.FeatureAt(at));
	}

	[AvaloniaFact]
	public async Task ScreenshotsOfTheMapPage_LightAndDark_WithALayerAndFloodZones()
	{
		foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "56-map"), (ThemeVariant.Dark, "56b-map-dark") })
		{
			using UiSession ui = new UiSession(theme);
			MapTestSupport.SetUpPlaces(ui, Basemaps.UsgsTopo);
			MapView view = MapTestSupport.ShowMap(ui);
			MapViewModel map = ui.Main.Map;
			ui.Dialogs.Answers.Enqueue(new[] { MapLayersTests.Town("parcels.geojson") });
			await map.AddFileLayerCommand.ExecuteAsync(null);
			await map.LayersLoaded;
			MapLayerViewModel parcels = map.Layers.Single(l => l.IsFeatures);
			map.ApplyStyle(parcels, new LayerStyleSheetViewModel.Result(LayerStyle.Categorized(parcels.Data, "flood_zone"),
				new Sde.Gis.Styles.LabelStyle { Field = "house_no", Size = 10 }, null, null));
			map.AddFloodZonesCommand.Execute(null);
			map.PanelIndex = 1;
			map.ZoomToLayerCommand.Execute(parcels);
			map.SetModeCommand.Execute(MapMode.Select);
			map.SelectTarget = parcels;
			map.SelectFeatures(parcels.Key, parcels.Data.Features.Where(f => (string)f["flood_zone"] == "AE").Take(5).ToList(), add: false);
			MapTestSupport.Settle(ui, view);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			ui.Snapshot(name);
		}
	}
}
