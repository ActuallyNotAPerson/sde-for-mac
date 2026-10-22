using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Features;
using Sde.Gis.Formats;
using Sde.Gis.Styles;

namespace Sde.App.ViewModels;

/// <summary>The Map page's layers: the basemap, files and ArcGIS layers copied into the layers GeoPackage, FEMA's flood zones.</summary>
public sealed partial class MapViewModel
{
	/// <summary>LayersStore metadata key holding an online layer's copyright text ("Copyright:" + table).</summary>
	private const string CopyrightKey = "Copyright:";

	private LayersStore _layersStore;
	private bool _layersRead;

	/// <summary>The layers, top of the drawing order first (as the Layers panel lists them); structures are always on top.</summary>
	public ObservableCollection<MapLayerViewModel> Layers { get; } = new();

	/// <summary>The row holding the structures' style and labels.</summary>
	public MapLayerViewModel StructuresLayer { get; private set; }

	/// <summary>The map should draw a different set of layers, or a layer changed.</summary>
	public event Action LayersChanged;

	/// <summary>The map should show an area.</summary>
	public event Action<MapExtent> ZoomToRequested;

	/// <summary>The area the map shows now (the page keeps this up to date), for online layers and bookmarks.</summary>
	public MapExtent ViewExtent { get; set; }

	/// <summary>Finishes when the layers' features have been read (tests wait on it).</summary>
	public Task LayersLoaded { get; private set; } = Task.CompletedTask;

	/// <summary>A warning when the layers file was made for another database, or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasLayersWarning))]
	public partial string LayersWarning { get; set; }

	public bool HasLayersWarning => LayersWarning != null;

	/// <summary>The panel shown beside the map: 0 structures, 1 layers.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsStructuresPanel), nameof(IsLayersPanel))]
	public partial int PanelIndex { get; set; }

	public bool IsStructuresPanel => PanelIndex == 0;

	public bool IsLayersPanel => PanelIndex == 1;

	/// <summary>The layers GeoPackage beside the database, opened (and made) on first use.</summary>
	private LayersStore LayersFile
	{
		get
		{
			if (_layersStore == null)
			{
				_layersStore = LayersStore.Open(_app.Store.Database.LayersPath, _app.Store.Database.DatabaseId);
				LayersWarning = _layersStore.Warning;
			}
			return _layersStore;
		}
	}

	/// <summary>
	/// Reads the MapLayer rows, the basemap choice and (in the background) each layer's features. The structures row is
	/// made the first time.
	/// </summary>
	public void ReadLayers()
	{
		if (_layersRead)
		{
			return;
		}
		_layersRead = true;
		List<MapLayerRecord> rows = _app.Store.GetMapLayers();
		MapLayerRecord structures = rows.FirstOrDefault(r => r.SourceKind == MapLayerKinds.Structures)
			?? _app.Store.SaveMapLayer(new MapLayerRecord { Name = "Structures", SourceKind = MapLayerKinds.Structures, GeometryType = "Point", SortOrder = 1_000_000 });
		Layers.Clear();
		StructuresLayer = new MapLayerViewModel(structures, LayerEdited);
		Layers.Add(StructuresLayer);
		foreach (MapLayerRecord row in rows.Where(r => r.SourceKind is MapLayerKinds.File or MapLayerKinds.ArcGisRest or MapLayerKinds.Nfhl).Reverse())
		{
			Layers.Add(new MapLayerViewModel(row, LayerEdited));
		}
		MapLayerRecord basemap = rows.FirstOrDefault(r => r.SourceKind == MapLayerKinds.Basemap);
		if (basemap != null)
		{
			_basemapRow = basemap;
			SetProperty(ref _basemap, Maps.Basemaps.WithId(basemap.Source), nameof(Basemap));
			OnBasemapChanged();
		}
		if (File.Exists(_app.Store.Database.LayersPath))
		{
			try
			{
				LayersWarning = LayersFile.Warning;
			}
			catch (GisException ex)
			{
				LayersWarning = ex.Message;
			}
		}
		List<MapLayerViewModel> toRead = Layers.Where(l => l.IsFeatures).ToList();
		LayersLoaded = toRead.Count == 0 ? Task.CompletedTask : ReadFeaturesAsync(toRead);
		OnPropertyChanged(nameof(StructuresLayer));
		_floodZonesAtStart = false;
		FollowSettings();
		RaiseLayersChanged();
		ReadBookmarks();
	}

