using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Core;
using Sde.Core.Field;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports;

namespace Sde.App.ViewModels;

/// <summary>One team in the Assignments page's list, with its progress in the chosen event.</summary>
public sealed partial class TeamRowViewModel(TeamRecord team, TeamProgress progress) : ObservableObject
{
	private TeamProgress _progress = progress;

	public TeamRecord Team { get; } = team;

	public string Name => Team.Name;

	public string Members => string.IsNullOrWhiteSpace(Team.Members) ? "No members listed" : Team.Members;

	public IBrush Colour { get; } = SolidColorBrush.Parse(string.IsNullOrWhiteSpace(team.Color) ? TeamSheetViewModel.Colours[0].Hex : team.Color);

	public int Assigned => _progress?.Assigned ?? 0;

	/// <summary>"12 assigned · 8 assessed · 3 done".</summary>
	public string ProgressText => Assigned == 0
		? "Nothing assigned"
		: string.Join(" · ", new[]
		{
			$"{Assigned} assigned",
			$"{_progress.Assessed} assessed",
			_progress.Done > 0 ? $"{_progress.Done} done" : null,
			_progress.Skipped > 0 ? $"{_progress.Skipped} skipped" : null,
		}.Where(x => x != null));

	/// <summary>Assessed as a share of assigned, for the progress bar (0 to 1).</summary>
	public double Fraction => Assigned == 0 ? 0 : Math.Clamp((double)_progress.Assessed / Assigned, 0, 1);

	/// <summary>Takes new numbers without replacing the row (so the list keeps its selection).</summary>
	public void Update(TeamProgress progress)
	{
		_progress = progress;
		OnPropertyChanged(nameof(Assigned));
		OnPropertyChanged(nameof(ProgressText));
		OnPropertyChanged(nameof(Fraction));
	}
}

/// <summary>One structure on a team's route: where it is in the order, its result and the assignment's status.</summary>
public sealed partial class AssignmentRowViewModel : ObservableObject
{
	private readonly Action<AssignmentRowViewModel, string> _setStatus;
	private bool _loading;

	public AssignmentRowViewModel(FieldKitStop stop, string mapsLink, Action<AssignmentRowViewModel, string> setStatus)
	{
		Stop = stop;
		MapsLink = mapsLink;
		_setStatus = setStatus;
		_loading = true;
		StatusIndex = Math.Max(0, AssignmentStatuses.All.ToList().IndexOf(stop.Assignment.Status ?? AssignmentStatuses.Assigned));
		_loading = false;
	}

	public FieldKitStop Stop { get; }

	public int Number => Stop.Number;

	public string Address
	{
		get
		{
			string address = Stop.Latest?.Address ?? $"{Stop.Property.StreetNumber} {Stop.Property.StreetName}".Trim();
			return string.IsNullOrWhiteSpace(address) ? "Structure with no address" : address;
		}
	}

	public string Result => Stop.Latest?.AssessmentID == null ? "Not assessed yet" : Stop.Latest.DamageMessage ?? "Data Entry Incomplete";

	public string Code => Stop.Assignment.ShortCode;

	public string Due => Stop.Assignment.DueDate is DateTime d ? d.ToString("MMM d", CultureInfo.InvariantCulture) : "";

	/// <summary>Directions in Apple Maps (null when the structure has neither coordinates nor an address).</summary>
	public string MapsLink { get; }

	public bool HasMapsLink => MapsLink != null;

	public Guid? AssessmentId => Stop.Latest?.AssessmentID;

	public string OpenName => "Open " + Address;

	public string StatusName => "Status of " + Address;

	public string MapsName => "Directions to " + Address;

	[ObservableProperty]
	public partial bool IsChecked { get; set; }

	/// <summary>An index into <see cref="AssignmentStatuses.All"/>: Assigned, Visited, Done or Skipped.</summary>
	[ObservableProperty]
	public partial int StatusIndex { get; set; }

	partial void OnStatusIndexChanged(int value)
	{
		if (!_loading && value >= 0 && value < AssignmentStatuses.All.Count)
		{
			_setStatus(this, AssignmentStatuses.All[value]);
		}
	}
}

/// <summary>
/// The Assignments page (C5): the teams with their progress in the chosen event, and the selected team's structures in
/// route order, each with its status, worksheet code and directions. Order the route, print the route sheet and the
/// field worksheets, or take structures off the team. Structures are assigned from Structures or the map.
/// </summary>
public sealed partial class AssignmentsViewModel : ViewModelBase
{
	public static IReadOnlyList<string> StatusChoices => AssignmentStatuses.All;

