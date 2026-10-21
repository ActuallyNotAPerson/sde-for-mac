using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;

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
		ui.Main.Structures.Reload();
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
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Snapshot("31-settings");

		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public void Tour_DarkMode()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(ThemeVariant.Dark);
		AddSamples(ui.Store);
		ui.Main.Structures.Reload();
		ui.Snapshot("40-dark-structures");
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("48")).AssessmentID!.Value);
		ui.Main.Editor.SelectedSection = ui.Main.Editor.Sections.OfType<DamageSectionViewModel>().Single();
		ui.Snapshot("41-dark-editor-damage");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');
}
