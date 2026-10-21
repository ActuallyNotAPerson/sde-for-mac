using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Sde.App.Help;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>Field help, the FEMA manual, the sample database and the walkthrough.</summary>
public class HelpUiTests
{
	/// <summary>A stand-in for the manual: 244 blank pages, page n being (100 + n) points wide so a page can be told apart.</summary>
	private static string MakeManual(string folder)
	{
		string path = Path.Combine(folder, "SDE-User-Manual.pdf");
		using PdfDocument doc = new PdfDocument();
		for (int n = 1; n <= 244; n++)
		{
			PdfPage page = doc.AddPage();
			page.Width = XUnitPoint(100 + n);
			page.Height = XUnitPoint(200);
		}
		doc.Save(path);
		return path;
	}

	private static PdfSharp.Drawing.XUnitPt XUnitPoint(double points) => PdfSharp.Drawing.XUnitPt.FromPoint(points);

	private static string Name(Control c) => ControlAutomationPeer.CreatePeerForElement(c).GetName();

	private static Button HelpButton(UiSession ui, string label)
	{
		ui.Pump();
		return ui.Window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("help") && b.IsEffectivelyVisible && Name(b) == "Help for " + label);
	}

	/// <summary>Presses a button the way its automation peer does (what VoiceOver's "press" does).</summary>
	private static void Click(Button b)
	{
		Assert.True(b.Command.CanExecute(b.CommandParameter));
		b.Command.Execute(b.CommandParameter);
	}

	private static AssessmentEditorViewModel OpenRanch(UiSession ui)
	{
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		return ui.Main.Editor;
	}

	// ───────────── Field help ─────────────

	[Fact]
	public void EveryCatalogField_HasHelp_WithAManualPage()
	{
		foreach (FieldInfo f in FieldCatalog.All)
		{
			FieldHelpEntry e = FieldHelp.Get(f.Key);
			Assert.True(e != null, "No help for " + f.Key);
			Assert.False(string.IsNullOrWhiteSpace(e.Text), f.Key);
			Assert.InRange(e.Page, 1, 244);
			Assert.DoesNotContain('—', e.Text);
			Assert.Equal(f.Label, e.Label);
		}
		Assert.Equal(FieldCatalog.All.Count, FieldHelp.All.Count);
	}

	[Fact]
	public void HelpFile_NamesOnlyRealFields()
	{
		string json = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Sde.App", "Help", "field-help.json"));
		string[] keys = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToArray();
		Assert.All(keys, k => Assert.NotNull(FieldCatalog.Get(k)));
		Assert.Equal(keys.Length, keys.Distinct().Count());
	}

	[AvaloniaFact]
	public void QuestionMark_OpensHelpForTheField_WithoutTheManual()
	{
		using UiSession ui = new UiSession();
		Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, Path.Combine(ui.Folder, "nothing.pdf"));
		try
		{
			AssessmentEditorViewModel e = OpenRanch(ui);
			e.SelectedSection = e.Sections[0];
			Button help = HelpButton(ui, "Street name");
			Assert.Equal("?", help.Content);
			Click(help);
			ui.Pump();

			FieldHelpSheetViewModel sheet = Assert.IsType<FieldHelpSheetViewModel>(ui.Main.Sheets.Current);
			Assert.Equal("Help: Street name", sheet.Title);
			Assert.Equal(FieldHelp.Get("Property.StreetName").Text, sheet.Text);
			Assert.Contains("page 3-28", sheet.Where);
			Assert.False(sheet.ManualAvailable);
			Assert.Contains("isn't included", sheet.MissingMessage);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == "Open the manual at this page");
			Assert.Contains(ui.Window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == "Open FEMA's SDE page");
			ui.Snapshot("69-field-help");

			sheet.OpenFemaPageCommand.Execute(null);
			Assert.Equal(ManualService.PublishedUrl, ui.Dialogs.Opened.Last());
			sheet.OpenAtPageCommand.Execute(null);
			Assert.Equal(ManualService.PublishedUrl, ui.Dialogs.Opened.Last());   // nothing new was opened

			ui.Main.Sheets.CancelCurrentCommand.Execute(null);
			Assert.False(ui.Main.Sheets.HasSheet);
		}
		finally
		{
			Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, null);
		}
	}

	[AvaloniaFact]
	public void QuestionMark_OnEveryVisibleField_IsNamed()
	{
		using UiSession ui = new UiSession(height: 1600);
		AssessmentEditorViewModel e = OpenRanch(ui);
		int seen = 0;
		foreach (SectionViewModel section in e.Sections)
		{
			e.SelectedSection = section;
			ui.Pump();
			List<Button> buttons = ui.Window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("help") && b.IsEffectivelyVisible).ToList();
			seen += buttons.Count;
			Assert.All(buttons, b => Assert.StartsWith("Help for ", Name(b)));
		}
		Assert.True(seen > 40, $"Only {seen} help buttons were shown.");
	}

	[AvaloniaFact]
	public void OpenAtPage_WritesATwoPageExcerpt_WhenTheManualIsThere()
	{
		using UiSession ui = new UiSession();
		string manual = MakeManual(ui.Folder);
		Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, manual);
		try
		{
			AssessmentEditorViewModel e = OpenRanch(ui);
			e.SelectedSection = e.Sections[0];
			Click(HelpButton(ui, "Street name"));
			ui.Pump();
			FieldHelpSheetViewModel sheet = Assert.IsType<FieldHelpSheetViewModel>(ui.Main.Sheets.Current);
			Assert.True(sheet.ManualAvailable);
			Assert.Contains(ui.Window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == "Open the manual at this page");
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

			sheet.OpenAtPageCommand.Execute(null);
			string excerpt = ui.Dialogs.Opened.Last();
			Assert.NotEqual(manual, excerpt);
			using (PdfDocument doc = PdfReader.Open(excerpt, PdfDocumentOpenMode.Import))
			{
				Assert.Equal(2, doc.PageCount);
				Assert.Equal(new[] { 154, 155 }, doc.Pages.Cast<PdfPage>().Select(p => (int)Math.Round(p.Width.Point)));   // PDF pages 54 and 55
			}

			sheet.OpenManualCommand.Execute(null);
			Assert.Equal(manual, ui.Dialogs.Opened.Last());

			// The last page has no page after it.
			using (PdfDocument last = PdfReader.Open(ui.Services.Manual.WriteExcerpt(244), PdfDocumentOpenMode.Import))
			{
				Assert.Equal(1, last.PageCount);
			}
			Assert.Throws<ArgumentOutOfRangeException>(() => ui.Services.Manual.WriteExcerpt(245));
		}
		finally
		{
			Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, null);
		}
	}

	[AvaloniaFact]
	public void HelpMenu_Manual_OpensTheWholeManual_OrSaysItIsMissing()
	{
		using UiSession ui = new UiSession();
		Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, Path.Combine(ui.Folder, "nothing.pdf"));
		try
		{
			ui.Main.OpenManualCommand.Execute(null);
			ManualMissingSheetViewModel missing = Assert.IsType<ManualMissingSheetViewModel>(ui.Main.Sheets.Current);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			missing.OpenFemaPageCommand.Execute(null);
			Assert.Equal(ManualService.PublishedUrl, ui.Dialogs.Opened.Last());
			missing.Cancel();

			string manual = MakeManual(ui.Folder);
			Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, manual);
			ui.Main.OpenManualCommand.Execute(null);
			Assert.False(ui.Main.Sheets.HasSheet);
			Assert.Equal(manual, ui.Dialogs.Opened.Last());
		}
		finally
		{
			Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, null);
		}
	}

	[AvaloniaFact]
	public void HelpIndex_ListsEveryField_AndSearches()
	{
		using UiSession ui = new UiSession();
		Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, Path.Combine(ui.Folder, "nothing.pdf"));
		try
		{
			ui.Main.ShowHelpCommand.Execute(null);
			ui.Pump();
			HelpIndexSheetViewModel index = Assert.IsType<HelpIndexSheetViewModel>(ui.Main.Sheets.Current);
			Assert.Equal(FieldCatalog.All.Count, index.Entries.Count);
			Assert.NotNull(index.Selected);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

			index.Search = "foundation";
			Assert.Contains(index.Entries, x => x.Key == "Property.FoundationTypeID");
			Assert.True(index.Entries.Count < FieldCatalog.All.Count);
			Assert.Contains(index.Entries, x => x == index.Selected);

			index.Search = "zzzzqqq";
			Assert.Empty(index.Entries);
			Assert.Null(index.Selected);
			Assert.Equal("No fields match.", index.CountText);

			index.Search = "latitude";
			Assert.Contains(index.Entries, x => x.Key == "Property.Latitude");
			Assert.Contains(index.Entries, x => x == index.Selected);
			ui.Pump();
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		}
		finally
		{
			Environment.SetEnvironmentVariable(ManualService.EnvironmentVariable, null);
		}
	}

	// ───────────── Sample database ─────────────

	[Fact]
	public void SampleDatabase_IsMarked_AndMadeOfMadeUpStructures()
	{
		string folder = Path.Combine(Path.GetTempPath(), "sde-sample-" + Guid.NewGuid().ToString("N"));
		try
		{
			string path = SampleDatabase.CreateFresh(Path.Combine(folder, SampleDatabase.FolderName));
			SdeStore store = new SdeStore(SdeDatabase.Open(path));
			Assert.True(store.GetSetting<bool>(SampleDatabase.SettingKey));

			List<StructureSummary> rows = store.ListStructures();
			Assert.Equal(5, rows.Count);
			Assert.All(rows, r => Assert.Equal(SampleDatabase.Town, r.City));
			Assert.All(rows.Where(r => r.InspectorName != null), r => Assert.Equal(SampleDatabase.Inspector, r.InspectorName));
			StructureSummary ranch = rows.Single(r => r.Address.StartsWith("123 "));
			Assert.Equal(75.0m, Math.Round(ranch.PercentDamage ?? 0, 1));
			Assert.Contains("Substantial", ranch.DamageMessage);
			Assert.Contains(rows, r => r.PercentDamage is < 50);

			// Nothing real: a made-up town and inspector.
			string everything = string.Join("|", store.LoadAllAssessments().SelectMany(a => new[]
			{
				a.Assessment.InspectorName, a.Property.OwnerFirstName, a.Property.OwnerLastName, a.Property.City, a.Property.NFIPCommunityName,
			}));
			Assert.All(store.LoadAllAssessments(), a => Assert.Equal("Exampleville", a.Property.City));
			Assert.Contains("Inspector A", everything);
			Assert.Contains("Pat", everything);

			// Making it again replaces it, and only a folder called Sample may be replaced.
			string again = SampleDatabase.CreateFresh(Path.Combine(folder, SampleDatabase.FolderName));
			Assert.Equal(5, new SdeStore(SdeDatabase.Open(again)).ListStructures().Count);
			Assert.Throws<ArgumentException>(() => SampleDatabase.CreateFresh(folder));
			Assert.Throws<IOException>(() => SampleDatabase.Create(again));
		}
		finally
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
	}

	[AvaloniaFact]
	public async Task OpenSampleDatabase_ShowsTheBanner_WarnsBeforeExporting_AndLeavesCleanly()
	{
		using UiSession ui = new UiSession(width: 1440, height: 900);
		ui.Services.SampleFolder = Path.Combine(ui.Folder, SampleDatabase.FolderName);
		string mine = ui.Store.Database.FilePath;
		string rememberedBefore = ui.Services.Settings.DatabasePath;
		Assert.False(ui.Main.IsSampleDatabase);

		// Declining the question leaves everything alone.
		ui.Dialogs.Answers.Enqueue(false);
		await ui.Main.OpenSampleDatabase();
		Assert.False(ui.Main.IsSampleDatabase);
		Assert.Equal(mine, ui.Store.Database.FilePath);

		await ui.Main.OpenSampleDatabase();
		ui.Pump();
		Assert.True(ui.Main.IsSampleDatabase);
		Assert.True(ui.Services.IsSample);
		Assert.StartsWith(ui.Services.SampleFolder, ui.Store.Database.FilePath);
		Assert.Equal(5, ui.Main.Structures.Rows.Count);
		Assert.Equal(rememberedBefore, ui.Services.Settings.DatabasePath);              // not what SDE opens next time
		Assert.DoesNotContain(ui.Store.Database.FilePath, ui.Services.Settings.RecentDatabases);
		Assert.Null(ui.Services.Backups);

		TextBlock banner = ui.Window.GetVisualDescendants().OfType<TextBlock>()
			.Single(t => t.IsEffectivelyVisible && t.Text == "This is the sample database. Changes here are practice only.");
		Assert.NotNull(banner);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("71-sample-banner");
		foreach (ViewModels.Page page in new[] { ViewModels.Page.ImportExport, ViewModels.Page.Settings, ViewModels.Page.Map, ViewModels.Page.Structures })
		{
			ui.Main.ShowCommand.Execute(page);
			ui.Pump();
			Assert.Contains(ui.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text.StartsWith("This is the sample database"));
		}

		// Exporting to FEMA SDE asks first. "No" writes nothing; "Yes" writes the folder.
		string outFolder = Path.Combine(ui.Folder, "out");
		Directory.CreateDirectory(outFolder);
		ui.Dialogs.Answers.Enqueue(false);
		ui.Dialogs.Answers.Enqueue(outFolder);
		await ui.Main.ImportExport.ExportAsync(null);
		Assert.Empty(Directory.GetFileSystemEntries(outFolder));
		Assert.Single(ui.Dialogs.Answers);                 // the folder was never asked for
		ui.Dialogs.Answers.Clear();
		ui.Dialogs.Answers.Enqueue(true);
		ui.Dialogs.Answers.Enqueue(outFolder);
		await ui.Main.ImportExport.ExportAsync(null);
		Assert.NotEmpty(Directory.GetFileSystemEntries(outFolder));

		// So does writing a submission folder.
		string subFolder = Path.Combine(ui.Folder, "submit");
		Directory.CreateDirectory(subFolder);
		ui.Main.Structures.OpenSubmissionCommand.Execute(null);
		Assert.True(ui.Main.Structures.Submission.CanWrite);
		ui.Dialogs.Answers.Enqueue(false);
		ui.Dialogs.Answers.Enqueue(subFolder);
		await ui.Main.Structures.Submission.WriteCommand.ExecuteAsync(null);
		Assert.Empty(Directory.GetFileSystemEntries(subFolder));
		Assert.Single(ui.Dialogs.Answers);
		ui.Dialogs.Answers.Clear();
		ui.Main.Structures.Submission.CancelCommand.Execute(null);

		// Back to my database.
		ui.Main.LeaveSampleDatabaseCommand.Execute(null);
		ui.Pump();
		Assert.False(ui.Main.IsSampleDatabase);
		Assert.Equal(mine, ui.Store.Database.FilePath);
		Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text != null && t.Text.StartsWith("This is the sample database"));
	}

	[AvaloniaFact]
	public async Task OpenSampleDatabase_LogsNoError()
	{
		using UiSession ui = new UiSession();
		ui.Services.SampleFolder = Path.Combine(ui.Folder, SampleDatabase.FolderName);
		await ui.Main.OpenSampleDatabase();
		await ui.Services.OpeningBackup;
		Assert.True(ui.Services.IsSample);
		string log = File.Exists(AppLog.TodayFile) ? File.ReadAllText(AppLog.TodayFile) : "";
		Assert.DoesNotContain("Couldn't make the automatic backup", log);
		Assert.DoesNotContain("Exception", log);
	}

	[AvaloniaFact]
	public async Task NormalDatabases_ExportWithoutTheSampleQuestion()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		string outFolder = Path.Combine(ui.Folder, "out");
		Directory.CreateDirectory(outFolder);
		ui.Dialogs.Answers.Enqueue(outFolder);   // the only answer: a question first would use it up
		await ui.Main.ImportExport.ExportAsync(null);
		Assert.NotEmpty(Directory.GetFileSystemEntries(outFolder));
	}

	// ───────────── Walkthrough ─────────────

	[AvaloniaFact]
	public void Walkthrough_StepsThroughItsPages_AndFinishes()
	{
		using UiSession ui = new UiSession();
		ui.Main.ShowWalkthroughCommand.Execute(null);
		ui.Pump();
		WalkthroughSheetViewModel w = Assert.IsType<WalkthroughSheetViewModel>(ui.Main.Sheets.Current);
		Assert.InRange(w.Pages.Count, 4, 5);
		Assert.Equal(0, w.PageIndex);
		Assert.False(w.CanGoBack);
		Assert.False(w.BackCommand.CanExecute(null));
		Assert.Equal("Next", w.NextText);
		Assert.Equal($"Page 1 of {w.Pages.Count}", w.PositionText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("70-walkthrough");

		w.NextCommand.Execute(null);
		Assert.Equal(1, w.PageIndex);
		Assert.True(w.BackCommand.CanExecute(null));
		w.BackCommand.Execute(null);
		Assert.Equal(0, w.PageIndex);

		for (int i = 1; i < w.Pages.Count; i++)
		{
			w.NextCommand.Execute(null);
			ui.Pump();
			Assert.Equal(i, w.PageIndex);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		}
		Assert.True(w.IsLast);
		Assert.Equal("Done", w.NextText);
		Assert.All(w.Pages, p => Assert.DoesNotContain('—', p.Body));
		w.NextCommand.Execute(null);
		Assert.True(w.IsClosed);
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public async Task Walkthrough_OpenSample_OpensTheSampleDatabase()
	{
		using UiSession ui = new UiSession();
		ui.Services.SampleFolder = Path.Combine(ui.Folder, SampleDatabase.FolderName);
		Task show = ui.Main.ShowWalkthrough();
		ui.Pump();
		WalkthroughSheetViewModel w = Assert.IsType<WalkthroughSheetViewModel>(ui.Main.Sheets.Current);
		w.OpenSampleCommand.Execute(null);
		await show;
		ui.Pump();
		Assert.True(ui.Main.IsSampleDatabase);
	}

	[AvaloniaFact]
	public void Walkthrough_ShowsOnFirstLaunchOnly_AndNeverWhenTheDatabaseIsForced()
	{
		using UiSession ui = new UiSession();
		bool seen = ui.Services.Settings.SeenWalkthrough;
		try
		{
			ui.Services.Settings.SeenWalkthrough = false;
			Assert.False(ui.Services.ShouldShowWalkthrough);            // SDE_DATABASE is set, as in every test
			Task none = ui.Main.ShowWalkthroughIfFirstRun();
			Assert.True(none.IsCompleted);
			Assert.False(ui.Main.Sheets.HasSheet);

			string forced = Environment.GetEnvironmentVariable("SDE_DATABASE");
			Environment.SetEnvironmentVariable("SDE_DATABASE", null);
			try
			{
				Assert.True(ui.Services.ShouldShowWalkthrough);
				ui.Services.Settings.SeenWalkthrough = true;
				Assert.False(ui.Services.ShouldShowWalkthrough);
			}
			finally
			{
				Environment.SetEnvironmentVariable("SDE_DATABASE", forced);
			}
		}
		finally
		{
			ui.Services.Settings.SeenWalkthrough = seen;
		}
	}
}
