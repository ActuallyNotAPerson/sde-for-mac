using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.Views;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>The import page (inspections or parcels) and the review in Structures, driven through the app's own view models.</summary>
public class SourceImportUiTests
{
	private const string Sda = "Substantial Damage Assessment";

	public static string Fixture(string name) => Path.Combine(TestPaths.Root, "tests", "fixtures", "accela", name);

	public static string ImportFixture(string name) => Path.Combine(TestPaths.Root, "tests", "fixtures", "import", name);

	private static SourceFieldRowViewModel Row(SourceImportViewModel acc, string key) => acc.Groups.SelectMany(g => g.Rows).Single(r => r.Info.Key == key);

	/// <summary>What a user does with the wide fixture (an Accela export): name the source, filter to damage assessments, two constants.</summary>
	internal static async Task SetUpWideExport(SourceImportViewModel acc)
	{
		acc.OpenFile(Fixture("inspections-wide.csv"));
		acc.SourceName = "Accela";
		acc.FilterColumn = "Inspection Type";
		acc.FilterValues.Single(v => v.Value == Sda).Include = true;
		Row(acc, "BaseCost").Constant = "118.75";
		Row(acc, "GeographicAdjustment").Constant = "0.93";
		acc.UseDefaultValues = false;
		await acc.RefreshPreviewAsync();
	}

	[AvaloniaFact]
	public async Task MapMatchPreviewImport_ThenReviewInStructures()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(height: 1500);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Main.RefreshStatus();
		SourceImportViewModel acc = ui.Main.SourceImport;
		ui.Main.ShowCommand.Execute(Page.SourceImport);
		Assert.True(ui.Main.IsImportExportPage);                 // a sub-page of Import & export

		await SetUpWideExport(acc);
		Assert.Equal(("Record ID", "Inspection ID"), (acc.RecordIdColumn, acc.InspectionIdColumn));
		Assert.Equal("Foundation % Damage", Row(acc, "Element.4").Column);
		Assert.Equal((int)Sde.Interop.StreetNameParsing.NumberNameSuffix, Row(acc, "StreetName").Mode.Value);
		Assert.Equal("e.g. 10%", Row(acc, "Element.4").Sample);
		Assert.Contains("→ 4 inspections (1 left out by the filter)", acc.TableSummary);
		Assert.StartsWith("Location & owner · ", acc.Groups[0].Header);

		// Two words in the file SDE doesn't know: pick what they mean.
		Assert.Equal(new[] { "Single Family", "Asphalt Shingles" }, acc.Values.Select(v => v.Text));
		Assert.Contains("2 values in the file don't match", acc.ValuesSummary);
		acc.Values[0].Choice = "Single Family Residence";
		acc.Values[1].Choice = "Shingles - Asphalt, Wood (Standard)";
		await acc.RefreshPreviewAsync();
		Assert.Equal("Every value in the file matches one of SDE's choices.", acc.ValuesSummary);
		Assert.All(acc.Values, v => Assert.True(v.IsMatched));    // still listed, so they can be changed

		Assert.Null(acc.MappingProblems);
		Assert.Equal("3 new structures · 1 can't be imported", acc.PreviewSummary);
		Assert.Equal("Import 3 inspections", acc.ImportText);
		SourcePreviewRowViewModel ranch = acc.PreviewRows.Single(r => r.Record == "BLD-2026-00123 · 4471");
		Assert.Equal(("New structure", "75.0% · Substantially Damaged"), (ranch.Status, ranch.Result));
		Assert.Equal("123 River Street, Exampleville", ranch.Address);
		Assert.Equal("No record ID", acc.PreviewRows.Single(r => r.IsProblem).Note);
		ui.Snapshot("36-accela-mapping");
		acc.ExpandAllCommand.Execute(null);                       // collapse the fields to show the preview
		Assert.All(acc.Groups, g => Assert.False(g.IsExpanded));
		for (int i = 0; i < 10; i++)
		{
			ui.Pump();                                            // let the expanders' chevrons finish turning
		}
		ScrollViewer page = ui.Window.GetVisualDescendants().OfType<SourceImportView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();
		page.Offset = new Vector(0, page.Extent.Height - page.Viewport.Height);
		ui.Snapshot("37-accela-preview");

		await acc.ImportCommand.ExecuteAsync(null);
		Assert.Equal("3 created from inspections-wide.csv.", acc.LastResult);
		Assert.Equal("Review the 3 in Structures →", acc.ReviewText);
		Assert.Equal("Nothing to import", acc.ImportText);       // the preview now says all unchanged
		Assert.False(acc.CanImport);
		Assert.True(File.Exists(Path.Combine(ui.Folder, "Import Profiles", "Import.json")));
		Assert.Equal("Import", ui.Services.Settings.LastImportProfile);
		Assert.Equal("Accela", SourceProfileOnDisk(ui, "Import").SourceName);
		Assert.Equal("6 structures · 6 assessments", ui.Main.CountsText);
		ui.Pump();
		page.Offset = new Vector(0, page.Extent.Height - page.Viewport.Height);
		ui.Snapshot("37b-accela-imported");

