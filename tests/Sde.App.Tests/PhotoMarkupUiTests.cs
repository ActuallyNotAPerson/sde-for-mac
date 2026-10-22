using Avalonia.Headless.XUnit;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Photos;
using Sde.Data;
using Sde.Reports;

namespace Sde.App.Tests;

/// <summary>
/// Photo markup (v2.5, C10): the sheet draws a water line, arrows, boxes, ellipses and text on a photo, keeps them beside
/// the unchanged photo file, shows them on the thumbnail, and the submission folder can add marked-up copies. Writes the
/// markup screenshot.
/// </summary>
public class PhotoMarkupUiTests
{
	/// <summary>Opens the first sample with a made-up wall photo added, on the Photos and notes step.</summary>
	private static async Task<(UiSession Ui, PhotoViewModel Photo)> WithWallPhoto()
	{
		UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value);
		ui.Pump();
		AssessmentEditorViewModel e = ui.Main.Editor;
		string wall = Path.Combine(ui.Folder, "front wall.jpg");
		File.WriteAllBytes(wall, FieldPhotos.Wall());
		await e.AddPhotosAsync(new[] { wall });
		e.SelectedSection = e.Sections.OfType<PhotosSectionViewModel>().Single();
		ui.Pump();
		PhotoViewModel photo = e.Photos.Last();
		await photo.ThumbnailLoaded;
		return (ui, photo);
	}

	private static MarkupColour Colour(string name) => PhotoMarkupSheetViewModel.Colours.Single(c => c.Name == name);

	[AvaloniaFact]
	public async Task MarkUp_DrawsAndSaves_TheThumbnailShowsIt_AndTheSubmissionAddsCopies()
	{
		(UiSession ui, PhotoViewModel photo) = await WithWallPhoto();
		using UiSession session = ui;
		Assert.False(photo.HasMarkup);
		byte[] original = File.ReadAllBytes(photo.Path);

		Task marking = photo.MarkUpCommand.ExecuteAsync(null);
		ui.Pump();
		PhotoMarkupSheetViewModel sheet = Assert.IsType<PhotoMarkupSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(sheet.IsEditable);
		Assert.Equal("No marks yet", sheet.CountText);

		// A water line, blue by default, with its label.
		Assert.Equal(MarkupKinds.WaterLine, sheet.Tool);
		Assert.Equal("Blue", sheet.Colour.Name);
		sheet.Label = "Water 2 ft 6 in";
		Assert.True(sheet.AddShape(0.04, 0.62, 0.96, 0.62));

		// An arrow: the colour follows the tool, and a slip of the mouse isn't a mark.
		sheet.ToolIndex = 1;
		Assert.Equal(MarkupKinds.Arrow, sheet.Tool);
		Assert.Equal("Red", sheet.Colour.Name);
		Assert.False(sheet.UsesLabel);
		Assert.False(sheet.AddShape(0.5, 0.5, 0.502, 0.5));
		Assert.True(sheet.AddShape(0.3, 0.22, 0.43, 0.42));

		// Text needs its words first.
		sheet.ToolIndex = 4;
		sheet.Label = "";
		Assert.False(sheet.AddShape(0.5, 0.2, 0.5, 0.2));
		Assert.True(sheet.HasError);
		sheet.Label = "Mud line on the siding";
		Assert.False(sheet.HasError);
		Assert.True(sheet.AddShape(0.08, 0.2, 0.08, 0.2));

		// A box in a colour the user picked; switching tools keeps that colour.
		sheet.ToolIndex = 2;
		sheet.Colour = Colour("Yellow");
		Assert.True(sheet.AddShape(0.58, 0.34, 0.74, 0.97));
		sheet.ToolIndex = 3;
		Assert.Equal("Yellow", sheet.Colour.Name);
		Assert.Equal("4 marks", sheet.CountText);

		// Undo takes back the last mark; Clear all takes every mark off, and Undo brings them back.
		sheet.UndoCommand.Execute(null);
		Assert.Equal("3 marks", sheet.CountText);
		sheet.ClearAllCommand.Execute(null);
		Assert.False(sheet.HasMarks);
		sheet.UndoCommand.Execute(null);
		Assert.Equal("3 marks", sheet.CountText);
		sheet.ToolIndex = 2;
		Assert.True(sheet.AddShape(0.58, 0.34, 0.74, 0.97));
		Assert.NotNull(sheet.Picture);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("61b-photo-markup");

		sheet.SaveCommand.Execute(null);
		await marking;
		PhotoMarkup stored = PhotoMarkup.Parse(ui.Store.GetPhotoMarkup(photo.FileName));
		Assert.Equal(new[] { MarkupKinds.WaterLine, MarkupKinds.Arrow, MarkupKinds.Text, MarkupKinds.Box }, stored.Shapes.Select(s => s.Kind));
		Assert.Equal("Water 2 ft 6 in", stored.Shapes[0].Text);
		Assert.Equal("#FDD835", stored.Shapes[3].Color);
		Assert.Equal(original, File.ReadAllBytes(photo.Path));   // the photo itself never changes
		Assert.True(photo.HasMarkup);
		Assert.StartsWith("Change the marks", photo.MarkUpLabel);
		await photo.ThumbnailLoaded;
		Assert.NotNull(photo.Thumbnail);

		// Opened again, the editor reads the marks back.
		Guid id = ui.Main.Editor.Assessment.AssessmentID;
		ui.Main.ShowStructures();
		ui.Pump();
		ui.Main.OpenAssessment(id);
		ui.Pump();
		Assert.True(ui.Main.Editor.Photos.Single(p => p.FileName == photo.FileName).HasMarkup);
		ui.Main.ShowStructures();
		ui.Pump();

		// The submission folder adds marked-up copies when asked; FEMA's package keeps the original photo.
		ui.Main.Structures.Rows.Single(r => r.AssessmentID == id).IsChecked = true;
		ui.Main.Structures.OpenSubmissionCommand.Execute(null);
		ui.Main.Structures.Submission.IncludeMarkedUpCopies = true;
		string parent = Path.Combine(ui.Folder, "submit");
		Directory.CreateDirectory(parent);
		ui.Dialogs.Answers.Enqueue(parent);
		ui.Dialogs.Answers.Enqueue(true);
		await ui.Main.Structures.Submission.WriteCommand.ExecuteAsync(null);
		string folder = Assert.Single(Directory.GetDirectories(parent));
		string copy = Assert.Single(Directory.GetFiles(Path.Combine(folder, SubmissionFolder.MarkedUpFolder)));
		Assert.EndsWith("(marked up).jpg", copy);
		Assert.Contains(Directory.GetFiles(folder, "*", SearchOption.AllDirectories), f => Path.GetFileName(f) == photo.FileName);
	}

	[AvaloniaFact]
	public async Task MarkUp_OnALockedAssessment_CanOnlyBeLookedAt()
	{
		(UiSession ui, PhotoViewModel photo) = await WithWallPhoto();
		using UiSession session = ui;
		Guid id = ui.Main.Editor.Assessment.AssessmentID;
		ui.Main.Editor.Save();
		ui.Store.SavePhotoMarkup(photo.FileName, new PhotoMarkup { Shapes = { new MarkupShape { Kind = MarkupKinds.Box, X1 = 0.1, Y1 = 0.1, X2 = 0.4, Y2 = 0.4, Color = "#E53935" } } }.ToJson());
		ui.Main.ShowStructures();
		ui.Pump();
		ui.Store.SetStatus(id, AssessmentStatus.Final);
		ui.Main.OpenAssessment(id);
		ui.Pump();
		AssessmentEditorViewModel e = ui.Main.Editor;
		Assert.True(e.IsLocked);
		PhotoViewModel locked = e.Photos.Single(p => p.FileName == photo.FileName);
		Assert.True(locked.HasMarkup);

		Task looking = locked.MarkUpCommand.ExecuteAsync(null);
		ui.Pump();
		PhotoMarkupSheetViewModel sheet = Assert.IsType<PhotoMarkupSheetViewModel>(ui.Main.Sheets.Current);
		Assert.False(sheet.IsEditable);
		Assert.Contains("locked", sheet.Instruction);
		Assert.False(sheet.AddShape(0.2, 0.2, 0.6, 0.6));
		Assert.Equal("1 mark", sheet.CountText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.CancelCommand.Execute(null);
		await looking;

		// The store refuses too.
		Assert.Throws<AssessmentLockedException>(() => ui.Store.SavePhotoMarkup(photo.FileName, null));
	}
}
