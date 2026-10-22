using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Analysis;
using Sde.Gis.Features;

namespace Sde.App.ViewModels;

/// <summary>Select by location, and copying a polygon layer's values onto structures.</summary>
public sealed partial class MapViewModel
{
	/// <summary>A select-by-location query waiting for its area to be drawn on the map, or null.</summary>
	private LocationQuery _pendingLocation;

	/// <summary>The structures on the map (with coordinates, in the event chosen in the sidebar), whatever the legend shows.</summary>
	public IReadOnlyList<MapPointViewModel> PlacedStructures => _all.Where(p => p.HasLocation).ToList();

	/// <summary>The features a layer has now (the structures as features).</summary>
	public FeatureLayer FeaturesOf(MapLayerViewModel layer) => layer == null ? null : layer.IsStructures ? StructureFeatures : DataOf(layer);

	/// <summary>The shapes of a layer's selected features (or the selected structures' points), in longitude and latitude.</summary>
	public IReadOnlyList<Geometry> SelectorsOf(MapLayerViewModel layer)
	{
		FeatureLayer data = FeaturesOf(layer);
		if (data == null)
		{
			return Array.Empty<Geometry>();
		}
		HashSet<long> ids = SelectedIds(layer);
		return data.Features.Where(f => ids.Contains(f.Id) && f.Geometry != null).Select(f => f.Geometry).ToList();
	}

	/// <summary>Select, then By location: the sheet, then the query (or drawing its area first).</summary>
	[RelayCommand]
	private async Task SelectByLocation()
	{
		ReadLayers();
		if (await _app.Sheets.ShowAsync(new SelectByLocationSheetViewModel(this, SelectTarget ?? StructuresLayer)) is not LocationQuery query)
		{
			return;
		}
		if (query.FromDrawnArea)
		{
			_pendingLocation = query;
			SelectTarget = query.Target.IsStructures ? null : query.Target;
			if (Mode != MapMode.Select)
			{
				await SetModeCommand.ExecuteAsync(MapMode.Select);
			}
			// The pending query outlives the mode change above.
			_pendingLocation = query;
			IsPolygonSelect = true;
			OnPropertyChanged(nameof(Hint));
			return;
		}
		int n = RunLocationQuery(query, SelectorsOf(query.Source));
		LocationMessage(query, n);
	}

	/// <summary>
	/// Runs a select-by-location query against some shapes and changes the target's selection by its method; with
	/// <paramref name="countOnly"/> it only counts. Returns how many features (or structures) matched.
	/// </summary>
	public int RunLocationQuery(LocationQuery query, IReadOnlyList<Geometry> selectors, bool countOnly = false)
	{
		FeatureLayer data = FeaturesOf(query.Target);
		if (data == null)
		{
			return 0;
		}
		IEnumerable<Feature> candidates = data.Features;
		HashSet<long> current = SelectedIds(query.Target);
		if (query.Method == SelectionMethod.Within)
		{
			candidates = candidates.Where(f => current.Contains(f.Id));
		}
		List<Feature> matches = SpatialSelect.ByLocation(candidates, selectors, query.Relation, query.Metres);
		if (countOnly)
		{
			return matches.Count;
		}
		bool add = query.Method == SelectionMethod.Add;
		if (query.Target.IsStructures)
		{
			SelectStructures(matches.Select(f => StructureOf(f.Id)).Where(p => p != null).Select(p => p.Id).ToList(), add);
		}
		else
		{
			query.Target.Select(matches.Select(f => f.Id), add);
			LayerSelectionChanged(query.Target);
		}
		return matches.Count;
	}

	private void LocationMessage(LocationQuery query, int matched)
	{
		if (query.Target.IsStructures)
		{
			int n = CheckedCount;
			Message = matched == 0 ? "No structures matched." : $"{n} structure{(n == 1 ? " is" : "s are")} selected.";
		}
		else
		{
			Message = matched == 0 ? $"Nothing in {query.Target.Name} matched." : $"{query.Target.SelectedCount} selected in {query.Target.Name}.";
		}
	}

