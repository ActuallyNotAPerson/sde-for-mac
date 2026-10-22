using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Shell;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Data;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The window's frame (v2.5): the grouped sidebar with counts, the placeholder pages, the ⌘K palette, the event and
/// database switchers, confirmations as sheets and notifications as toasts. Also writes the shell's screenshots.
/// </summary>
public class ShellTests
{
	private static UiSession WithSamples(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	/// <summary>Opens a button's flyout inside the window (not as a separate popup window), so a screenshot shows it.</summary>
	private static void OpenFlyoutInWindow(UiSession ui, string buttonName)
	{
		Control button = ui.Window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == buttonName);
		FlyoutBase flyout = button is Button b ? b.Flyout : null;
		PopupFlyoutBase popup = Assert.IsAssignableFrom<PopupFlyoutBase>(flyout);
		popup.Popup.ShouldUseOverlayLayer = true;
		popup.ShowAt(button);
		ui.Pump();
	}

	[AvaloniaFact]
	public void Sidebar_IsGrouped_WithCountsOnStructuresAndRecentlyDeleted()
	{
		using UiSession ui = WithSamples();
		Assert.Equal(new[] { "WORK", "DATA", "MANAGE" }, ui.Main.SidebarGroups.Select(g => g.Title));
		NavItemViewModel structures = ui.Main.SidebarItems.Single(i => i.Page == Page.Structures);
		NavItemViewModel trash = ui.Main.SidebarItems.Single(i => i.Page == Page.Trash);
		Assert.Equal(ui.Store.Counts().Properties, structures.Count);
		Assert.True(structures.HasCount);
		Assert.False(trash.HasCount);
		Assert.All(ui.Main.SidebarItems, i => Assert.True(ui.Window.TryFindResource(Icon.ResourceKey(i.Icon), out object icon) && icon is Avalonia.Media.Geometry, i.Icon));

		// Moving a structure to Recently deleted counts it there; the toast's Undo puts it back.
		StructureRowViewModel row = ui.Main.Structures.Rows.First(r => r.AssessmentID != null);
		ui.Main.Structures.TrashCommand.Execute(row);
		ui.Main.ShowCommand.Execute(Page.Trash);
		ui.Pump();
		Assert.Equal(1, trash.Count);
		Assert.Equal(structures.Count, ui.Store.Counts().Properties);
		Assert.True(trash.IsActive);
		Assert.Equal("Recently deleted", ui.Main.PageTitle);
	}

