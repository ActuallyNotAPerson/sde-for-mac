using Avalonia.Headless.XUnit;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.Data;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The Assignments page (v2.5, C5): make a team, assign structures to it from Structures, order the route, mark a
/// stop done, print the route sheet and take a structure off the team. Writes the page's screenshots.
/// </summary>
public class AssignmentsUiTests
{
	[AvaloniaFact]
	public async Task Team_Assign_OrderRoute_Status_AndFieldKit()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		ui.Pump();
		ui.Main.ShowCommand.Execute(Page.Assignments);
		ui.Pump();
		AssignmentsViewModel page = ui.Main.Assignments;
		Assert.True(page.HasNoTeams);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// A new team.
		Task adding = page.NewTeamCommand.ExecuteAsync(null);
		ui.Pump();
		TeamSheetViewModel sheet = Assert.IsType<TeamSheetViewModel>(ui.Main.Sheets.Current);
		Assert.False(sheet.SaveCommand.CanExecute(null));
		sheet.Name = "Team North";
		sheet.Members = "Inspector A, Inspector B";
		sheet.Colour = TeamSheetViewModel.Colours.Single(c => c.Name == "Teal");
		sheet.SaveCommand.Execute(null);
		await adding;
		ui.Pump();
		Assert.Equal("Team North", page.SelectedTeam.Name);
		Assert.True(page.HasNoStops);

		// Assigned from Structures: every structure in the list.
		ui.Main.ShowCommand.Execute(Page.Structures);
		ui.Pump();
		Task assigning = ui.Main.Structures.AssignToTeamCommand.ExecuteAsync(null);
		ui.Pump();
		AssignTeamSheetViewModel assign = Assert.IsType<AssignTeamSheetViewModel>(ui.Main.Sheets.Current);
		assign.Team = assign.Teams.Single(t => t.Name == "Team North");
		assign.AssignCommand.Execute(null);
		await assigning;
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Assigned: Assigned 3 structures to Team North."));

		ui.Main.ShowCommand.Execute(Page.Assignments);
		ui.Pump();
		Assert.Equal(3, page.Stops.Count);
		Assert.Contains("3 assigned", page.SelectedTeam.ProgressText);

		// Order the route: the two with coordinates first, the one without at the end.
		page.OrderRouteCommand.Execute(null);
		Assert.StartsWith("Route ordered", page.Message);
		Assert.Contains("1 without coordinates is at the end", page.Message);
		Assert.Equal("700 Riverside", page.Stops[2].Address);
		Assert.True(page.Stops[0].HasMapsLink);

		// Mark the first stop done; the team's progress follows.
		AssignmentRowViewModel first = page.Stops[0];
		first.StatusIndex = AssignmentStatuses.All.ToList().IndexOf(AssignmentStatuses.Done);
		Assert.Equal(AssignmentStatuses.Done, ui.Store.GetAssignment(first.Stop.Assignment.AssignmentID).Status);
		Assert.Contains("1 done", page.SelectedTeam.ProgressText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("115-assignments");

		// The route sheet as a PDF.
		string pdf = Path.Combine(ui.Folder, "route.pdf");
		ui.Dialogs.Answers.Enqueue(pdf);
		await page.RouteSheetCommand.ExecuteAsync(null);
		Assert.True(File.Exists(pdf));
		Assert.Contains(pdf, ui.Dialogs.Opened);

		// Directions open in Maps.
		page.DirectionsCommand.Execute(first);
		Assert.Contains(first.MapsLink, ui.Dialogs.Opened);

		// Take one off the team.
		page.Stops[2].IsChecked = true;
		await page.UnassignCommand.ExecuteAsync(null);   // the test dialogs say yes
		Assert.Equal(2, page.Stops.Count);
		Assert.Equal("Took 1 structure off the team.", page.Message);
	}
}
