using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels.Shell;

/// <summary>One line in the ⌘K palette: a structure, a page or a command.</summary>
public sealed class PaletteItem
{
	public PaletteItem(string group, string title, string detail, string icon, string shortcut, Action run, string keywords = null)
	{
		Group = group;
		Title = title;
		Detail = detail;
		Icon = icon;
		Shortcut = shortcut;
		Run = run;
		Keywords = keywords;
	}

	/// <summary>"Structures", "Pages" or "Commands": shown as a heading above the first item of each group.</summary>
	public string Group { get; }

	public string Title { get; }

	/// <summary>A second line (the owner and city for a structure); may be null.</summary>
	public string Detail { get; }

	public string Icon { get; }

	/// <summary>"⌘N" and the like, shown on the right; may be null.</summary>
	public string Shortcut { get; }

	/// <summary>Other words it is found by (for a structure: its parcel and record numbers).</summary>
	public string Keywords { get; }

	public Action Run { get; }

	/// <summary>Set for the first item of a group, so the list shows the group's heading above it.</summary>
	public bool IsFirstInGroup { get; set; }

	public bool HasDetail => !string.IsNullOrEmpty(Detail);

	public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);

	/// <summary>What VoiceOver reads for the line.</summary>
	public string AccessibleName => HasDetail ? $"{Title}, {Detail}" : Title;

	/// <summary>True when every word of <paramref name="words"/> appears in the title, detail or keywords.</summary>
	public bool Matches(IReadOnlyList<string> words)
	{
		string text = (Title + " " + Detail + " " + Keywords).ToLowerInvariant();
		return words.All(w => text.Contains(w, StringComparison.Ordinal));
	}
}

