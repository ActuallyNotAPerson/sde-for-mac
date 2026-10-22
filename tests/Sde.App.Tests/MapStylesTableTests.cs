using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Sde.Data;
using Sde.Gis.Features;
using Sde.Gis.Styles;
using AColor = Avalonia.Media.Color;
using Point = Avalonia.Point;

namespace Sde.App.Tests;

/// <summary>Layer styles (one colour, categories, number ranges, labels, scale range), the structures' colouring, the attribute table and the identify card.</summary>
public class MapStylesTableTests
{
	internal static async Task<(UiSession Ui, MapView View, MapLayerViewModel Parcels)> OpenWithParcels()
	{
		UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		ui.Dialogs.Answers.Enqueue(new[] { MapLayersTests.Town("parcels.geojson") });
		await ui.Main.Map.AddFileLayerCommand.ExecuteAsync(null);
		ui.Pump();
		MapLayerViewModel parcels = ui.Main.Map.Layers.Single(l => l.IsFeatures);
		return (ui, view, parcels);
	}

	private static Task _styling;

	private static Task<LayerStyleSheetViewModel> OpenStyle(UiSession ui, MapLayerViewModel layer)
	{
		_styling = ui.Main.Map.StyleLayerCommand.ExecuteAsync(layer);
		ui.Pump();
		return Task.FromResult(Assert.IsType<LayerStyleSheetViewModel>(ui.Main.Sheets.Current));
	}

	/// <summary>Presses Apply and waits for the page to take the new style.</summary>
	private static async Task Apply(UiSession ui, LayerStyleSheetViewModel sheet)
	{
		sheet.ApplyCommand.Execute(null);
		await _styling;
		ui.Pump();
	}

	internal static Point Middle(MapView view, Feature f)
	{
		Envelope e = f.Geometry.EnvelopeInternal;
		return view.Map.ToScreen(e.Centre.Y, e.Centre.X);
	}