	/// <summary>Every icon the shell and the editor's steps name is in Styles/Icons.axaml.</summary>
	[AvaloniaFact]
	public void EveryNamedIcon_IsThere()
	{
		using UiSession ui = WithSamples();
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.AssessmentID != null).AssessmentID!.Value);
		List<string> kinds = ui.Main.Editor.Sections.Select(s => s.Icon)
			.Concat(ui.Main.SidebarItems.Select(i => i.Icon))
			.ToList();
		Assert.All(ui.Main.Editor.Sections, s => Assert.False(string.IsNullOrEmpty(s.Icon), s.Title));
		Assert.All(kinds, k => Assert.True(ui.Window.TryFindResource(Icon.ResourceKey(k), out object g) && g is Avalonia.Media.Geometry, k));
	}

	[AvaloniaFact]
	public void CmdK_FindsAStructure_AndReturnOpensIt()
	{
		using UiSession ui = WithSamples();
		ui.Window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Meta);
		ui.Pump();
		CommandPaletteViewModel palette = ui.Main.Palette;
		Assert.True(palette.IsOpen);
		TextBox box = Assert.IsType<TextBox>(ui.Window.FocusManager.GetFocusedElement());
		Assert.Equal("PaletteBox", box.Name);

		// With nothing typed: the pages, then the commands.
		Assert.Equal("Pages", palette.Results[0].Group);
		Assert.Contains(palette.Results, r => r.Group == "Commands" && r.Title == "New residential structure" && r.Shortcut == "⌘N");

		box.Text = "mill creek";
		ui.Pump();
		PaletteItem first = palette.Results[0];
		Assert.Equal("Structures", first.Group);
		Assert.Equal("48 Mill Creek Street", first.Title);
		Assert.Same(first, palette.Selected);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		ui.Pump();
		Assert.False(palette.IsOpen);
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.StartsWith("48 Mill Creek Street", ui.Main.Editor.Title);
		Assert.Equal(ui.Main.Editor.Title, ui.Main.PageTitle);
		Assert.True(ui.Main.SidebarItems.Single(i => i.Page == Page.Structures).IsActive);
	}

	[AvaloniaFact]
	public void Palette_ArrowsMove_PagesAndCommandsRun_EscCloses()
	{
		using UiSession ui = WithSamples();
		CommandPaletteViewModel palette = ui.Main.Palette;
		palette.Open();
		ui.Pump();
		palette.Query = "settings";
		ui.Pump();
		Assert.Equal("Settings", palette.Selected.Title);
		Assert.Equal("Pages", palette.Selected.Group);
		ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(Page.Settings, ui.Main.ActivePage);

		palette.Open();
		palette.Query = "shortcuts";
		ui.Pump();
		palette.Run(null);
		ui.Pump();
		Assert.IsType<ShortcutsSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);

		palette.Open();
		ui.Pump();
		int count = palette.Results.Count;
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
		ui.Pump();
		Assert.Same(palette.Results[1], palette.Selected);
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
		ui.Pump();
		Assert.Same(palette.Results[count - 1], palette.Selected);       // wraps around

		palette.Query = "zzzz no such thing";
		ui.Pump();
		Assert.True(palette.IsEmpty);
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		Assert.False(palette.IsOpen);
		Assert.Equal(Page.Settings, ui.Main.ActivePage);
	}

	[AvaloniaFact]
	public void EventSwitcher_HoldsAllEvents_UntilEventsAreAdded()
	{
		using UiSession ui = new UiSession();
		EventSwitcherViewModel events = ui.Main.Events;
		Assert.Single(events.Events);
		Assert.Same(EventChoice.All, events.Selected);
		Assert.Equal("All events", events.Label);

		EventChoice chosen = null;
		events.SelectionChanged += c => chosen = c;
		EventRecord storm = ui.Store.SaveEvent(new EventRecord { Name = "Spring storm", StartDate = new DateTime(2026, 4, 2) });
		ui.Services.RaiseDataChanged();
		Assert.Equal(2, events.Events.Count);
		Assert.Null(chosen);                                              // still All events
		EventChoice choice = events.Events.Single(e => e.Id == storm.EventID);
		events.ChooseCommand.Execute(choice);
		Assert.Same(choice, chosen);
		Assert.Equal("Spring storm", events.Label);
		ui.Store.DeleteEvent(storm.EventID);                              // its event is gone: back to All events
		ui.Services.RaiseDataChanged();
		Assert.Equal(EventChoice.All, events.Selected);
		Assert.True(ui.Services.Scope.IsAll);
		ui.Store.SaveEvent(new EventRecord { Name = "Spring storm", StartDate = new DateTime(2026, 4, 2) });
		ui.Services.RaiseDataChanged();

		OpenFlyoutInWindow(ui, "EventSwitcher");
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("84-event-switcher");
	}

	[AvaloniaFact]
	public async Task DatabaseSwitcher_ListsRecent_AndCopiesTheDatabase()
	{
		using UiSession ui = WithSamples();
		string original = ui.Store.Database.FilePath;
		string other = Path.Combine(ui.Folder, "other.sqlite");
		ui.Services.Open(other);
		ui.Services.Open(original);
		ui.Pump();
		DatabaseSwitcherViewModel db = ui.Main.DatabaseSwitcher;
		Assert.Equal(Path.GetFileName(original), db.Name);
		Assert.Equal(other, Assert.Single(db.Recent).Path);                // the open one isn't listed

		// Move or copy database…, then "Copy it and open the copy".
		Task moving = db.MoveOrCopyCommand.ExecuteAsync(null);
		ui.Pump();
		MoveOrCopySheetViewModel sheet = Assert.IsType<MoveOrCopySheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(SheetSize.Medium, sheet.Size);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.IsCopy = true;
		Assert.Equal(MoveOrCopyChoice.CopyAndOpen, sheet.Choice);
		string copy = Path.Combine(ui.Folder, "copy", "copied.sqlite");
		Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
		ui.Dialogs.Answers.Enqueue(copy);
		sheet.ContinueCommand.Execute(null);
		await moving;
		ui.Pump();
		Assert.Equal(copy, ui.Store.Database.FilePath);
		Assert.True(File.Exists(original));
		Assert.Equal(ui.Store.Counts().Properties, ui.Main.SidebarItems.Single(i => i.Page == Page.Structures).Count);
		Assert.Equal("copied.sqlite", db.Name);

		// Show in Finder and Backups use the Settings page's and the File menu's commands.
		db.ShowInFinderCommand.Execute(null);
		Assert.Contains("reveal:" + copy, ui.Dialogs.Opened);
		db.BackupsCommand.Execute(null);
		ui.Pump();
		Assert.IsType<RestoreBackupSheetViewModel>(ui.Main.Sheets.Current);
	}

	[AvaloniaFact]
	public async Task TheWindowsConfirm_IsASheet()
	{
		using UiSession ui = new UiSession();
		IDialogs dialogs = ui.Window;
		Task<bool> asking = dialogs.ConfirmAsync("Delete for good?", "123 River Street\n\nThis can't be undone.", "Delete", destructive: true);
		ui.Pump();
		ConfirmSheetViewModel sheet = Assert.IsType<ConfirmSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(SheetSize.Small, sheet.Size);
		Button delete = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content as string == "Delete");
		Assert.Contains("destructive", delete.Classes);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("86-confirm-sheet");
		sheet.ConfirmCommand.Execute(null);
		Assert.True(await asking);

		asking = dialogs.ConfirmAsync("Open the sample database?", "It is rebuilt each time.", "Open Sample");
		ui.Pump();
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		Assert.False(await asking);
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public void TheWindowsNotify_ShowsToasts_WithUndo()
	{
		using UiSession ui = new UiSession();
		IDialogs dialogs = ui.Window;
		int undone = 0;
		dialogs.Notify("Backup saved", "SDE backup 2026-10-21 0930");
		dialogs.Notify("Moved to Recently deleted", "123 River Street can be restored from Recently deleted for 30 days.", "Undo", () => undone++);
		ui.Pump();
		Assert.Equal(2, ui.Window.Toasts.Items.Count);
		List<Toast> shown = ui.Window.GetVisualDescendants().OfType<Toast>().ToList();
		Assert.Equal(2, shown.Count);
		Assert.Equal("Undo", shown[1].ActionText);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		shown[1].ActionCommand.Execute(null);
		Assert.Equal(1, undone);
		Assert.Single(ui.Window.Toasts.Items);                             // the action closes its toast

		dialogs.Notify("Couldn't open the database", "It is not an SDE database.", isError: true);
		dialogs.Notify("One", "1");
		dialogs.Notify("Two", "2");
		Assert.Equal(ToastHost.MaxItems, ui.Window.Toasts.Items.Count);    // the oldest goes
		Assert.Equal(Tone.Danger, ui.Window.Toasts.Items[0].Tone);
		ui.Window.Toasts.Items[0].CloseCommand.Execute(null);
		Assert.Equal(2, ui.Window.Toasts.Items.Count);
	}

	/// <summary>The new shell's screenshots, in light and dark.</summary>
	[AvaloniaTheory]
	[InlineData("light")]
	[InlineData("dark")]
	public void ShellScreenshots(string themeName)
	{
		ThemeVariant theme = themeName == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		string suffix = themeName == "dark" ? "-dark" : "";
		using (UiSession ui = WithSamples(theme))
		{
			ui.Snapshot("80-shell" + suffix);

			ui.Main.Palette.Open();
			ui.Main.Palette.Query = "river";
			ui.Pump();
			ui.Snapshot("81-command-palette" + suffix);
			ui.Main.Palette.Close();

			StructureRowViewModel row = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("700"));
			ui.Window.Toasts.Show("Moved to Recently deleted", $"{row.Address} can be restored from Recently deleted for {SdeStore.KeepDays} days.", Tone.Success, "Undo", () => { }, TimeSpan.Zero);
			ui.Pump();
			ui.Snapshot("82-toast" + suffix);
			ui.Window.Toasts.Items.Clear();

			OpenFlyoutInWindow(ui, "DatabaseButton");
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			ui.Snapshot("83-database-switcher" + suffix);
		}
	}
}
