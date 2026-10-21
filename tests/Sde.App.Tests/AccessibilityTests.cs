using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.Core.Assessments;

namespace Sde.App.Tests;

/// <summary>
/// Every input the user can reach must have an accessible name, which is what VoiceOver reads and what assistive and
/// automation tools find it by. Avalonia names a button after its text, but an input needs AutomationProperties.Name
/// (a visible label beside it isn't linked to it automatically).
/// </summary>
public class AccessibilityTests
{
	internal static IEnumerable<string> Unnamed(Window window) =>
		window.GetVisualDescendants().OfType<Control>()
			.Where(c => c.IsEffectivelyVisible && c is TextBox or NumericUpDown or ComboBox or CalendarDatePicker or Slider or ToggleButton or Button)
			// Inner parts belong to their control, except the text box inside a NumericUpDown or CalendarDatePicker: that is
			// the element macOS exposes (and VoiceOver reads), so it must carry the name.
			.Where(c => c.FindAncestorOfType<Slider>() == null &&
				(c.FindAncestorOfType<NumericUpDown>() == null && c.FindAncestorOfType<CalendarDatePicker>() == null || c is TextBox))
			.Where(c => string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(c).GetName()))
			.Select(c => $"{c.GetType().Name} in {c.FindAncestorOfType<UserControl>()?.GetType().Name} (DataContext {c.DataContext?.GetType().Name})");

	[AvaloniaFact]
	public async Task EveryInputHasAnAccessibleName()
	{
		using UiSession ui = new UiSession(height: 1600);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		List<string> problems = new();
		void Check(string page)
		{
			ui.Pump();
			problems.AddRange(Unnamed(ui.Window).Distinct().Select(p => page + ": " + p));
		}

		Check("Structures");
		ui.Main.Structures.OpenSubmissionCommand.Execute(null);
		Check("Structures (submission folder)");
		ui.Main.Structures.Submission.CancelCommand.Execute(null);
		AssessmentFile f = ui.Store.LoadAllAssessments().First(a => a.Property.StructureTypeID == 1);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		AssessmentEditorViewModel editor = ui.Main.Editor;
		foreach (SectionViewModel section in editor.Sections)
		{
			editor.SelectedSection = section;
			Check("Editor/" + section.Title);
		}
		ui.Main.ShowCommand.Execute(ViewModels.Page.ImportExport);
		Check("Import & export");
		ui.Main.ShowCommand.Execute(ViewModels.Page.Settings);
		Check("Settings");

		// The import page, empty and then with a file, every field group open, values to match and a preview.
		ui.Main.ShowCommand.Execute(ViewModels.Page.SourceImport);
		Check("Import (no file)");
		SourceImportViewModel acc = ui.Main.SourceImport;
		await SourceImportUiTests.SetUpWideExport(acc);
		foreach (SourceFieldGroupViewModel g in acc.Groups)
		{
			g.IsExpanded = true;
		}
		Assert.NotEmpty(acc.Values);
		Assert.NotEmpty(acc.FilterValues);
		Check("Import");
		acc.LayoutIndex = 1;                                    // shows the checklist item and value columns
		Check("Import (checklist layout)");
		acc.ModeIndex = 1;                                      // structures: source, matching and parcel options
		acc.MatchByIndex = 1;
		Check("Import (structures by parcel)");
		acc.MatchByIndex = 2;
		Check("Import (structures by address)");
		acc.ModeIndex = 0;
		acc.MatchByIndex = 0;
		acc.LayoutIndex = 0;
		await acc.RefreshPreviewAsync();
		await acc.ImportCommand.ExecuteAsync(null);
		Check("Import (imported)");
		acc.ReviewCommand.Execute(null);
		Check("Structures (import review)");

		Assert.True(problems.Count == 0, string.Join("\n", problems));
	}
}
