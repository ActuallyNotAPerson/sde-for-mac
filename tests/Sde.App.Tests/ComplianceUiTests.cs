using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DocumentFormat.OpenXml.Packaging;
using Sde.App.Controls;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Shell;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports;
using Sde.Reports.Letters;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The Compliance page (v2.5, M8): cases opened from the editor, in bulk and from an SI determination; the case sheet
/// with its fields, timeline and letters; the filters, sidebar badge and Dashboard tiles; the PDF and CSV. Also writes
/// the Compliance screenshots.
/// </summary>
public class ComplianceUiTests
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

	private static ComplianceViewModel ShowCompliance(UiSession ui)
	{
		ui.Main.ShowCommand.Execute(Page.Compliance);
		ui.Pump();
		return ui.Main.Compliance;
	}

	private static NavItemViewModel Sidebar(UiSession ui, Page page) => ui.Main.SidebarItems.Single(i => i.Page == page);

	/// <summary>
	/// Cases for the ranch (overdue, elevating, ICC filed) and Mill Creek (relocating, ICC paid), with a call on the
	/// ranch's timeline.
	/// </summary>
	private static (ComplianceCaseRecord Ranch, ComplianceCaseRecord Mill) TwoCases(UiSession ui)
	{
		ComplianceCaseRecord ranch = ui.Store.OpenComplianceCase(Row(ui, "123"));
		ranch.Path = CompliancePaths.Elevate;
		ranch.PermitNumber = "BP-0001";
		ranch.Deadline = DateTime.Today.AddDays(-12);
		ranch.IccClaimNumber = "ICC-0001";
		ranch.IccStatus = IccStatuses.Filed;
		ranch.IccAmount = 30000m;
		ranch = ui.Store.SaveComplianceCase(ranch);
		ui.Store.AddComplianceEntry(ranch.CaseID, "Call", "Owner called: getting bids from contractors.");
		ComplianceCaseRecord mill = ui.Store.OpenComplianceCase(Row(ui, "48"));
		mill.Path = CompliancePaths.Relocate;
		mill.Deadline = DateTime.Today.AddDays(60);
		mill.IccStatus = IccStatuses.Paid;
		mill.IccAmount = 12000m;
		mill.EcReceivedOn = DateTime.Today.AddDays(-3);
		mill = ui.Store.SaveComplianceCase(mill);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return (ranch, mill);
	}

	[AvaloniaFact]
	public async Task Cases_OpenFromTheEditor_InBulk_AndFromAnSiDetermination()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		Guid ranch = Row(ui, "123");
		Guid office = Row(ui, "700");
		ComplianceViewModel page = ShowCompliance(ui);
		Assert.True(page.IsEmpty);
		Assert.Equal(2, page.MissingCount);
		Assert.Equal("2 substantially damaged structures have no compliance case yet.", page.MissingText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// From the editor of a substantially damaged assessment (the test dialogs say yes to opening one).
		ui.Main.OpenAssessment(ranch);
		ui.Pump();
		Task opening = ui.Main.Editor.ComplianceCaseCommand.ExecuteAsync(null);
		ui.Pump();
		ComplianceCaseSheetViewModel sheet = Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("123 River Street", sheet.Address);
		Assert.Contains("Substantial damage (SD)", sheet.Detail);
		Assert.False(sheet.CanOpenAssessment);
		Assert.DoesNotContain(sheet.PathChoices, p => p.Value == CompliancePaths.Floodproof);   // the ranch is residential
		sheet.DoneCommand.Execute(null);
		await opening;
		ComplianceCaseRecord ranchCase = Assert.Single(ui.Store.GetComplianceCases());
		Assert.Equal(ranch, ranchCase.AssessmentID);

		// Not for an assessment that isn't substantially damaged.
		ui.Main.OpenAssessment(office);
		await ui.Main.Editor.ComplianceCaseCommand.ExecuteAsync(null);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("No compliance case needed", StringComparison.Ordinal));
		Assert.Single(ui.Store.GetComplianceCases());

		// In bulk: every substantially damaged structure without one.
		page = ShowCompliance(ui);
		Assert.Equal(1, page.MissingCount);
		Assert.True(page.ShowMissingBanner);
		await page.OpenMissingCasesCommand.ExecuteAsync(null);
		ui.Pump();
		Assert.Equal("Opened 1 compliance case.", page.LastResult);
		Assert.Equal(2, page.Rows.Count);
		Assert.False(page.HasMissing);
		await page.OpenMissingCasesCommand.ExecuteAsync(null);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Nothing to open", StringComparison.Ordinal));

		// From a substantial improvement determination (the office is non-residential, so floodproofing is a choice).
		PropertyRecord officeStructure = ui.Store.GetProperty(ui.Store.LoadAssessment(office).Property.PropertyID);
		ui.Store.SaveSiDetermination(new SIDeterminationRecord
		{
			PropertyID = officeStructure.PropertyID,
			MarketValue = 200000m,
			ThresholdPercent = 50m,
			CumulativeCost = 120000m,
			Percent = 60m,
			Result = SiResult.SubstantialImprovement,
		});
		Task improvements = ui.Main.Sheets.ShowAsync(new ImprovementsSheetViewModel(ui.Services, officeStructure.PropertyID, "700 Riverside"));
		ui.Pump();
		ImprovementsSheetViewModel si = Assert.IsType<ImprovementsSheetViewModel>(ui.Main.Sheets.Current);
		SiDeterminationRowViewModel determination = Assert.Single(si.Determinations);
		Assert.True(determination.IsSubstantial);
		Assert.Equal("Open compliance case…", determination.CaseText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		Task siCase = determination.OpenCase.ExecuteAsync(determination);
		ui.Pump();
		ComplianceCaseSheetViewModel siSheet = Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Contains("Substantial improvement (SI)", siSheet.Detail);
		Assert.Contains(siSheet.PathChoices, p => p.Value == CompliancePaths.Floodproof);
		siSheet.DoneCommand.Execute(null);
		await siCase;
		Assert.Equal("Compliance case…", Assert.Single(si.Determinations).CaseText);
		si.CancelCommand.Execute(null);
		await improvements;
		ui.Pump();
		Assert.Contains(page.Rows, r => r.Kind == "SI" && r.Address == "700 Riverside");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task CaseSheet_SavesEveryField_KeepsATimelineNewestFirst_AndAddsNotes()
	{
		using UiSession ui = WithSamples();
		ui.Store.OpenComplianceCase(Row(ui, "123"));
		ComplianceViewModel page = ShowCompliance(ui);
		Task open = page.OpenCaseCommand.ExecuteAsync(Assert.Single(page.Rows));
		ui.Pump();
		ComplianceCaseSheetViewModel sheet = Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(sheet.CanOpenAssessment);
		Assert.False(sheet.IsDirty);
		Assert.False(sheet.SaveCommand.CanExecute(null));

		sheet.Path = sheet.PathChoices.Single(p => p.Value == CompliancePaths.Elevate);
		sheet.PermitNumber = "BP-0001";
		sheet.PermitIssuedOn = DateTime.Today.AddDays(-40);
		sheet.Deadline = DateTime.Today.AddDays(-12);
		sheet.IccClaimNumber = "ICC-0001";
		sheet.IccStatus = ComplianceCaseSheetViewModel.IccChoices.Single(c => c.Value == IccStatuses.Filed);
		sheet.IccAmount = 30000m;
		sheet.EcReceivedOn = DateTime.Today.AddDays(-5);
		sheet.Notes = "Owner prefers to elevate on piers.";
		Assert.True(sheet.IsDirty);
		Assert.True(sheet.IsOverdue);
		Assert.Equal("The deadline passed 12 days ago and the case is still open.", sheet.OverdueText);
		sheet.SaveCommand.Execute(null);
		Assert.False(sheet.IsDirty);
		Assert.Equal("Saved.", sheet.SavedText);

		ComplianceCaseRecord saved = ui.Store.GetComplianceCase(sheet.CaseId);
		Assert.Equal(CompliancePaths.Elevate, saved.Path);
		Assert.Equal("BP-0001", saved.PermitNumber);
		Assert.Equal(DateTime.Today.AddDays(-12), saved.Deadline);
		Assert.Equal(IccStatuses.Filed, saved.IccStatus);
		Assert.Equal(30000m, saved.IccAmount);
		Assert.Equal(DateTime.Today.AddDays(-5), saved.EcReceivedOn);
		Assert.Equal("Owner prefers to elevate on piers.", saved.Notes);

		// The timeline, newest first: what the save recorded, then the opening.
		Assert.StartsWith("Deadline: ", sheet.Entries[0].Text);
		Assert.Contains(sheet.Entries, e => e.Text == "Path: Elevate");
		Assert.Contains(sheet.Entries, e => e.Text == "ICC claim: Filed" && e.Icon == "Cost");
		Assert.Equal("Case opened for the substantial damage determination", sheet.Entries[^1].Text);
		Assert.EndsWith(" · Reviewer A", sheet.Entries[0].Byline);
		Assert.False(sheet.AddNoteCommand.CanExecute(null));
		sheet.NoteKind = "Call";
		sheet.NoteText = "Owner called: getting bids from contractors.";
		sheet.AddNoteCommand.Execute(null);
		Assert.Equal("Call", sheet.Entries[0].Kind);
		Assert.Equal("Phone", sheet.Entries[0].Icon);
		Assert.Null(sheet.NoteText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// Closing it: Done saves.
		sheet.Status = ComplianceCaseSheetViewModel.StatusChoices.Single(s => s.Value == ComplianceStatuses.Closed);
		Assert.False(sheet.IsOverdue);
		sheet.ClosedReason = "Elevated and inspected";
		sheet.DoneCommand.Execute(null);
		await open;
		ui.Pump();
		Assert.False(ui.Store.GetComplianceCase(sheet.CaseId).IsOpen);
		Assert.Contains(ui.Store.GetComplianceEntries(sheet.CaseId), e => e.Text == "Case closed: Elevated and inspected");
		Assert.True(page.NoMatches);
		page.ShowFilter = 2;
		Assert.Equal("Closed", Assert.Single(page.Rows).StatusText);

		// Open the assessment from the case: the editor, whose breadcrumb leads back to Compliance.
		open = page.OpenCaseCommand.ExecuteAsync(page.Rows[0]);
		ui.Pump();
		Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current).OpenAssessmentCommand.Execute(null);
		await open;
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.Equal("Compliance", ui.Main.PageParent);
	}

	[AvaloniaFact]
	public void Filters_TheBadge_AndTheDashboard_FollowTheCasesAndTheEvent()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		TwoCases(ui);
		ComplianceViewModel page = ShowCompliance(ui);
		Assert.Equal(2, page.OpenCount);
		Assert.Equal(1, page.OverdueCount);
		Assert.Equal(1, page.IccInProgressCount);
		Assert.Equal("$12,000", page.IccPaidText);
		Assert.Equal(1, Sidebar(ui, Page.Compliance).Count);
		Assert.Equal(Tone.Danger, Sidebar(ui, Page.Compliance).CountTone);
		ComplianceRowViewModel ranch = page.Rows[0];
		Assert.Equal("123 River Street", ranch.Address);
		Assert.True(ranch.IsOverdue);
		Assert.Equal("12 days overdue", ranch.OverdueText);
		Assert.Equal("Filed · $30,000", ranch.IccText);
		Assert.Equal("Elevate", ranch.PathText);

		page.ToggleOverdueCommand.Execute(null);
		Assert.Equal("123 River Street", Assert.Single(page.Rows).Address);
		page.ToggleOverdueCommand.Execute(null);
		Assert.Equal(2, page.Rows.Count);
		page.PathFilter = ComplianceViewModel.PathChoices.ToList().IndexOf(CompliancePaths.Label(CompliancePaths.Relocate));
		Assert.Equal("48 Mill Creek Street", Assert.Single(page.Rows).Address);
		page.ShowIccInProgressCommand.Execute(null);
		Assert.Equal("123 River Street", Assert.Single(page.Rows).Address);
		page.ShowIccPaidCommand.Execute(null);
		Assert.Equal("48 Mill Creek Street", Assert.Single(page.Rows).Address);
		page.ClearFiltersCommand.Execute(null);
		Assert.Equal(2, page.Rows.Count);

		// The dashboard counts them.
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		DashboardViewModel d = ui.Main.Dashboard;
		Assert.True(d.HasCompliance);
		Assert.Equal("2", d.ComplianceOpen);
		Assert.Equal("1", d.ComplianceOverdue);
		Assert.True(d.HasOverdue);
		Assert.Equal("1", d.IccInProgress);
		Assert.Equal("$12,000", d.IccPaid);

		// In an event: only its assessments' cases, in the list, the badge and the dashboard.
		EventRecord e = ui.Store.SaveEvent(new EventRecord { Name = "September flood", StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 30) });
		ui.Store.SetEvent(new[] { Row(ui, "48") }, e.EventID);
		ui.Services.RaiseDataChanged();
		ui.Main.Events.ChooseCommand.Execute(ui.Main.Events.Events.Single(c => c.Id == e.EventID));
		ui.Pump();
		Assert.Equal("1", d.ComplianceOpen);
		Assert.Equal("0", d.ComplianceOverdue);
		Assert.Equal(0, Sidebar(ui, Page.Compliance).Count);
		page = ShowCompliance(ui);
		Assert.Equal("48 Mill Creek Street", Assert.Single(page.Rows).Address);
		Assert.Contains("September flood", page.Subtitle);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task ALetter_OffersTheStarterTemplates_ThenIsWrittenFromTheCase_AndLogged()
	{
		using UiSession ui = WithSamples();
		Guid ranch = Row(ui, "123");
		(ComplianceCaseRecord ranchCase, _) = TwoCases(ui);
		ui.Store.SetSetting(LetterSender.SettingKey, new LetterSender { CommunityName = "Town of Exampleville", OfficeName = "Floodplain Office" });
		ComplianceViewModel page = ShowCompliance(ui);
		Task open = page.OpenCaseCommand.ExecuteAsync(page.Rows.Single(r => r.Case.CaseID == ranchCase.CaseID));
		ui.Pump();
		ComplianceCaseSheetViewModel sheet = Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(LetterKinds.Compliance, sheet.Letters.Select(l => l.Kind));
		Assert.False(File.Exists(Path.Combine(sheet.TemplatesFolder, LetterTemplates.IccSupportFile)));

		string letter = Path.Combine(ui.Folder, "123 River Street (ICC support).docx");
		ui.Dialogs.Answers.Enqueue(true);       // create the starter templates
		ui.Dialogs.Answers.Enqueue(letter);     // where to save the letter
		await sheet.WriteLetterCommand.ExecuteAsync(LetterKinds.IccSupport);

		Assert.True(File.Exists(Path.Combine(sheet.TemplatesFolder, LetterTemplates.IccSupportFile)));
		Assert.True(File.Exists(letter));
		Assert.Contains(letter, ui.Dialogs.Opened);
		Assert.StartsWith("Wrote \"123 River Street (ICC support).docx\"", sheet.LetterText);
		Assert.Null(sheet.LetterWarning);
		using (WordprocessingDocument doc = WordprocessingDocument.Open(letter, false))
		{
			string text = doc.MainDocumentPart.Document.InnerText;
			Assert.Contains("ICC-0001", text);
			Assert.Contains("BP-0001", text);
			Assert.Contains("Town of Exampleville", text);
			Assert.DoesNotContain("«", text);
		}
		LetterLogRecord logged = Assert.Single(ui.Store.GetLetters(ranch));
		Assert.Equal(LetterKinds.IccSupport, logged.Kind);
		Assert.Equal("Letter", sheet.Entries[0].Kind);
		Assert.Equal("Letter written: ICC support", sheet.Entries[0].Text);

		// The templates are there now: the next letter only asks where to save it.
		string reminder = Path.Combine(ui.Folder, "reminder.docx");
		ui.Dialogs.Answers.Enqueue(reminder);
		await sheet.WriteLetterCommand.ExecuteAsync(LetterKinds.DeadlineReminder);
		Assert.True(File.Exists(reminder));
		Assert.Equal(2, ui.Store.GetLetters(ranch).Count);
		sheet.DoneCommand.Execute(null);
		await open;
	}

	[AvaloniaFact]
	public async Task Exports_WriteThePdfAndCsvOfTheCasesShown()
	{
		using UiSession ui = WithSamples();
		TwoCases(ui);
		ComplianceViewModel page = ShowCompliance(ui);
		string pdf = Path.Combine(ui.Folder, "compliance.pdf");
		ui.Dialogs.Answers.Enqueue(pdf);
		await page.ExportPdfCommand.ExecuteAsync(null);
		Assert.True(File.Exists(pdf));
		Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));
		Assert.Contains(pdf, ui.Dialogs.Opened);
		Assert.Equal("Saved compliance.pdf (2 cases).", page.LastResult);

		page.OverdueOnly = true;
		string csv = Path.Combine(ui.Folder, "compliance.csv");
		ui.Dialogs.Answers.Enqueue(csv);
		await page.ExportCsvCommand.ExecuteAsync(null);
		string[] lines = File.ReadAllLines(csv);
		Assert.Equal(2, lines.Length);
		Assert.StartsWith(string.Join(",", ComplianceReport.CsvHeaders.Take(3)), lines[0], StringComparison.Ordinal);
		Assert.StartsWith("123 River Street,", lines[1]);
		Assert.Contains("reveal:" + csv, ui.Dialogs.Opened);

		page.PathFilter = ComplianceViewModel.PathChoices.ToList().IndexOf(CompliancePaths.Label(CompliancePaths.Demolish));
		await page.ExportCsvCommand.ExecuteAsync(null);
		Assert.Contains(ui.Dialogs.Notifications, n => n == "Nothing to export: No cases match the filters.");
	}

	/// <summary>The Compliance screenshots, in light and dark: the page and a case with its timeline.</summary>
	[AvaloniaTheory]
	[InlineData("light")]
	[InlineData("dark")]
	public async Task ComplianceScreenshots(string themeName)
	{
		ThemeVariant theme = themeName == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		string suffix = themeName == "dark" ? "-dark" : "";
		using UiSession ui = WithSamples(theme);
		(ComplianceCaseRecord ranchCase, _) = TwoCases(ui);
		ComplianceViewModel page = ShowCompliance(ui);
		ui.Snapshot("113-compliance" + suffix);

		Task open = page.OpenCaseCommand.ExecuteAsync(page.Rows.Single(r => r.Case.CaseID == ranchCase.CaseID));
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("114-compliance-case" + suffix);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await open;
	}
}
