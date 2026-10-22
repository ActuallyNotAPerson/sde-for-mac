using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// Opens every page and every assessment step in the real UI (off-screen), saves screenshots to docs/screenshots,
/// and fails on any binding error. The screenshots are how the UI gets reviewed without driving the Mac's screen.
/// </summary>
public class ScreenTourTests
{
	/// <summary>Three made-up structures (see <see cref="SampleDatabase.AddSamples"/>), shared by most UI tests.</summary>
	internal static void AddSamples(Sde.Data.SdeStore store) => SampleDatabase.AddSamples(store);

	[AvaloniaFact]
	public void Tour_AllScreens_RenderWithoutBindingErrors()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(ThemeVariant.Light);
		ui.Snapshot("00-structures-empty");
		AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Snapshot("01-structures");

		Guid ranch = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123")).AssessmentID!.Value;
		ui.Main.OpenAssessment(ranch);
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.NotNull(editor);
		for (int i = 0; i < editor.Sections.Count; i++)
		{
			editor.SelectedSection = editor.Sections[i];
			ui.Snapshot($"{10 + i:00}-editor-{Slug(editor.Sections[i].Title)}");
		}

		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Snapshot("30-import-export");
		// A made-up name: without one the field shows the Mac account's full name, which must never reach the docs.
		ui.Services.SetYourName("Reviewer A");
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Snapshot("31-settings");
		ui.Main.Settings.Show(SettingsCategory.Defaults);
		ui.Snapshot("31b-settings-defaults");
		ui.Main.Settings.Show(SettingsCategory.ReportsAndLetters);
		ui.Snapshot("31c-settings-reports");
		ui.Main.Settings.Show(SettingsCategory.PhoneForm);
		ui.Snapshot("31d-settings-phone-form");
		ui.Main.Settings.Show(SettingsCategory.AiAssistants);
		ui.Snapshot("31e-settings-placeholder");
		ui.Main.Settings.Show(SettingsCategory.General);

		// Empty states
		ui.Main.ShowCommand.Execute(Page.Trash);
		ui.Snapshot("52b-recently-deleted-empty");

		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public void Tour_DarkMode()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(ThemeVariant.Dark);
		AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Snapshot("40-dark-structures");
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("48")).AssessmentID!.Value);
		ui.Main.Editor.SelectedSection = ui.Main.Editor.Sections.OfType<DamageSectionViewModel>().Single();
		ui.Snapshot("41-dark-editor-damage");
		ui.Main.Editor.SelectedSection = ui.Main.Editor.Sections[1];
		ui.Snapshot("42-dark-editor-building");
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Snapshot("43-dark-dashboard");
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Snapshot("44-dark-import-export");
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Snapshot("45-dark-settings");
		ui.Main.Settings.Show(SettingsCategory.PhoneForm);
		ui.Snapshot("45b-dark-settings-phone-form");
		ui.Main.ShowCommand.Execute(Page.Trash);
		ui.Snapshot("46-dark-recently-deleted-empty");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public void ThemeSwitch_RecoloursStatusPills_TheResultCard_AndLegendDots()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(ThemeVariant.Light);
		AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();

		// Each check keeps the same control across the switch: a control made after it would be coloured right anyway.
		Border pill = ui.Window.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && b.Classes.Contains("pill") && b.DataContext is StructureRowViewModel);
		TextBlock pillText = pill.GetVisualDescendants().OfType<TextBlock>().Single();
		string state = ((StructureRowViewModel)pill.DataContext).State.ToString();
		AssertThemeColour("Status" + state + "Bg", pill.Background);
		SwitchTheme(ui, ThemeVariant.Dark);
		AssertThemeColour("Status" + state + "Bg", pill.Background);
		AssertThemeColour("Status" + state + "Fg", pillText.Foreground);

		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123")).AssessmentID!.Value);
		ui.Pump();
		Border card = ui.Window.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "PERCENT DAMAGED").FindAncestorOfType<Border>();
		Ellipse issueDot = ui.Window.GetVisualDescendants().OfType<Ellipse>().First(d => d.IsEffectivelyVisible && d.DataContext is IssueItem);
		string issueKey = ((IssueItem)issueDot.DataContext).Severity + "Fg";
		string cardKey = "Status" + ui.Main.Editor.State + "Bg";
		AssertThemeColour(cardKey, card.Background);
		AssertThemeColour(issueKey, issueDot.Fill);
		SwitchTheme(ui, ThemeVariant.Light);
		AssertThemeColour(cardKey, card.Background);
		AssertThemeColour(issueKey, issueDot.Fill);

		ui.Main.ShowCommand.Execute(Page.Map);
		ui.Pump();
		Ellipse legendDot = ui.Window.GetVisualDescendants().OfType<Ellipse>().First(d => d.IsEffectivelyVisible && d.DataContext is MapClassViewModel);
		string legendKey = ((MapClassViewModel)legendDot.DataContext).ColourKey;
		AssertThemeColour(legendKey, legendDot.Fill);
		SwitchTheme(ui, ThemeVariant.Dark);
		AssertThemeColour(legendKey, legendDot.Fill);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	private static void SwitchTheme(UiSession ui, ThemeVariant theme)
	{
		Application.Current!.RequestedThemeVariant = theme;
		ui.Pump();
		Assert.Equal(theme, Application.Current.ActualThemeVariant);
	}

	/// <summary>The brush has the colour of the named resource in the theme now showing.</summary>
	private static void AssertThemeColour(string key, IBrush actual)
	{
		Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object expected), key);
		Assert.Equal(((ISolidColorBrush)expected).Color, Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
	}

	private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');
}