	[AvaloniaFact]
	public async Task ByCategory_ColoursEachFloodZone_HidesAnUntickedOne_AndIsSaved()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await OpenWithParcels();
		using (ui)
		{
			LayerStyleSheetViewModel sheet = await OpenStyle(ui, parcels);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			sheet.KindIndex = 1;
			sheet.CategoryField = sheet.Fields.Single(f => f.Key == "flood_zone");
			Assert.Equal(new[] { "A", "AE", "X" }, sheet.Categories.Select(c => c.Label));
			sheet.Categories.Single(c => c.Label == "X").IsShown = false;
			ui.Pump();
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			await Apply(ui, sheet);
			Assert.Null(ui.Main.Sheets.Current);

			Assert.Equal(StyleKind.Categorized, parcels.Style.Kind);
			Assert.Equal(new[] { "A", "AE" }, parcels.Legend.Select(l => l.Label));
			LayerStyle saved = LayerStyle.FromJson(ui.Store.GetMapLayers().Single(r => r.LayerID == parcels.Id).Style);
			Assert.Equal("flood_zone", saved.Field);
			Assert.False(saved.Categories.Single(c => c.Value == "X").Visible);

			// Zone X parcels are no longer drawn (or clickable); zone AE ones are, in their category's colour.
			Feature x = parcels.Data.Features.First(f => (string)f["flood_zone"] == "X");
			Feature ae = parcels.Data.Features.First(f => (string)f["flood_zone"] == "AE");
			view.Map.FitTo(new MapExtent(-84.051, 39.619, -84.044, 39.624));
			ui.Pump();
			Assert.Null(view.Map.FeatureAt(Middle(view, x)));
			Assert.Equal(ae.Id, view.Map.FeatureAt(Middle(view, ae))?.Feature.Id);
			WriteableBitmap frame = MapTestSupport.Capture(ui);
			Point inAe = MapTestSupport.InWindow(ui, view, Middle(view, ae));
			Point inX = MapTestSupport.InWindow(ui, view, Middle(view, x));
			Assert.True(MapTestSupport.Near(MapTestSupport.At(frame, inX.X, inX.Y), MapTestSupport.ThemeColour("SubtleBg"), 3), "zone X is drawn");
			Assert.False(MapTestSupport.Near(MapTestSupport.At(frame, inAe.X, inAe.Y), MapTestSupport.ThemeColour("SubtleBg"), 3), "zone AE isn't drawn");
		}
	}

	[AvaloniaFact]
	public async Task ByNumberRange_MakesEqualIntervalsAlongARamp()
	{
		(UiSession ui, MapView _, MapLayerViewModel parcels) = await OpenWithParcels();
		using (ui)
		{
			LayerStyleSheetViewModel sheet = await OpenStyle(ui, parcels);
			sheet.KindIndex = 2;
			Assert.DoesNotContain(sheet.NumberFields, f => f.Key == "street");
			sheet.RangeField = sheet.NumberFields.Single(f => f.Key == "land_value");
			sheet.ClassCount = 4;
			sheet.Ramp = "Reds";
			Assert.Equal(4, sheet.RangeLegend.Count);
			Assert.Equal("21,500 to 32,375", sheet.RangeLegend[0].Label);
			await Apply(ui, sheet);
			Assert.Equal(StyleKind.Graduated, parcels.Style.Kind);
			Assert.Equal("Reds", parcels.Style.Ramp);
			Assert.Equal(4, parcels.Style.Classes.Count);
			Assert.Equal(4, parcels.Legend.Count);
		}
	}

	[AvaloniaFact]
	public async Task Labels_HaveAHalo_AndAScaleRange_AndTheLayerHasOneToo()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await OpenWithParcels();
		using (ui)
		{
			LayerStyleSheetViewModel sheet = await OpenStyle(ui, parcels);
			sheet.ShowLabels = true;
			sheet.LabelField = sheet.Fields.Single(f => f.Key == "parcel_id");
			sheet.LabelMinScale = 6000;
			sheet.MinScale = 40000;
			ui.Pump();
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			await Apply(ui, sheet);
			MapLayerRecord row = ui.Store.GetMapLayers().Single(r => r.LayerID == parcels.Id);
			Assert.Equal(40000, row.MinScale);
			Sde.Gis.Styles.LabelStyle labels = Sde.Gis.Styles.LabelStyle.FromJson(row.Labels);
			Assert.Equal("parcel_id", labels.Field);
			Assert.True(labels.Halo);
			Assert.Equal(6000, labels.MinScale);

			Feature first = parcels.Data.Features[0];
			Envelope e = first.Geometry.EnvelopeInternal;
			int Ink(double zoom)
			{
				view.Map.SetView(e.Centre.Y, e.Centre.X, zoom);
				ui.Pump();
				WriteableBitmap frame = MapTestSupport.Capture(ui);
				Point p = MapTestSupport.InWindow(ui, view, view.Map.ToScreen(e.Centre.Y, e.Centre.X));
				int dark = 0;
				for (double y = p.Y - 8; y < p.Y + 8; y++)
				{
					for (double x = p.X - 30; x < p.X + 30; x++)
					{
						// The label's letters and their edges: Linux (FreeType) draws text lighter than macOS does, so a
						// near-black cut-off finds only about 20 pixels of a short label there.
						AColor c = MapTestSupport.At(frame, x, y);
						dark += c.R < 110 && c.G < 110 && c.B < 110 ? 1 : 0;
					}
				}
				return dark;
			}
			Assert.True(Ink(18) > 20, "no label at zoom 18");
			Assert.Equal(0, Ink(15.5));
			// Further out than 1:40,000 the layer itself hides: nothing to click.
			view.Map.SetView(e.Centre.Y, e.Centre.X, 13);
			ui.Pump();
			Assert.True(view.Map.Scale > 40000);
			Assert.Null(view.Map.FeatureAt(view.Map.ToScreen(e.Centre.Y, e.Centre.X)));
			view.Map.SetView(e.Centre.Y, e.Centre.X, 16);
			ui.Pump();
			Assert.Equal(first.Id, view.Map.FeatureAt(view.Map.ToScreen(e.Centre.Y, e.Centre.X))?.Feature.Id);
		}
	}

	[AvaloniaFact]
	public async Task TheStructures_CanBeColouredByStatus_OrAnyField_AndBackByResult()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		MapPointViewModel river = map.Visible.Single(p => p.Address.Contains("River"));
		ui.Store.SetStatus(river.AssessmentID!.Value, AssessmentStatus.Reviewed);
		map.Reload();

		LayerStyleSheetViewModel sheet = await OpenStyle(ui, map.StructuresLayer);
		Assert.Equal(new[] { "By result", "By category", "By number range" }, sheet.Kinds);
		Assert.True(sheet.IsByResult);
		sheet.KindIndex = 1;
		sheet.CategoryField = sheet.Fields.Single(f => f.Key == "Status");
		Assert.Equal(new[] { "Draft", "Reviewed" }, sheet.Categories.Select(c => c.Label));
		await Apply(ui, sheet);

		Assert.False(map.ColoursByResult);
		Assert.Equal("COLOURED BY STATUS", map.StructureLegendTitle);
		Assert.Equal(new[] { "Draft", "Reviewed" }, map.StructureLegend.Select(l => l.Label));
		Assert.Equal("Coloured by status", map.StructuresLayer.Detail);
		IReadOnlyDictionary<Guid, string> fills = map.StructureFills();
		string reviewed = map.StructuresLayer.Style.Categories.Single(c => c.Value == "Reviewed").Symbol.Fill;
		Assert.Equal(reviewed, fills[river.Id]);
		MapMarker marker = view.Map.Markers.Single(m => m.Id == river.Id);
		Assert.Equal(LegendLineViewModel.Brush(reviewed) is Avalonia.Media.ISolidColorBrush b ? b.Color : default, ((Avalonia.Media.ISolidColorBrush)marker.Fill).Color);

		// Any assessment field: the inspector's name, read from the assessments.
		sheet = await OpenStyle(ui, map.StructuresLayer);
		sheet.KindIndex = 1;
		sheet.CategoryField = sheet.Fields.Single(f => f.Key == "Assessment.InspectorName");
		Assert.NotEmpty(sheet.Categories);
		await Apply(ui, sheet);
		Assert.Contains("inspector", map.StructuresLayer.Detail);

		// Back to the result colours of the theme.
		sheet = await OpenStyle(ui, map.StructuresLayer);
		sheet.KindIndex = 0;
		await Apply(ui, sheet);
		Assert.True(map.ColoursByResult);
		Assert.False(map.HasStructureLegend);
		Assert.Equal(MapTestSupport.ThemeColour("Status" + river.Class + "Fg"), ((Avalonia.Media.ISolidColorBrush)view.Map.Markers.Single(m => m.Id == river.Id).Fill).Color);
	}

	[AvaloniaFact]
	public async Task TheTable_ShowsALayersRows_Filters_SortsByValue_AndShowsTheSelection()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await OpenWithParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.ShowTableCommand.Execute(parcels);
			ui.Pump();
			Assert.True(map.IsTableOpen);
			Assert.Equal(parcels.Data.Fields.Count, map.TableColumns.Count);
			Assert.Equal(30, map.TableRows.Count);
			Assert.Equal("30 rows", map.TableCountText);
			DataGrid grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
			Assert.Equal(parcels.Data.Fields.Count, grid.Columns.Count);
			Assert.Equal("parcel_id", grid.Columns[0].Header);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

			map.TableFilter = "mill race";
			Assert.Equal(6, map.TableRows.Count);
			Assert.Equal("6 of 30 rows", map.TableCountText);
			map.TableFilter = null;

			// Numbers sort as numbers: 9 before 10.
			int column = parcels.Data.Fields.IndexOf("land_value");
			List<AttributeRowViewModel> sorted = map.TableRows.ToList();
			sorted.Sort(new AttributeComparer(column).Compare);
			Assert.Equal(parcels.Data.Features.Min(f => (double)f["land_value"]), (double)sorted[0].Feature["land_value"]);
			Assert.Equal(parcels.Data.Features.Max(f => (double)f["land_value"]), (double)sorted[^1].Feature["land_value"]);

			// Selecting in the map, then "Selected only".
			map.SelectFeatures(parcels.Key, parcels.Data.Features.Take(3).ToList(), add: false);
			map.TableSelectedOnly = true;
			Assert.Equal(3, map.TableRows.Count);
			// Rows picked in the table become the map's selection.
			map.TableSelectedOnly = false;
			map.SelectTableRows(map.TableRows.Skip(10).Take(2));
			Assert.Equal(new[] { parcels.Data.Features[10].Id, parcels.Data.Features[11].Id }, parcels.Selected.OrderBy(i => i));
			ui.Snapshot("56d-map-table");
		}
	}

	[AvaloniaFact]
	public void TheStructuresTable_ListsTheirDetails_AndPickingRowsChecksThem()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		map.ShowTableCommand.Execute(map.StructuresLayer);
		ui.Pump();
		Assert.Equal(new[] { "Address", "City", "Result", "Percent damaged", "Status", "Team", "Event", "Latitude", "Longitude" }, map.TableColumns.Select(c => c.Header));
		Assert.Equal(2, map.TableRows.Count);
		AttributeRowViewModel river = map.TableRows.Single(r => r[0].Contains("River"));
		Assert.Equal("Substantially damaged", river[2]);
		Assert.Equal("Draft", river[4]);
		map.SelectTableRows(new[] { river });
		Assert.Equal(1, ui.Main.Structures.CheckedCount);
		Assert.Equal(map.StructureOf(river.Id).Id, Assert.Single(map.SelectedStructures()));
	}

	[AvaloniaFact]
	public async Task ClickingAFeature_InOpenMode_ShowsItsValues_AndCanShowItInTheTable()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await OpenWithParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Feature first = parcels.Data.Features[0];
			view.Map.FitTo(new MapExtent(-84.051, 39.619, -84.044, 39.624));
			ui.Pump();
			view.Map.Click(Middle(view, first));
			ui.Pump();
			Assert.True(map.HasIdentified);
			Assert.Equal("parcels", map.Identified.LayerName);
			Assert.Equal("CF-0001", map.Identified.Title);
			Assert.Contains(map.Identified.Lines, l => l.Field == "street" && l.Value == "Mill Race Road");
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

			map.ShowIdentifiedInTableCommand.Execute(null);
			Assert.True(map.IsTableOpen);
			Assert.Same(parcels, map.TableLayer);
			Assert.Equal(new[] { first.Id }, parcels.Selected);

			// In Select mode the same click selects instead; Command adds.
			map.SetModeCommand.Execute(MapMode.Select);
			Feature second = parcels.Data.Features[1];
			view.Map.Click(Middle(view, second), add: true);
			Assert.Equal(new[] { first.Id, second.Id }, parcels.Selected.OrderBy(i => i));
			map.CloseIdentifyCommand.Execute(null);
			Assert.False(map.HasIdentified);
		}
	}
}