	/// <summary>The area for a pending select-by-location query was drawn: the query runs against it. False when none was waiting.</summary>
	private bool RunPendingLocation(Geometry area)
	{
		if (_pendingLocation is not LocationQuery query)
		{
			return false;
		}
		_pendingLocation = null;
		IsPolygonSelect = false;
		LocationMessage(query, RunLocationQuery(query, new[] { area }));
		return true;
	}

	/// <summary>The help line while the area of a select-by-location query is being drawn, or null.</summary>
	private string PendingLocationHint => _pendingLocation is LocationQuery q
		? $"Click the corners of the area; double-click or press Return to select {(q.Target.IsStructures ? "the structures" : "the features of " + q.Target.Name)} that "
			+ (q.Relation == SpatialRelation.WithinDistance ? $"are within {Length(q.Metres)} of" : RelationChoice.All.First(r => r.Relation == q.Relation).Label) + " it."
		: null;

	// ───────────── Copy values onto structures ─────────────

	/// <summary>The Layers panel's "Copy values onto structures…" for a layer of areas (also on the structures' selection bar).</summary>
	[RelayCommand]
	private async Task CopyValues(MapLayerViewModel layer)
	{
		ReadLayers();
		if (!Layers.Any(l => l.IsAreaLayer))
		{
			Message = "Add a layer of areas first (parcels or flood zones, say), to copy its values onto structures.";
			return;
		}
		if (await _app.Sheets.ShowAsync(new CopyValuesSheetViewModel(_app, this, layer)) is CopyValuesSheetViewModel sheet)
		{
			ApplyValueCopy(sheet.Layer, sheet.SourceField.Label, sheet.Target.Field, sheet.Rows.Where(r => r.IsTicked).ToList());
		}
	}

	/// <summary>
	/// The preview rows for copying a layer's field into a structure field: each structure (the selected ones, or all on
	/// the map) with its value now, the new value and how it was matched. Structures without an assessment, and Final,
	/// Letter sent and checked-out assessments, can't be ticked.
	/// </summary>
	public List<CopyValueRowViewModel> PlanValueCopy(MapLayerViewModel layer, string sourceField, FieldInfo target, bool onlySelected)
	{
		HashSet<Guid> selected = onlySelected ? SelectedStructures() : null;
		List<MapPointViewModel> structures = PlacedStructures.Where(p => selected == null || selected.Contains(p.Id)).OrderBy(p => p.Address, StringComparer.OrdinalIgnoreCase).ToList();
		FeatureLayer points = new FeatureLayer("Structures", GeometryKind.Point);
		for (int i = 0; i < structures.Count; i++)
		{
			points.AddFeature(GisSetup.Factory.CreatePoint(new Coordinate(structures[i].Longitude, structures[i].Latitude)), i + 1);
		}
		List<ValueCopyRow> plan = ValueCopy.Plan(points.Features, DataOf(layer).Features, sourceField, target, _app.Store.Reference);
		Dictionary<Guid, AssessmentFile> files = Safe(() => _app.Store.LoadAssessments(structures.Where(p => p.AssessmentID != null).Select(p => p.AssessmentID!.Value)).ToDictionary(f => f.Assessment.AssessmentID),
			new Dictionary<Guid, AssessmentFile>());
		Dictionary<Guid, AssessmentStatusRecord> statuses = Safe(() => _app.Store.GetStatuses(), new Dictionary<Guid, AssessmentStatusRecord>());
		Dictionary<Guid, CheckOutRecord> checkOuts = Safe(() => _app.Store.GetCheckOuts().ToDictionary(c => c.AssessmentID), new Dictionary<Guid, CheckOutRecord>());
		List<CopyValueRowViewModel> rows = new List<CopyValueRowViewModel>();
		foreach (ValueCopyRow r in plan)
		{
			MapPointViewModel p = structures[(int)r.Point.Id - 1];
			string now = "";
			string how;
			bool can = r.CanCopy;
			if (p.AssessmentID is Guid aid && files.TryGetValue(aid, out AssessmentFile file))
			{
				now = FieldCatalog.Display(target, FieldCatalog.GetValue(file, target), _app.Store.Reference);
				if (statuses.TryGetValue(aid, out AssessmentStatusRecord s) && s.IsLocked)
				{
					can = false;
					how = $"Locked ({AssessmentStatuses.Label(s.Value)})";
				}
				else if (checkOuts.TryGetValue(aid, out CheckOutRecord c))
				{
					can = false;
					how = "Checked out to " + (string.IsNullOrWhiteSpace(c.CheckedOutTo) ? "another Mac" : c.CheckedOutTo);
				}
				else if (!target.AppliesTo(file))
				{
					can = false;
					how = $"{target.Label} doesn't apply to this assessment";
				}
				else
				{
					how = Matched(r);
				}
			}
			else
			{
				can = false;
				how = "No assessment yet";
			}
			rows.Add(new CopyValueRowViewModel(p.Id, p.AssessmentID, p.Address, now, r.NewText, r.InputValue, how, can));
		}
		return rows;
	}

