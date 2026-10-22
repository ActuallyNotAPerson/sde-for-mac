using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Editing;
using Sde.Gis.Features;
using Sde.Gis.Formats;
using Sde.Gis.Styles;

namespace Sde.App.ViewModels;

/// <summary>A tool in the Edit bar: what clicks do to the layer being edited.</summary>
public sealed record EditToolChoice(MapEditTool Tool, string Label)
{
	public override string ToString() => Label;
}

/// <summary>
/// The Map page's Edit mode: draw and change the shapes of a layer added from a file (or made here), with snapping,
/// undo and redo, values typed in the attribute table, and Save or Discard. Online layers and FEMA's flood zones are
/// read only. Nothing reaches the layers file until Save; leaving with unsaved edits asks first.
/// </summary>
public sealed partial class MapViewModel
{
	private EditSession _session;
	private int _editsRevision;
	private bool _typingInTable;

	/// <summary>The layers whose shapes can be edited (from files or made here), top first.</summary>
	public IReadOnlyList<MapLayerViewModel> EditableLayers => Layers.Where(l => l.CanEdit).ToList();

	public bool HasEditableLayers => Layers.Any(l => l.CanEdit);

	private MapLayerViewModel _editLayer;

	/// <summary>
	/// The layer being edited. Choosing another one with unsaved edits first asks whether to save them (and stays on
	/// this one when the person cancels).
	/// </summary>
	public MapLayerViewModel EditLayer
	{
		get => _editLayer;
		set
		{
			if (ReferenceEquals(value, _editLayer))
			{
				return;
			}
			if (HasUnsavedEdits)
			{
				_ = SwitchEditLayerAsync(value);
				return;
			}
			StartEditing(value);
		}
	}