		acc.ReviewCommand.Execute(null);
		StructuresViewModel s = ui.Main.Structures;
		Assert.Equal(Page.Structures, ui.Main.ActivePage);
		Assert.Equal("Last import · 3", s.OnlyLabel);
		Assert.Equal(3, s.Rows.Count);
		Assert.All(s.Rows, r => Assert.StartsWith("Accela ", r.Source));
		Assert.Equal("Pat Example · Accela BLD-2026-00123", s.Rows.Single(r => r.Address.StartsWith("123")).Detail);
		Assert.True(s.HasImported);
		ui.Snapshot("38-structures-accela-review");

		s.Search = "com-2026";                                     // record IDs are searchable
		Assert.Equal("800 Main Street", Assert.Single(s.Rows).Address);
		s.Search = null;
		s.ClearOnlyCommand.Execute(null);
		Assert.Equal(6, s.Rows.Count);
		s.SourceFilter = 2;
		Assert.Equal(3, s.Rows.Count);
		Assert.All(s.Rows, r => Assert.Null(r.Source));
		s.SourceFilter = 1;
		Assert.Equal(3, s.Rows.Count);

		ui.Main.OpenAssessment(s.Rows.Single(r => r.Address.StartsWith("123")).AssessmentID!.Value);
		Assert.EndsWith("Accela BLD-2026-00123 / 4471", ui.Main.Editor.Subtitle);
		Assert.Equal("75.0%", ui.Main.Editor.PercentText);

		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task TheProfileIsRemembered_AndTheSameExportAgainChangesNothing()
	{
		using UiSession ui = new UiSession();
		SourceImportViewModel acc = ui.Main.SourceImport;
		// The Import & export card (and File → Import Inspections or Parcels…) opens the page and asks for the file.
		ui.Dialogs.Answers.Enqueue(new[] { Fixture("inspections-wide.csv") });
		await ui.Main.ImportExport.OpenSourceImportCommand.ExecuteAsync(null);
		Assert.Equal(Page.SourceImport, ui.Main.ActivePage);
		Assert.Equal("inspections-wide.csv", acc.FileName);
		Assert.Equal("Import", acc.ProfileName);
		Assert.Equal("Imported", acc.SourceName);              // a new profile's source until it's named

		await SetUpWideExport(acc);
		acc.Values.Single(v => v.Text == "Single Family").Choice = "Single Family Residence";
		acc.Values.Single(v => v.Text == "Asphalt Shingles").Choice = "Shingles - Asphalt, Wood (Standard)";
		acc.ProfileName = "Example County";
		await acc.ImportCommand.ExecuteAsync(null);
		acc.CloseCommand.Execute(null);
		Assert.False(acc.HasFile);

		// Next time: the saved profile is picked for the same export, with its filter, constants and translations.
		acc.OpenFile(Fixture("inspections-wide.csv"));
		Assert.Equal(new[] { "Example County" }, acc.SavedProfiles);
		Assert.Equal("Example County", acc.SelectedProfile);
		Assert.Equal("118.75", Row(acc, "BaseCost").Constant);
		Assert.True(acc.FilterValues.Single(v => v.Value == Sda).Include);
		await acc.RefreshPreviewAsync();
		Assert.Equal("3 unchanged · 1 can't be imported", acc.PreviewSummary);
		Assert.Equal(2, acc.Values.Count(v => v.IsMatched));
		Assert.False(acc.CanImport);

		// The same inspections as a checklist export: switch the layout and map a checklist item. Elm's plumbing was
		// corrected in the checklist (30 → 35), so that is the one change.
		acc.OpenFile(Fixture("checklist-long.csv"));
		Assert.Equal("Example County", acc.SelectedProfile);       // it has a Record ID column, so the profile applies
		Assert.Equal(0, acc.LayoutIndex);
		acc.LayoutIndex = 1;
		acc.ItemColumn = "Checklist Item";
		acc.ValueColumn = "Checklist Item Value";
		Row(acc, "Element.12").Column = "Checklist: Plumbing % Damage";
		await acc.RefreshPreviewAsync();
		Assert.Contains("BLD-2026-00124 / 4472", acc.ShapeNotes);
		SourcePreviewRowViewModel elm = acc.PreviewRows.Single(r => r.Record == "BLD-2026-00124 · 4472");
		Assert.Equal("Update 1 field", elm.Status);
		Assert.StartsWith("Plumbing % damaged: '30' → '35'", elm.Note);
		Assert.Equal("Import 1 inspection", acc.ImportText);

		// Deleting the profile.
		await acc.DeleteProfileCommand.ExecuteAsync(null);
		Assert.Empty(acc.SavedProfiles);
	}

	private static Sde.Interop.SourceProfile SourceProfileOnDisk(UiSession ui, string name) => ui.Services.ImportProfiles.Load(name, out _);

