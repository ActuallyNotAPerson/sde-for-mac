using Avalonia.Headless.XUnit;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Data;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// Work packages (v2.5, C8) through the app: check assessments out from Structures, see them read-only in the editor,
/// open the package on a "field Mac", change something, send it back, and check it in at the office; or take one back
/// early and copy the field's values over. Writes the screenshots of the checked-out editor and the field banner.
/// </summary>
public class WorkPackagesUiTests
{
	private static UiSession WithSamples()
	{
		UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	private static Guid Row(UiSession ui, string addressStart) =>
		ui.Main.Structures.Rows.First(r => r.Address.StartsWith(addressStart, StringComparison.Ordinal)).AssessmentID!.Value;

	/// <summary>Checks the two River and Mill Creek assessments out to Team North; returns the package's path.</summary>
	private static async Task<string> CheckOutTwo(UiSession ui)
	{
		ui.Main.Structures.Rows.Single(r => r.AssessmentID == Row(ui, "123")).IsChecked = true;
		ui.Main.Structures.Rows.Single(r => r.AssessmentID == Row(ui, "48")).IsChecked = true;
		Task making = ui.Main.Structures.WorkPackageCommand.ExecuteAsync(null);
		ui.Pump();
		CheckOutSheetViewModel sheet = Assert.IsType<CheckOutSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(2, sheet.Count);
		sheet.CheckedOutTo = "Team North";
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		string package = Path.Combine(ui.Folder, "Team North work package.sdework");
		ui.Dialogs.Answers.Enqueue(package);
		await sheet.CreateCommand.ExecuteAsync(null);
		await making;
		Assert.True(File.Exists(package));
		return package;
	}

	/// <summary>Opens a package as the field Mac would, sets an inspector's name, and sends it back.</summary>
	private static async Task<string> FieldWork(UiSession ui, string package, Guid assessment, string inspector)
	{
		ui.Dialogs.Answers.Enqueue(new[] { package });
		await ui.Main.OpenWorkPackageCommand.ExecuteAsync(null);
		ui.Pump();
		Assert.True(ui.Main.IsWorkPackage);
		ui.Store.Apply(assessment, f => f.Assessment.InspectorName = inspector);
		string back = Path.Combine(ui.Folder, "Team North work done.sdework");
		ui.Dialogs.Answers.Enqueue(back);
		await ui.Main.SendBackCommand.ExecuteAsync(null);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Ready to go back"));
		return back;
	}

	[AvaloniaFact]
	public async Task CheckOut_FieldWork_SendBack_CheckIn()
	{
		using UiSession ui = WithSamples();
		string office = ui.Store.Database.FilePath;
		Guid river = Row(ui, "123");
		string package = await CheckOutTwo(ui);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Work package saved: Checked out 2 assessments"));

		// At the office: listed on Import & export, and read-only in the editor.
		Assert.Contains("checked out to Team North", Assert.Single(ui.Main.ImportExport.CheckedOut).Text);
		ui.Main.OpenAssessment(river);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.True(editor.IsCheckedOut);
		Assert.False(editor.IsEditable);
		Assert.StartsWith("Checked out to Team North", editor.CheckOutText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("116-editor-checked-out");
		ui.Main.ShowStructures();
		ui.Pump();

		// In the field: the package opens as a database with a banner, and goes back with the work in it.
		string back = await FieldWork(ui, package, river, "Inspector B");
		Assert.Contains("A work package from", ui.Main.WorkPackageText);
		Assert.Equal(2, ui.Main.Structures.Rows.Count);
		ui.Snapshot("117-work-package-banner");

		// Back at the office: check it in.
		ui.Services.Open(office);
		ui.Pump();
		Assert.False(ui.Main.IsWorkPackage);
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Pump();
		ui.Dialogs.Answers.Enqueue(new[] { back });
		await ui.Main.ImportExport.CheckInCommand.ExecuteAsync(null);
		Assert.StartsWith("Checked in 2 assessments.", ui.Main.ImportExport.LastResult);
		Assert.False(ui.Main.ImportExport.HasCheckedOut);
		Assert.Equal("Inspector B", ui.Store.LoadAssessment(river).Assessment.InspectorName);
		Assert.Null(ui.Store.GetCheckOut(river));
	}

	[AvaloniaFact]
	public async Task TakeBackEarly_ThenCopyTheFieldsValues()
	{
		using UiSession ui = WithSamples();
		string office = ui.Store.Database.FilePath;
		Guid river = Row(ui, "123");
		string package = await CheckOutTwo(ui);
		string back = await FieldWork(ui, package, river, "Inspector B");
		ui.Services.Open(office);
		ui.Pump();

		// Take it back early from the editor's banner.
		ui.Main.OpenAssessment(river);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.True(editor.IsCheckedOut);
		await editor.TakeBackCommand.ExecuteAsync(null);   // the test dialogs say yes
		Assert.False(editor.IsCheckedOut);
		Assert.True(editor.IsEditable);
		Assert.Contains(ui.Store.GetHistory(river), h => h.IsEvent && (h.Label ?? "").StartsWith("Taken back from a work package", StringComparison.Ordinal));

		// Then copy the field's inspector over from the package that came back.
		ui.Dialogs.Answers.Enqueue(new[] { back });
		Task copying = editor.CopyFromPackageCommand.ExecuteAsync(null);
		ui.Pump();
		PackageCompareSheetViewModel sheet = Assert.IsType<PackageCompareSheetViewModel>(ui.Main.Sheets.Current);
		PackageDiffRowViewModel inspector = Assert.Single(sheet.Rows, r => r.Diff.Key.EndsWith(".InspectorName", StringComparison.Ordinal));
		Assert.Equal("Inspector B", inspector.Field);
		foreach (PackageDiffRowViewModel other in sheet.Rows.Where(r => r != inspector))
		{
			other.IsChecked = false;
		}
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.CopyCommand.Execute(null);
		await copying;
		Assert.Equal("Inspector B", ui.Store.LoadAssessment(river).Assessment.InspectorName);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Values copied: 1 value came over"));

		// The package can no longer bring the river assessment back; the other one still checks in.
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Pump();
		ui.Dialogs.Answers.Enqueue(new[] { back });
		await ui.Main.ImportExport.CheckInCommand.ExecuteAsync(null);
		Assert.StartsWith("Checked in 1 assessment.", ui.Main.ImportExport.LastResult);
		Assert.Contains("Not checked in:", ui.Main.ImportExport.LastResult);
	}
}
