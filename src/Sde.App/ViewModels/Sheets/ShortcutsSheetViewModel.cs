using Sde.App.Services;

namespace Sde.App.ViewModels.Sheets;

/// <summary>One keyboard shortcut: the keys and what they do.</summary>
public sealed record Shortcut(string Keys, string Action);

/// <summary>Help, then Keyboard Shortcuts.</summary>
public sealed class ShortcutsSheetViewModel() : SheetViewModel("Keyboard shortcuts")
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Medium;

	public IReadOnlyList<Shortcut> Shortcuts { get; } = new[]
	{
		new Shortcut("⌘N", "New residential structure"),
		new Shortcut("⇧⌘N", "New non-residential structure"),
		new Shortcut("⌘O", "Open a database"),
		new Shortcut("⌘S", "Save the assessment now (it also saves by itself)"),
		new Shortcut("⌘F", "Search the structures"),
		new Shortcut("⌘1, ⌘2, ⌘3, ⌘4", "Structures, Import and export, Settings, Map"),
		new Shortcut("⌘[  ⌘]", "Previous and next step of the assessment"),
		new Shortcut("0 to 4", "On a damage slider: 0, 25, 50, 75 or 100 percent"),
		new Shortcut("⌘I", "Import SDE data"),
		new Shortcut("⇧⌘I", "Import inspections or parcels"),
		new Shortcut("⌘E", "Export everything to SDE"),
		new Shortcut("Return", "Open the selected structure"),
		new Shortcut("Esc", "Close the panel in front"),
	};
}
