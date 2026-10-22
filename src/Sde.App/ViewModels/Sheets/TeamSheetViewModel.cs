using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>A team colour to choose from (it marks the team on the map and its route).</summary>
public sealed record TeamColour(string Name, string Hex)
{
	public IBrush Brush { get; } = SolidColorBrush.Parse(Hex);
}

/// <summary>Adds a team or changes one: its name, who is on it and its colour. Closes with the saved <see cref="TeamRecord"/>.</summary>
public sealed partial class TeamSheetViewModel : SheetViewModel
{
	/// <summary>Team colours, chosen to stand apart from the result colours (red, amber, green, grey).</summary>
	public static readonly IReadOnlyList<TeamColour> Colours = new[]
	{
		new TeamColour("Blue", "#1E88E5"),
		new TeamColour("Purple", "#8E24AA"),
		new TeamColour("Teal", "#00897B"),
		new TeamColour("Indigo", "#3949AB"),
		new TeamColour("Pink", "#D81B60"),
		new TeamColour("Brown", "#6D4C41"),
	};

	private readonly AppServices _app;
	private readonly TeamRecord _team;

	public TeamSheetViewModel(AppServices app, TeamRecord team) : base(team == null ? "New team" : "Edit team")
	{
		_app = app;
		_team = team;
		Name = team?.Name ?? "";
		Members = team?.Members ?? "";
		// A new team takes the first colour no other team uses.
		HashSet<string> used = app.Store.GetTeams().Select(t => t.Color).Where(c => c != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Colour = Colours.FirstOrDefault(c => string.Equals(c.Hex, team?.Color, StringComparison.OrdinalIgnoreCase))
			?? Colours.FirstOrDefault(c => !used.Contains(c.Hex)) ?? Colours[0];
	}

	public override SheetSize Size => SheetSize.Small;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	public partial string Name { get; set; }

	/// <summary>Who is on the team, as typed ("Inspector A, Inspector B").</summary>
	[ObservableProperty]
	public partial string Members { get; set; }

	[ObservableProperty]
	public partial TeamColour Colour { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; }

	public bool HasError => !string.IsNullOrEmpty(Error);

	private bool CanSave => !string.IsNullOrWhiteSpace(Name);

	[RelayCommand(CanExecute = nameof(CanSave))]
	private void Save()
	{
		TeamRecord team = _team ?? new TeamRecord();
		string oldName = team.Name;
		team.Name = Name;
		team.Members = string.IsNullOrWhiteSpace(Members) ? null : Members.Trim();
		team.Color = Colour?.Hex;
		try
		{
			Close(_app.Store.SaveTeam(team));
		}
		catch (ArgumentException ex)
		{
			team.Name = oldName;
			Error = ex.Message;
		}
	}
}