	/// <summary>
	/// An assessor roll as structures (the "County parcels" example), then inspections matched to them by address (the
	/// "Generic inspections" example): its building value needs the person's word that it excludes the land, and an
	/// address shared by two parcels is shown as ambiguous.
	/// </summary>
	[AvaloniaFact]
	public async Task ParcelsAsStructures_ThenInspectionsByAddress_WithTheBuildingValueQuestion()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(height: 1500);
		SourceImportViewModel page = ui.Main.SourceImport;
		ui.Main.ShowCommand.Execute(Page.SourceImport);
		page.OpenFile(ImportFixture("county-parcels.csv"));
		Assert.Equal(1, page.ModeIndex);                           // no record ID but a parcel column: structures, by parcel
		Assert.Equal(1, page.MatchByIndex);
		Assert.Equal(new[] { "Generic inspections", "County parcels", "Accela (example)" }, page.ExampleProfiles);
		page.SelectedExample = "County parcels";
		Assert.Equal(("County parcels", "County parcels"), (page.ProfileName, page.SourceName));
		Assert.True(page.IsStructuresMode);
		Assert.All(page.Groups, g => Assert.Equal(g.AppliesToStructures, g.IsShown));
		await page.RefreshPreviewAsync();
		Assert.Null(page.MappingProblems);
		Assert.Equal("4 new structures", page.PreviewSummary);
		Assert.Equal("Import 4 structures", page.ImportText);
		SourcePreviewRowViewModel first = page.PreviewRows[0];
		Assert.Equal(("900-01-001", "New structure", "Structure only"), (first.Record, first.Status, first.Result));
		ui.Snapshot("36c-import-parcels");
		await page.ImportCommand.ExecuteAsync(null);
		Assert.Equal("4 created from county-parcels.csv.", page.LastResult);
		Assert.Equal("4 structures · 0 assessments", ui.Main.CountsText);
		page.ReviewCommand.Execute(null);
		Assert.Equal("Last import · 4", ui.Main.Structures.OnlyLabel);
		Assert.Equal(4, ui.Main.Structures.Rows.Count);
		Assert.Contains(ui.Main.Structures.Rows, r => r.Source == "County parcels 900-01-001");

		// Inspections from a spreadsheet, matched to those structures by address.
		ui.Main.ShowCommand.Execute(Page.SourceImport);
		page.OpenFile(ImportFixture("generic-inspections.csv"));
		page.SelectedExample = "Generic inspections";
		Assert.Equal((0, 2), (page.ModeIndex, page.MatchByIndex));
		await page.RefreshPreviewAsync();
		Assert.True(page.NeedsBuildingValueConfirmation);
		Assert.Contains("FEMA counts the building only", page.MappingProblems);
		Assert.False(page.CanImport);
		ui.Dialogs.Answers.Enqueue(false);                         // "not sure": still refused
		await page.ConfirmBuildingValueCommand.ExecuteAsync(null);
		Assert.True(page.NeedsBuildingValueConfirmation);
		ui.Dialogs.Answers.Enqueue(true);
		await page.ConfirmBuildingValueCommand.ExecuteAsync(null);
		Assert.False(page.NeedsBuildingValueConfirmation);
		Assert.Null(page.MappingProblems);
		Assert.Equal("1 new structure · 2 added to existing structures · 1 ambiguous match", page.PreviewSummary);
		SourcePreviewRowViewModel ambiguous = page.PreviewRows.Single(r => r.Record == "INS-0002");
		Assert.Equal("Ambiguous", ambiguous.Status);
		Assert.Contains("2 structures have this address", ambiguous.Note);
		Assert.False(ambiguous.CanInclude);
		Assert.Equal("42.1% · Not Substantially Damaged", page.PreviewRows.Single(r => r.Record == "INS-0001").Result);
		ui.Snapshot("36d-import-inspections-by-address");
		await page.ImportCommand.ExecuteAsync(null);
		Assert.Equal("3 created from generic-inspections.csv.", page.LastResult);
		Assert.Equal("5 structures · 3 assessments", ui.Main.CountsText);
		Assert.True(SourceProfileOnDisk(ui, "Generic inspections").ValueIsBuildingOnly);

		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	/// <summary>The first time, v1.5's "Accela Profiles" folder is copied to "Import Profiles"; the old one is left alone.</summary>
	[Fact]
	public void OldAccelaProfiles_AreCopiedOnce()
	{
		using TempDatabase t = new TempDatabase();
		string old = Path.Combine(t.Folder, ImportProfiles.OldFolderName);
		Directory.CreateDirectory(old);
		File.WriteAllText(Path.Combine(old, "Example County.json"), """{ "Name": "Example County", "Layout": "OneRowPerInspection", "RecordIdColumn": "Record ID" }""");

		ImportProfiles profiles = new ImportProfiles(Path.Combine(t.Folder, ImportProfiles.FolderName));
		Assert.Equal(new[] { "Example County" }, profiles.Names());
		Sde.Interop.SourceProfile p = profiles.Load("Example County", out List<string> warnings);
		Assert.Empty(warnings);
		Assert.Equal(("Accela", Sde.Interop.SourceMode.Inspections), (p.SourceName, p.Mode));
		Assert.True(File.Exists(Path.Combine(old, "Example County.json")));

		// Once only: a profile deleted from the new folder doesn't come back.
		profiles.Delete("Example County");
		Assert.Empty(new ImportProfiles(profiles.Folder).Names());
	}
}