	/// <summary>Whether the "start with FEMA flood zones on" setting was on when last looked at (it acts as it turns on).</summary>
	private bool _floodZonesAtStart;

	/// <summary>
	/// Settings, then Map and GIS, changes the database's map settings while this page isn't showing: the background
	/// chosen there, and FEMA's flood zones turned on when that setting is (or turns) on.
	/// </summary>
	private void FollowSettings()
	{
		try
		{
			string id = _app.Store.GetMapBasemapId();
			if (id != null && id != Basemap.Id)
			{
				SetProperty(ref _basemap, Maps.Basemaps.WithId(id), nameof(Basemap));
				BasemapProblem = null;
				OnBasemapChanged();
			}
			bool floodZones = _app.Store.GetMapFloodZonesAtStart();
			if (floodZones && !_floodZonesAtStart)
			{
				// Quietly: the panel stays on the tab it was on.
				int panel = PanelIndex;
				TurnOnFloodZones();
				PanelIndex = panel;
			}
			_floodZonesAtStart = floodZones;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't read the map settings", ex);
		}
	}

	/// <summary>FEMA's flood zones on, added first if the map doesn't have them.</summary>
	private void TurnOnFloodZones()
	{
		if (Layers.FirstOrDefault(l => l.IsTiles) is MapLayerViewModel existing)
		{
			existing.IsVisible = true;
			return;
		}
		MapLayerRecord row = _app.Store.SaveMapLayer(new MapLayerRecord
		{
			Name = TileOverlays.FloodZones.Name,
			SourceKind = MapLayerKinds.Nfhl,
			Source = TileOverlays.FloodZones.UrlTemplate,
			GeometryType = "Polygon",
			Opacity = 0.7,
		});
		Show(row, null, null);
	}

	/// <summary>Another database was opened: its own layers and basemap.</summary>
	private void ResetLayers()
	{
		SaveEditsBeforeSwitching();
		EndSession();
		_editLayer = null;
		EditFeature = null;
		_layersRead = false;
		_layersStore = null;
		_basemapRow = null;
		LayersWarning = null;
		Layers.Clear();
		StructuresLayer = null;
		SelectTarget = null;
		Bookmarks.Clear();
		_basemap = Maps.Basemaps.UsgsTopo;
		OnBasemapChanged();
		if (IsActive)
		{
			ReadLayers();
		}
	}

	private async Task ReadFeaturesAsync(IReadOnlyList<MapLayerViewModel> layers)
	{
		LayersStore store;
		try
		{
			store = LayersFile;
		}
		catch (GisException ex)
		{
			foreach (MapLayerViewModel l in layers)
			{
				l.Problem = ex.Message;
			}
			return;
		}
		foreach (MapLayerViewModel layer in layers)
		{
			string table = layer.Record.TableName;
			(FeatureLayer data, string copyright, string problem) = await Task.Run(() =>
			{
				try
				{
					if (string.IsNullOrEmpty(table) || !store.Exists(table))
					{
						return ((FeatureLayer)null, (string)null, "This layer's features aren't in the map layers file. Remove it and add it again.");
					}
					return (store.ReadTable(table), store.GetMetadata(CopyrightKey + table), (string)null);
				}
				catch (GisException ex)
				{
					return (null, null, ex.Message);
				}
			});
			layer.Copyright = copyright;
			layer.Problem = problem;
			if (data != null)
			{
				layer.Data = data;
			}
		}
		RaiseLayersChanged();
	}

	/// <summary>A layer was ticked, unticked, restyled or made more or less opaque: save it and redraw.</summary>
	private void LayerEdited(MapLayerViewModel layer)
	{
		try
		{
			_app.Store.SaveMapLayer(layer.Record);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't save the map layer " + layer.Name, ex);
		}
		if (layer.IsStructures)
		{
			MarkersChanged?.Invoke();
		}
		RaiseLayersChanged();
	}

