using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Sde.Core.Records;
using Sde.Data;
using Sde.Gis.Analysis;
using Point = Avalonia.Point;

namespace Sde.App.Tests;

/// <summary>Moving a structure by dragging its dot in Set location: the question, the history, and the locks.</summary>
public class MapMoveTests
{
	private static (UiSession Ui, MapView View, MapPointViewModel River) SetLocationAtRiver(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		ui.Main.Map.SetModeCommand.Execute(MapMode.SetLocation);
		view.Map.SetView(39.1031, -84.5120, 18);
		ui.Pump();
		return (ui, view, ui.Main.Map.Visible.Single(p => p.Address.Contains("River ", StringComparison.Ordinal)));
	}

	[AvaloniaFact]
	public async Task DraggingADot_AsksWithTheDistanceAndDirection_ThenSavesTheNewPlaceToHistory()
	{
		(UiSession ui, MapView view, MapPointViewModel river) = SetLocationAtRiver();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Assert.True(view.Map.MarkersDraggable);
			Assert.Contains("Or drag any dot to where the structure really is.", map.Hint);
			Point dot = view.Map.ToScreen(river.Latitude, river.Longitude);
			Assert.True(view.Map.DragOnMap(dot, dot + new Point(30, -30)));
			ui.Pump();

			MoveStructureSheetViewModel sheet = Assert.IsType<MoveStructureSheetViewModel>(ui.Main.Sheets.Current);
			(double lat, double lon) = view.Map.ToLatLon(dot + new Point(30, -30));
			double feet = Measure.MetresToFeet(Measure.Distance(river.Longitude, river.Latitude, lon, lat));
			Assert.Equal($"Move {river.Address} about {Math.Round(feet):0} ft north-east?", sheet.Question);
			Assert.Equal("39.103100, -84.512000", sheet.FromText);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			sheet.MoveCommand.Execute(null);
			ui.Pump();

			PropertyRecord moved = ui.Store.GetProperty(river.Id);
			Assert.Equal(Math.Round((decimal)lat, 6), moved.Latitude);
			Assert.Equal(Math.Round((decimal)lon, 6), moved.Longitude);
			Assert.StartsWith($"Moved {river.Address} about ", map.Message);
			List<EditHistoryRecord> history = ui.Store.GetPropertyHistory(river.Id);
			EditHistoryRecord latitude = history.First(h => h.Field == "Property.Latitude");
			Assert.Equal(SaveVia.Map, latitude.Via);
			Assert.Equal("39.1031", latitude.OldValue);
			Assert.Equal("Reviewer A", latitude.ChangedBy);

			// The dot is drawn in the new place.
			MapPointViewModel now = map.Visible.Single(p => p.Id == river.Id);
			Assert.Equal(river.Id, view.Map.MarkerAt(view.Map.ToScreen(now.Latitude, now.Longitude)));

			// Cancel leaves it where it is.
			Point from = view.Map.ToScreen(now.Latitude, now.Longitude);
			view.Map.DragOnMap(from, from + new Point(-50, 0));
			ui.Pump();
			MoveStructureSheetViewModel again = Assert.IsType<MoveStructureSheetViewModel>(ui.Main.Sheets.Current);
			Assert.EndsWith(" west?", again.Question);
			again.Cancel();
			ui.Pump();
			Assert.Equal(moved.Latitude, ui.Store.GetProperty(river.Id).Latitude);
			Assert.Equal($"{river.Address} stays where it was.", map.Message);

			// A click on a dot (no drag) still picks it as the structure to place.
			view.Map.DragOnMap(from, from);
			ui.Pump();
			Assert.Null(ui.Main.Sheets.Current);
			Assert.Equal(river.Id, map.PlaceTarget?.Id);
			await Task.CompletedTask;
		}
	}

	[AvaloniaFact]
	public void FinalAndCheckedOutAssessments_CantBeMoved_AndSaySo()
	{
		(UiSession ui, MapView view, MapPointViewModel river) = SetLocationAtRiver();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Guid assessment = river.AssessmentID!.Value;
			ui.Store.SetStatus(assessment, AssessmentStatus.Final);
			ui.Services.RaiseDataChanged();
			Point dot = view.Map.ToScreen(river.Latitude, river.Longitude);
			view.Map.DragOnMap(dot, dot + new Point(40, 0));
			ui.Pump();
			Assert.Null(ui.Main.Sheets.Current);
			Assert.Equal($"{river.Address} can't be moved: its assessment is Final, which locks it. Reopen the assessment to change its location.", map.Message);
			Assert.Equal(39.1031m, ui.Store.GetProperty(river.Id).Latitude);

			// Clicking the map with it picked doesn't place it either.
			map.PlaceTarget = map.Visible.Single(p => p.Id == river.Id);
			view.Map.Click(view.Map.ToScreen(39.1040, -84.5100));
			Assert.Equal(39.1031m, ui.Store.GetProperty(river.Id).Latitude);
			Assert.StartsWith($"{river.Address} can't be moved", map.Message);

			// Checked out to a work package.
			MapPointViewModel mill = map.Visible.Single(p => p.Address.Contains("Mill Creek", StringComparison.Ordinal));
			WorkPackages.CheckOut(ui.Store, new[] { mill.AssessmentID!.Value }, Path.Combine(ui.Folder, "trip.sdework"), "Team 2");
			ui.Services.RaiseDataChanged();
			view.Map.SetView(mill.Latitude, mill.Longitude, 18);
			ui.Pump();
			Point millDot = view.Map.ToScreen(mill.Latitude, mill.Longitude);
			view.Map.DragOnMap(millDot, millDot + new Point(0, 40));
			ui.Pump();
			Assert.Null(ui.Main.Sheets.Current);
			Assert.Equal($"{mill.Address} can't be moved: its assessment is checked out to Team 2 in a work package. Check the package back in, or take the assessment back, first.", map.Message);
		}
	}

	[Fact]
	public void DistancesAndDirections_ReadAsAPersonWouldSayThem()
	{
		Assert.Equal("about 40 ft", MapViewModel.DistanceInWords(12.2));
		Assert.Equal("about 330 ft", MapViewModel.DistanceInWords(100));
		Assert.Equal("about 1.0 mi", MapViewModel.DistanceInWords(1609.344));
		Assert.Equal("less than a foot", MapViewModel.DistanceInWords(0.1));
		Assert.Equal("north", MapViewModel.Direction(39, -84, new LatLon(39.001, -84)));
		Assert.Equal("north-east", MapViewModel.Direction(39, -84, new LatLon(39.001, -83.9987)));
		Assert.Equal("south-west", MapViewModel.Direction(39, -84, new LatLon(38.999, -84.0013)));
		Assert.Equal("west", MapViewModel.Direction(39, -84, new LatLon(39, -84.01)));
	}

	[AvaloniaFact]
	public void ScreenshotsOfTheMoveQuestion_LightAndDark()
	{
		foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "56g-map-move"), (ThemeVariant.Dark, "56g-map-move-dark") })
		{
			(UiSession ui, MapView view, MapPointViewModel river) = SetLocationAtRiver(theme);
			using (ui)
			{
				ui.Main.Map.Basemap = Basemaps.UsgsTopo;
				// The dot below and to the left of the question.
				view.Map.SetView(39.1041, -84.5089, 17);
				ui.Pump();
				Point dot = view.Map.ToScreen(river.Latitude, river.Longitude);
				view.Map.DragOnMap(dot, dot + new Point(46, -34));
				MapTestSupport.Settle(ui, view);
				Assert.IsType<MoveStructureSheetViewModel>(ui.Main.Sheets.Current);
				Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
				ui.Snapshot(name);
			}
		}
	}
}
