using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Shell;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The v2.5 page restyle: the title band's breadcrumb, Settings in categories (and in the ⌘K palette), the editor's
/// step rail and result bar, and the two layout panels the forms use (FieldGrid and TickPanel).
/// </summary>
public class RestyleTests
{
	private static UiSession WithSamples(int width = 1440)
	{
		UiSession ui = new UiSession(width: width);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	private static Guid Ranch(UiSession ui) => ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123")).AssessmentID!.Value;

	[AvaloniaFact]
	public void Breadcrumb_ShowsWhereYouAre_AndGoesBack()
	{
		using UiSession ui = WithSamples();
		Assert.False(ui.Main.HasPageParent);

		ui.Main.OpenAssessment(Ranch(ui));
		ui.Pump();
		Assert.Equal("Structures", ui.Main.PageParent);
		Assert.Equal(ui.Main.Editor.Title, ui.Main.PageTitle);
		Button crumb = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("crumb"));
		Assert.True(crumb.IsEffectivelyVisible);
		Assert.Equal("Back to Structures", AutomationProperties.GetName(crumb));
		// The editor no longer repeats the address under the title band.
		Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<TextBlock>(),
			t => t.IsEffectivelyVisible && t.Text == ui.Main.Editor.Title && t.Name != "PageTitleText");

		crumb.Command.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Structures, ui.Main.ActivePage);
		Assert.False(ui.Main.HasPageParent);

		ui.Main.ShowCommand.Execute(Page.SourceImport);
		Assert.Equal("Import & export", ui.Main.PageParent);
		ui.Main.GoToParentCommand.Execute(null);
		Assert.Equal(Page.ImportExport, ui.Main.ActivePage);
	}

	[AvaloniaFact]
	public void Settings_ShowsOneCategoryAtATime_AndThePaletteFindsThem()
	{
		using UiSession ui = WithSamples();
		ui.Main.ShowCommand.Execute(Page.Settings);
		SettingsViewModel settings = ui.Main.Settings;
		Assert.Equal(new[] { "General", "Defaults", "Reports & letters", "Online services", "Phone form", "AI assistants", "Map & GIS", "Depth tables", "Backups" },
			settings.Categories.Select(c => c.Title));
		Assert.True(settings.IsGeneral);
		Assert.False(settings.IsPhoneForm);

		settings.SelectedCategory = null;                       // a list can't be left with nothing chosen
		Assert.Equal(SettingsCategory.General, settings.SelectedCategory.Category);

		CommandPaletteViewModel palette = ui.Main.Palette;
		palette.Open();
		Assert.DoesNotContain(palette.Results, r => r.Title == "Phone form");   // not listed until something is typed
		palette.Query = "phone form";
		ui.Pump();
		PaletteItem item = Assert.Single(palette.Results, r => r.Group == "Pages");
		Assert.Equal("Phone form", item.Title);
		palette.Run(item);
		ui.Pump();
		Assert.Equal(Page.Settings, ui.Main.ActivePage);
		Assert.True(ui.Main.Settings.IsPhoneForm);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
	}

	[AvaloniaFact]
	public void Editor_StepsShowIcons_TheResultHasItsBar_AndANarrowWindowKeepsIconsOnly()
	{
		using UiSession ui = WithSamples(width: 1100);
		ui.Main.OpenAssessment(Ranch(ui));
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.True(editor.HasPercent);
		Assert.Equal(75.0, editor.PercentValue, 1);
		ProgressBar bar = ui.Window.GetVisualDescendants().OfType<ProgressBar>().Single(p => p.Classes.Contains("threshold"));
		Assert.Equal(75.0, bar.Value, 1);

		ListBox steps = ui.Window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("steps"));
		Assert.Equal(editor.Sections.Select(s => s.Icon), steps.GetVisualDescendants().OfType<Icon>().Where(i => i.FindAncestorOfType<Border>()?.Classes.Contains("stepIcon") == true).Select(i => i.Kind));
		Grid grid = ui.Window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "EditorGrid");
		Assert.Contains("narrow", grid.Classes);                       // 1100 wide: the step titles are hidden
		Assert.All(steps.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("stepText")), p => Assert.False(p.IsEffectivelyVisible));

		ui.Window.Width = 1440;
		ui.Pump();
		Assert.DoesNotContain("narrow", grid.Classes);
	}

	/// <summary>A FieldGrid with the given width and children, laid out.</summary>
	private static FieldGrid Lay(double width, params Control[] children)
	{
		FieldGrid grid = new FieldGrid();
		grid.Children.AddRange(children);
		grid.Measure(new Size(width, double.PositiveInfinity));
		grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
		return grid;
	}

	private static Border Box(double height = 40, bool wide = false, bool visible = true)
	{
		Border b = new Border { Height = height, IsVisible = visible };
		if (wide)
		{
			b.Classes.Add("wide");
		}
		return b;
	}

	[AvaloniaFact]
	public void FieldGrid_TwoColumns_WideTakesARow_HiddenTakesNoSpace_AndOneColumnWhenNarrow()
	{
		Border a = Box(), b = Box(), hidden = Box(visible: false), c = Box(), wide = Box(60, wide: true);
		FieldGrid grid = Lay(620, a, b, hidden, c, wide);
		Assert.Equal(new Rect(0, 0, 300, 40), a.Bounds);                    // (620 - 20) / 2
		Assert.Equal(new Rect(320, 0, 300, 40), b.Bounds);
		Assert.Equal(new Rect(0, 54, 300, 40), c.Bounds);                    // the hidden one is skipped
		Assert.Equal(new Rect(0, 108, 620, 60), wide.Bounds);
		Assert.Equal(168, grid.DesiredSize.Height);

		Border x = Box(), y = Box();
		Lay(400, x, y);                                                      // under 2 × 220 + 20: one column
		Assert.Equal(new Rect(0, 0, 400, 40), x.Bounds);
		Assert.Equal(new Rect(0, 54, 400, 40), y.Bounds);

		Border p = Box(), q = Box(), r = Box();
		FieldGrid three = new FieldGrid { Columns = 3, MinColumnWidth = 100 };
		three.Children.AddRange(new Control[] { p, q, r });
		three.Measure(new Size(641, double.PositiveInfinity));
		three.Arrange(new Rect(0, 0, 641, 40));
		Assert.Equal(200, p.Bounds.Width);                                   // whole pixels: (641 - 40) / 3 = 200.33
		Assert.Equal(440, r.Bounds.X);
	}

	[AvaloniaFact]
	public void TickPanel_CentresEachLabelOnItsPlace()
	{
		TickPanel panel = new TickPanel { Inset = 10 };
		Border[] ticks = Enumerable.Range(0, 5).Select(_ => new Border { Width = 20, Height = 10 }).ToArray();
		panel.Children.AddRange(ticks);
		panel.Measure(new Size(220, 20));
		panel.Arrange(new Rect(0, 0, 220, 10));
		// Centres at 10, 60, 110, 160 and 210 (the track runs from 10 to 210).
		Assert.Equal(new[] { 0.0, 50, 100, 150, 200 }, ticks.Select(t => t.Bounds.X));
	}
}
