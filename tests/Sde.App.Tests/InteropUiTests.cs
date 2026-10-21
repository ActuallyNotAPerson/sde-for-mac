using Avalonia.Headless.XUnit;
using ClosedXML.Excel;
using Sde.App.ViewModels;
using Sde.Core.Records;
using Sde.Interop;

namespace Sde.App.Tests;

/// <summary>Reports, spreadsheets, maps, coordinates, Enterprise Import and settings files, driven through the app's own commands.</summary>
public class InteropUiTests
{
	[AvaloniaTheory]
	[InlineData("Summary")]
	[InlineData("Summary-NoPhotos")]
	[InlineData("Detail")]
	[InlineData("Detail-NoPhotos")]
	[InlineData("PercentDamaged")]
	[InlineData("Community")]
	public async Task Print_FromTheStructuresList_SavesAPdfAndOpensIt(string kind)
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		string pdf = Path.Combine(ui.Folder, kind + ".pdf");
		ui.Dialogs.Answers.Enqueue(pdf);
		await ui.Main.Structures.PrintCommand.ExecuteAsync(kind);
		Assert.Empty(ui.Dialogs.Notifications);
		Assert.Equal(new[] { pdf }, ui.Dialogs.Opened);
		byte[] bytes = File.ReadAllBytes(pdf);
		Assert.True(bytes.Length > 2000);
		Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
	}

	[AvaloniaFact]
	public async Task Print_FromTheEditor_IncludesUnsavedEdits()
	{
		using UiSession ui = new UiSession();
		AssessmentFileHelper.OpenRanch(ui);
		ui.Main.Editor.Elements.Single(x => x.Name == "HVAC").UserDamage = 0; // not autosaved yet
		string percent = ui.Main.Editor.PercentText;
		string pdf = Path.Combine(ui.Folder, "detail.pdf");
		ui.Dialogs.Answers.Enqueue(pdf);
		await ui.Main.Editor.PrintCommand.ExecuteAsync("Detail");
		Assert.Contains(pdf, ui.Dialogs.Opened);
		Assert.Equal(percent, $"{ui.Store.LoadAssessment(ui.Main.Editor.Assessment.AssessmentID).Assessment.PercentDamage:0.0}%");
	}

	[AvaloniaFact]
	public async Task Excel_CoordinatesAndMap_RoundTrip()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(height: 1500);
		ScreenTourTests.AddSamples(ui.Store);
		ImportExportViewModel ie = ui.Main.ImportExport;

		// Excel: a header row plus one row per assessment.
		string xlsx = Path.Combine(ui.Folder, "all.xlsx");
		ui.Dialogs.Answers.Enqueue(xlsx);
		await ie.ExportExcelCommand.ExecuteAsync(null);
		using (XLWorkbook wb = new XLWorkbook(xlsx))
		{
			Assert.Equal(1 + 3, wb.Worksheet(1).RowsUsed().Count());
			Assert.Equal(ExcelExport.Headers.Count, wb.Worksheet(1).Row(1).CellsUsed().Count());
		}

		// No coordinates yet: the map has no pins and says why.
		string kmz = Path.Combine(ui.Folder, "map.kmz");
		ui.Dialogs.Answers.Enqueue(kmz);
		await ie.ExportMapCommand.ExecuteAsync(null);
		Assert.Contains("0 pins", ie.LastResult);
		Assert.Contains("3 assessments have no latitude/longitude", ie.LastResult);

		// Save the coordinates sheet, fill it in "in Excel", read it back, apply.
		string sheet = Path.Combine(ui.Folder, "coords.xlsx");
		ui.Dialogs.Answers.Enqueue(sheet);
		await ie.ExportLatLongCommand.ExecuteAsync(null);
		using (XLWorkbook wb = new XLWorkbook(sheet))
		{
			IXLWorksheet ws = wb.Worksheet(1);
			int row = 2;
			foreach (IXLRow r in ws.RowsUsed().Skip(1))
			{
				r.Cell(3).Value = 39.10 + row / 1000.0;
				r.Cell(4).Value = -84.51;
				row++;
			}
			ws.Row(2).Cell(3).Value = "not a number"; // one bad row
			wb.Save();
		}
		await ie.LoadLatLongAsync(sheet);
		Assert.True(ie.HasLatLong);
		Assert.Equal(3, ie.LatLongRows.Count);
		Assert.Single(ie.LatLongRows, r => r.IsProblem);
		Assert.Equal(2, ie.LatLongRows.Count(r => r.Include));
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Snapshot("33-coordinates-preview");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
		ie.ApplyLatLongCommand.Execute(null);
		Assert.False(ie.HasLatLong);
		Assert.Equal(2, ui.Store.GetProperties(activeOnly: true).Count(p => p.Latitude != null));

		ui.Dialogs.Answers.Enqueue(kmz);
		await ie.ExportMapCommand.ExecuteAsync(null);
		Assert.Contains("2 pins", ie.LastResult);
	}

	[AvaloniaFact]
	public async Task EnterpriseImport_GuessesColumns_PreviewsAndCreatesStructures()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(height: 2700);
		string csv = Path.Combine(ui.Folder, "parcels.csv");
		File.WriteAllText(csv,
			"Parcel Number,Owner,Site Address,City,State,Zip Code,Year Built\n" +
			"001-0001,Jane Smith,12 Elm St,Exampleville,OH,43702,1955\n" +
			"001-0002,Bob Jones,400 Martin Luther King Blvd,Exampleville,Ohio,43719,1920\n" +
			"001-0003,No Address,,Exampleville,OH,43702,1980\n");
		EnterpriseImportViewModel ent = ui.Main.ImportExport.Enterprise;
		ent.OpenFile(csv);
		Assert.True(ent.HasFile);

		EnterpriseFieldRowViewModel Row(EnterpriseField f) => ent.Groups.SelectMany(g => g.Rows).Single(r => r.Info.Field == f);
		Assert.Equal("Parcel Number", Row(EnterpriseField.ParcelNumber).Column);
		Assert.Equal("City", Row(EnterpriseField.City).Column);
		Assert.Equal("Zip Code", Row(EnterpriseField.Zip).Column);
		Assert.Equal("Site Address", Row(EnterpriseField.StreetName).Column); // whole address in one column
		Assert.Equal((int)StreetNameParsing.NumberNameSuffix, Row(EnterpriseField.StreetName).Mode.Value);

		// Map the owner by hand: one "John Doe" column feeds both names.
		Row(EnterpriseField.OwnerFirstName).Column = "Owner";
		Row(EnterpriseField.OwnerFirstName).Mode = Row(EnterpriseField.OwnerFirstName).Modes.Single(m => m.Value == (int)NameParsing.FirstLast);
		Row(EnterpriseField.OwnerLastName).Column = "Owner";
		Row(EnterpriseField.OwnerLastName).Mode = Row(EnterpriseField.OwnerLastName).Modes.Single(m => m.Value == (int)NameParsing.FirstLast);

		Assert.Null(ent.MappingProblems);
		Assert.Equal(3, ent.PreviewRows.Count);
		Assert.Equal("Jane Smith", ent.PreviewRows[0].Owner);
		Assert.Equal("400 Martin Luther King Boulevard, Exampleville", ent.PreviewRows[1].Address);
		Assert.Equal("(no address), Exampleville", ent.PreviewRows[2].Address);
		Assert.True(ent.CanImport);
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Snapshot("34-enterprise-import");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));

		await ent.ImportCommand.ExecuteAsync(null);
		List<PropertyRecord> created = ui.Store.GetProperties(activeOnly: true);
		Assert.InRange(created.Count, 2, 3); // the row without an address is only imported if the parser allows it
		PropertyRecord mlk = created.Single(p => p.ParcelNumber == "001-0002");
		Assert.Equal("400", mlk.StreetNumber);
		Assert.Equal("Martin Luther King", mlk.StreetName);
		Assert.NotNull(mlk.StreetSuffixID);
		Assert.Equal("Jones", mlk.OwnerLastName);
		Assert.Equal(1920, mlk.YearOfConstruction);
		Assert.Single(ent.SavedMappings); // remembered, as the original did
		Assert.False(ent.HasFile);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Import complete"));
	}

	[AvaloniaFact]
	public async Task Sde2Folder_PreviewsWithNotes_AndImportsWithPhotos()
	{
		using UiSession ui = new UiSession();
		string folder = LegacyFixtures.WriteSde2Export(ui.Folder);
		ImportExportViewModel ie = ui.Main.ImportExport;
		await ie.LoadPendingAsync(folder);
		Assert.True(ie.HasPending);
		Assert.Equal(2, ie.PendingRows.Count);
		Assert.NotNull(ie.PendingNotes); // the fixture has a depreciation value SDE 2.x may have meant differently
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Snapshot("35-import-sde2-preview");

		await ie.RunImportCommand.ExecuteAsync(null);
		Assert.False(ie.HasPending);
		Assert.Equal(2, ui.Store.ListStructures().Count);
		// Three photo records; the fixture's "missing.jpg" has no file, the two IMG_0001.JPGs don't overwrite each other.
		List<AttachmentRecord> photos = ui.Store.LoadAllAssessments().SelectMany(a => a.Attachments).ToList();
		Assert.Equal(3, photos.Count);
		Assert.Equal(2, photos.Count(a => File.Exists(ui.Store.PhotoPath(a))));
		Assert.Equal(2, photos.Where(a => File.Exists(ui.Store.PhotoPath(a))).Select(a => File.ReadAllText(ui.Store.PhotoPath(a))).Distinct().Count());
	}

	[AvaloniaFact]
	public async Task SettingsFile_ExportsAndImportsIntoAnotherDatabase()
	{
		using UiSession a = new UiSession();
		a.Store.SaveNote(new NoteRecord { NoteID = Guid.NewGuid(), NoteName = "Flood note", Notes = "Water line measured from the floor.", Active = true });
		DefaultValuesRecord d = a.Store.GetDefaults() ?? new DefaultValuesRecord();
		d.City = "Exampleville";
		a.Store.SaveDefaults(d);
		string file = Path.Combine(a.Folder, "settings.json");
		a.Dialogs.Answers.Enqueue(file);
		await a.Main.ImportExport.ExportSettingsFileCommand.ExecuteAsync(null);
		Assert.True(File.Exists(file));

		using UiSession b = new UiSession();
		b.Dialogs.Answers.Enqueue(new[] { file });
		b.Dialogs.Answers.Enqueue(true); // "Import settings?"
		await b.Main.ImportExport.ImportSettingsFileCommand.ExecuteAsync(null);
		Assert.Contains(b.Dialogs.Notifications, n => n.StartsWith("Settings imported"));
		Assert.Single(b.Store.GetNotes(), n => n.NoteName == "Flood note");
		Assert.Equal("Exampleville", b.Store.GetDefaults().City);
	}
}

internal static class AssessmentFileHelper
{
	public static void OpenRanch(UiSession ui)
	{
		Core.Assessments.AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
	}
}
