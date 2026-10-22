using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Gis;
using Sde.Gis.Analysis;
using Sde.Gis.Features;

namespace Sde.App.ViewModels;

/// <summary>
/// The Map page's selection: structures (the ones checked in the Structures list) and layers' features, picked by
/// click, box, polygon or attribute; and what can be done with selected structures.
/// </summary>
public sealed partial class MapViewModel
{
	/// <summary>The map should ask for its tiles again (after "Try again").</summary>
	public event Action RetryRequested;

	/// <summary>The selected structures changed (the map rings them).</summary>
	public event Action SelectionChanged;

	/// <summary>The map should forget the line or polygon being drawn.</summary>
	public event Action ClearSketchRequested;

	/// <summary>The layer clicks, boxes and polygons select in (Select mode): the structures or a layer of features.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Hint), nameof(SelectLayerKey))]
	public partial MapLayerViewModel SelectTarget { get; set; }

	/// <summary>The map key of <see cref="SelectTarget"/>, or null for the structures.</summary>
	public string SelectLayerKey => SelectTarget == null || SelectTarget.IsStructures ? null : SelectTarget.Key;

	/// <summary>In Select mode, clicks draw the corners of an area to select what is inside it.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Hint), nameof(Sketch))]
	public partial bool IsPolygonSelect { get; set; }

	/// <summary>What clicks on the map draw now: a polygon to select with, a line or area to measure, or nothing.</summary>
	public MapSketch Sketch => Mode switch
	{
		MapMode.Select when IsPolygonSelect => MapSketch.Polygon,
		MapMode.Measure => MeasureArea ? MapSketch.Polygon : MapSketch.Line,
		MapMode.Edit when EditLayer != null && EditTool == MapEditTool.AddLine => MapSketch.Line,
		MapMode.Edit when EditLayer != null && EditTool == MapEditTool.AddArea => MapSketch.Polygon,
		_ => MapSketch.None,
	};

	/// <summary>The structures selected on the map: those whose assessments are checked in the Structures list.</summary>
	public HashSet<Guid> SelectedStructures()
	{
		HashSet<Guid> checkedIds = _main.Structures.CheckedAssessmentIds().ToHashSet();
		return _all.Where(p => p.AssessmentID is Guid a && checkedIds.Contains(a)).Select(p => p.Id).ToHashSet();
	}

	/// <summary>
	/// Selects structures, which checks their assessments in the Structures list (replacing what was checked, unless
	/// <paramref name="add"/>). Structures without an assessment can't be checked there, so they aren't selected.
	/// </summary>
	public void SelectStructures(IReadOnlyCollection<Guid> propertyIds, bool add)
	{
		HashSet<Guid> ids = _all.Where(p => propertyIds.Contains(p.Id) && p.AssessmentID != null).Select(p => p.AssessmentID!.Value).ToHashSet();
		if (add)
		{
			ids.UnionWith(_main.Structures.CheckedAssessmentIds());
		}
		_main.Structures.CheckOnly(ids);
		OnPropertyChanged(nameof(CheckedCount));
		OnPropertyChanged(nameof(HasChecked));
		OnPropertyChanged(nameof(SelectionText));
		SelectionChanged?.Invoke();
		if (TableSelectedOnly && TableLayer?.IsStructures == true)
		{
			RefreshTable();
		}
	}

	/// <summary>"3 structures selected".</summary>
	public string SelectionText => CheckedCount == 1 ? "1 structure selected" : $"{CheckedCount} structures selected";

	/// <summary>The layer of features with a selection (shown in the selection bar), or null.</summary>
	public MapLayerViewModel LayerWithSelection => Layers.FirstOrDefault(l => l.IsFeatures && l.SelectedCount > 0);

	public bool HasLayerSelection => LayerWithSelection != null;

	/// <summary>"12 selected in Parcels".</summary>
	public string LayerSelectionText => LayerWithSelection is MapLayerViewModel l ? $"{l.SelectedCount} selected in {l.Name}" : null;

