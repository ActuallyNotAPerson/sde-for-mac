using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>One structure on the map page (also one row of the list beside the map).</summary>
public sealed class MapPointViewModel(MapPointRow row, DeterminationClass resultClass)
{
	public MapPointRow Row { get; } = row;

	/// <summary>The structure's ID; the map uses it to say which dot was clicked.</summary>
	public Guid Id => Row.PropertyID;

	public Guid? AssessmentID => Row.AssessmentID;

	public DeterminationClass Class { get; } = resultClass;

	public string Address => string.IsNullOrWhiteSpace(Row.Address) ? "(no address)" : Row.Address;

	public string ResultText => Class switch
	{
		DeterminationClass.Incomplete => Row.AssessmentID == null ? "No assessment" : "Incomplete",
		DeterminationClass.Undetermined => "Undetermined",
		_ => $"{Determinations.Label(Class)} {Row.PercentDamage:0.#}%",
	};

	/// <summary>The line under the address in the list.</summary>
	public string Detail => string.IsNullOrWhiteSpace(Row.City) ? ResultText : $"{ResultText} · {Row.City}";

	/// <summary>What a screen reader says for the row.</summary>
	public string SpokenName => $"{Address}, {Detail}";

	public bool HasLocation => Row.Latitude is decimal lat && Row.Longitude is decimal lon
		&& !(lat == 0m && lon == 0m) && Math.Abs(lat) <= 90m && Math.Abs(lon) <= 180m;

	public double Latitude => (double)(Row.Latitude ?? 0m);

	public double Longitude => (double)(Row.Longitude ?? 0m);
}

/// <summary>A result class shown in the legend. Ticking it shows its dots, and unticking hides them.</summary>
public sealed partial class MapClassViewModel(DeterminationClass resultClass, string label, string rule, Action changed) : ObservableObject
{
	public DeterminationClass Class { get; } = resultClass;

	public string Label { get; } = label;

	/// <summary>"50% or more"; shown under the label.</summary>
	public string Rule { get; } = rule;

	/// <summary>The name of the colour in the app's resources (the same colours as the Structures tiles).</summary>
	public string ColourKey => "Status" + Class + "Fg";

	[ObservableProperty]
	public partial bool IsOn { get; set; } = true;

	[ObservableProperty]
	public partial int Count { get; set; }

	public string SpokenName => $"{Label}: {Count} on the map";

	partial void OnIsOnChanged(bool value) => changed();

	partial void OnCountChanged(int value) => OnPropertyChanged(nameof(SpokenName));
}

/// <summary>What clicking and dragging on the map does.</summary>
public enum MapMode
{
	/// <summary>Click a dot to open its assessment. Command-drag draws a selection box.</summary>
	Browse,

	/// <summary>Drag a box to check the structures inside it in the Structures list.</summary>
	Select,

	/// <summary>Click the map to give a structure its latitude and longitude.</summary>
	SetLocation,
}

/// <summary>
/// The Map page: every structure that has coordinates as a dot coloured by its result, with the same classes as the
/// Structures tiles. Click a dot to open the assessment, draw a box to check structures in the list, or switch to
/// "Set location" and click to give a structure its coordinates.
/// </summary>
public sealed partial class MapViewModel : ViewModelBase
{
	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private List<MapPointViewModel> _all = new();