	private readonly AppServices _app;
	private readonly MainViewModel _main;

	public AssignmentsViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
		app.Scope.Changed += () =>
		{
			if (IsActive)
			{
				Reload();
			}
		};
	}

	/// <summary>Set by the window while this is the page showing (it then reloads when the data changes).</summary>
	public bool IsActive { get; set; }

	public ObservableCollection<TeamRowViewModel> Teams { get; } = new();

	public ObservableCollection<AssignmentRowViewModel> Stops { get; } = new();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasTeam), nameof(TeamTitle), nameof(TeamDetail))]
	[NotifyCanExecuteChangedFor(nameof(EditTeamCommand), nameof(RemoveTeamCommand), nameof(OrderRouteCommand), nameof(RouteSheetCommand), nameof(WorksheetsCommand), nameof(KeepOfflineCommand))]
	public partial TeamRowViewModel SelectedTeam { get; set; }

	public bool HasTeams => Teams.Count > 0;

	public bool HasNoTeams => Teams.Count == 0;

	public bool HasTeam => SelectedTeam != null;

	public bool HasStops => Stops.Count > 0;

	public bool HasNoStops => SelectedTeam != null && Stops.Count == 0;

	public string TeamTitle => SelectedTeam?.Name;

	public string TeamDetail => SelectedTeam == null ? null : SelectedTeam.Members + " · " + SelectedTeam.ProgressText;

	/// <summary>"Who inspects what, and in what order." with the event when one is chosen.</summary>
	public string Subtitle => _app.Scope.IsAll
		? "Who inspects what, and in what order, in every event."
		: $"Who inspects what, and in what order, in {_app.Scope.Event.Label}.";

	/// <summary>A line after an action ("Route ordered: 3.2 miles").</summary>
	[ObservableProperty]
	public partial string Message { get; set; }

	private Guid? EventId => _app.Scope.IsAll ? null : _app.Scope.EventId;

	/// <summary>Reads the teams and the selected team's route again.</summary>
	public void Reload()
	{
		Guid? keep = SelectedTeam?.Team.TeamID;
		Dictionary<Guid, TeamProgress> progress = _app.Store.GetTeamProgress(EventId).ToDictionary(p => p.TeamID);
		Teams.Clear();
		foreach (TeamRecord t in _app.Store.GetTeams())
		{
			Teams.Add(new TeamRowViewModel(t, progress.GetValueOrDefault(t.TeamID)));
		}
		SelectedTeam = Teams.FirstOrDefault(t => t.Team.TeamID == keep) ?? Teams.FirstOrDefault();
		OnPropertyChanged(nameof(HasTeams));
		OnPropertyChanged(nameof(HasNoTeams));
		OnPropertyChanged(nameof(Subtitle));
		LoadStops();
	}

	partial void OnSelectedTeamChanged(TeamRowViewModel value) => LoadStops();

	private void LoadStops()
	{
		Stops.Clear();
		if (SelectedTeam != null)
		{
			ReferenceData reference = _app.Store.Reference;
			foreach (FieldKitStop stop in FieldKit.Stops(_app.Store, SelectedTeam.Team.TeamID, EventId))
			{
				Stops.Add(new AssignmentRowViewModel(stop, FieldKit.MapsLink(stop.Property, reference), SetStatus));
			}
		}
		OnPropertyChanged(nameof(HasStops));
		OnPropertyChanged(nameof(HasNoStops));
	}

	private void SetStatus(AssignmentRowViewModel row, string status)
	{
		_app.Store.SetAssignmentStatus(row.Stop.Assignment.AssignmentID, status);
		row.Stop.Assignment.Status = status;
		Message = $"{row.Address}: {status}.";
		Dictionary<Guid, TeamProgress> progress = _app.Store.GetTeamProgress(EventId).ToDictionary(p => p.TeamID);
		foreach (TeamRowViewModel t in Teams)
		{
			t.Update(progress.GetValueOrDefault(t.Team.TeamID));
		}
		OnPropertyChanged(nameof(TeamDetail));
	}

	[RelayCommand]
	private async Task NewTeam()
	{
		if (await _app.Sheets.ShowAsync(new TeamSheetViewModel(_app, null)) is TeamRecord saved)
		{
			Reload();
			SelectedTeam = Teams.FirstOrDefault(t => t.Team.TeamID == saved.TeamID);
		}
	}

	[RelayCommand(CanExecute = nameof(HasTeam))]
	private async Task EditTeam()
	{
		if (await _app.Sheets.ShowAsync(new TeamSheetViewModel(_app, SelectedTeam.Team)) is TeamRecord)
		{
			Reload();
		}
	}

	[RelayCommand(CanExecute = nameof(HasTeam))]
	private async Task RemoveTeam()
	{
		TeamRowViewModel team = SelectedTeam;
		if (!await _app.Dialogs.ConfirmAsync($"Remove {team.Name}?",
			"The team leaves the lists. What it was assigned stays in each structure's history.", "Remove", destructive: true))
		{
			return;
		}
		_app.Store.RemoveTeam(team.Team.TeamID);
		SelectedTeam = null;
		Reload();
		Message = $"Removed {team.Name}.";
	}

	/// <summary>Puts the team's structures in a short driving order (straight-line distance) and saves it.</summary>
	[RelayCommand(CanExecute = nameof(HasTeam))]
	private void OrderRoute()
	{
		if (Stops.Count == 0)
		{
			return;
		}
		RoutePlan plan = RouteOrder.Plan(Stops.Select(s => new RouteStop(s.Stop.Assignment.AssignmentID, (double?)s.Stop.Property.Latitude, (double?)s.Stop.Property.Longitude, s.Address)));
		_app.Store.SaveRouteOrder(plan.Stops.Select(s => s.Id).ToList());
		LoadStops();
		double miles = plan.Meters / 1609.344;
		Message = $"Route ordered: about {miles.ToString(miles < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} miles in a straight line."
			+ (plan.WithoutPosition > 0 ? $" {plan.WithoutPosition} without coordinates {(plan.WithoutPosition == 1 ? "is" : "are")} at the end." : "");
	}

	[RelayCommand(CanExecute = nameof(HasTeam))]
	private Task RouteSheet() => WriteKitAsync("route sheet", (team, stops, options, path) => FieldKit.WriteRouteSheet(team, stops, _app.Store.Reference, options, path));

	/// <summary>A page per structure, each with a small map around it (from the map's background, when it has one).</summary>
	[RelayCommand(CanExecute = nameof(HasTeam))]
	private Task Worksheets() => WriteKitAsync("worksheets", (team, stops, options, path) => FieldKit.WriteWorksheets(team, stops, _app.Store.Reference, options, path), withMaps: true);

	/// <summary>How much ground a worksheet's map shows across, in metres (about 820 ft).</summary>
	public const double WorksheetMapMetres = 250;

	/// <summary>How long a worksheet's map waits for its background pieces.</summary>
	public static TimeSpan WorksheetMapWait { get; set; } = TimeSpan.FromSeconds(5);

	private static readonly IBrush WorksheetDot = new ImmutableSolidColorBrush(Color.Parse("#C62828"));

	/// <summary>
	/// A small map for each structure with coordinates, by assignment: the map's background around it with its dot.
	/// When the first one gets none of its background (no connection), the rest are left without a map.
	/// </summary>
	private async Task<Dictionary<Guid, byte[]>> WorksheetMapsAsync(IReadOnlyList<FieldKitStop> stops)
	{
		Dictionary<Guid, byte[]> maps = new Dictionary<Guid, byte[]>();
		Basemap basemap = _main.Map.Basemap;
		if (basemap?.HasTiles != true)
		{
			return maps;
		}
		const double widthInches = 7.0;
		foreach (FieldKitStop s in stops)
		{
			if (s.Property.Latitude is not decimal lat || s.Property.Longitude is not decimal lon || (lat == 0 && lon == 0))
			{
				continue;
			}
			(double x, double y) = WebMercator.ToMetres((double)lon, (double)lat);
			MapPrintScene scene = new MapPrintScene
			{
				CenterX = x,
				CenterY = y,
				Resolution = WorksheetMapMetres / Math.Cos((double)lat * Math.PI / 180) / (widthInches * 96),
				ScreenWidth = widthInches * 96,
				ScreenHeight = FieldKit.MapHeight * 96,
				Basemap = basemap,
				Markers = new[] { new MapMarker(s.Property.PropertyID, (double)lat, (double)lon, WorksheetDot) },
			};
			MapPrintImage image = await MapPrinter.RenderAsync(scene, _main.Map.Tiles, widthInches, FieldKit.MapHeight, WorksheetMapWait, CancellationToken.None);
			if (image.TilesWanted > 0 && image.TilesMissing == image.TilesWanted)
			{
				break;
			}
			maps[s.Assignment.AssignmentID] = image.Png;
		}
		return maps;
	}

	/// <summary>Keep offline: the USGS map around the team's structures, kept on this Mac for the field.</summary>
	[RelayCommand(CanExecute = nameof(HasTeam))]
	private async Task KeepOffline()
	{
		List<PropertyRecord> placed = Stops.Select(s => s.Stop.Property)
			.Where(p => p.Latitude is decimal lat && p.Longitude is decimal lon && !(lat == 0 && lon == 0)).ToList();
		if (placed.Count == 0)
		{
			_app.Dialogs.Notify("No area to keep", $"None of {SelectedTeam.Name}'s structures has coordinates yet.");
			return;
		}
		const double margin = 0.004; // about 400 m
		OfflineAreaSheetViewModel sheet = new OfflineAreaSheetViewModel(_main.Map.Tiles,
			(double)placed.Min(p => p.Longitude!.Value) - margin, (double)placed.Min(p => p.Latitude!.Value) - margin,
			(double)placed.Max(p => p.Longitude!.Value) + margin, (double)placed.Max(p => p.Latitude!.Value) + margin,
			_main.Map.Basemap, $"{SelectedTeam.Name}'s structures, and about 400 m around them");
		if (await _app.Sheets.ShowAsync(sheet) is string done)
		{
			Message = done;
		}
	}

	private async Task WriteKitAsync(string what, Action<TeamRecord, IReadOnlyList<FieldKitStop>, FieldKitOptions, string> write, bool withMaps = false)
	{
		TeamRecord team = SelectedTeam.Team;
		List<FieldKitStop> stops = Stops.Select(s => s.Stop).ToList();
		if (stops.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to print", $"{team.Name} has no structures assigned.");
			return;
		}
		string path = await _app.Dialogs.SaveFileAsync($"Save the {what}", $"{team.Name} {what}.pdf", "PDF document", "pdf");
		if (path == null)
		{
			return;
		}
		Dictionary<Guid, byte[]> maps = withMaps ? await WorksheetMapsAsync(stops) : new Dictionary<Guid, byte[]>();
		FieldKitOptions options = new FieldKitOptions
		{
			CommunityName = _app.Store.GetDefaults()?.NFIPCommunityName,
			EventName = _app.Scope.Event?.Label,
			MapSnippet = s => maps.GetValueOrDefault(s.Assignment.AssignmentID),
		};
		try
		{
			await Task.Run(() => write(team, stops, options, path));
			_app.Dialogs.Open(path);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show($"Couldn't make the {what}", ex);
		}
	}

	/// <summary>Takes the ticked structures (or the selected one) off the team.</summary>
	[RelayCommand]
	private async Task Unassign()
	{
		List<AssignmentRowViewModel> rows = Stops.Where(s => s.IsChecked).ToList();
		if (rows.Count == 0)
		{
			return;
		}
		string what = rows.Count == 1 ? rows[0].Address : $"{rows.Count} structures";
		if (!await _app.Dialogs.ConfirmAsync($"Take {what} off {SelectedTeam.Name}?", "They stay in the database and can be assigned again.", "Take off"))
		{
			return;
		}
		int removed = 0;
		foreach (IGrouping<Guid?, AssignmentRowViewModel> byEvent in rows.GroupBy(r => r.Stop.Assignment.EventID))
		{
			removed += _app.Store.Unassign(byEvent.Select(r => r.Stop.Property.PropertyID).ToList(), byEvent.Key, SaveContext.Batch(SaveVia.App));
		}
		Reload();
		Message = removed == 1 ? "Took 1 structure off the team." : $"Took {removed} structures off the team.";
	}

	[RelayCommand]
	private void Open(AssignmentRowViewModel row)
	{
		if (row?.AssessmentId is Guid id)
		{
			_main.OpenAssessment(id);
		}
	}

	[RelayCommand]
	private void Directions(AssignmentRowViewModel row)
	{
		if (row?.MapsLink != null)
		{
			_app.Dialogs.Open(row.MapsLink);
		}
	}
}