	/// <summary>A layer's selection changed: the map redraws the highlight and the selection bar follows.</summary>
	private void NotifyLayerSelection()
	{
		OnPropertyChanged(nameof(LayerWithSelection));
		OnPropertyChanged(nameof(HasLayerSelection));
		OnPropertyChanged(nameof(LayerSelectionText));
		RaiseLayersChanged();
	}

	/// <summary>The same, and a table showing only that layer's selection is read again.</summary>
	private void LayerSelectionChanged(MapLayerViewModel layer)
	{
		NotifyLayerSelection();
		if (TableSelectedOnly && ReferenceEquals(TableLayer, layer))
		{
			RefreshTable();
		}
	}

	/// <summary>A feature of a layer was clicked: in Open mode its values are shown; in Select mode it is selected.</summary>
	public void ClickFeature(string layerKey, Feature feature, bool add)
	{
		if (Mode == MapMode.Select)
		{
			if (LayerWithKey(layerKey) is MapLayerViewModel layer)
			{
				if (add && layer.Selected.Remove(feature.Id))
				{
					layer.Select(Array.Empty<long>(), add: true);
				}
				else
				{
					layer.Select(new[] { feature.Id }, add);
				}
				Message = $"{layer.SelectedCount} selected in {layer.Name}.";
				LayerSelectionChanged(layer);
			}
			return;
		}
		if (Mode == MapMode.Browse)
		{
			Identify(layerKey, feature);
		}
	}

	/// <summary>A box was drawn over a layer's features (in Select mode, selecting in that layer).</summary>
	public void SelectFeatures(string layerKey, IReadOnlyList<Feature> features, bool add)
	{
		if (LayerWithKey(layerKey) is not MapLayerViewModel layer)
		{
			return;
		}
		layer.Select(features.Select(f => f.Id), add);
		Message = layer.SelectedCount == 0 ? $"Nothing selected in {layer.Name}." : $"{layer.SelectedCount} selected in {layer.Name}.";
		LayerSelectionChanged(layer);
	}

	/// <summary>Selects what lies in (or touches) an area drawn on the map, in the select target.</summary>
	public void SelectInside(IReadOnlyList<LatLon> corners, bool add = false)
	{
		if (corners.Count < 3)
		{
			return;
		}
		Coordinate[] ring = corners.Select(c => new Coordinate(c.Longitude, c.Latitude)).Append(new Coordinate(corners[0].Longitude, corners[0].Latitude)).ToArray();
		Geometry area = GisSetup.Factory.CreatePolygon(ring);
		if (!area.IsValid)
		{
			area = area.Buffer(0);
		}
		MapLayerViewModel target = SelectTarget ?? StructuresLayer;
		if (target == null || target.IsStructures)
		{
			List<Guid> ids = SpatialSelect.ByLocation(StructureFeatures.Features, new[] { area }, SpatialRelation.Intersects)
				.Select(f => StructureOf(f.Id)).Where(p => p != null).Select(p => p.Id).ToList();
			SelectArea(ids, add);
			return;
		}
		if (target.Data != null)
		{
			SelectFeatures(target.Key, SpatialSelect.ByLocation(target.Data.Features, new[] { area }, SpatialRelation.Intersects), add);
		}
	}

	/// <summary>A corner was added to the line or polygon being drawn, or the shape was finished.</summary>
	public void SketchChanged(IReadOnlyList<LatLon> points, bool finished)
	{
		if (Mode == MapMode.Measure)
		{
			UpdateMeasurement(points, finished);
		}
		else if (Mode == MapMode.Edit && finished)
		{
			AddSketchedShape(points);
		}
		else if (Mode == MapMode.Select && IsPolygonSelect && finished)
		{
			SelectInside(points);
			ClearSketchRequested?.Invoke();
		}
	}

	/// <summary>The layer selection bar's Clear: no features are selected in any layer (the structures stay checked).</summary>
	[RelayCommand]
	private void ClearSelection()
	{
		foreach (MapLayerViewModel layer in Layers.Where(l => l.SelectedCount > 0).ToList())
		{
			layer.Select(Array.Empty<long>(), add: false);
			LayerSelectionChanged(layer);
		}
		Message = null;
	}

