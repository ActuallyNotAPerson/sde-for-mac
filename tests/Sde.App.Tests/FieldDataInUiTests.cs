using Avalonia.Headless.XUnit;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.ElevationCertificates;
using Sde.Interop.Tests;

namespace Sde.App.Tests;

/// <summary>
/// Field data in (v2.5, M6): an elevation certificate read into the open assessment or typed on the entry sheet,
/// with the PDF kept with the structure. Also writes the screenshots.
/// </summary>
public class FieldDataInUiTests
{
	private static UiSession WithSamples()
	{
		UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	private static Guid Ranch(UiSession ui) =>
		ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value;

	private static string Pdf(UiSession ui, string name, byte[] bytes)
	{
		string path = Path.Combine(ui.Folder, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[AvaloniaFact]
	public async Task ElevationCertificate_TheTickedValuesReachTheEditor_AndThePdfIsKept()
	{
		using UiSession ui = WithSamples();
		Guid id = Ranch(ui);
		ui.Main.OpenAssessment(id);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.True(editor.Sections.Single(s => s.HasElevationCertificate).AllFields.Any(f => f.Member == nameof(PropertyRecord.BFE)));

		ui.Dialogs.Answers.Enqueue(new[] { Pdf(ui, "ec.pdf", EcTestForms.Xfa(EcTestForms.Datasets(EcTestForms.FilledValues()))) });
		Task import = editor.ImportElevationCertificateCommand.ExecuteAsync(null);
		ui.Pump();
		EcImportSheetViewModel sheet = Assert.IsType<EcImportSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(sheet.CanRead);
		Assert.True(sheet.HasAddressWarning);   // the made-up certificate is for 123 Example Road
		EcRowViewModel bfe = Assert.Single(sheet.Rows, r => r.Key == "Property.BFE");
		Assert.True(bfe.IsChecked);
		foreach (EcRowViewModel r in sheet.Rows.Where(r => r.Key != "Property.BFE"))
		{
			r.IsChecked = false;
		}
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("64-elevation-certificate");
		sheet.SaveCommand.Execute(null);
		await import;
		ui.Pump();

		Assert.Equal(742.5m, ui.Store.LoadAssessment(id).Property.BFE);
		Assert.Equal(742.5m, ui.Main.Editor.Property.BFE);   // the open editor shows it
		Assert.Single(ui.Store.GetDocuments(ui.Main.Editor.Property.PropertyID), d => d.Kind == DocumentKinds.ElevationCertificate);
		Assert.Contains(ui.Store.GetHistory(id), h => h.Via == SaveVia.EcImport && h.Field == "Property.BFE");
	}

	[AvaloniaFact]
	public async Task AFlatCertificate_OpensTheEntrySheet_AndARefusedValueSavesNothing()
	{
		using UiSession ui = WithSamples();
		Guid id = Ranch(ui);
		ui.Main.OpenAssessment(id);
		ui.Pump();
		ui.Dialogs.Answers.Enqueue(new[] { Pdf(ui, "scan.pdf", EcTestForms.Plain()) });
		Task import = ui.Main.Editor.ImportElevationCertificateCommand.ExecuteAsync(null);
		ui.Pump();
		EcImportSheetViewModel sheet = Assert.IsType<EcImportSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(sheet.IsEntrySheet);
		Assert.Equal(EcImport.EntryKeys, sheet.Rows.Select(r => r.Key));

		sheet.Rows.Single(r => r.Key == "Property.FirstFloorElevation").Value = "not a number";
		sheet.SaveCommand.Execute(null);
		Assert.True(sheet.HasError);
		Assert.Empty(ui.Store.GetDocuments(ui.Main.Editor.Property.PropertyID));

		sheet.Rows.Single(r => r.Key == "Property.FirstFloorElevation").Value = "741.2";
		sheet.SaveCommand.Execute(null);
		await import;
		ui.Pump();
		Assert.Equal(741.2m, ui.Store.LoadAssessment(id).Property.FirstFloorElevation);
		Assert.Single(ui.Store.GetDocuments(ui.Main.Editor.Property.PropertyID));
	}
}