/// <summary>
/// The ⌘K command palette: type to find a structure (by address, owner, parcel, city or record number), a page or a
/// command, then Return to go there. ↑ and ↓ move through the results; Esc closes it.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
	/// <summary>How many structures are listed at most (the rest are reached by typing more).</summary>
	public const int MaxStructures = 8;

	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private List<PaletteItem> _structures = new();

	public CommandPaletteViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
	}

	[ObservableProperty]
	public partial bool IsOpen { get; private set; }

	[ObservableProperty]
	public partial string Query { get; set; } = "";

	public ObservableCollection<PaletteItem> Results { get; } = new();

	[ObservableProperty]
	public partial PaletteItem Selected { get; set; }

	[ObservableProperty]
	public partial bool IsEmpty { get; private set; }

	/// <summary>Raised when the palette opens, so the window can put the cursor in its search box.</summary>
	public event Action Opened;

	partial void OnQueryChanged(string value) => Refresh();

	/// <summary>⌘K: opens the palette with an empty search.</summary>
	[RelayCommand]
	public void Open()
	{
		_structures = LoadStructures();
		Query = "";
		Refresh();
		IsOpen = true;
		Opened?.Invoke();
	}

	[RelayCommand]
	public void Close() => IsOpen = false;

	/// <summary>Return (or a click): closes the palette and does what the line is for.</summary>
	[RelayCommand]
	public void Run(PaletteItem item)
	{
		item ??= Selected;
		if (item == null)
		{
			return;
		}
		Close();
		item.Run();
	}

	/// <summary>↑ or ↓: the previous or next line, wrapping around.</summary>
	public void Move(int by)
	{
		if (Results.Count == 0)
		{
			return;
		}
		int at = Selected == null ? -1 : Results.IndexOf(Selected);
		int next = ((at + by) % Results.Count + Results.Count) % Results.Count;
		Selected = Results[next];
	}

	/// <summary>Lists what matches the search: up to <see cref="MaxStructures"/> structures, then pages, then commands.</summary>
	private void Refresh()
	{
		List<string> words = (Query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
		List<PaletteItem> list = new();
		if (words.Count > 0)
		{
			list.AddRange(_structures.Where(s => s.Matches(words)).Take(MaxStructures));
		}
		list.AddRange(Pages().Where(p => p.Matches(words)));
		if (words.Count > 0)
		{
			list.AddRange(SettingsPages().Where(p => p.Matches(words)));
			list.AddRange(EventPages().Where(p => p.Matches(words)));
		}
		list.AddRange(Commands().Where(c => c.Matches(words)));
		string group = null;
		foreach (PaletteItem item in list)
		{
			item.IsFirstInGroup = item.Group != group;
			group = item.Group;
		}
		Results.Clear();
		foreach (PaletteItem item in list)
		{
			Results.Add(item);
		}
		Selected = Results.FirstOrDefault();
		IsEmpty = Results.Count == 0;
	}

	private List<PaletteItem> LoadStructures()
	{
		try
		{
			return _app.Store.ListStructures()
				.OrderByDescending(s => s.LastUpdate)
				.Select(s => new PaletteItem("Structures", string.IsNullOrWhiteSpace(s.Address) ? "(No address)" : s.Address.Trim(),
					string.Join(" · ", new[] { s.Owner, s.City }.Where(x => !string.IsNullOrWhiteSpace(x))),
					s.StructureTypeID == 2 ? "NonResidential" : "Structures", null, () => _main.OpenStructure(s),
					string.Join(" ", s.ParcelNumber, s.ExternalId, s.ZipCode, s.NFIPCommunityName)))
				.ToList();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't list structures for the command palette", ex);
			return new List<PaletteItem>();
		}
	}

	private IEnumerable<PaletteItem> Pages() =>
		_main.SidebarGroups.SelectMany(g => g.Items).Select(n => new PaletteItem("Pages", n.Title, null, n.Icon, n.Shortcut, () => _main.ShowCommand.Execute(n.Page), "go page"));

	/// <summary>Each Settings category, found by its name ("phone form") or by "settings"; only once something is typed.</summary>
	private IEnumerable<PaletteItem> SettingsPages() =>
		_main.Settings.Categories.Select(c => new PaletteItem("Pages", c.Title, "Settings", c.Icon, null, () =>
		{
			_main.ShowCommand.Execute(ViewModels.Page.Settings);
			_main.Settings.Show(c.Category);
		}, "settings preferences"));

	/// <summary>Each event, to choose it in the sidebar ("september flood"); only once something is typed.</summary>
	private IEnumerable<PaletteItem> EventPages() =>
		_main.Events.Events.Select(e => new PaletteItem("Pages", e.Name, "Event", "Event", null, () => _main.Events.ChooseCommand.Execute(e), "event show only"));

	private IEnumerable<PaletteItem> Commands()
	{
		PaletteItem Command(string title, string icon, string shortcut, ICommand command, object parameter = null, string keywords = null) =>
			new PaletteItem("Commands", title, null, icon, shortcut, () =>
			{
				if (command.CanExecute(parameter))
				{
					command.Execute(parameter);
				}
			}, keywords);

		SettingsViewModel settings = _main.Settings;
		yield return Command("New residential structure", "Plus", "⌘N", _main.Structures.NewStructureCommand, "1", "add assessment house");
		yield return Command("New non-residential structure", "Plus", "⇧⌘N", _main.Structures.NewStructureCommand, "2", "add assessment business commercial");
		yield return Command("Import inspections or parcels…", "Import", "⇧⌘I", _main.OpenSourceImportCommand, keywords: "csv excel accela");
		yield return Command("Import SDE data…", "Import", "⌘I", _main.ImportExport.ChooseSdeFolderCommand, keywords: "fema");
		yield return Command("Export all to SDE…", "Export", "⌘E", _main.ImportExport.ExportAllCommand, keywords: "fema");
		yield return Command("Search and replace…", "Search", null, _main.Structures.SearchReplaceCommand, keywords: "find");
		yield return Command("Set a field on checked structures…", "Edit", null, _main.Structures.BulkEditCommand, keywords: "bulk");
		yield return Command("Set event for checked structures…", "Event", null, _main.Structures.SetEventCommand, keywords: "disaster");
		yield return Command("Assign checked structures to a team…", "Assignments", null, _main.Structures.AssignToTeamCommand, keywords: "field route crew");
		yield return Command("New team…", "Assignments", null, _main.Assignments.NewTeamCommand, keywords: "field crew");
		yield return Command("Make a web map…", "Globe", null, _main.Structures.WebMapCommand, keywords: "share public leaflet html");
		yield return Command("Check in a work package…", "Download", null, _main.ImportExport.CheckInCommand, keywords: "sdework field back");
		yield return Command("Open a work package…", "Download", null, _main.OpenWorkPackageCommand, keywords: "sdework field offline");
		yield return Command("Work package for the field…", "Upload", null, _main.Structures.WorkPackageCommand, keywords: "check out offline sdework");
		yield return Command("New event…", "Event", null, _main.NewEventCommand, keywords: "disaster flood dr");
		yield return Command("Manage events…", "Event", null, _main.ManageEventsCommand, keywords: "disaster rename delete");
		yield return Command("Open database…", "FolderOpen", "⌘O", settings.OpenDatabaseCommand);
		yield return Command("New database…", "Database", null, settings.NewDatabaseCommand);
		yield return Command("Move or copy database…", "Copy", null, _main.DatabaseSwitcher.MoveOrCopyCommand, keywords: "backup");
		yield return Command("Show database in Finder", "Folder", null, settings.RevealDatabaseCommand);
		yield return Command("Restore from backup…", "History", null, _main.RestoreBackupCommand, keywords: "backups");
		yield return Command("Keyboard shortcuts", "Keyboard", null, _main.ShowShortcutsCommand);
		yield return Command("SDE help", "Help", null, _main.ShowHelpCommand);
		yield return Command("FEMA SDE user manual", "Report", null, _main.OpenManualCommand);
		yield return Command("Show the walkthrough", "Info", null, _main.ShowWalkthroughCommand);
		yield return Command("Open sample database", "Database", null, _main.OpenSampleDatabaseCommand, keywords: "practice");
		yield return Command("Show log folder", "Folder", null, _main.ShowLogsCommand, keywords: "logs problem");
	}
}
