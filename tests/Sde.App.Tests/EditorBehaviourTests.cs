using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>What happens when someone actually uses the editor: live results, saving, switching building type, photos.</summary>
public class EditorBehaviourTests
{
	private static AssessmentEditorViewModel OpenRanch(UiSession ui)
	{
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		return ui.Main.Editor;
	}

	private static T Field<T>(AssessmentEditorViewModel e, string member)
		where T : FieldViewModel =>
		e.Sections.SelectMany(s => s.AllFields).OfType<T>().First(f => f.Member == member);

	[AvaloniaFact]
	public void EditingAnInput_UpdatesTheResultImmediately_AndSaves()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		Assert.Equal("75.0%", e.PercentText);
		Assert.Equal(ResultState.Substantial, e.State);

		// Raise the base cost: the building is worth more, so the same damage is a smaller share... and so is the damage. Ratio unchanged.
		// Instead lower the damage: make HVAC undamaged.
		ElementRowViewModel hvac = e.Elements.Single(x => x.Name == "HVAC");
		hvac.UserDamage = 0;
		Assert.NotEqual("75.0%", e.PercentText);
		Assert.Equal("Editing…", e.SaveState);

		e.Save();
		Assert.Equal("Saved", e.SaveState);
		AssessmentFile reloaded = ui.Store.LoadAssessment(e.Assessment.AssessmentID);
		Assert.Equal(0m, reloaded.ElementPercentages.Single(x => x.ElementName == "HVAC").UserDamage);
		Assert.Equal(e.Assessment.PercentDamage, reloaded.Assessment.PercentDamage);
		Assert.Equal(e.PercentText, $"{reloaded.Assessment.PercentDamage:0.0}%");
	}

	[AvaloniaFact]
	public void ValueBasisChoice_SwitchesTheCalculation()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		Field<NumberFieldViewModel>(e, "MarketAppraisal").Value = 400000m;
		e.SetValueBasisCommand.Execute("391");
		Assert.True(e.IsValueAppraisal);
		Assert.Equal(400000m, e.ValueBasisAmount);
		Assert.Equal("24.2%", e.PercentText); // 96,900.46 / 400,000
		Assert.Equal(ResultState.NotSubstantial, e.State);
	}

	[AvaloniaFact]
	public void SwitchingToNonResidential_ShowsTheNonResidentialElementsAndFields()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		Assert.Equal(12, e.Elements.Count);
		ChoiceFieldViewModel type = Field<ChoiceFieldViewModel>(e, "StructureTypeID");
		type.Selected = type.Options.Single(o => o.Id == 2);
		Assert.Equal(7, e.Elements.Count);
		Assert.Contains(e.Elements, x => x.Name == "Interiors");
		Assert.False(Field<ChoiceFieldViewModel>(e, "ResidenceTypeID").IsVisible);
		ChoiceFieldViewModel use = Field<ChoiceFieldViewModel>(e, "StructureUseID");
		Assert.True(use.IsVisible);

		// Stories filter the uses (FEMA's LookupValue1 lists the story IDs each use allows).
		ChoiceFieldViewModel stories = Field<ChoiceFieldViewModel>(e, "StoryTypeID");
		Assert.All(stories.Options.Where(o => o.Id != null), o => Assert.Contains(o.Id, new int?[] { 101, 102, 103 }));
		stories.Selected = stories.Options.Single(o => o.Id == 103);
		Assert.Contains(use.Options, o => o.Name == "Hotel");
		Assert.DoesNotContain(use.Options, o => o.Name == "Restaurants");
		Assert.False(Field<ChoiceFieldViewModel>(e, "SprinklerSystem").IsEnabled); // only for 1–4 stories
	}

	[AvaloniaFact]
	public void DepreciationRating_FillsThePercentFromFemasTable()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		ChoiceFieldViewModel rating = Field<ChoiceFieldViewModel>(e, "DepreciationRatingID");
		NumberFieldViewModel pct = Field<NumberFieldViewModel>(e, "DepreciationValue");
		rating.Selected = rating.Options.Single(o => o.Name.StartsWith("1 - Very Poor"));
		Assert.Equal(e.Assessment.DepreciationValue, pct.Value);
		Assert.False(pct.IsEnabled);
		rating.Selected = rating.Options.Single(o => o.Name == "Other");
		Assert.True(pct.IsEnabled);
		Assert.True(Field<TextFieldViewModel>(e, "DepreciationExplanation").IsVisible);
	}

	[AvaloniaFact]
	public void FloodFields_OnlyForFloodCauses()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		NumberFieldViewModel duration = Field<NumberFieldViewModel>(e, "DurationOfFlood");
		Assert.True(duration.IsVisible);
		ChoiceFieldViewModel cause = Field<ChoiceFieldViewModel>(e, "CauseofDamageID");
		cause.Selected = cause.Options.Single(o => o.Name == "Wind");
		Assert.False(duration.IsVisible);
		Assert.Null(e.Assessment.DurationOfFlood); // cleared, as in the original
		cause.Selected = cause.Options.Single(o => o.Name == "Other");
		Assert.True(Field<TextFieldViewModel>(e, "CauseOfDamageDescription").IsVisible);
	}

	[AvaloniaFact]
	public void ShapesCalculator_SetsSquareFootage()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		foreach (ShapeViewModel s in e.Shapes.ToList())
		{
			s.RemoveCommand.Execute(null);
		}
		// No floor area: replacement cost is just the adjustments. (% damaged is unchanged: with FEMA's default bases
		// it is Σ element share × damage ÷ (1 − depreciation), independent of the building's size.)
		Assert.Null(e.SquareFootage);
		Assert.Equal(10300m, e.Assessment.TotalReplacementCost);
		Assert.Equal("75.0%", e.PercentText);
		e.AddShapeCommand.Execute("l");
		ShapeViewModel l = e.Shapes.Single();
		l.LengthA = 40; l.WidthA = 25;
		l.LengthB = 10; l.WidthB = 12; l.StoriesB = 2;
		Assert.Equal(1240m, e.SquareFootage);   // 40×25×1 + 10×12×2
		e.Save();
		Assert.Equal(1240m, ui.Store.GetProperty(e.Property.PropertyID).SquareFootage);
		Assert.Single(ui.Store.GetShapes(e.Property.PropertyID));
	}

	[AvaloniaFact]
	public async Task Photos_AddMakeDefaultRemove()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		string a = Path.Combine(ui.Folder, "front.png");
		string b = Path.Combine(ui.Folder, "kitchen.png");
		File.WriteAllBytes(a, TinyPng);
		File.WriteAllBytes(b, TinyPng);
		await e.AddPhotosAsync(new[] { a, b });
		Assert.Equal(2, e.Photos.Count);
		Assert.True(e.Photos[0].IsDefault);
		PhotoViewModel kitchen = e.Photos[1];
		kitchen.MakeDefaultCommand.Execute(null);
		Assert.Same(kitchen, e.Photos[0]);   // the report photo moves to the front: reports print it first
		Assert.False(e.Photos[1].IsDefault);
		e.Save();
		Assert.Equal(2, ui.Store.LoadAssessment(e.Assessment.AssessmentID).Attachments.Count);

		PhotoViewModel removed = e.Photos[0];
		removed.RemoveCommand.Execute(null);
		Assert.True(e.Photos[0].IsDefault); // the remaining photo becomes the report photo
		e.Save();
		Assert.Single(ui.Store.LoadAssessment(e.Assessment.AssessmentID).Attachments);
		// Removed photos go to Recently deleted; the file stays until it is emptied.
		Assert.True(File.Exists(removed.Path));
		Assert.Contains(ui.Store.GetDeletedItems(), d => d.Value == DeletedKind.Photo);

		// The screenshot shows the Photos & notes step with the photo that is left.
		e.SelectedSection = e.Sections.OfType<PhotosSectionViewModel>().Single();
		ui.Pump();
		Assert.Contains(ui.Window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Pictures of the damage and your own fields");
		ui.Snapshot("17b-editor-photos-with-photo");
	}

	[AvaloniaFact]
	public async Task Photos_FromAnIphone_AreStoredAsJpeg()
	{
		Assert.SkipUnless(OperatingSystem.IsMacOS(), "HEIC conversion uses macOS's sips.");
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		string png = Path.Combine(ui.Folder, "porch.png");
		string heic = Path.Combine(ui.Folder, "IMG_4021.HEIC");
		File.WriteAllBytes(png, TinyPng);
		System.Diagnostics.Process.Start("/usr/bin/sips", new[] { "-s", "format", "heic", png, "--out", heic })!.WaitForExit();
		Assert.True(File.Exists(heic));

		await e.AddPhotosAsync(new[] { heic });
		PhotoViewModel photo = Assert.Single(e.Photos);
		Assert.EndsWith(".jpg", photo.Path);
		Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(photo.Path)[..2]); // a JPEG
		Assert.Equal("IMG_4021", photo.Attachment.Description);
		Assert.Empty(ui.Dialogs.Notifications);
	}

	[AvaloniaFact]
	public void NewStructure_FromTheList_OpensAnEmptyAssessment()
	{
		using UiSession ui = new UiSession();
		ui.Main.Structures.NewStructureCommand.Execute("2");
		ui.Pump();
		AssessmentEditorViewModel e = ui.Main.Editor;
		Assert.NotNull(e);
		Assert.Equal(2, e.Property.StructureTypeID);
		Assert.Equal(ResultState.Incomplete, e.State);
		Assert.True(e.RequiredIssueCount > 0);
		Assert.Equal("New structure (no address yet)", e.Title);
		Assert.True(e.IsDraft);
		ui.Snapshot("18-editor-new-nonresidential");
		Field<TextFieldViewModel>(e, nameof(Sde.Core.Records.PropertyRecord.StreetName)).Text = "Commerce";
		ui.Main.ShowStructures();
		Assert.Single(ui.Main.Structures.Rows);
		Assert.Equal((1, 1), ui.Store.Counts());
		Assert.Contains(ui.Store.GetPropertyHistory(ui.Main.Structures.Rows[0].Summary.PropertyID), h => h.Label == SdeStore.CreatedEvent);
	}

	[AvaloniaFact]
	public void NewStructure_ClosedWithoutEditing_LeavesNothingBehind()
	{
		using UiSession ui = new UiSession();
		ui.Main.Structures.NewStructureCommand.Execute("1");
		ui.Pump();
		Assert.True(ui.Main.Editor.IsDraft);
		ui.Main.ShowStructures();
		Assert.Equal((0, 0), ui.Store.Counts());
		Assert.Empty(ui.Main.Structures.Rows);
	}

	[AvaloniaFact]
	public async Task ExportThenImport_ThroughTheImportPage()
	{
		using UiSession source = new UiSession();
		ScreenTourTests.AddSamples(source.Store);
		string exports = Path.Combine(source.Folder, "exports");
		Directory.CreateDirectory(exports);
		source.Dialogs.Answers.Enqueue(exports);
		await source.Main.ImportExport.ExportAsync(null);
		string folder = Directory.GetDirectories(exports).Single();

		using UiSession target = new UiSession();
		await target.Main.ImportExport.LoadPendingAsync(folder);
		Assert.True(target.Main.ImportExport.HasPending);
		Assert.Equal(3, target.Main.ImportExport.PendingRows.Count);
		target.Main.ShowCommand.Execute(Page.ImportExport);
		target.Snapshot("32-import-preview");
		await target.Main.ImportExport.RunImportCommand.ExecuteAsync(null);
		Assert.Equal(3, target.Store.ListStructures().Count);
		Assert.Contains(target.Dialogs.Notifications, n => n.StartsWith("Import complete"));
	}

	/// <summary>A 1×1 PNG.</summary>
	private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
