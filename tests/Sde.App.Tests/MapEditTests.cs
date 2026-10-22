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
using Sde.Gis.Editing;
using Sde.Gis.Features;
using Sde.Gis.Formats;
using Page = Sde.App.ViewModels.Page;
using Point = Avalonia.Point;

namespace Sde.App.Tests;

/// <summary>
/// Edit mode on the Map page: picking a shape, dragging, adding and taking away corners, moving and deleting shapes,
/// snapping, undo and redo, values typed in the table, new layers, Save and Discard, and the question when leaving.
/// </summary>
public class MapEditTests
{
	/// <summary>The parcels layer in Edit mode, zoomed in to its south-west corner.</summary>
	internal static async Task<(UiSession Ui, MapView View, MapLayerViewModel Parcels)> EditParcels(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		ui.Dialogs.Answers.Enqueue(new[] { MapLayersTests.Town("parcels.geojson") });
		await ui.Main.Map.AddFileLayerCommand.ExecuteAsync(null);
		ui.Pump();
		MapLayerViewModel parcels = ui.Main.Map.Layers.Single(l => l.IsFeatures);
		await ui.Main.Map.SetModeCommand.ExecuteAsync(MapMode.Edit);
		view.Map.FitTo(new MapExtent(-84.0502, 39.6198, -84.0486, 39.6208), maxZoom: 19, margin: 40);
		ui.Pump();
		return (ui, view, parcels);
	}

	private static Feature Parcel(MapLayerViewModel parcels, string id) => parcels.Data.Features.Single(f => (string)f["parcel_id"] == id);

	private static Point Middle(MapView view, Geometry g)
	{
		Coordinate c = g.InteriorPoint.Coordinate;
		return view.Map.ToScreen(c.Y, c.X);
	}

