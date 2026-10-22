using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.App.ViewModels.Shell;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Quality;
using Sde.Data;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The Review page (v2.5, M8): the queue of assessments waiting for review, the quality checks grouped by check (open,
/// set aside with a reason, brought back), reviewers' comments on the page and in the editor, Mark reviewed, and the
/// question asked before an export when there are quality errors. Also writes the Review screenshots.
/// </summary>
public class ReviewUiTests
{
	private static UiSession WithSamples(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	private static Guid Row(UiSession ui, string addressStart) =>
		ui.Main.Structures.Rows.First(r => r.Address.StartsWith(addressStart, StringComparison.Ordinal)).AssessmentID!.Value;

	private static ReviewViewModel ShowReview(UiSession ui)
	{
		ui.Main.ShowCommand.Execute(Page.Review);
		ui.Pump();
		return ui.Main.Review;
	}

	private static NavItemViewModel Sidebar(UiSession ui, Page page) => ui.Main.SidebarItems.Single(i => i.Page == page);

	/// <summary>The ranch inspected before its damage date: an error the checks find.</summary>
	private static void InspectRanchTooEarly(UiSession ui, Guid ranch)
	{
		ui.Store.Apply(ranch, f => f.Assessment.DateofInspection = new DateTime(2026, 9, 1));
		ui.Services.RaiseDataChanged();
		ui.Pump();
	}

	[AvaloniaFact]
	public void Queue_ShowsDraftAndReviewed_WithChecksAndComments_AndFollowsTheEvent()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		Guid river = Row(ui, "123");
		Guid mill = Row(ui, "48");
		Guid office = Row(ui, "700");
		ui.Store.SetStatus(office, AssessmentStatus.Final);
		ui.Store.AddReviewComment(river, "Check the garage area against the sketch.");
		ReviewViewModel r = ShowReview(ui);

		// Draft and Reviewed by default; the one with the most problems first.
		Assert.Equal(new[] { mill, river }, r.Rows.Select(x => x.AssessmentID));
		ReviewRowViewModel riverRow = r.Rows.Single(x => x.AssessmentID == river);
		Assert.Equal(3, riverRow.FindingCount);
		Assert.Equal(1, riverRow.CommentCount);
		Assert.Equal("Substantially Damaged", riverRow.Result);
		Assert.Equal(Tone.Warning, riverRow.FindingTone);
		Assert.Equal(2, r.WaitingCount);
		Assert.Equal(1, r.OpenCommentCount);
		Assert.Equal(11, r.OpenFindingCount);
		Assert.Equal(0, r.ErrorCount);
		Assert.Equal(2, Sidebar(ui, Page.Review).Count);
		Assert.Equal(Tone.Accent, Sidebar(ui, Page.Review).CountTone);

		// Another status, or every status.
		r.StatusFilter = ReviewViewModel.StatusChoices.ToList().IndexOf("Final");
		Assert.Equal(office, Assert.Single(r.Rows).AssessmentID);
		r.StatusFilter = ReviewViewModel.StatusChoices.Count - 1;
		Assert.Equal(3, r.Rows.Count);
		r.StatusFilter = ReviewViewModel.StatusChoices.ToList().IndexOf("Appeal");
		ui.Pump();
		Assert.True(r.NoRowsMatch);
		r.ShowWaitingCommand.Execute(null);
		Assert.Equal(2, r.Rows.Count);

		// Every column but the tick box sorts.
		ui.Pump();
		DataGrid grid = ui.Window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.IsEffectivelyVisible);
		Assert.Equal("Assessments to review", Avalonia.Automation.AutomationProperties.GetName(grid));
		Assert.True(grid.CanUserSortColumns);
		Assert.All(grid.Columns.Skip(1), c => Assert.False(string.IsNullOrEmpty(c.SortMemberPath)));

		// The comments tile shows the assessments with open threads, whatever their status.
		r.ShowCommentedCommand.Execute(null);
		Assert.Equal(river, Assert.Single(r.Rows).AssessmentID);
		Assert.True(r.HasOnly);
		r.ClearOnlyCommand.Execute(null);

		// In an event, only its assessments, checks and counts.
		EventRecord e = ui.Store.SaveEvent(new EventRecord { Name = "September flood", StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 30) });
		ui.Store.SetEvent(new[] { river }, e.EventID);
		ui.Services.RaiseDataChanged();
		ui.Main.Events.ChooseCommand.Execute(ui.Main.Events.Events.Single(c => c.Id == e.EventID));
		ui.Pump();
		r.StatusFilter = 0;
		Assert.Equal(river, Assert.Single(r.Rows).AssessmentID);
		Assert.All(r.Findings, f => Assert.Equal(river, f.AssessmentId));
		Assert.Equal(1, Sidebar(ui, Page.Review).Count);
		Assert.Contains("September flood", r.Subtitle);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task Checks_AreGroupedByCheck_SetAsideWithAReason_AndBroughtBack()
	{
		using UiSession ui = WithSamples();
		Guid mill = Row(ui, "48");
		ReviewViewModel r = ShowReview(ui);
		r.ShowAllFindingsCommand.Execute(null);
		ui.Pump();
		Assert.True(r.IsChecksTab);

		// In the catalog's order, each with its check's title, severity and description.
		Assert.Equal(new[] { "No location", "No photos", "No damage date", "No mailing address" }, r.Groups.Select(g => g.Title));
		QaGroupViewModel photos = r.Groups.Single(g => g.Rule.Id == QaRuleIds.MissingPhotos);
		Assert.Equal(QaRules.Rule(QaRuleIds.MissingPhotos).Description, photos.Description);
		Assert.Equal("Warning", photos.SeverityText);
		Assert.Equal("Note", r.Groups.Single(g => g.Rule.Id == QaRuleIds.MissingMailingAddress).SeverityText);
		Assert.Equal("3 open", photos.CountText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// Setting one aside needs a reason.
		QaFindingViewModel millPhotos = photos.Findings.Single(f => f.Address.StartsWith("48", StringComparison.Ordinal));
		Assert.True(millPhotos.CanDismiss);
		Task dismissing = r.DismissFindingCommand.ExecuteAsync(millPhotos);
		ui.Pump();
		DismissFindingSheetViewModel sheet = Assert.IsType<DismissFindingSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("No photos", sheet.RuleTitle);
		Assert.Equal("48 Mill Creek Street", sheet.Address);
		sheet.DismissCommand.Execute(null);
		Assert.True(sheet.HasError);
		Assert.False(sheet.IsClosed);
		sheet.Reason = "Photos are on the inspector's camera and come in after the event.";
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.DismissCommand.Execute(null);
		await dismissing;
		ui.Pump();

		Assert.Equal(10, r.OpenFindingCount);
		photos = r.Groups.Single(g => g.Rule.Id == QaRuleIds.MissingPhotos);
		Assert.Equal(2, photos.Findings.Count);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Check set aside", StringComparison.Ordinal));
		Assert.Contains(ui.Store.GetHistory(mill), h => h.Label == "Quality check set aside: No photos (Photos are on the inspector's camera and come in after the event.)");
		Assert.Equal(3, r.Rows.Single(x => x.AssessmentID == mill).FindingCount);

		// Shown on request, with the reason; and brought back.
		r.ShowDismissed = true;
		photos = r.Groups.Single(g => g.Rule.Id == QaRuleIds.MissingPhotos);
		Assert.Equal("2 open, 1 set aside", photos.CountText);
		QaFindingViewModel aside = Assert.Single(photos.Findings, f => f.IsDismissed);
		Assert.StartsWith("Set aside by Reviewer A on ", aside.DismissedText);
		Assert.EndsWith(": Photos are on the inspector's camera and come in after the event.", aside.DismissedText);
		Assert.False(aside.CanDismiss);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		aside.BringBack.Execute(aside);
		Assert.Equal(11, r.OpenFindingCount);
		Assert.Contains(ui.Store.GetHistory(mill), h => h.Label == "Quality check brought back: No photos");

		// The severity filter.
		r.SeverityFilter = 1;
		Assert.DoesNotContain(r.Groups, g => g.Rule.Severity == QaSeverity.Info);
		r.ShowErrorsCommand.Execute(null);
		Assert.True(r.ChecksEmpty);
		Assert.Equal("Nothing matches", r.ChecksEmptyTitle);
	}

	[AvaloniaFact]
	public void OpeningACheck_GoesToItsField_AndClosingTheEditorComesBackToReview()
	{
		using UiSession ui = WithSamples();
		Guid mill = Row(ui, "48");
		ReviewViewModel r = ShowReview(ui);
		r.TabIndex = 1;
		ui.Pump();
		QaFindingViewModel noDate = r.Groups.Single(g => g.Rule.Id == QaRuleIds.MissingDamageDate).Findings.Single(f => f.Finding.AssessmentId == mill);
		Assert.Equal("Go to field", noDate.OpenText);
		noDate.Open.Execute(noDate);
		ui.Pump();

		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.Equal(mill, editor.Assessment.AssessmentID);
		Assert.Contains(editor.SelectedSection.AllFields, f => f.Member == "DateDamageOccurred");
		Control focused = Assert.IsAssignableFrom<Control>(ui.Window.FocusManager.GetFocusedElement());
		Assert.Equal("DateDamageOccurred", Assert.IsAssignableFrom<FieldViewModel>(focused.DataContext).Member);
		Assert.Equal("Review", ui.Main.PageParent);
		Assert.True(Sidebar(ui, Page.Review).IsActive);

		// The breadcrumb (and closing) go back to Review; opened from Structures, back to Structures.
		ui.Main.GoToParentCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Review, ui.Main.ActivePage);
		Assert.Null(ui.Main.Editor);
		r.Selected = r.Rows.First();
		r.OpenSelectedCommand.Execute(null);
		Assert.Equal("Review", ui.Main.PageParent);
		ui.Main.ShowStructures();
		Assert.Equal(Page.Review, ui.Main.ActivePage);
		ui.Main.ShowCommand.Execute(Page.Structures);
		ui.Main.OpenAssessment(mill);
		Assert.Equal("Structures", ui.Main.PageParent);
		ui.Main.ShowStructures();
		Assert.Equal(Page.Structures, ui.Main.ActivePage);
	}

	[AvaloniaFact]
	public void MarkReviewed_SetsTheCheckedToReviewed_AndLeavesFinalAlone()
	{
		using UiSession ui = WithSamples();
		Guid river = Row(ui, "123");
		Guid mill = Row(ui, "48");
		Guid office = Row(ui, "700");
		ui.Store.SetStatus(office, AssessmentStatus.Final);
		ui.Store.SetStatus(mill, AssessmentStatus.Appeal);
		ReviewViewModel r = ShowReview(ui);
		Assert.False(r.MarkReviewedCommand.CanExecute(null));
		r.StatusFilter = ReviewViewModel.StatusChoices.Count - 1;
		foreach (ReviewRowViewModel row in r.Rows)
		{
			row.IsChecked = true;
		}
		Assert.Equal("3 checked", r.CheckedText);
		Assert.True(r.MarkReviewedCommand.CanExecute(null));
		r.MarkReviewedCommand.Execute(null);
		ui.Pump();

		Assert.Equal(AssessmentStatus.Reviewed, ui.Store.GetStatus(river).Value);
		Assert.Equal(AssessmentStatus.Reviewed, ui.Store.GetStatus(mill).Value);
		Assert.Equal(AssessmentStatus.Final, ui.Store.GetStatus(office).Value);
		Assert.Contains(ui.Dialogs.Notifications, n => n == "Marked reviewed: Marked 2 assessments reviewed. 1 Final or Letter sent assessment was left alone.");
		Assert.False(r.HasChecked);
		Assert.Equal(2, r.WaitingCount);

		// With nothing checked, the selected one.
		r.StatusFilter = 0;
		ui.Store.SetStatus(river, AssessmentStatus.Draft);
		ui.Services.RaiseDataChanged();
		r.Selected = r.Rows.Single(x => x.AssessmentID == river);
		r.MarkReviewedCommand.Execute(null);
		Assert.Equal(AssessmentStatus.Reviewed, ui.Store.GetStatus(river).Value);
	}

	[AvaloniaFact]
	public async Task Comments_HaveRepliesAndResolve_BesideTheQueueAndInTheEditor()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		Guid river = Row(ui, "123");
		ReviewViewModel r = ShowReview(ui);
		r.Selected = r.Rows.Single(x => x.AssessmentID == river);
		ui.Pump();
		CommentThreadsViewModel threads = r.SelectedComments;
		Assert.True(threads.IsEmpty);
		Assert.False(threads.AddCommand.CanExecute(null));
		threads.NewComment = "The HVAC damage looks high for two days of water. Can you confirm it from the photos?";
		threads.AddCommand.Execute(null);
		CommentThreadItemViewModel thread = Assert.Single(threads.Threads);
		Assert.StartsWith("Reviewer A · ", thread.Root.Byline);
		Assert.Equal(1, r.Selected.CommentCount);
		Assert.Equal(1, r.OpenCommentCount);
		thread.StartReplyCommand.Execute(null);
		thread.ReplyText = "Confirmed: the air handler was in the crawl space.";
		thread.SendReplyCommand.Execute(null);
		Assert.Single(Assert.Single(threads.Threads).Replies);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// The editor shows the count, and the same thread in its Comments sheet.
		r.OpenSelectedCommand.Execute(null);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.Equal("Comments (1)", editor.CommentsText);
		Task showing = editor.ShowCommentsCommand.ExecuteAsync(null);
		ui.Pump();
		CommentsSheetViewModel sheet = Assert.IsType<CommentsSheetViewModel>(ui.Main.Sheets.Current);
		CommentThreadItemViewModel inEditor = Assert.Single(sheet.Threads.Threads);
		Assert.Single(inEditor.Replies);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		inEditor.ResolveCommand.Execute(null);
		Assert.True(sheet.Threads.AllResolvedHidden);
		sheet.Threads.ToggleResolvedCommand.Execute(null);
		CommentThreadItemViewModel resolved = Assert.Single(sheet.Threads.Threads);
		Assert.StartsWith("Resolved by Reviewer A on ", resolved.ResolvedText);
		sheet.CancelCommand.Execute(null);
		await showing;
		Assert.Equal(0, editor.OpenCommentCount);
		Assert.Equal("Comments", editor.CommentsText);

		// A comment about a field takes the editor there.
		ui.Store.AddReviewComment(river, "Is this the date of the first visit?", "Assessment.DateofInspection");
		showing = editor.ShowCommentsCommand.ExecuteAsync(null);
		ui.Pump();
		sheet = Assert.IsType<CommentsSheetViewModel>(ui.Main.Sheets.Current);
		CommentThreadItemViewModel aboutField = Assert.Single(sheet.Threads.Threads);
		Assert.Equal("About: " + Sde.Core.Assessments.FieldCatalog.Get("Assessment.DateofInspection").Label, aboutField.FieldText);
		Assert.True(aboutField.CanGoToField);
		aboutField.GoToFieldCommand.Execute(null);
		await showing;
		Assert.Contains(editor.SelectedSection.AllFields, f => f.Member == "DateofInspection");
		Assert.Equal(1, editor.OpenCommentCount);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task ExportWithQualityErrors_AsksFirst_ThenShowsThemOnReview_OrExportsAnyway()
	{
		using UiSession ui = WithSamples();
		Guid river = Row(ui, "123");
		InspectRanchTooEarly(ui, river);
		string exports = Path.Combine(ui.Folder, "exports");
		Directory.CreateDirectory(exports);

		// Show them on Review: nothing is exported, and the page lists the errors of what was being exported.
		ui.Dialogs.Answers.Enqueue(exports);
		Task exporting = ui.Main.ImportExport.ExportAllCommand.ExecuteAsync(null);
		ui.Pump();
		QaBeforeExportSheetViewModel sheet = Assert.IsType<QaBeforeExportSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(1, sheet.FindingCount);
		Assert.StartsWith("1 of these assessments has 1 problem", sheet.Message);
		Assert.StartsWith("123 River Street: Inspected on Sep 1, 2026", sheet.Examples[0]);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.ShowOnReviewCommand.Execute(null);
		await exporting;
		ui.Pump();
		Assert.Null(ui.Main.ImportExport.LastResult);
		Assert.Empty(Directory.GetDirectories(exports));
		ReviewViewModel r = ui.Main.Review;
		Assert.Equal(Page.Review, ui.Main.ActivePage);
		Assert.True(r.IsChecksTab);
		Assert.True(r.HasOnly);
		QaGroupViewModel errors = Assert.Single(r.Groups);
		Assert.Equal(QaRuleIds.InspectedBeforeDamage, errors.Rule.Id);
		Assert.Equal("Error", errors.SeverityText);
		Assert.Equal(1, r.ErrorCount);

		// Export anyway.
		ui.Dialogs.Answers.Enqueue(exports);
		exporting = ui.Main.ImportExport.ExportAllCommand.ExecuteAsync(null);
		ui.Pump();
		Assert.IsType<QaBeforeExportSheetViewModel>(ui.Main.Sheets.Current).ExportAnywayCommand.Execute(null);
		await exporting;
		Assert.StartsWith("Exported 3 structures and 3 assessments", ui.Main.ImportExport.LastResult);

		// The submission folder asks too; cancelling writes nothing.
		ui.Main.ShowCommand.Execute(Page.Structures);
		ui.Main.Structures.OpenSubmissionCommand.Execute(null);
		string submissions = Path.Combine(ui.Folder, "submissions");
		Directory.CreateDirectory(submissions);
		ui.Dialogs.Answers.Enqueue(submissions);
		Task writing = ui.Main.Structures.Submission.WriteCommand.ExecuteAsync(null);
		ui.Pump();
		Assert.IsType<QaBeforeExportSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await writing;
		Assert.Empty(Directory.GetFileSystemEntries(submissions));

		// Once fixed, nothing is asked.
		ui.Store.Apply(river, f => f.Assessment.DateofInspection = new DateTime(2026, 9, 20));
		ui.Dialogs.Answers.Enqueue(exports);
		await ui.Main.ImportExport.ExportAllCommand.ExecuteAsync(null);
		Assert.Null(ui.Main.Sheets.Current);
	}

	/// <summary>The Review screenshots, in light and dark: the page, a dismiss sheet and the comments thread.</summary>
	[AvaloniaTheory]
	[InlineData("light")]
	[InlineData("dark")]
	public async Task ReviewScreenshots(string themeName)
	{
		ThemeVariant theme = themeName == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		string suffix = themeName == "dark" ? "-dark" : "";
		using UiSession ui = WithSamples(theme);
		Guid river = Row(ui, "123");
		Guid mill = Row(ui, "48");
		InspectRanchTooEarly(ui, river);
		ui.Store.SetStatus(mill, AssessmentStatus.Reviewed);
		ReviewCommentRecord question = ui.Store.AddReviewComment(river, "The HVAC damage looks high for two days of water. Can you confirm it from the photos?");
		ui.Store.AddReviewComment(river, "Confirmed: the air handler was in the crawl space.", parentId: question.CommentID, context: new SaveContext { User = "Inspector A" });
		ui.Store.AddReviewComment(river, "Is the inspection date right? It is before the flood.", "Assessment.DateofInspection");
		ReviewViewModel r = ShowReview(ui);
		r.Selected = r.Rows.Single(x => x.AssessmentID == river);
		ui.Pump();
		ui.Snapshot("110-review" + suffix);

		r.ShowAllFindingsCommand.Execute(null);
		ui.Pump();
		QaFindingViewModel photos = r.Groups.Single(g => g.Rule.Id == QaRuleIds.MissingPhotos).Findings.Single(f => f.Finding.AssessmentId == mill);
		Task dismissing = r.DismissFindingCommand.ExecuteAsync(photos);
		ui.Pump();
		DismissFindingSheetViewModel sheet = Assert.IsType<DismissFindingSheetViewModel>(ui.Main.Sheets.Current);
		sheet.Reason = "Photos are on the inspector's camera and come in after the event.";
		ui.Snapshot("111-review-dismiss" + suffix);
		sheet.DismissCommand.Execute(null);
		await dismissing;

		r.Selected = r.Rows.Single(x => x.AssessmentID == river);
		r.OpenSelectedCommand.Execute(null);
		ui.Pump();
		Task showing = ui.Main.Editor.ShowCommentsCommand.ExecuteAsync(null);
		ui.Pump();
		ui.Snapshot("112-review-comments" + suffix);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await showing;
	}
}
