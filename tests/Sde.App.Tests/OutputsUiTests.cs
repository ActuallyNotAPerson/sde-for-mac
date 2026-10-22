using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Data;
using Sde.Reports.Letters;

namespace Sde.App.Tests;

/// <summary>
/// Substantial improvement, owner letters, appeals and cumulative damage (v2.0 items 14, 16 and 18), and the
/// warning before exporting assessments that aren't Final. None of these may change FEMA's percent or determination
/// except "Use this bid", which changes the repair cost through the normal save.
/// </summary>
public class OutputsUiTests
{
	private static UiSession WithSamples()
	{
		UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	/// <summary>The sample ranch (75.0%, Substantially Damaged).</summary>
	private static AssessmentFile Ranch(UiSession ui) => ui.Store.LoadAllAssessments().Single(f => f.Property.StreetName == "River");

	private static void AssertNoBindingErrors() =>
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));

	[AvaloniaFact]
	public async Task Improvements_ProjectsResultDeterminationAndPdf()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		AssessmentFile ranch = Ranch(ui);
		Guid pid = ranch.Property.PropertyID;
		ui.Store.SetSiSettings(new SiSettings { ThresholdPercent = 50m, LookbackYears = 5, IncludePriorProjects = true });
		ui.Main.OpenAssessment(ranch.Assessment.AssessmentID);
		_ = ui.Main.Editor.ShowImprovementsCommand.ExecuteAsync(null);
		ui.Pump();
		ImprovementsSheetViewModel sheet = Assert.IsType<ImprovementsSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Contains("5 years", sheet.SettingsText);
		// The market value starts as the latest assessment's building value.
		Assert.Equal(Math.Round(CumulativeDamage.BuildingValue(ranch.Assessment)!.Value, 2), sheet.MarketValue);
		Assert.False(string.IsNullOrEmpty(sheet.MarketValueBasis));

		sheet.NewProjectCommand.Execute(null);
		sheet.PermitNumber = "BP-0001";
		sheet.Description = "Kitchen remodel";
		sheet.ProjectDate = new DateTime(2023, 3, 15);
		sheet.Cost = 18000m;
		sheet.SaveProject();
		sheet.NewProjectCommand.Execute(null);
		sheet.PermitNumber = "BP-0002";
		sheet.Description = "Second-story addition";
		sheet.ProjectDate = new DateTime(2026, 6, 1);
		sheet.Cost = 42000m;
		sheet.CostBasis = ImprovementsSheetViewModel.CostBases[0];
		Assert.True(sheet.IsProjectDirty);
		sheet.MarketValue = 110000m;
		Assert.Equal("54.5%", sheet.PercentText);
		Assert.Equal(SiResult.SubstantialImprovement, sheet.ResultText);
		Assert.Contains(sheet.Lines, l => l.Project == "BP-0001 · Kitchen remodel" && l.Counted);
		sheet.MarketValueBasis = "Professional appraisal";
		sheet.Notes = "Synthetic test data.";
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("65-si");

		// Excluding the earlier project takes it out of the total.
		sheet.SaveProject();
		sheet.SelectedProject = sheet.Projects.Single(p => p.Project.PermitNumber == "BP-0001");
		sheet.Included = false;
		sheet.ExclusionReason = "routine maintenance";
		sheet.SaveProject();
		sheet.SelectedProject = sheet.Projects.Single(p => p.Project.PermitNumber == "BP-0002");
		Assert.Equal("38.1%", sheet.PercentText);
		Assert.Equal(SiResult.NotSubstantialImprovement, sheet.ResultText);

		sheet.SaveDetermination();
		SIDeterminationRecord saved = Assert.Single(ui.Store.GetSiDeterminations(pid));
		Assert.Equal(SiResult.NotSubstantialImprovement, saved.Result);
		Assert.Equal("Professional appraisal", saved.MarketValueBasis);
		Assert.Single(sheet.Determinations);
		Assert.Equal(2, ui.Store.GetImprovementProjects(pid).Count);

		string pdf = Path.Combine(ui.Folder, "si.pdf");
		ui.Dialogs.Answers.Enqueue(pdf);
		await sheet.SavePdfCommand.ExecuteAsync(null);
		Assert.True(File.Exists(pdf));
		Assert.Contains(pdf, ui.Dialogs.Opened);

		// SI never touches FEMA's result.
		AssessmentFile after = ui.Store.LoadAssessment(ranch.Assessment.AssessmentID);
		Assert.Equal(ranch.Assessment.PercentDamage, after.Assessment.PercentDamage);
		Assert.Equal(ranch.Assessment.DamageMessage, after.Assessment.DamageMessage);
		sheet.CancelCommand.Execute(null);
		AssertNoBindingErrors();
	}

	[AvaloniaFact]
	public async Task OwnerLetters_WriteFilesAndLog_ThenMarkSentLocks()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		// The two-story sample, below the threshold on a community estimate, so there's a letter of each kind.
		AssessmentFile mill = ui.Store.LoadAllAssessments().Single(f => f.Property.StreetName == "Mill Creek");
		mill.Assessment.CostOfRepairsID = LookupIds.RepairCommunityEstimate;
		mill.Assessment.CommunityEstimate = 20000m;
		ui.Store.SaveAssessment(mill);
		Assert.Equal("Not Substantially Damaged", ui.Store.LoadAssessment(mill.Assessment.AssessmentID).Assessment.DamageMessage);
		StructuresViewModel s = ui.Main.Structures;
		s.Reload();
		_ = s.OwnerLettersCommand.ExecuteAsync(null);
		ui.Pump();
		OwnerLettersSheetViewModel sheet = Assert.IsType<OwnerLettersSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(2, sheet.LetterCount);
		Assert.Contains("700 Riverside", sheet.LeftOutText);
		Assert.Equal(new[] { LetterKinds.SubstantialDamage, LetterKinds.NotSubstantiallyDamaged }.OrderBy(k => k), sheet.Kinds.Select(k => k.Kind).OrderBy(k => k));
		Assert.False(sheet.HasTemplates);
		Assert.StartsWith(ui.Folder, sheet.TemplatesFolder);   // next to the database when SDE_DATABASE chose it

		sheet.CreateStarterTemplatesCommand.Execute(null);
		Assert.True(sheet.HasTemplates);
		Assert.All(sheet.Kinds, k => Assert.Equal(LetterTemplates.FileName(k.Kind), k.Template));
		sheet.CommunityName = "Sample Community";
		sheet.OfficeName = "Floodplain Management Office";
		sheet.OfficialName = "A. Official";
		sheet.OfficialTitle = "Floodplain Administrator";
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("66-letters");

		string parent = Path.Combine(ui.Folder, "letters-out");
		Directory.CreateDirectory(parent);
		ui.Dialogs.Answers.Enqueue(parent);
		await sheet.Write();
		string folder = Assert.Single(Directory.GetDirectories(parent));
		Assert.StartsWith("Letters ", Path.GetFileName(folder));
		string[] files = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n).ToArray();
		Assert.Equal(new[] { "123 River Street (Substantial damage).docx", "48 Mill Creek Street (Not substantially damaged).docx" }, files);
		Assert.Contains("reveal:" + folder, ui.Dialogs.Opened);
		Assert.True(sheet.HasWritten);
		Assert.Null(sheet.WarningText);

		Guid ranchId = Ranch(ui).Assessment.AssessmentID;
		LetterLogRecord logged = Assert.Single(ui.Store.GetLetters(ranchId));
		Assert.Equal(LetterKinds.SubstantialDamage, logged.Kind);
		Assert.Equal(LetterTemplates.SubstantialDamageFile, logged.Template);
		Assert.Null(logged.SentOn);
		// The sender is remembered for this database.
		Assert.Equal("A. Official", ui.Store.GetSetting<LetterSender>(LetterSender.SettingKey).OfficialName);

		await sheet.MarkSent();
		Assert.StartsWith("Marked 2 letters as sent", sheet.ResultText);
		Assert.NotNull(Assert.Single(ui.Store.GetLetters(ranchId)).SentOn);
		Assert.Equal(AssessmentStatus.LetterSent, ui.Store.GetStatus(ranchId).Value);
		Assert.True(ui.Store.IsLocked(ranchId));
		Assert.False(sheet.MarkSentCommand.CanExecute(null));
		sheet.CancelCommand.Execute(null);
		AssertNoBindingErrors();
	}

	[AvaloniaFact]
	public async Task MarkLetterSent_FromTheEditor_SetsLetterSent()
	{
		using UiSession ui = WithSamples();
		Guid id = Ranch(ui).Assessment.AssessmentID;
		ui.Store.AddLetters(new[] { new LetterLogRecord { AssessmentID = id, Kind = LetterKinds.SubstantialDamage, FileName = "x.docx" } });
		ui.Main.OpenAssessment(id);
		await ui.Main.Editor.MarkLetterSentCommand.ExecuteAsync(null);
		Assert.Equal(AssessmentStatus.LetterSent, ui.Store.GetStatus(id).Value);
		Assert.NotNull(ui.Store.GetLetters(id)[0].SentOn);
		Assert.True(ui.Main.Editor.IsLocked);   // the editor reloaded, locked

		// Saying no leaves things as they are.
		AssessmentFile other = ui.Store.LoadAllAssessments().Single(f => f.Property.StreetName == "Mill Creek");
		ui.Main.OpenAssessment(other.Assessment.AssessmentID);
		ui.Dialogs.Answers.Enqueue(false);
		await ui.Main.Editor.MarkLetterSentCommand.ExecuteAsync(null);
		Assert.Equal(AssessmentStatus.Draft, ui.Store.GetStatus(other.Assessment.AssessmentID).Value);
	}

	[AvaloniaFact]
	public async Task Appeal_BidWhatIfMatchesTheEngine_AndUsingABidReopensFirst()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		AssessmentFile ranch = Ranch(ui);
		Guid id = ranch.Assessment.AssessmentID;
		ui.Store.SetStatus(id, AssessmentStatus.Final);
		ui.Main.OpenAssessment(id);
		_ = ui.Main.Editor.ShowAppealCommand.ExecuteAsync(null);
		ui.Pump();
		AppealSheetViewModel sheet = Assert.IsType<AppealSheetViewModel>(ui.Main.Sheets.Current);
		Assert.StartsWith("75.0%: Substantially Damaged", sheet.CurrentText);

		// Recording the appeal asks to set the status to Appeal (the Final assessment is reopened for it).
		sheet.ReceivedOn = new DateTime(2026, 10, 3);
		sheet.Basis = "Repair estimate too high";
		await sheet.SaveAppeal();
		Assert.Equal(AppealOutcomes.Pending, ui.Store.GetAppeal(id).Outcome);
		AssessmentStatusRecord status = ui.Store.GetStatus(id);
		Assert.Equal(AssessmentStatus.Appeal, status.Value);
		Assert.Equal("Appeal received 10/03/2026", status.Reason);

		sheet.NewContractor = "Sample Builders";
		sheet.NewAmount = 41000m;
		sheet.AddBid();
		sheet.NewContractor = "Example Contracting";
		sheet.NewAmount = 52000m;
		sheet.NewNotes = "Includes new HVAC";
		sheet.AddBid();
		Assert.Equal(2, sheet.Bids.Count);

		// The what-if is FEMA's engine on a copy, with the bid as the contractor's estimate; nothing is saved.
		BidRowViewModel bid = sheet.Bids.Single(b => b.Contractor == "Sample Builders");
		AssessmentFile copy = ui.Store.LoadAssessment(id);
		copy.Assessment.CostOfRepairsID = LookupIds.RepairContractorEstimate;
		copy.Assessment.ContractorEstimate = 41000m;
		ui.Store.Calculator.Recalculate(copy);
		Assert.Equal(copy.Assessment.PercentDamage, bid.WhatIf.Percent);
		Assert.Equal(copy.Assessment.DamageMessage, bid.WhatIf.DamageMessage);
		Assert.NotEqual(ranch.Assessment.PercentDamage, bid.WhatIf.Percent);
		Assert.Equal(ranch.Assessment.PercentDamage, ui.Store.LoadAssessment(id).Assessment.PercentDamage);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("67-appeal");

		// Locked again: using a bid says so, asks why, reopens, then saves through the normal tracked save.
		ui.Store.SetStatus(id, AssessmentStatus.Final);
		Task use = sheet.UseBid(bid);
		ui.Pump();
		ReopenSheetViewModel reopen = Assert.IsType<ReopenSheetViewModel>(ui.Main.Sheets.Current);
		reopen.Reason = "Owner's contractor bid accepted on appeal";
		reopen.ReopenCommand.Execute(null);
		await use;
		AssessmentFile used = ui.Store.LoadAssessment(id);
		Assert.Equal(LookupIds.RepairContractorEstimate, used.Assessment.CostOfRepairsID);
		Assert.Equal(41000m, used.Assessment.ContractorEstimate);
		Assert.Equal(bid.WhatIf.Percent, used.Assessment.PercentDamage);
		Assert.Equal(AssessmentStatus.Appeal, ui.Store.GetStatus(id).Value);
		Assert.Contains(ui.Store.GetHistory(id), h => h.Label == "Repair cost from contractor bid: Sample Builders, $41,000.00");
		Assert.Contains(ui.Store.GetHistory(id), h => h.Label == "Reopened: Owner's contractor bid accepted on appeal");
		Assert.True(sheet.Bids.Single(b => b.Contractor == "Sample Builders").IsInUse);
		Assert.Equal(used.Assessment.PercentDamage, ui.Main.Editor.Assessment.PercentDamage);   // the editor shows it

		// Saying no to "Use this bid?" changes nothing.
		ui.Dialogs.Answers.Enqueue(false);
		await sheet.UseBid(sheet.Bids.Single(b => b.Contractor == "Example Contracting"));
		Assert.Equal(41000m, ui.Store.LoadAssessment(id).Assessment.ContractorEstimate);
		sheet.CancelCommand.Execute(null);
		AssertNoBindingErrors();
	}

	[AvaloniaFact]
	public void CumulativeCard_OffByDefault_OnWithSettings_AndFemaResultUnchanged()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		AssessmentFile ranch = Ranch(ui);
		ui.Main.OpenAssessment(ranch.Assessment.AssessmentID);
		ui.Pump();
		Assert.False(ui.Main.Editor.ShowCumulative);
		Border card = ui.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CumulativeCard");
		Assert.False(card.IsEffectivelyVisible);

		// An earlier event on the same structure: 20% (a $20,000 contractor's estimate on a $100,000 appraisal).
		AssessmentFile earlier = ui.Store.CreateAssessment(ranch.Property.PropertyID, false);
		earlier.Assessment.DateDamageOccurred = new DateTime(2022, 5, 1);
		earlier.Assessment.DateofInspection = new DateTime(2022, 5, 8);
		earlier.Assessment.ValueOfBuildingID = LookupIds.ValueMarketAppraisal;
		earlier.Assessment.MarketAppraisal = 100000m;
		earlier.Assessment.CostOfRepairsID = LookupIds.RepairContractorEstimate;
		earlier.Assessment.ContractorEstimate = 20000m;
		ui.Store.SaveAssessment(earlier);
		Assert.Equal(20m, ui.Store.LoadAssessment(earlier.Assessment.AssessmentID).Assessment.PercentDamage);

		// Turned on in Settings.
		ui.Main.ShowCommand.Execute(ViewModels.Page.Settings);
		ui.Main.Settings.Show(SettingsCategory.ReportsAndLetters);
		OutputSettingsViewModel settings = ui.Main.Settings.Outputs;
		Assert.False(settings.CumulativeEnabled);
		settings.CumulativeEnabled = true;
		settings.CumulativeLookbackYears = 10;
		settings.CumulativeThreshold = 50;
		Assert.True(ui.Store.GetCumulativeSettings().Enabled);
		Assert.Equal(10, ui.Store.GetCumulativeSettings().LookbackYears);

		ui.Main.OpenAssessment(ranch.Assessment.AssessmentID);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.True(editor.ShowCumulative);
		Assert.Equal("Cumulative over 10 years: 95.0%", editor.CumulativeTitle);
		Assert.Equal("Meets the 50% cumulative threshold", editor.CumulativeVerdict);
		Assert.Contains(editor.CumulativeLines, l => l.StartsWith("05/01/2022: 20.0%"));
		Assert.Equal("75.0%", editor.PercentText);                         // FEMA's own result is unchanged
		Assert.Equal("Substantially Damaged", editor.Determination);
		card = ui.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CumulativeCard");
		Assert.True(card.IsEffectivelyVisible);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("68-cumulative");
		AssessmentFile stored = ui.Store.LoadAssessment(ranch.Assessment.AssessmentID);
		Assert.Equal(ranch.Assessment.PercentDamage, stored.Assessment.PercentDamage);
		Assert.Equal(ranch.Assessment.DamageMessage, stored.Assessment.DamageMessage);
		AssertNoBindingErrors();
	}

	[AvaloniaFact]
	public void Settings_SiAndLetterSections_SaveAndCreateTemplates()
	{
		using UiSession ui = new UiSession();
		ui.Main.ShowCommand.Execute(ViewModels.Page.Settings);
		ui.Main.Settings.Show(SettingsCategory.ReportsAndLetters);
		OutputSettingsViewModel o = ui.Main.Settings.Outputs;
		Assert.Equal(50m, o.SiThreshold);
		o.SiThreshold = 40m;
		o.SiLookbackYears = 10m;
		SiSettings si = ui.Store.GetSiSettings();
		Assert.Equal(40m, si.ThresholdPercent);
		Assert.Equal(10, si.LookbackYears);

		Assert.False(o.HasTemplates);
		o.CreateStarterTemplatesCommand.Execute(null);
		Assert.Equal(3, o.Templates.Count);
		Assert.Contains(ui.Dialogs.Notifications, n => n.Contains("counsel"));
		// Never overwritten: an edited template stays as it is.
		string edited = Path.Combine(o.TemplatesFolder, LetterTemplates.SubstantialDamageFile);
		File.WriteAllText(edited, "edited");
		o.CreateStarterTemplatesCommand.Execute(null);
		Assert.Equal("edited", File.ReadAllText(edited));
		o.ShowTemplatesFolderCommand.Execute(null);
		Assert.Contains("reveal:" + o.TemplatesFolder, ui.Dialogs.Opened);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
	}

	[AvaloniaFact]
	public async Task Export_WarnsWhenAssessmentsArentFinal()
	{
		using UiSession ui = WithSamples();
		string exports = Path.Combine(ui.Folder, "exports");
		Directory.CreateDirectory(exports);

		// Three drafts: asked, and "no" stops the export.
		ui.Dialogs.Answers.Enqueue(exports);
		ui.Dialogs.Answers.Enqueue(false);
		await ui.Main.ImportExport.ExportAsync(null);
		Assert.Empty(ui.Dialogs.Answers);
		Assert.Empty(Directory.GetDirectories(exports));

		// All Final: not asked.
		foreach (AssessmentFile f in ui.Store.LoadAllAssessments())
		{
			ui.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		}
		ui.Dialogs.Answers.Enqueue(exports);
		await ui.Main.ImportExport.ExportAsync(null);
		Assert.Single(Directory.GetDirectories(exports));
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Export complete"));

		// The submission folder asks too.
		ui.Store.SetStatus(Ranch(ui).Assessment.AssessmentID, AssessmentStatus.Draft, "Rechecking");
		ui.Main.Structures.OpenSubmissionCommand.Execute(null);
		string parent = Path.Combine(ui.Folder, "out");
		Directory.CreateDirectory(parent);
		ui.Dialogs.Answers.Enqueue(parent);
		ui.Dialogs.Answers.Enqueue(false);
		await ui.Main.Structures.Submission.WriteCommand.ExecuteAsync(null);
		Assert.Empty(ui.Dialogs.Answers);
		Assert.Empty(Directory.GetDirectories(parent));
	}
}