	public MapViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
		Classes = new ObservableCollection<MapClassViewModel>
		{
			new MapClassViewModel(DeterminationClass.Substantial, "Substantially damaged", "50% or more", Apply),
			new MapClassViewModel(DeterminationClass.Close, "Close to the threshold", "40% to 50%", Apply),
			new MapClassViewModel(DeterminationClass.NotSubstantial, "Not substantially damaged", "Under 40%", Apply),
			new MapClassViewModel(DeterminationClass.Undetermined, "Undetermined", "Damage could not be determined", Apply),
			new MapClassViewModel(DeterminationClass.Incomplete, "Incomplete", "No assessment or data entry incomplete", Apply),
		};
		Tiles = new TileCache(TileFolder(app));
		Tiles.DownloadFailed += reason => BasemapProblem = reason + " The structures are still shown.";
		app.DataChanged += () =>
		{
			if (IsActive)
			{
				Reload();
			}
		};
		app.DatabaseChanged += ResetLayers;
		_basemap = Maps.Basemaps.UsgsTopo;
	}

	/// <summary>Where downloaded tiles are kept: next to the app's other files (or the test database's folder).</summary>
	private static string TileFolder(AppServices app)
	{
		string forced = Environment.GetEnvironmentVariable("SDE_DATABASE");
		string root = string.IsNullOrEmpty(forced) ? Path.GetDirectoryName(SdeDatabase.DefaultPath)! : Path.GetDirectoryName(app.Store.Database.FilePath)!;
		return Path.Combine(root, "map-tiles");
	}

	/// <summary>Set by the window while this is the page showing, so changes elsewhere only reload a map that is on screen.</summary>
	public bool IsActive { get; set; }

	public TileCache Tiles { get; }

	public ObservableCollection<MapClassViewModel> Classes { get; }

	/// <summary>The structures with coordinates that the legend ticks allow, which the map and the list show.</summary>
	public ObservableCollection<MapPointViewModel> Visible { get; } = new();

	/// <summary>Structures without coordinates, for "Set location".</summary>
	public ObservableCollection<MapPointViewModel> Unplaced { get; } = new();

	public IReadOnlyList<Basemap> Basemaps => Maps.Basemaps.All;

	private Basemap _basemap;

	/// <summary>The basemap, kept for each database (in its MapLayer table).</summary>
	public Basemap Basemap
	{
		get => _basemap;
		set
		{
			if (SetProperty(ref _basemap, value ?? Maps.Basemaps.None))
			{
				BasemapProblem = null;
				OnBasemapChanged();
				SaveBasemap();
			}
		}
	}

	private void OnBasemapChanged()
	{
		OnPropertyChanged(nameof(Attribution));
		OnPropertyChanged(nameof(HasAttribution));
		OnPropertyChanged(nameof(UsesInternet));
	}

	/// <summary>True while a basemap that is downloaded is chosen (a note says so, and that "No basemap" works offline).</summary>
	public bool UsesInternet => Basemap.HasTiles;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasBasemapProblem))]
	public partial string BasemapProblem { get; set; }

	public bool HasBasemapProblem => BasemapProblem != null;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBrowse), nameof(IsSelect), nameof(IsSetLocation), nameof(Hint))]
	public partial MapMode Mode { get; set; }

	public bool IsBrowse => Mode == MapMode.Browse;

	public bool IsSelect => Mode == MapMode.Select;

	public bool IsSetLocation => Mode == MapMode.SetLocation;

	/// <summary>One line of help for the current mode.</summary>
	public string Hint => Mode switch
	{
		MapMode.Select => "Drag a box around the structures to check in the Structures list. Hold Command to add to what is already checked.",
		MapMode.SetLocation => PlaceTarget == null
			? "Choose a structure, then click the map where it is. You can also click a dot to move that structure."
			: $"Click the map where {PlaceTarget.Address} is.",
		_ => "Click a dot to open its assessment. Drag to move the map, scroll to zoom. Command-drag selects structures.",
	};

	[ObservableProperty]
	public partial MapPointViewModel Selected { get; set; }

	/// <summary>The structure that the next click in "Set location" gives coordinates to.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Hint), nameof(HasPlaceTarget))]
	public partial MapPointViewModel PlaceTarget { get; set; }

	public bool HasPlaceTarget => PlaceTarget != null;

	/// <summary>What happened last ("Placed 12 Oak Street."), read out and shown under the controls.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMessage))]
	public partial string Message { get; set; }

	public bool HasMessage => Message != null;

	[ObservableProperty]
	public partial int TotalCount { get; set; }

	[ObservableProperty]
	public partial int OnMapCount { get; set; }

	[ObservableProperty]
	public partial int ShownCount { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasUnplaced), nameof(UnplacedText))]
	public partial int UnplacedCount { get; set; }

	public bool HasUnplaced => UnplacedCount > 0;

	public string UnplacedText => UnplacedCount == 1 ? "1 structure has no coordinates" : $"{UnplacedCount} structures have no coordinates";

	public bool HasStructures => TotalCount > 0;

	public string CountText => $"{ShownCount} of {OnMapCount} on the map";

	/// <summary>The map should redraw its dots.</summary>
	public event Action MarkersChanged;

	/// <summary>The map should zoom to show every dot.</summary>
	public event Action FitRequested;

	/// <summary>The map should move to a structure.</summary>
	public event Action<MapPointViewModel> CenterRequested;

	/// <summary>Reads the structures again. Keeps the legend ticks and the mode.</summary>
	public void Reload()
	{
		ReadLayers();
		Guid? selected = Selected?.Id;
		Guid? target = PlaceTarget?.Id;
		_all = _app.Store.ListMapPoints()
			.Select(r => new MapPointViewModel(r, Determinations.Classify(r.DamageMessage, r.PercentDamage)))
			.ToList();
		TotalCount = _all.Count;
		OnPropertyChanged(nameof(HasStructures));
		Unplaced.Clear();
		foreach (MapPointViewModel p in _all.Where(p => !p.HasLocation))
		{
			Unplaced.Add(p);
		}
		UnplacedCount = Unplaced.Count;
		OnMapCount = _all.Count - UnplacedCount;
		foreach (MapClassViewModel c in Classes)
		{
			c.Count = _all.Count(p => p.HasLocation && p.Class == c.Class);
		}
		Apply();
		Selected = selected == null ? null : Visible.FirstOrDefault(p => p.Id == selected);
		PlaceTarget = (target == null ? null : _all.FirstOrDefault(p => p.Id == target)) ?? Unplaced.FirstOrDefault();
	}

	/// <summary>Asks the map to zoom to everything (when the page is opened).</summary>
	public void RequestFit() => FitRequested?.Invoke();

	private void Apply()
	{
		HashSet<DeterminationClass> on = Classes.Where(c => c.IsOn).Select(c => c.Class).ToHashSet();
		// Least severe first, so the substantially damaged dots are drawn on top.
		IEnumerable<MapPointViewModel> shown = _all.Where(p => p.HasLocation && on.Contains(p.Class)).OrderBy(p => p.Class);
		Visible.Clear();
		foreach (MapPointViewModel p in shown)
		{
			Visible.Add(p);
		}
		ShownCount = Visible.Count;
		OnPropertyChanged(nameof(CountText));
		_structureFeatures = null;
		MarkersChanged?.Invoke();
		StructuresChanged();
	}

	partial void OnSelectedChanged(MapPointViewModel value)
	{
		if (value != null)
		{
			CenterRequested?.Invoke(value);
		}
	}

	partial void OnModeChanged(MapMode value) => Message = null;

	// ───────────── Commands ─────────────

	[RelayCommand]
	private void SetMode(MapMode mode) => Mode = mode;

	[RelayCommand]
	private void ShowAll()
	{
		foreach (MapClassViewModel c in Classes)
		{
			c.IsOn = true;
		}
	}

	[RelayCommand]
	private void FitToStructures() => RequestFit();

	/// <summary>Opens the assessment of a structure (or starts its first assessment when it has none).</summary>
	public void OpenStructure(Guid propertyId)
	{
		MapPointViewModel p = _all.FirstOrDefault(x => x.Id == propertyId);
		if (p == null)
		{
			return;
		}
		if (p.AssessmentID is Guid id)
		{
			_main.OpenAssessment(id);
		}
		else if (_app.Store.GetProperty(p.Id) is PropertyRecord record)
		{
			_main.OpenDraft(_app.Store.NewDraft(record, _app.Store.GetPreferences().UseDefaults));
		}
	}

	[RelayCommand]
	private void OpenSelected()
	{
		if (Selected != null)
		{
			OpenStructure(Selected.Id);
		}
	}

	/// <summary>A dot was clicked: opens it, or in "Set location" picks it as the structure to move.</summary>
	public void ClickStructure(Guid propertyId, bool add = false)
	{
		if (Mode != MapMode.SetLocation)
		{
			OpenStructure(propertyId);
			return;
		}
		PlaceTarget = _all.FirstOrDefault(p => p.Id == propertyId);
		Message = null;
	}

	/// <summary>
	/// An empty spot was clicked. In "Set location" this saves the latitude and longitude of the chosen structure
	/// (as an edit in its history, through the same path as every other change made outside the editor).
	/// </summary>
	public void ClickMap(double latitude, double longitude)
	{
		if (Mode != MapMode.SetLocation || PlaceTarget == null)
		{
			return;
		}
		MapPointViewModel target = PlaceTarget;
		decimal lat = Math.Round((decimal)latitude, 6);
		decimal lon = Math.Round((decimal)longitude, 6);
		try
		{
			PropertyRecord p = _app.Store.GetProperty(target.Id);
			if (p == null)
			{
				Message = "That structure no longer exists.";
				return;
			}
			p.Latitude = lat;
			p.Longitude = lon;
			// Matched by structure: an editor showing any assessment of it (not only the newest) catches up.
			_app.Writes.WriteForStructures(new[] { p.PropertyID }, () => _app.Store.SaveProperty(p, new SaveContext { Via = SaveVia.Map }));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the location", ex);
			return;
		}
		// The write reloaded the page. Move on to the next structure that still needs a location.
		Message = $"Saved the location of {target.Address}: {lat:0.000000}, {lon:0.000000}.";
		PlaceTarget = Unplaced.FirstOrDefault();
		MapPointViewModel placed = _all.FirstOrDefault(x => x.Id == target.Id);
		if (placed != null && Visible.Contains(placed))
		{
			Selected = placed;
		}
	}

	/// <summary>
	/// A box was drawn: checks those structures in the Structures list (replacing what was checked, unless
	/// <paramref name="add"/> because Command was held).
	/// </summary>
	public void SelectArea(IReadOnlyCollection<Guid> propertyIds, bool add)
	{
		SelectStructures(propertyIds, add);
		int n = _main.Structures.CheckedCount;
		Message = n == 0 ? "No assessed structures in that box." : $"{n} structure{(n == 1 ? " is" : "s are")} checked in the Structures list.";
	}

	public int CheckedCount => _main.Structures.CheckedCount;

	public bool HasChecked => CheckedCount > 0;

	[RelayCommand]
	private void ShowChecked() => _main.ShowCommand.Execute(Page.Structures);

	[RelayCommand]
	private void ClearChecked()
	{
		SelectStructures(Array.Empty<Guid>(), add: false);
		Message = "Nothing is checked in the Structures list.";
	}

	/// <summary>The link under "N structures have no coordinates": the page with the bulk coordinate tools.</summary>
	[RelayCommand]
	private void ShowCoordinateTools() => _main.ShowCommand.Execute(Page.ImportExport);
}