	private async Task SwitchEditLayerAsync(MapLayerViewModel layer)
	{
		// The list goes back to the layer being edited while the question is shown.
		Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(EditLayer)));
		if (await ConfirmLeaveEditsAsync())
		{
			StartEditing(layer);
		}
	}

	/// <summary>The edit tools for the layer's kind of shape: Change, then Add points, lines or areas.</summary>
	public IReadOnlyList<EditToolChoice> EditTools
	{
		get
		{
			List<EditToolChoice> tools = new List<EditToolChoice> { new EditToolChoice(MapEditTool.Change, "Change") };
			GeometryKind kind = EditLayer?.GeometryKind ?? GeometryKind.Unknown;
			if (kind is GeometryKind.Point or GeometryKind.Unknown)
			{
				tools.Add(new EditToolChoice(MapEditTool.AddPoint, "Add points"));
			}
			if (kind is GeometryKind.Line or GeometryKind.Unknown)
			{
				tools.Add(new EditToolChoice(MapEditTool.AddLine, "Add lines"));
			}
			if (kind is GeometryKind.Polygon or GeometryKind.Unknown)
			{
				tools.Add(new EditToolChoice(MapEditTool.AddArea, "Add areas"));
			}
			return tools;
		}
	}

	/// <summary>The tool chosen in the Edit bar.</summary>
	public EditToolChoice SelectedEditTool
	{
		get => EditTools.FirstOrDefault(t => t.Tool == EditTool) ?? EditTools[0];
		set
		{
			if (value != null)
			{
				EditTool = value.Tool;
			}
		}
	}

	/// <summary>What clicks and drags do to the layer being edited.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Sketch), nameof(Hint), nameof(SelectedEditTool), nameof(IsChangeTool))]
	public partial MapEditTool EditTool { get; set; }

	public bool IsChangeTool => EditTool == MapEditTool.Change;

	partial void OnEditToolChanged(MapEditTool value)
	{
		if (value != MapEditTool.Change)
		{
			EditFeature = null;
		}
	}

	/// <summary>The shape picked to change (as it is now), or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasEditFeature), nameof(Hint))]
	public partial Feature EditFeature { get; set; }

	public bool HasEditFeature => EditFeature != null;

	/// <summary>New corners snap to nearby corners and edges of the visible layers and to structure dots.</summary>
	[ObservableProperty]
	public partial bool SnapToShapes { get; set; } = true;

	/// <summary>True when there are edits not saved to the layers file yet.</summary>
	public bool HasUnsavedEdits => _session?.HasChanges == true;

	/// <summary>"3 unsaved changes", or "No unsaved changes".</summary>
	public string EditStatus
	{
		get
		{
			int n = _session?.ChangeCount ?? 0;
			return n == 0 ? "No unsaved changes" : n == 1 ? "1 unsaved change" : $"{n.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} unsaved changes";
		}
	}

	public bool CanUndoEdit => _session?.CanUndo == true;

	public bool CanRedoEdit => _session?.CanRedo == true;

	/// <summary>"Undo Move corner".</summary>
	public string UndoEditText => _session?.UndoLabel is string label ? "Undo " + label.ToLowerInvariant() : "Undo";

	public string RedoEditText => _session?.RedoLabel is string label ? "Redo " + label.ToLowerInvariant() : "Redo";

	/// <summary>The editing session (for tests).</summary>
	internal EditSession Session => _session;

	/// <summary>The help line for Edit mode.</summary>
	private string EditHint
	{
		get
		{
			if (EditLayer == null)
			{
				return HasEditableLayers
					? "Choose a layer to edit."
					: "Add a layer from a file, or make a new layer, to draw and change its shapes. Online layers and FEMA's flood zones can't be edited.";
			}
			return EditTool switch
			{
				MapEditTool.AddPoint => "Click to add a point. Corners and edges of nearby shapes pull it onto them.",
				MapEditTool.AddLine => "Click the corners of the line; double-click or press Return to finish. Delete removes the last corner.",
				MapEditTool.AddArea => "Click the corners of the area; double-click or press Return to finish. Delete removes the last corner.",
				_ => EditFeature == null
					? "Click a shape to change it."
					: "Drag a corner, or a dot between corners to add one. Option-click a corner to take it away. Drag inside the shape to move it. Delete removes the picked corner, or the shape.",
			};
		}
	}

	/// <summary>The data a layer shows now: the layer being edited as it is in the editing session, any other as read.</summary>
	public FeatureLayer DataOf(MapLayerViewModel layer) => layer != null && ReferenceEquals(layer, EditLayer) && _session != null ? _session.Working : layer?.Data;

	// ───────────── Starting and stopping ─────────────

	/// <summary>Edit mode was entered or left.</summary>
	private void EditModeChanged(bool editing)
	{
		if (editing)
		{
			ReadLayers();
			StartEditing(EditLayer?.CanEdit == true ? EditLayer : EditableLayers.FirstOrDefault());
		}
		else
		{
			StopEditing();
		}
		OnPropertyChanged(nameof(EditableLayers));
		OnPropertyChanged(nameof(HasEditableLayers));
	}

	/// <summary>Starts a new editing session on a layer (null: none).</summary>
	private void StartEditing(MapLayerViewModel layer)
	{
		EndSession();
		_editLayer = layer?.CanEdit == true ? layer : null;
		if (_editLayer != null && IsEdit)
		{
			_session = new EditSession(_editLayer.Data);
			_session.Changed += SessionChanged;
		}
		EditTool = MapEditTool.Change;
		EditFeature = null;
		OnPropertyChanged(nameof(EditLayer));
		OnPropertyChanged(nameof(EditTools));
		OnPropertyChanged(nameof(SelectedEditTool));
		OnPropertyChanged(nameof(Sketch));
		OnPropertyChanged(nameof(Hint));
		EditStateChanged();
		RefreshTable();
	}

	/// <summary>Leaves Edit mode: the session ends (unsaved edits were saved or thrown away before this).</summary>
	private void StopEditing()
	{
		EndSession();
		EditFeature = null;
		EditStateChanged();
		RefreshTable();
	}

	private void EndSession()
	{
		if (_session != null)
		{
			_session.Changed -= SessionChanged;
			_session = null;
		}
		if (_editLayer != null)
		{
			_editLayer.Edits = null;
		}
		ClearSketchRequested?.Invoke();
	}

	/// <summary>Something changed in the session: the map draws the edits again and the bar, the picked shape and the table follow.</summary>
	private void SessionChanged()
	{
		if (EditFeature != null)
		{
			EditFeature = _session.Find(EditFeature.Id);
		}
		EditStateChanged();
		if (!_typingInTable && TableEditable)
		{
			UpdateTableRows();
		}
	}

	/// <summary>Hands the map the edits to draw and tells the Edit bar what changed.</summary>
	private void EditStateChanged()
	{
		if (_editLayer != null)
		{
			_editLayer.Edits = _session == null ? null : new MapLayerEdits
			{
				Hidden = _session.ChangedOriginalIds(),
				Drawn = _session.ChangedFeatures(),
				Revision = ++_editsRevision,
			};
		}
		OnPropertyChanged(nameof(HasUnsavedEdits));
		OnPropertyChanged(nameof(EditStatus));
		OnPropertyChanged(nameof(CanUndoEdit));
		OnPropertyChanged(nameof(CanRedoEdit));
		OnPropertyChanged(nameof(UndoEditText));
		OnPropertyChanged(nameof(RedoEditText));
		OnPropertyChanged(nameof(TableEditable));
		UndoEditCommand.NotifyCanExecuteChanged();
		RedoEditCommand.NotifyCanExecuteChanged();
		SaveEditsCommand.NotifyCanExecuteChanged();
		DiscardEditsCommand.NotifyCanExecuteChanged();
		LayersChanged?.Invoke();
	}

	/// <summary>The table's rows for the layer being edited, brought up to date (rows added or removed: read again).</summary>
	private void UpdateTableRows()
	{
		Dictionary<long, Feature> now = _session.Working.Features.ToDictionary(f => f.Id);
		bool sameRows = TableSelectedOnly || TableRows.All(r => now.ContainsKey(r.Id)) && (!string.IsNullOrWhiteSpace(TableFilter) || TableRows.Count == now.Count);
		if (!sameRows)
		{
			RefreshTable();
			return;
		}
		foreach (AttributeRowViewModel row in TableRows)
		{
			if (now.TryGetValue(row.Id, out Feature f) && !ReferenceEquals(f, row.Feature))
			{
				row.Show(f);
			}
		}
	}

	// ───────────── What the map reports ─────────────

	/// <summary>A shape of the layer being edited was clicked (null: empty map): it is picked to change.</summary>
	public void PickEditFeature(Feature feature)
	{
		if (_session == null)
		{
			return;
		}
		EditFeature = feature == null ? null : _session.Find(feature.Id);
		Message = null;
	}

	/// <summary>The picked shape was changed on the map (a corner moved, added or taken away, or the shape moved).</summary>
	public void ChangeEditShape(Geometry geometry, string what)
	{
		if (_session == null || EditFeature == null || geometry == null)
		{
			return;
		}
		_session.SetGeometry(EditFeature.Id, geometry, what);
	}

	/// <summary>A point was clicked with Add points.</summary>
	public void AddEditPoint(Geometry point)
	{
		if (_session == null || point == null)
		{
			return;
		}
		AddShape(point);
	}

	/// <summary>A line or area was finished with Add lines or Add areas (from the corners clicked).</summary>
	private void AddSketchedShape(IReadOnlyList<LatLon> corners)
	{
		if (_session == null)
		{
			return;
		}
		Coordinate[] coords = corners.Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray();
		if (EditTool == MapEditTool.AddLine && coords.Length >= 2)
		{
			AddShape(GisSetup.Factory.CreateLineString(coords));
		}
		else if (EditTool == MapEditTool.AddArea && coords.Length >= 3)
		{
			AddShape(GisSetup.Factory.CreatePolygon(coords.Append(coords[0]).ToArray()));
		}
		else
		{
			Message = EditTool == MapEditTool.AddLine ? "A line needs at least two corners." : "An area needs at least three corners.";
		}
		ClearSketchRequested?.Invoke();
	}

	private void AddShape(Geometry geometry)
	{
		Feature added = _session.Add(geometry);
		Message = TableEditable
			? "Added. Type its values in the table, or keep adding."
			: "Added. Open the attribute table to type its values, or keep adding.";
		EditFeature = _session.Find(added.Id);
	}

	/// <summary>A value typed in the attribute table: checked against the field's type, then kept as an edit.</summary>
	private void EditTableValue(AttributeRowViewModel row, int column, string text)
	{
		if (_session == null)
		{
			return;
		}
		_typingInTable = true;
		try
		{
			_session.SetValue(row.Id, column, text);
			Message = null;
		}
		catch (FormatException ex)
		{
			Message = ex.Message;
		}
		finally
		{
			_typingInTable = false;
		}
		if (_session.Find(row.Id) is Feature now)
		{
			row.Show(now);
		}
	}

	// ───────────── Commands ─────────────

	[RelayCommand(CanExecute = nameof(CanUndoEdit))]
	private void UndoEdit()
	{
		string what = _session?.UndoLabel;
		if (_session?.Undo() == true)
		{
			Message = "Undid: " + what.ToLowerInvariant() + ".";
		}
	}

	[RelayCommand(CanExecute = nameof(CanRedoEdit))]
	private void RedoEdit()
	{
		string what = _session?.RedoLabel;
		if (_session?.Redo() == true)
		{
			Message = "Redid: " + what.ToLowerInvariant() + ".";
		}
	}

	/// <summary>Deletes the picked shape (it can be undone until the edits are saved).</summary>
	[RelayCommand]
	private void DeleteEditFeature()
	{
		if (_session == null || EditFeature == null)
		{
			return;
		}
		_session.Delete(EditFeature.Id);
		EditFeature = null;
		Message = "Deleted the shape. Undo brings it back.";
	}

	/// <summary>Writes the edits to the layers file in one go: every change is saved, or none is.</summary>
	[RelayCommand(CanExecute = nameof(HasUnsavedEdits))]
	private void SaveEdits() => SaveEditsNow();

	/// <summary>Saves the edits; false (with the reason shown) when they couldn't be saved.</summary>
	public bool SaveEditsNow()
	{
		if (_session == null || !_session.HasChanges)
		{
			return true;
		}
		MapLayerViewModel layer = _editLayer;
		int n = _session.ChangeCount;
		try
		{
			LayersFile.ApplyEdits(layer.Record.TableName, _session.ToEdits());
		}
		catch (GisException ex)
		{
			_app.Errors.Show("Couldn't save the edits to " + layer.Name, ex);
			return false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
		{
			_app.Errors.Show("Couldn't save the edits to " + layer.Name, ex);
			return false;
		}
		// A new shape that is picked now has the id the layers file gave it (set on the same feature).
		Feature picked = EditFeature;
		FeatureLayer saved = _session.Saved();
		layer.Data = saved;
		EditFeature = picked == null ? null : _session.Find(picked.Id);
		EditStateChanged();
		RaiseLayersChanged();
		RefreshTable();
		Message = n == 1 ? $"Saved 1 change to {layer.Name}." : $"Saved {n} changes to {layer.Name}.";
		return true;
	}

	/// <summary>Throws away the unsaved edits (after asking).</summary>
	[RelayCommand(CanExecute = nameof(HasUnsavedEdits))]
	private async Task DiscardEdits()
	{
		if (!HasUnsavedEdits)
		{
			return;
		}
		int n = _session.ChangeCount;
		if (!await _app.Dialogs.ConfirmAsync($"Discard {(n == 1 ? "1 change" : $"{n} changes")} to {_editLayer.Name}?",
			"The shapes and values go back to how they were when last saved.", "Discard", destructive: true))
		{
			return;
		}
		DiscardEditsNow();
	}

	/// <summary>Throws away the unsaved edits without asking.</summary>
	public void DiscardEditsNow()
	{
		if (_editLayer == null)
		{
			return;
		}
		string name = _editLayer.Name;
		StartEditing(_editLayer);
		Message = $"Discarded the edits to {name}.";
	}

	/// <summary>
	/// Asks what to do with unsaved edits before leaving them (another mode, layer or page, or quitting): save them,
	/// throw them away, or stay. True when it is all right to go on.
	/// </summary>
	public async Task<bool> ConfirmLeaveEditsAsync()
	{
		if (!HasUnsavedEdits)
		{
			return true;
		}
		SaveEditsSheetViewModel sheet = new SaveEditsSheetViewModel(_editLayer.Name, _session.ChangeCount);
		switch (await _app.Sheets.ShowAsync(sheet) as SaveEditsChoice?)
		{
			case SaveEditsChoice.Save:
				return SaveEditsNow();
			case SaveEditsChoice.Discard:
				DiscardEditsNow();
				return true;
			default:
				return false;
		}
	}

	/// <summary>Another database is being opened: unsaved edits are saved to this one's layers file first (there is no asking then).</summary>
	private void SaveEditsBeforeSwitching()
	{
		if (!HasUnsavedEdits)
		{
			return;
		}
		string name = _editLayer.Name;
		int n = _session.ChangeCount;
		try
		{
			LayersFile.ApplyEdits(_editLayer.Record.TableName, _session.ToEdits());
			_app.Dialogs.Notify("Map edits saved", $"Saved {(n == 1 ? "1 change" : $"{n} changes")} to {name} before opening the other database.");
		}
		catch (Exception ex) when (ex is GisException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
		{
			AppLog.Write("Couldn't save the map edits before switching databases", ex);
			_app.Dialogs.Notify("Map edits not saved", $"The edits to {name} couldn't be saved before opening the other database.", isError: true);
		}
	}

	// ───────────── A new layer ─────────────

	/// <summary>Add layer, then New layer to draw on: an empty layer of points, lines or areas in the layers file.</summary>
	[RelayCommand]
	private async Task NewLayer()
	{
		ReadLayers();
		if (await _app.Sheets.ShowAsync(new NewLayerSheetViewModel(Layers.Select(l => l.Name).ToList())) is NewLayerSheetViewModel.Result result)
		{
			await AddEmptyLayerAsync(result.Name, result.Kind);
		}
	}

	/// <summary>Makes an empty layer (with "name" and "notes" fields) and starts editing it with its Add tool.</summary>
	public async Task<MapLayerViewModel> AddEmptyLayerAsync(string name, GeometryKind kind)
	{
		ReadLayers();
		if (!await ConfirmLeaveEditsAsync())
		{
			return null;
		}
		FeatureLayer empty = new FeatureLayer(string.IsNullOrWhiteSpace(name) ? "Sketch" : name.Trim(), kind);
		empty.AddField("name", FieldType.Text);
		empty.AddField("notes", FieldType.Text);
		MapLayerRecord row;
		try
		{
			LayersStore store = LayersFile;
			string table = store.NewTableName(empty.Name);
			store.WriteTable(table, empty, description: "Drawn in SDE");
			row = _app.Store.SaveMapLayer(new MapLayerRecord
			{
				Name = empty.Name,
				SourceKind = MapLayerKinds.File,
				Source = null,
				TableName = table,
				GeometryType = kind.ToString(),
				Style = LayerStyle.SingleSymbol(DefaultSymbol(kind, Layers.Count)).ToJson(),
			});
		}
		catch (Exception ex) when (ex is GisException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
		{
			_app.Errors.Show("Couldn't make the layer", ex);
			return null;
		}
		MapLayerViewModel layer = Show(row, empty, null);
		await SetModeCommand.ExecuteAsync(MapMode.Edit);
		StartEditing(layer);
		EditTool = kind switch
		{
			GeometryKind.Point => MapEditTool.AddPoint,
			GeometryKind.Line => MapEditTool.AddLine,
			_ => MapEditTool.AddArea,
		};
		Message = $"Made {layer.Name}. Draw on the map, then Save.";
		return layer;
	}
}