	/// <summary>Select, then By attribute: field, operator and value conditions, joined by AND or OR.</summary>
	[RelayCommand]
	private async Task SelectByAttribute()
	{
		ReadLayers();
		await _app.Sheets.ShowAsync(new SelectByAttributeSheetViewModel(this, SelectTarget ?? StructuresLayer));
	}

	/// <summary>Selects the features of a layer that an attribute query matches (the select-by-attribute sheet calls this).</summary>
	public int SelectWhere(MapLayerViewModel layer, AttributeQuery query, bool add)
	{
		FeatureLayer data = layer.IsStructures ? StructureFeaturesFor(query) : layer.Data;
		List<Feature> matches = query.Select(data);
		if (layer.IsStructures)
		{
			SelectArea(matches.Select(f => StructureOf(f.Id)).Where(p => p != null).Select(p => p.Id).ToList(), add);
			return matches.Count;
		}
		SelectFeatures(layer.Key, matches, add);
		return matches.Count;
	}

	/// <summary>The structure features with every assessment field a query names.</summary>
	public FeatureLayer StructureFeaturesFor(AttributeQuery query)
	{
		foreach (AttributeCondition c in query.Conditions)
		{
			UseStructureField(c.Field);
		}
		return StructureFeatures;
	}

	// ───────────── What to do with the selected structures ─────────────

	/// <summary>The checked assessments (the selected structures' latest assessments).</summary>
	private IReadOnlyList<Guid> SelectedAssessments() => _main.Structures.CheckedAssessmentIds();

	/// <summary>Selection bar, then Set a field: bulk edit on the selected structures.</summary>
	[RelayCommand]
	private async Task SetFieldOnSelection()
	{
		IReadOnlyList<Guid> ids = SelectedAssessments();
		if (ids.Count > 0)
		{
			await _app.Sheets.ShowAsync(new BulkEditSheetViewModel(_app, ids));
		}
	}

	/// <summary>Selection bar, then Assign to team: gives the selected structures to a team.</summary>
	[RelayCommand]
	private async Task AssignSelection()
	{
		List<Guid> structures = SelectedStructures().ToList();
		if (structures.Count == 0)
		{
			return;
		}
		if (await _app.Sheets.ShowAsync(new AssignTeamSheetViewModel(_app, structures)) is string done)
		{
			Message = done;
			_structureFeatures = null;
			RefreshTable();
		}
	}

	/// <summary>Selection bar, then Reports: one of FEMA's reports for the selected structures.</summary>
	[RelayCommand]
	private async Task PrintSelection(string kind)
	{
		IReadOnlyList<Guid> ids = SelectedAssessments();
		if (ids.Count > 0)
		{
			await _main.PrintAsync(kind, ids);
		}
	}

	/// <summary>Selection bar, then Add to web map: a web map folder of the selected structures.</summary>
	[RelayCommand]
	private async Task WebMapOfSelection()
	{
		IReadOnlyList<Guid> ids = SelectedAssessments();
		if (ids.Count == 0)
		{
			return;
		}
		if (await _app.Sheets.ShowAsync(new WebMapSheetViewModel(_app, ids)) is string done)
		{
			Message = done;
		}
	}

	/// <summary>Selection bar for a layer: show its selected features in the table.</summary>
	[RelayCommand]
	private void ShowSelectionInTable()
	{
		if (LayerWithSelection is MapLayerViewModel layer)
		{
			TableLayer = layer;
			TableSelectedOnly = true;
			IsTableOpen = true;
			RefreshTable();
		}
	}

	/// <summary>"Try again" after tiles couldn't be downloaded.</summary>
	[RelayCommand]
	private void RetryTiles()
	{
		BasemapProblem = null;
		Tiles.ResetFailures();
		RetryRequested?.Invoke();
	}
}