	[AvaloniaFact]
	public async Task OnlyLayersFromFiles_CanBeEdited_AndTheBarShowsTheirTools()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Assert.True(map.IsEdit);
			Assert.Same(parcels, map.EditLayer);
			Assert.Equal(parcels.Key, view.Map.EditLayer);
			Assert.Equal(new[] { "Change", "Add areas" }, map.EditTools.Select(t => t.Label));
			Assert.Equal("Click a shape to change it.", map.Hint);
			Assert.Equal("No unsaved changes", map.EditStatus);
			Assert.False(map.SaveEditsCommand.CanExecute(null));
			Border bar = ui.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "EditBar");
			Assert.True(bar.IsEffectivelyVisible);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

			// Online layers and FEMA's flood zones are read only.
			FeatureLayer online = new FeatureLayer("county parcels", GeometryKind.Polygon);
			online.AddFeature(GisSetup.Factory.CreatePolygon(new[] { new Coordinate(-84.04, 39.62), new Coordinate(-84.039, 39.62), new Coordinate(-84.039, 39.621), new Coordinate(-84.04, 39.62) }), 1);
			MapLayerViewModel arcgis = map.AddOnline(online, "https://example.test/arcgis/rest/services/Parcels/FeatureServer/0", null);
			map.AddFloodZonesCommand.Execute(null);
			Assert.False(arcgis.CanEdit);
			Assert.Equal(new[] { parcels }, map.EditableLayers);

			// Leaving Edit mode with nothing changed asks nothing.
			await map.SetModeCommand.ExecuteAsync(MapMode.Browse);
			Assert.Null(ui.Main.Sheets.Current);
			Assert.Null(view.Map.EditLayer);
		}
	}

	[AvaloniaFact]
	public async Task Corners_AreDraggedAddedAndTakenAway_AndShapesMoved_WithUndoAndRedo()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Feature first = Parcel(parcels, "CF-0001");
			view.Map.Click(Middle(view, first.Geometry));
			ui.Pump();
			Assert.Equal(first.Id, map.EditFeature?.Id);
			Assert.Same(map.EditFeature, view.Map.EditFeature);
			Assert.Contains("Drag a corner", map.Hint);

			// Drag the north-east corner 30 px right and 20 px down.
			VertexRef northEast = new VertexRef(0, 0, 2);
			Point from = view.Map.CornerOnScreen(northEast);
			Assert.True(view.Map.DragOnMap(from, from + new Point(30, 20)));
			ui.Pump();
			Assert.Equal(1, map.Session.ChangeCount);
			Assert.Equal("1 unsaved change", map.EditStatus);
			Assert.Equal("Undo move corner", map.UndoEditText);
			Point now = view.Map.CornerOnScreen(northEast);
			Assert.Equal(from.X + 30, now.X, 1);
			Assert.Equal(from.Y + 20, now.Y, 1);
			// The other corners didn't move by a hair.
			Assert.True(GeometryEdit.VertexAt(map.EditFeature.Geometry, new VertexRef(0, 0, 0)).Equals2D(GeometryEdit.VertexAt(first.Geometry, new VertexRef(0, 0, 0))));
			// The layer itself is unchanged until Save.
			Assert.Equal(5, first.Geometry.NumPoints);
			Assert.Equal("POLYGON ((-84.05 39.62, -84.0496 39.62, -84.0496 39.6203, -84.05 39.6203, -84.05 39.62))", first.Geometry.ToText());

			// A middle dot dragged adds a corner; Option-click takes it away again.
			Point middle = view.Map.MiddleOnScreen(new VertexRef(0, 0, 0));
			Assert.True(view.Map.DragOnMap(middle, middle + new Point(0, 25)));
			Assert.Equal(5, GeometryEdit.Vertices(map.EditFeature.Geometry).Count);
			Assert.Equal("Undo add corner", map.UndoEditText);
			Point added = view.Map.CornerOnScreen(new VertexRef(0, 0, 1));
			Assert.True(view.Map.DragOnMap(added, added, KeyModifiers.Alt));
			Assert.Equal(4, GeometryEdit.Vertices(map.EditFeature.Geometry).Count);
			Assert.Equal("Undo delete corner", map.UndoEditText);

			// A triangle's corner can't go: it says why.
			Assert.True(view.Map.DragOnMap(view.Map.CornerOnScreen(new VertexRef(0, 0, 0)), view.Map.CornerOnScreen(new VertexRef(0, 0, 0)), KeyModifiers.Alt));
			Assert.True(view.Map.DragOnMap(view.Map.CornerOnScreen(new VertexRef(0, 0, 0)), view.Map.CornerOnScreen(new VertexRef(0, 0, 0)), KeyModifiers.Alt));
			Assert.Equal(3, GeometryEdit.Vertices(map.EditFeature.Geometry).Count);
			Assert.True(view.Map.DragOnMap(view.Map.CornerOnScreen(new VertexRef(0, 0, 0)), view.Map.CornerOnScreen(new VertexRef(0, 0, 0)), KeyModifiers.Alt));
			Assert.Equal(3, GeometryEdit.Vertices(map.EditFeature.Geometry).Count);
			Assert.StartsWith("A line needs at least two corners and an area three.", map.Message);

			// Undo three times: back to the first drag; redo once.
			for (int i = 0; i < 3; i++)
			{
				map.UndoEditCommand.Execute(null);
			}
			Assert.Equal(4, GeometryEdit.Vertices(map.EditFeature.Geometry).Count);
			Assert.Equal(now.X, view.Map.CornerOnScreen(northEast).X, 1);
			map.RedoEditCommand.Execute(null);
			Assert.Equal(5, GeometryEdit.Vertices(map.EditFeature.Geometry).Count);
			Assert.Equal("Redid: add corner.", map.Message);
			map.UndoEditCommand.Execute(null);

			// Drag inside the shape: the whole shape moves.
			Envelope before = map.EditFeature.Geometry.EnvelopeInternal;
			Point inside = Middle(view, map.EditFeature.Geometry);
			Assert.True(view.Map.DragOnMap(inside, inside + new Point(-40, 0)));
			Assert.True(map.EditFeature.Geometry.EnvelopeInternal.MinX < before.MinX - 1e-5);
			Assert.Equal("Undo move shape", map.UndoEditText);

			// A press that doesn't move picks a corner; Delete then takes that corner away.
			Point corner = view.Map.CornerOnScreen(new VertexRef(0, 0, 3));
			Assert.True(view.Map.DragOnMap(corner, corner));
			Assert.Equal(new VertexRef(0, 0, 3), view.Map.SelectedVertex);
			view.Map.Focus();
			ui.Window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
			Assert.Equal(3, GeometryEdit.Vertices(map.EditFeature.Geometry).Count);

			// With no corner picked, Delete removes the shape (and Undo brings it back).
			ui.Window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
			Assert.Null(map.EditFeature);
			Assert.Null(map.Session.Find(first.Id));
			Assert.Null(view.Map.FeatureAt(Middle(view, first.Geometry), parcels.Key)?.Feature);
			map.UndoEditCommand.Execute(null);
			Assert.NotNull(map.Session.Find(first.Id));
			Assert.Equal(1, map.Session.ChangeCount);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		}
	}

	[AvaloniaFact]
	public async Task ADraggedCorner_SnapsToTheNeighboursCorner_UnlessSnappingIsOff()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Feature first = Parcel(parcels, "CF-0001");
			Feature second = Parcel(parcels, "CF-0002");
			view.Map.Click(Middle(view, first.Geometry));
			ui.Pump();
			// CF-0002's south-east corner, (-84.0492, 39.62).
			Coordinate target = new Coordinate(-84.0492, 39.62);
			Assert.Contains(GeometryEdit.Vertices(second.Geometry), v => v.At.Equals2D(target));
			Point near = view.Map.ToScreen(target.Y, target.X) + new Point(4, -3);
			Assert.True(view.Map.DragOnMap(view.Map.CornerOnScreen(new VertexRef(0, 0, 1)), near));
			Assert.True(GeometryEdit.VertexAt(map.EditFeature.Geometry, new VertexRef(0, 0, 1)).Equals2D(target));

			map.UndoEditCommand.Execute(null);
			map.SnapToShapes = false;
			ui.Pump();
			Assert.False(view.Map.Snap);
			Assert.True(view.Map.DragOnMap(view.Map.CornerOnScreen(new VertexRef(0, 0, 1)), near));
			Assert.False(GeometryEdit.VertexAt(map.EditFeature.Geometry, new VertexRef(0, 0, 1)).Equals2D(target));
		}
	}

	[AvaloniaFact]
	public async Task Saving_WritesTheEditsToTheLayersFile_AndTheMapDrawsThem()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Feature first = Parcel(parcels, "CF-0001");
			view.Map.Click(Middle(view, first.Geometry));
			Point inside = Middle(view, first.Geometry);
			view.Map.DragOnMap(inside, inside + new Point(0, 60));
			Feature second = Parcel(parcels, "CF-0002");
			view.Map.Click(Middle(view, second.Geometry));
			map.DeleteEditFeatureCommand.Execute(null);
			ui.Pump();
			Assert.Equal(2, map.Session.ChangeCount);

			// The map draws the edits before they are saved: the old place is empty, the new place hits the moved parcel.
			Assert.Null(view.Map.FeatureAt(Middle(view, second.Geometry), parcels.Key));
			Assert.Equal(first.Id, view.Map.FeatureAt(inside + new Point(0, 60), parcels.Key)?.Feature.Id);

			map.SaveEditsCommand.Execute(null);
			ui.Pump();
			Assert.Equal("Saved 2 changes to parcels.", map.Message);
			Assert.False(map.HasUnsavedEdits);
			Assert.Equal(29, parcels.Data.Features.Count);
			LayersStore store = LayersStore.Open(ui.Store.Database.LayersPath, ui.Store.Database.DatabaseId);
			FeatureLayer saved = store.ReadTable(parcels.Record.TableName);
			Assert.Equal(29, saved.Features.Count);
			Assert.DoesNotContain(saved.Features, f => (string)f["parcel_id"] == "CF-0002");
			Assert.True(saved.Features.Single(f => (string)f["parcel_id"] == "CF-0001").Geometry.EnvelopeInternal.MaxY < 39.6203 - 1e-5);
			Assert.Equal(first.Id, view.Map.FeatureAt(inside + new Point(0, 60), parcels.Key)?.Feature.Id);
			Assert.Equal("29 areas, from parcels.geojson", parcels.Detail);
		}
	}

	[AvaloniaFact]
	public async Task TypingInTheTable_ChecksTheFieldsType_AndOnlyWhileEditing()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.ShowTableCommand.Execute(parcels);
			ui.Pump();
			Assert.True(map.TableEditable);
			DataGrid grid = ui.Window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "TableGrid");
			Assert.False(grid.IsReadOnly);
			Assert.All(grid.Columns, c => Assert.False(c.IsReadOnly));
			int value = map.TableColumns.Single(c => c.Header == "land_value").Index;
			int idColumn = map.TableColumns.Single(c => c.Header == "parcel_id").Index;
			AttributeRowViewModel row = map.TableRows.Single(r => r[idColumn] == "CF-0003");
			string before = row[value];

			row[value] = "lots";
			Assert.Equal("land_value needs a number, not \"lots\".", map.Message);
			Assert.Equal(before, row[value]);
			Assert.False(map.HasUnsavedEdits);

			row[value] = "45,000";
			Assert.Equal("45000", row[value]);
			Assert.Equal(45000.0, map.Session.Find(row.Id)["land_value"]);
			Assert.Equal("1 unsaved change", map.EditStatus);
			Assert.Equal("Undo change land_value", map.UndoEditText);
			map.UndoEditCommand.Execute(null);
			Assert.Equal(before, map.TableRows.Single(r => r[idColumn] == "CF-0003")[value]);

			// Out of Edit mode the table is read only again.
			await map.SetModeCommand.ExecuteAsync(MapMode.Browse);
			ui.Pump();
			Assert.False(map.TableEditable);
			Assert.True(grid.IsReadOnly);
			map.TableRows.Single(r => r[idColumn] == "CF-0003")[value] = "1";
			Assert.Equal(before, map.TableRows.Single(r => r[idColumn] == "CF-0003")[value]);
		}
	}

	[AvaloniaFact]
	public async Task ANewLayer_IsDrawnOnWithEachAddTool_AndSaved()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		await map.SetModeCommand.ExecuteAsync(MapMode.Edit);
		Assert.Null(map.EditLayer);
		Assert.StartsWith("Add a layer from a file, or make a new layer", map.Hint);

		Task making = map.NewLayerCommand.ExecuteAsync(null);
		ui.Pump();
		NewLayerSheetViewModel sheet = Assert.IsType<NewLayerSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("Sketch", sheet.LayerName);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.LayerName = " ";
		sheet.CreateCommand.Execute(null);
		Assert.Equal("Give the layer a name.", sheet.Problem);
		sheet.LayerName = "Debris piles";
		sheet.KindIndex = 0;
		sheet.CreateCommand.Execute(null);
		await making;
		ui.Pump();

		MapLayerViewModel debris = Assert.Single(map.Layers, l => l.Name == "Debris piles");
		Assert.Same(debris, map.EditLayer);
		Assert.Equal(MapEditTool.AddPoint, map.EditTool);
		Assert.Equal("0 points, drawn in SDE", debris.Detail);
		Assert.Equal(new[] { "Change", "Add points" }, map.EditTools.Select(t => t.Label));
		view.Map.SetView(39.1065, -84.5060, 17);
		ui.Pump();

		// A click adds a point; one near a structure snaps onto its dot.
		view.Map.Click(view.Map.ToScreen(39.1070, -84.5080));
		Point nearRiver = view.Map.ToScreen(39.1031, -84.5120) + new Point(3, 3);
		view.Map.Click(nearRiver);
		Assert.Equal(2, map.Session.Working.Features.Count);
		Coordinate onRiver = map.Session.Working.Features[1].Geometry.Coordinate;
		Assert.Equal(-84.5120, onRiver.X, 9);
		Assert.Equal(39.1031, onRiver.Y, 9);
		Assert.Equal("Added. Open the attribute table to type its values, or keep adding.", map.Message);

		// Type a name for it in the table.
		map.ShowTableCommand.Execute(debris);
		AttributeRowViewModel row = map.TableRows.Single(r => r.Id == map.Session.Working.Features[1].Id);
		row[0] = "Pile by River St";
		Assert.Equal("Pile by River St", map.Session.Working.Features[1]["name"]);

		map.SaveEditsCommand.Execute(null);
		Assert.Equal("Saved 2 changes to Debris piles.", map.Message);
		LayersStore store = LayersStore.Open(ui.Store.Database.LayersPath, ui.Store.Database.DatabaseId);
		FeatureLayer saved = store.ReadTable(debris.Record.TableName);
		Assert.Equal(2, saved.Features.Count);
		Assert.Equal("Pile by River St", saved.Features.Single(f => f["name"] != null)["name"]);
		Assert.All(saved.Features, f => Assert.True(f.Id > 0));
		MapLayerRecord record = ui.Store.GetMapLayers().Single(r => r.LayerID == debris.Id);
		Assert.Equal("Point", record.GeometryType);
		Assert.Equal(MapLayerKinds.File, record.SourceKind);

		// The same database opened again: the layer and its points are read back, and it can be edited.
		ui.Services.Open(ui.Store.Database.FilePath);
		ui.Main.ShowCommand.Execute(Page.Map);
		await ui.Main.Map.LayersLoaded;
		ui.Pump();
		MapLayerViewModel back = Assert.Single(ui.Main.Map.Layers, l => l.Name == "Debris piles");
		Assert.Equal(2, back.Data.Features.Count);
		Assert.True(back.CanEdit);
	}

	[AvaloniaFact]
	public async Task LinesAndAreas_AreDrawnCornerByCorner()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.SelectedEditTool = map.EditTools.Single(t => t.Tool == MapEditTool.AddArea);
			ui.Pump();
			Assert.Equal(MapSketch.Polygon, view.Map.Sketch);
			Assert.Null(view.Map.EditFeature);

			// Corners that snap onto CF-0001's corners make an area with the same outline.
			Feature first = Parcel(parcels, "CF-0001");
			foreach ((VertexRef _, Coordinate c) in GeometryEdit.Vertices(first.Geometry))
			{
				view.Map.Click(view.Map.ToScreen(c.Y, c.X) + new Point(2, -2));
			}
			view.Map.Focus();
			ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
			ui.Pump();
			Feature drawn = map.Session.Working.Features[^1];
			Assert.True(drawn.Id < 0);
			Assert.True(drawn.Geometry.EqualsTopologically(first.Geometry));
			Assert.Empty(view.Map.SketchPoints);

			// Two corners aren't an area.
			view.Map.Click(view.Map.ToScreen(39.6205, -84.0490));
			view.Map.Click(view.Map.ToScreen(39.6205, -84.0488));
			ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
			Assert.Equal("An area needs at least three corners.", map.Message);
			Assert.Equal(1, map.Session.ChangeCount);
		}
	}

	[AvaloniaFact]
	public async Task LeavingWithUnsavedEdits_AsksToSaveThrowAwayOrStay()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Feature first = Parcel(parcels, "CF-0001");
			view.Map.Click(Middle(view, first.Geometry));
			map.DeleteEditFeatureCommand.Execute(null);
			Assert.True(map.HasUnsavedEdits);

			// Another mode: Cancel stays in Edit with the edit kept.
			Task leaving = map.SetModeCommand.ExecuteAsync(MapMode.Browse);
			ui.Pump();
			SaveEditsSheetViewModel question = Assert.IsType<SaveEditsSheetViewModel>(ui.Main.Sheets.Current);
			Assert.Equal("Save your edits to parcels?", question.Title);
			Assert.Equal("1 change to parcels isn't saved yet. Save them to the map layers file, or throw them away.", question.Message);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			question.Cancel();
			await leaving;
			Assert.True(map.IsEdit);
			Assert.True(map.HasUnsavedEdits);

			// Another page: Don't save throws the edit away and goes.
			ui.Main.ShowCommand.Execute(Page.Structures);
			ui.Pump();
			Assert.True(ui.Main.IsMapPage);
			question = Assert.IsType<SaveEditsSheetViewModel>(ui.Main.Sheets.Current);
			question.DiscardCommand.Execute(null);
			ui.Pump();
			Assert.True(ui.Main.IsStructuresPage);
			Assert.False(map.HasUnsavedEdits);
			Assert.Equal(30, parcels.Data.Features.Count);

			// Back on the map, another layer to edit: Save writes the edit, then switches.
			ui.Main.ShowCommand.Execute(Page.Map);
			ui.Pump();
			Assert.True(map.IsEdit);
			view = ui.Window.GetVisualDescendants().OfType<MapView>().Single();
			view.Map.FitTo(new MapExtent(-84.0502, 39.6198, -84.0486, 39.6208), maxZoom: 19, margin: 40);
			ui.Pump();
			view.Map.Click(Middle(view, first.Geometry));
			map.DeleteEditFeatureCommand.Execute(null);
			Task made = map.AddEmptyLayerAsync("Notes", GeometryKind.Point);
			ui.Pump();
			question = Assert.IsType<SaveEditsSheetViewModel>(ui.Main.Sheets.Current);
			question.SaveCommand.Execute(null);
			await made;
			Assert.Equal(29, parcels.Data.Features.Count);
			Assert.Equal("Notes", map.EditLayer.Name);

			// Discard asks, then puts the shapes back.
			view.Map.Click(view.Map.ToScreen(39.6201, -84.0495));
			Assert.True(map.HasUnsavedEdits);
			ui.Dialogs.Answers.Enqueue(true);
			await map.DiscardEditsCommand.ExecuteAsync(null);
			Assert.False(map.HasUnsavedEdits);
			Assert.Empty(map.EditLayer.Data.Features);
			Assert.Equal("Discarded the edits to Notes.", map.Message);
		}
	}

	[AvaloniaFact]
	public async Task OpeningAnotherDatabase_SavesTheEditsFirst()
	{
		(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			string layersFile = ui.Store.Database.LayersPath;
			Guid databaseId = ui.Store.Database.DatabaseId;
			string table = parcels.Record.TableName;
			view.Map.Click(Middle(view, Parcel(parcels, "CF-0001").Geometry));
			map.DeleteEditFeatureCommand.Execute(null);
			string mine = ui.Store.Database.FilePath;
			using TempDatabase other = new TempDatabase();
			ui.Services.Open(other.Database.FilePath);
			ui.Pump();
			Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Map edits saved: Saved 1 change to parcels", StringComparison.Ordinal));
			Assert.Equal(29, LayersStore.Open(layersFile, databaseId).ReadTable(table).Features.Count);
			Assert.False(map.HasUnsavedEdits);
			Assert.Null(map.EditLayer);
			ui.Services.Open(mine);
			ui.Pump();
		}
	}

	/// <summary>
	/// Editing a layer of 50,000 parcels: picking a parcel, dragging a corner (the dragged shape is drawn over the
	/// layer's raster tiles) and letting go (only the changed parcel is drawn again, not the layer) stay quick.
	/// </summary>
	[AvaloniaFact]
	public void FiftyThousandPolygons_EditQuickly()
	{
		using UiSession ui = new UiSession();
		MapView view = MapTestSupport.ShowMap(ui);
		MapViewModel map = ui.Main.Map;
		FeatureLayer big = new FeatureLayer("Parcels", GeometryKind.Polygon);
		big.AddField("parcel_id", FieldType.Text);
		const double south = 39.60;
		const double west = -84.10;
		const double dLat = 0.000135;
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
		Stopwatch add = Stopwatch.StartNew();
		MapLayerViewModel layer = map.AddOnline(big, "file", null);
		layer.Record.SourceKind = MapLayerKinds.File;
		add.Stop();
		map.SetModeCommand.Execute(MapMode.Edit);
		map.EditLayer = layer;
		double midLat = south + 100 * dLat;
		double midLon = west + 125 * dLon;
		view.Map.SetView(midLat, midLon, 18);
		MapTestSupport.Settle(ui, view, 30000);
		Assert.True(view.Map.IsRasterized(layer.Key));

		Feature target = big.Features[100 * 250 + 125];
		Point inside = MapTestSupport.InWindow(ui, view, Middle(view, target.Geometry));
		Stopwatch pick = Stopwatch.StartNew();
		ui.Window.MouseDown(inside, MouseButton.Left);
		ui.Window.MouseUp(inside, MouseButton.Left);
		using (WriteableBitmap _ = MapTestSupport.Capture(ui))
		{
		}
		pick.Stop();
		Assert.Equal(target.Id, map.EditFeature?.Id);

		Point corner = MapTestSupport.InWindow(ui, view, view.Map.CornerOnScreen(new VertexRef(0, 0, 2)));
		List<double> frames = new List<double>();
		ui.Window.MouseDown(corner, MouseButton.Left);
		for (int i = 1; i <= 40; i++)
		{
			Stopwatch frame = Stopwatch.StartNew();
			ui.Window.MouseMove(corner + new Point(i, i * 0.5), RawInputModifiers.LeftMouseButton);
			using WriteableBitmap shot = MapTestSupport.Capture(ui);
			frame.Stop();
			if (i > 5)
			{
				frames.Add(frame.Elapsed.TotalMilliseconds);
			}
		}
		Stopwatch release = Stopwatch.StartNew();
		ui.Window.MouseUp(corner + new Point(40, 20), MouseButton.Left);
		MapTestSupport.Settle(ui, view, 30000);
		release.Stop();
		Assert.Equal(1, map.Session.ChangeCount);

		frames.Sort();
		double median = frames[frames.Count / 2];
		double p95 = frames[(int)(frames.Count * 0.95)];
		string line = $"50,000 polygons in Edit mode: layer added in {add.ElapsedMilliseconds} ms; pick {pick.ElapsedMilliseconds} ms; corner drag frames median {median:0.0} ms, 95th percentile {p95:0.0} ms, slowest {frames[^1]:0.0} ms; let go and redraw {release.ElapsedMilliseconds} ms";
		Console.WriteLine("[frame-times] " + line);
		File.WriteAllText(Path.Combine(Path.GetTempPath(), "sde-map-edit-frame-times.txt"), line);
		Assert.True(median < 50, $"Median drag frame took {median:0.0} ms.");
	}

	[AvaloniaFact]
	public async Task ScreenshotsOfEditMode_LightAndDark_WithAChangedParcelAndASketch()
	{
		foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "56f-map-edit"), (ThemeVariant.Dark, "56f-map-edit-dark") })
		{
			(UiSession ui, MapView view, MapLayerViewModel parcels) = await EditParcels(theme);
			using (ui)
			{
				MapViewModel map = ui.Main.Map;
				map.Basemap = Basemaps.UsgsTopo;
				map.ZoomToLayerCommand.Execute(parcels);
				ui.Pump();
				map.ApplyStyle(parcels, new LayerStyleSheetViewModel.Result(Sde.Gis.Styles.LayerStyle.Categorized(parcels.Data, "flood_zone"), null, null, null));
				// One parcel's corner pulled out, then a new area being drawn beside it.
				view.Map.Click(Middle(view, Parcel(parcels, "CF-0007").Geometry));
				Point corner = view.Map.CornerOnScreen(new VertexRef(0, 0, 2));
				view.Map.DragOnMap(corner, corner + new Point(35, -25));
				map.SelectedEditTool = map.EditTools.Single(t => t.Tool == MapEditTool.AddArea);
				ui.Pump();
				Feature next = Parcel(parcels, "CF-0003");
				Envelope e = next.Geometry.EnvelopeInternal;
				view.Map.Click(view.Map.ToScreen(e.MinY + e.Height * 0.2, e.MinX + e.Width * 0.2));
				view.Map.Click(view.Map.ToScreen(e.MinY + e.Height * 0.2, e.MinX + e.Width * 0.8));
				view.Map.Click(view.Map.ToScreen(e.MinY + e.Height * 0.8, e.MinX + e.Width * 0.7));
				MapTestSupport.Settle(ui, view);
				Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
				ui.Snapshot(name);
			}
		}
	}
}
