using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>The dashboard's field teams chart (C5): each team's structures, assessed and still to do, for the chosen event.</summary>
public sealed partial class DashboardViewModel
{
	public ObservableCollection<BarRowViewModel> ByTeam { get; } = new();

	/// <summary>True when a team has something assigned in scope (the chart is hidden otherwise).</summary>
	[ObservableProperty]
	public partial bool HasTeams { get; private set; }

	private void LoadTeams()
	{
		List<TeamProgress> teams = _app.Store.GetTeamProgress(_app.Scope.IsAll ? null : _app.Scope.EventId).Where(t => t.Assigned > 0).ToList();
		HasTeams = teams.Count > 0;
		Fill(ByTeam, teams.Select(t =>
		{
			int assessed = Math.Min(t.Assessed, t.Assigned);
			string detail = $"{assessed:N0} of {t.Assigned:N0} assessed" + (t.Done > 0 ? $", {t.Done:N0} done" : "") + (t.Skipped > 0 ? $", {t.Skipped:N0} skipped" : "");
			return (t.Name, t.Assigned, detail, new[] { (assessed, "Accent"), (t.Assigned - assessed, "StatusIncompleteFg") });
		}));
	}
}
