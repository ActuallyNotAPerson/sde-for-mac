using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.App.Tests;

/// <summary>
/// The keyboard: the menu's shortcuts (sent through the window's key bindings, which is how they arrive where there is no
/// macOS menu bar), damage slider presets, focus moving to a field from "Needs attention", and the window's memory.
/// </summary>
public class KeyboardTests
{
	private static AssessmentEditorViewModel OpenRanch(UiSession ui)
	{
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		return ui.Main.Editor;
	}

	[AvaloniaFact]
	public void CmdS_SavesNow_AndCmdBrackets_MoveBetweenSteps()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		e.Assessment.InspectorName = "Typed just now";
		e.Edited();
		Assert.Equal("Editing…", e.SaveState);
		ui.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Meta);
		ui.Pump();
		Assert.Equal("Saved", e.SaveState);
		Assert.Equal("Typed just now", ui.Store.LoadAssessment(e.Assessment.AssessmentID).Assessment.InspectorName);

		ui.Window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.Meta);
		ui.Pump();
		Assert.Same(e.Sections[1], e.SelectedSection);
		ui.Window.KeyPressQwerty(PhysicalKey.BracketLeft, RawInputModifiers.Meta);
		ui.Pump();
		Assert.Same(e.Sections[0], e.SelectedSection);
	}

	[AvaloniaFact]
	public void CmdF_GoesToTheSearchBox()
	{
		using UiSession ui = new UiSession();
		OpenRanch(ui);
		ui.Window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Meta);
		ui.Pump();
		Assert.Same(ui.Main.Structures, ui.Main.CurrentPage);
		TextBox focused = Assert.IsType<TextBox>(ui.Window.FocusManager.GetFocusedElement());
		Assert.Equal("SearchBox", focused.Name);
	}

	[AvaloniaFact]
	public void CmdNumbers_SwitchPages()
	{
		using UiSession ui = new UiSession();
		ui.Window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Meta);
		ui.Pump();
		Assert.Equal(ViewModels.Page.Settings, ui.Main.ActivePage);
		ui.Window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Meta);
		ui.Pump();
		Assert.Equal(ViewModels.Page.ImportExport, ui.Main.ActivePage);
	}

	[AvaloniaFact]
	public void NumberKeys_OnADamageSlider_SetQuarters()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		e.SelectedSection = e.Sections.OfType<DamageSectionViewModel>().Single();
		ui.Pump();
		Slider slider = ui.Window.GetVisualDescendants().OfType<Slider>().First(s => s.IsEffectivelyVisible);
		ElementRowViewModel row = (ElementRowViewModel)slider.DataContext;
		slider.Focus();
		ui.Window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(50m, row.Row.UserDamage);
		ui.Window.KeyPressQwerty(PhysicalKey.Digit4, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(100m, row.Row.UserDamage);
		ui.Window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(0m, row.Row.UserDamage);
	}

	[AvaloniaFact]
	public void ChoosingAnIssue_PutsTheCursorInThatField()
	{
		using UiSession ui = new UiSession();
		AssessmentEditorViewModel e = OpenRanch(ui);
		e.Assessment.InspectorName = null;
		e.Assessment.BaseCost = null;
		e.Edited();
		ui.Pump();
		IssueItem issue = e.Issues.First(i => i.Issue.ControlName == nameof(AssessmentRecord.BaseCost));
		issue.Go.Execute(issue.Issue);
		ui.Pump();
		Assert.IsType<CostSectionViewModel>(e.SelectedSection);
		Control focused = Assert.IsAssignableFrom<Control>(ui.Window.FocusManager.GetFocusedElement());
		Control field = focused.GetVisualAncestors().OfType<Control>().First(c => c.DataContext is FieldViewModel);
		Assert.Equal(nameof(AssessmentRecord.BaseCost), ((FieldViewModel)field.DataContext).Member);
	}

	[AvaloniaFact]
	public void CheckAll_IsThreeState()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		StructuresViewModel s = ui.Main.Structures;
		Assert.False(s.AllChecked);
		s.Rows[0].IsChecked = true;
		Assert.Null(s.AllChecked);
		s.AllChecked = true;
		Assert.All(s.Rows, r => Assert.True(r.IsChecked));
		Assert.True(s.AllChecked);
		s.AllChecked = false;
		Assert.Equal(0, s.CheckedCount);
	}

	[AvaloniaFact]
	public void ShortcutsSheet_ListsTheShortcuts_AndEscCloses()
	{
		using UiSession ui = new UiSession();
		ui.Main.ShowShortcutsCommand.Execute(null);
		ui.Pump();
		ShortcutsSheetViewModel sheet = Assert.IsType<ShortcutsSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Contains(sheet.Shortcuts, x => x.Keys == "⌘S");
		Assert.DoesNotContain(sheet.Shortcuts, x => x.Action.Contains('—'));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("51-keyboard-shortcuts");
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public void Window_RemembersItsSize()
	{
		using UiSession ui = new UiSession();
		MainWindow w = new MainWindow();
		w.Restore(new WindowPlacement { Width = 1250, Height = 800, X = -50000, Y = -50000 });
		Assert.Equal(1250, w.Width);
		Assert.Equal(800, w.Height);
		Assert.NotEqual(WindowStartupLocation.Manual, w.WindowStartupLocation);   // off every screen: centred instead
		w.Restore(new WindowPlacement { Width = 200, Height = 100 });
		Assert.Equal(1250, w.Width);                                               // too small to be real: ignored
	}

	[AvaloniaFact]
	public void OpeningADatabase_AddsItToOpenRecent()
	{
		using UiSession ui = new UiSession();
		string other = Path.Combine(ui.Folder, "other.sqlite");
		ui.Services.Open(other);
		Assert.Equal(other, ui.Main.RecentDatabases[0]);
		ui.Main.Settings.OpenRecentCommand.Execute(Path.Combine(ui.Folder, "gone.sqlite"));
		Assert.Contains(ui.Dialogs.Notifications, n => n.Contains("isn't there any more"));
	}
}