	/// <summary>How a structure was matched to an area, or why nothing can be copied.</summary>
	private static string Matched(ValueCopyRow r) => r.Problem ?? r.Outcome switch
	{
		JoinOutcome.InOne => "Inside one area",
		JoinOutcome.InSeveral => $"Inside {r.AreaCount} areas: the smallest is used",
		JoinOutcome.Nearest => "Near an area",
		_ => "Not inside any area",
	};

	/// <summary>
	/// Copies the ticked rows' values into the structures' latest assessments through <see cref="FieldInput"/>, as one
	/// change in edit history (Via Map), so History can revert it; the toast offers Undo. Locked and checked-out
	/// assessments (also ones locked since the preview) are skipped and counted.
	/// </summary>
	public (int Copied, int Skipped, string BatchId) ApplyValueCopy(MapLayerViewModel layer, string sourceLabel, FieldInfo target, IReadOnlyList<CopyValueRowViewModel> rows)
	{
		List<CopyValueRowViewModel> todo = rows.Where(r => r.CanCopy && r.AssessmentId != null && r.Input != null).ToList();
		if (todo.Count == 0)
		{
			return (0, 0, null);
		}
		SaveContext context = SaveContext.Batch(SaveVia.Map);
		int copied = 0;
		int skipped = 0;
		try
		{
			_app.Writes.Write(todo.Select(r => r.AssessmentId!.Value).ToList(), () =>
			{
				foreach (CopyValueRowViewModel row in todo)
				{
					try
					{
						_app.Store.Apply(row.AssessmentId!.Value, file => FieldInput.Apply(file, new[] { new FieldInputChange(target.Key, row.Input) }, _app.Store.Reference, _app.Store.Calculator), context);
						copied++;
					}
					catch (Exception ex) when (ex is AssessmentLockedException or AssessmentCheckedOutException or FieldInputException)
					{
						skipped++;
					}
				}
			});
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't copy the values", ex);
			return (copied, skipped, context.BatchId);
		}
		_structureFeatures = null;
		RefreshTable();
		string what = $"Copied {sourceLabel} from {layer.Name} into {target.Label} on {copied} structure{(copied == 1 ? "" : "s")}.";
		if (skipped > 0)
		{
			what += $" {skipped} {(skipped == 1 ? "was" : "were")} skipped: locked or checked out since the preview.";
		}
		Message = what;
		string batch = context.BatchId;
		_app.Dialogs.Notify("Values copied", what, "Undo", () => UndoValueCopy(batch));
		return (copied, skipped, batch);
	}

	/// <summary>The toast's Undo after copying values: the batch is reverted (as History's Revert does).</summary>
	private void UndoValueCopy(string batchId)
	{
		try
		{
			_app.Writes.Write(null, () => _app.Store.RevertBatch(batchId, SaveContext.Batch(SaveVia.Map)));
			Message = "Put back the values from before the copy.";
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't put the values back", ex);
		}
	}
}