	private void RaiseLayersChanged()
	{
		OnPropertyChanged(nameof(Attribution));
		OnPropertyChanged(nameof(HasAttribution));
		OnPropertyChanged(nameof(SelectableLayers));
		OnPropertyChanged(nameof(EditableLayers));
		OnPropertyChanged(nameof(HasEditableLayers));
		LayersChanged?.Invoke();
	}

	/// <summary>What the map draws over the basemap, bottom first (the structures are drawn by the map itself, on top).</summary>
	public IReadOnlyList<object> MapLayers() =>
		Layers.Reverse().Where(l => !l.IsStructures).Select(l => l.ToMapLayer()).Where(o => o != null).ToList();

	/// <summary>The layers whose features can be selected (and shown in the table): structures first, then the feature layers.</summary>
	public IReadOnlyList<MapLayerViewModel> SelectableLayers => Layers.Where(l => l.IsStructures || l.IsFeatures && l.Data != null).ToList();

	/// <summary>The feature layer with a map key, or null.</summary>
	public MapLayerViewModel LayerWithKey(string key) => Layers.FirstOrDefault(l => l.Key == key);

	// ───────────── Adding layers ─────────────

	/// <summary>Add layer, then From a file: a Shapefile, GeoJSON, GeoPackage or CSV, copied into the layers GeoPackage.</summary>
	[RelayCommand]
	private async Task AddFileLayer()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Add a map layer", true, "Map layers",
			"shp", "geojson", "json", "gpkg", "csv", "txt");
		foreach (string file in files)
		{
			await AddFileAsync(file);
		}
	}

	/// <summary>Reads a layer file and adds it (every feature table of a GeoPackage, each as a layer).</summary>
	public async Task<MapLayerViewModel> AddFileAsync(string path)
	{
		ReadLayers();
		JobOutcome<List<(MapLayerRecord Row, FeatureLayer Data)>> outcome = await _app.Jobs.RunAsync("Adding " + Path.GetFileName(path), (progress, cancel) =>
		{
			GisSetup.EnsureInitialized();
			List<FeatureLayer> read = new List<FeatureLayer>();
			if (string.Equals(Path.GetExtension(path), ".gpkg", StringComparison.OrdinalIgnoreCase))
			{
				foreach (GeoPackageTableInfo table in GeoPackageFormat.ListLayers(path))
				{
					read.Add(GeoPackageFormat.Read(path, table.Name));
				}
				if (read.Count == 0)
				{
					throw new GisException("This GeoPackage has no layers of features in it.", path);
				}
			}
			else
			{
				read.Add(GisFile.Read(path));
			}
			List<(MapLayerRecord, FeatureLayer)> added = new List<(MapLayerRecord, FeatureLayer)>();
			foreach (FeatureLayer layer in read)
			{
				cancel.ThrowIfCancellationRequested();
				if (layer.Features.Count == 0)
				{
					throw new GisException($"{layer.Name} has no features with a location, so there is nothing to show.", path);
				}
				added.Add((AddToLayersFile(layer, MapLayerKinds.File, path, null), layer));
			}
			return added;
		}, "Couldn't add the layer");
		if (!outcome.Completed)
		{
			return null;
		}
		MapLayerViewModel last = null;
		foreach ((MapLayerRecord row, FeatureLayer data) in outcome.Value)
		{
			last = Show(row, data, null);
			Message = data.Warnings.Count > 0
				? $"Added {row.Name}. {string.Join(" ", data.Warnings)}"
				: $"Added {row.Name}.";
		}
		if (last?.Data != null)
		{
			ZoomTo(last);
		}
		return last;
	}

	/// <summary>Writes a layer's features into the layers GeoPackage and adds its MapLayer row (on any thread).</summary>
	private MapLayerRecord AddToLayersFile(FeatureLayer layer, string kind, string source, string copyright)
	{
		LayersStore store = LayersFile;
		layer.DetectGeometryKind();
		string table = store.NewTableName(layer.Name);
		store.WriteTable(table, layer, description: source);
		if (!string.IsNullOrWhiteSpace(copyright))
		{
			store.SetMetadata(CopyrightKey + table, copyright.Trim());
		}
		MapLayerRecord row = new MapLayerRecord
		{
			Name = string.IsNullOrWhiteSpace(layer.Name) ? table : layer.Name,
			SourceKind = kind,
			Source = source,
			TableName = table,
			GeometryType = layer.GeometryKind == GeometryKind.Unknown ? null : layer.GeometryKind.ToString(),
			Style = LayerStyle.SingleSymbol(DefaultSymbol(layer.GeometryKind, Layers.Count)).ToJson(),
		};
		return _app.Store.SaveMapLayer(row);
	}

	/// <summary>A first symbol for a new layer: the next colour of the categories palette, light fill for areas.</summary>
	private static Symbol DefaultSymbol(GeometryKind kind, int n)
	{
		Rgba colour = Rgba.Parse(ColorRamp.Categories[n % ColorRamp.Categories.Count], new Rgba(47, 111, 179));
		Symbol s = new Symbol().WithFill(colour);
		if (kind == GeometryKind.Polygon)
		{
			s.Fill = new Rgba(colour.R, colour.G, colour.B, 70).ToHex();
			s.Stroke = colour.ToHex();
			s.StrokeWidth = 1.2;
		}
		else if (kind == GeometryKind.Line)
		{
			s.Stroke = colour.ToHex();
			s.StrokeWidth = 2;
		}
		return s;
	}

	/// <summary>Puts a newly added layer at the top of the panel (just under the structures) and draws it.</summary>
	private MapLayerViewModel Show(MapLayerRecord row, FeatureLayer data, string copyright)
	{
		MapLayerViewModel layer = new MapLayerViewModel(row, LayerEdited) { Copyright = copyright };
		Layers.Insert(Math.Min(1, Layers.Count), layer);
		if (data != null)
		{
			layer.Data = data;
		}
		SaveOrder();
		PanelIndex = 1;
		RaiseLayersChanged();
		return layer;
	}

	/// <summary>Add layer, then From an ArcGIS web address: a FeatureServer or MapServer layer, for the area in view.</summary>
	[RelayCommand]
	private async Task AddOnlineLayer()
	{
		ReadLayers();
		await _app.Sheets.ShowAsync(new OnlineLayerSheetViewModel(_app, ViewExtent, AddOnlineAsync));
	}

	/// <summary>Writes a downloaded ArcGIS layer into the layers GeoPackage and shows it (the online layer sheet calls this).</summary>
	public MapLayerViewModel AddOnline(FeatureLayer data, string url, string copyright)
	{
		ReadLayers();
		MapLayerRecord row = AddToLayersFile(data, MapLayerKinds.ArcGisRest, url, copyright);
		MapLayerViewModel layer = Show(row, data, copyright);
		Message = $"Added {row.Name}.";
		return layer;
	}

	private Task<MapLayerViewModel> AddOnlineAsync(FeatureLayer data, string url, string copyright) =>
		Task.FromResult(AddOnline(data, url, copyright));

	/// <summary>Add layer, then FEMA flood zones: the National Flood Hazard Layer's zones as an overlay.</summary>
	[RelayCommand]
	private void AddFloodZones()
	{
		ReadLayers();
		if (Layers.Any(l => l.IsTiles))
		{
			TurnOnFloodZones();
			PanelIndex = 1;
			return;
		}
		TurnOnFloodZones();
		Message = "Added FEMA flood zones. They show once the map is zoomed in to a town.";
	}

	/// <summary>The layers file was made for another database: the person says these layers belong to this one.</summary>
	[RelayCommand]
	private void KeepLayers()
	{
		try
		{
			LayersFile.ClaimForDatabase();
			LayersWarning = null;
		}
		catch (GisException ex)
		{
			_app.Errors.Show("Couldn't update the map layers file", ex);
		}
	}

	// ───────────── Changing layers ─────────────

	[RelayCommand]
	private async Task RemoveLayer(MapLayerViewModel layer)
	{
		if (layer == null || !layer.CanRemove)
		{
			return;
		}
		if (!await _app.Dialogs.ConfirmAsync("Remove " + layer.Name + "?",
			layer.IsTiles ? "The flood zones overlay is taken off the map. You can add it again." : "The layer and its copy of the features are removed from this database. The original file or service isn't changed.",
			"Remove", destructive: true))
		{
			return;
		}
		try
		{
			_app.Store.DeleteMapLayer(layer.Id);
			if (layer.IsFeatures && !string.IsNullOrEmpty(layer.Record.TableName) && File.Exists(_app.Store.Database.LayersPath))
			{
				LayersFile.DeleteTable(layer.Record.TableName);
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't remove the layer", ex);
			return;
		}
		if (ReferenceEquals(layer, _editLayer))
		{
			// Its edits went with it.
			EndSession();
			_editLayer = null;
			EditFeature = null;
			EditStateChanged();
			OnPropertyChanged(nameof(EditLayer));
		}
		Layers.Remove(layer);
		if (ReferenceEquals(SelectTarget, layer))
		{
			SelectTarget = null;
		}
		if (ReferenceEquals(TableLayer, layer))
		{
			IsTableOpen = false;
			TableLayer = null;
		}
		Message = $"Removed {layer.Name}.";
		RaiseLayersChanged();
	}

	/// <summary>Draws a layer above the one over it (structures stay on top).</summary>
	[RelayCommand]
	private void MoveLayerUp(MapLayerViewModel layer) => MoveLayer(layer, -1);

	[RelayCommand]
	private void MoveLayerDown(MapLayerViewModel layer) => MoveLayer(layer, 1);

	private void MoveLayer(MapLayerViewModel layer, int by)
	{
		int at = Layers.IndexOf(layer);
		int to = at + by;
		if (layer == null || layer.IsStructures || at < 0 || to < 1 || to >= Layers.Count)
		{
			return;
		}
		Layers.Move(at, to);
		SaveOrder();
		RaiseLayersChanged();
	}

	/// <summary>Saves the panel's order as the drawing order (the panel lists the top first).</summary>
	private void SaveOrder()
	{
		List<Guid> bottomFirst = Layers.Reverse().Where(l => !l.IsStructures).Select(l => l.Id).ToList();
		if (_basemapRow != null)
		{
			bottomFirst.Insert(0, _basemapRow.LayerID);
		}
		if (StructuresLayer != null)
		{
			bottomFirst.Add(StructuresLayer.Id);
		}
		try
		{
			_app.Store.SetMapLayerOrder(bottomFirst);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't save the map layers' order", ex);
		}
	}

	[RelayCommand]
	private void ZoomToLayer(MapLayerViewModel layer) => ZoomTo(layer);

	private void ZoomTo(MapLayerViewModel layer)
	{
		if (layer?.Data == null)
		{
			return;
		}
		NetTopologySuite.Geometries.Envelope e = layer.Data.GetExtent();
		if (!e.IsNull)
		{
			ZoomToRequested?.Invoke(new MapExtent(e.MinX, e.MinY, e.MaxX, e.MaxY));
		}
	}

	// ───────────── Basemap ─────────────

	private MapLayerRecord _basemapRow;

	/// <summary>Saves the basemap choice in this database's MapLayer table.</summary>
	private void SaveBasemap()
	{
		if (!_layersRead)
		{
			return;
		}
		try
		{
			_basemapRow = _app.Store.SetMapBasemapId(Basemap.Id);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't save the basemap", ex);
		}
	}

	/// <summary>
	/// The credits the map's sources ask to be shown, in one line: the basemap's, FEMA's when the flood zones are shown,
	/// and each visible online layer's copyright.
	/// </summary>
	public string Attribution
	{
		get
		{
			List<string> parts = new List<string>();
			if (!string.IsNullOrEmpty(Basemap.Attribution))
			{
				parts.Add(Basemap.Attribution);
			}
			foreach (MapLayerViewModel layer in Layers.Where(l => l.IsVisible))
			{
				if (layer.IsTiles)
				{
					parts.Add(TileOverlays.FloodZones.Attribution);
				}
				else if (!string.IsNullOrWhiteSpace(layer.Copyright))
				{
					parts.Add($"{layer.Name}: {layer.Copyright.Trim()}");
				}
			}
			return string.Join(" · ", parts.Distinct());
		}
	}

	public bool HasAttribution => Attribution.Length > 0;
}
