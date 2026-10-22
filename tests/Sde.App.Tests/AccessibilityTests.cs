using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
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
		foreach (SettingsCategoryItem category in ui.Main.Settings.Categories)
		{
			ui.Main.Settings.SelectedCategory = category;
			Check("Settings/" + category.Title);
		}

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

	/// <summary>
	/// The window's frame: the sidebar's pages and switchers (with their menus open), the title band, the placeholder
	/// pages, the ⌘K palette, a confirmation sheet, the move-or-copy sheet and a toast.
	/// </summary>
	[AvaloniaFact]
	public async Task TheShellHasAccessibleNames()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		List<string> problems = new();
		void Check(string part)
		{
			ui.Pump();
			problems.AddRange(Unnamed(ui.Window).Distinct().Select(p => part + ": " + p));
		}

		// Named for VoiceOver: each sidebar page by its title, the switchers by what they show, ⌘K and New.
		List<string> names = ui.Window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is Button)
			.Select(c => ControlAutomationPeer.CreatePeerForElement(c).GetName()).ToList();
		foreach (string title in ui.Main.SidebarItems.Select(i => i.Title))
		{
			Assert.Contains(title, names);
		}
		Assert.Contains("Event: All events. Choose an event", names);
		Assert.Contains("Database: sde.sqlite. Database actions", names);
		Assert.Contains("Search structures, pages and commands", names);
		Assert.Contains("New", names);
		Check("Shell");

		foreach (ViewModels.Page page in new[] { ViewModels.Page.Review, ViewModels.Page.Compliance, ViewModels.Page.Assignments, ViewModels.Page.Dashboard, ViewModels.Page.Trash })
		{
			ui.Main.ShowCommand.Execute(page);
			Check(page.ToString());
		}

		foreach (string switcher in new[] { "EventSwitcher", "DatabaseButton" })
		{
			Button button = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == switcher);
			PopupFlyoutBase flyout = (PopupFlyoutBase)button.Flyout;
			flyout.Popup.ShouldUseOverlayLayer = true;
			flyout.ShowAt(button);
			Check(switcher + " menu");
			flyout.Hide();
		}

		ui.Main.Palette.Open();
		ui.Main.Palette.Query = "river";
		Check("Command palette");
		ui.Main.Palette.Close();

		Task<bool> asking = ((IDialogs)ui.Window).ConfirmAsync("Delete for good?", "This can't be undone.", "Delete", destructive: true);
		Check("Confirmation");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		Assert.False(await asking);

		_ = ui.Main.Sheets.ShowAsync(new MoveOrCopySheetViewModel("sde.sqlite"));
		Check("Move or copy sheet");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);

		((IDialogs)ui.Window).Notify("Moved to Recently deleted", "123 River Street", "Undo", () => { });
		Check("Toast");

		Assert.True(problems.Count == 0, string.Join("\n", problems));
	}

	/// <summary>
	/// The office pages (v2.5): Review's queue with an assessment's checks and comments beside it, its quality checks
	/// with one set aside, and Compliance with cases; and their sheets: set aside, comments, the question before an
	/// export, and a compliance case.
	/// </summary>
	[AvaloniaFact]
	public async Task TheOfficePagesAndTheirSheetsHaveAccessibleNames()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		List<string> problems = new();
		void Check(string part)
		{
			ui.Pump();
			problems.AddRange(Unnamed(ui.Window).Distinct().Select(p => part + ": " + p));
		}
		Guid ranch = ui.Store.ListStructures().First(s => s.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value;
		Sde.Data.ReviewCommentRecord question = ui.Store.AddReviewComment(ranch, "Can you confirm the HVAC damage?");
		ui.Store.AddReviewComment(ranch, "Confirmed.", parentId: question.CommentID);
		ui.Store.AddReviewComment(ranch, "Is this the right date?", "Assessment.DateofInspection");
		ui.Store.Apply(ranch, f => f.Assessment.DateofInspection = new DateTime(2026, 9, 1));

		ui.Main.ShowCommand.Execute(ViewModels.Page.Review);
		ReviewViewModel review = ui.Main.Review;
		review.Selected = review.Rows.Single(r => r.AssessmentID == ranch);
		review.SelectedComments.Threads[0].StartReplyCommand.Execute(null);
		Check("Review queue");
		review.ShowAllFindingsCommand.Execute(null);
		QaFindingViewModel finding = review.Groups.First(g => g.Rule.Id == Sde.Core.Quality.QaRuleIds.MissingPhotos).Findings[0];
		Task dismissing = review.DismissFindingCommand.ExecuteAsync(finding);
		Check("Set aside sheet");
		((DismissFindingSheetViewModel)ui.Main.Sheets.Current).Reason = "Checked on site.";
		((DismissFindingSheetViewModel)ui.Main.Sheets.Current).DismissCommand.Execute(null);
		await dismissing;
		review.ShowDismissed = true;
		Check("Review checks");

		ui.Main.OpenAssessment(ranch);
		Task comments = ui.Main.Editor.ShowCommentsCommand.ExecuteAsync(null);
		Check("Comments sheet");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await comments;

		ui.Dialogs.Answers.Enqueue(ui.Folder);
		Task exporting = ui.Main.ImportExport.ExportAllCommand.ExecuteAsync(null);
		Check("Quality errors before an export");
		Assert.IsType<QaBeforeExportSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await exporting;

		ui.Store.OpenComplianceCase(ranch);
		ui.Main.ShowCommand.Execute(ViewModels.Page.Compliance);
		Check("Compliance");
		Task open = ui.Main.Compliance.OpenCaseCommand.ExecuteAsync(ui.Main.Compliance.Rows[0]);
		Check("Compliance case sheet");
		Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await open;

		Assert.True(problems.Count == 0, string.Join("\n", problems));
	}
}
