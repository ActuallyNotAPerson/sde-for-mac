using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.ViewModels.Shell;

/// <summary>One choice in the event switcher. <see cref="Id"/> is null for "All events".</summary>
public sealed record EventChoice(Guid? Id, string Name, string Detail)
{
	/// <summary>Every structure in the database, whatever event it belongs to.</summary>
	public static readonly EventChoice All = new EventChoice(null, "All events", "Every structure in this database");
}

/// <summary>
/// The event switcher at the top of the sidebar. It lists "All events" and the database's events, and the chosen one
/// scopes the pages that follow it (<see cref="SelectionChanged"/>). Until events exist it holds only "All events";
/// adding them means filling <see cref="Events"/> and listening for <see cref="SelectionChanged"/>.
/// </summary>
public sealed partial class EventSwitcherViewModel : ObservableObject
{
	public EventSwitcherViewModel()
	{
		Events.Add(EventChoice.All);
		Selected = EventChoice.All;
	}

	public ObservableCollection<EventChoice> Events { get; } = new();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Label), nameof(Detail), nameof(AccessibleName))]
	public partial EventChoice Selected { get; set; }

	public string Label => Selected?.Name ?? EventChoice.All.Name;

	public string Detail => Selected?.Detail ?? EventChoice.All.Detail;

	/// <summary>What VoiceOver reads for the switcher button.</summary>
	public string AccessibleName => $"Event: {Label}. Choose an event";

	/// <summary>Raised after the chosen event changes.</summary>
	public event Action<EventChoice> SelectionChanged;

	/// <summary>Chooses an event (from the switcher's list).</summary>
	[RelayCommand]
	private void Choose(EventChoice choice)
	{
		if (choice == null || choice == Selected)
		{
			return;
		}
		Selected = choice;
		SelectionChanged?.Invoke(choice);
	}

	/// <summary>The menu's "New event…" (set by the main window's view model).</summary>
	public System.Windows.Input.ICommand NewEventCommand { get; set; }

	/// <summary>The menu's "Manage events…".</summary>
	public System.Windows.Input.ICommand ManageEventsCommand { get; set; }

	/// <summary>Shows <paramref name="eventId"/> as chosen without raising <see cref="SelectionChanged"/> (a database was opened).</summary>
	public void ShowChosen(Guid? eventId)
	{
		Selected = Events.FirstOrDefault(e => e.Id == eventId) ?? EventChoice.All;
	}

	/// <summary>Replaces the list with "All events" and <paramref name="events"/>, keeping the choice when it is still there.</summary>
	public void SetEvents(IEnumerable<EventChoice> events)
	{
		Guid? keep = Selected?.Id;
		Events.Clear();
		Events.Add(EventChoice.All);
		foreach (EventChoice e in events)
		{
			Events.Add(e);
		}
		EventChoice again = Events.FirstOrDefault(e => e.Id == keep) ?? EventChoice.All;
		if (again != Selected)
		{
			Selected = again;
			SelectionChanged?.Invoke(again);
		}
	}
}

/// <summary>A database in the switcher's Open Recent list.</summary>
public sealed record RecentDatabase(string Path, string Name, string Folder);

/// <summary>
/// The database switcher at the bottom of the sidebar: the open database's name and size, and the database actions
/// (open, open recent, new, move or copy, show in Finder, backups). The actions are the Settings page's and the File
/// menu's commands.
/// </summary>
public sealed partial class DatabaseSwitcherViewModel : ObservableObject
{
	private readonly AppServices _app;
	private readonly MainViewModel _main;

	public DatabaseSwitcherViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
		Refresh();
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AccessibleName))]
	public partial string Name { get; private set; }

	/// <summary>The folder the database is in, for the tooltip and the flyout's header.</summary>
	[ObservableProperty]
	public partial string Folder { get; private set; }

	public string AccessibleName => $"Database: {Name}. Database actions";

	/// <summary>Other databases opened recently (newest first; not the open one).</summary>
	public ObservableCollection<RecentDatabase> Recent { get; } = new();

	[ObservableProperty]
	public partial bool HasRecent { get; private set; }

	/// <summary>Reads the open database's name and the recent list again (after opening or moving a database).</summary>
	public void Refresh()
	{
		string path = _app.Store.Database.FilePath;
		Name = System.IO.Path.GetFileName(path);
		Folder = System.IO.Path.GetDirectoryName(path);
		Recent.Clear();
		foreach (string p in _app.RecentDatabases().Where(p => !string.Equals(p, path, StringComparison.Ordinal)).Take(AppSettings.MaxRecent))
		{
			Recent.Add(new RecentDatabase(p, System.IO.Path.GetFileName(p), System.IO.Path.GetDirectoryName(p)));
		}
		HasRecent = Recent.Count > 0;
	}

	[RelayCommand]
	private Task Open() => _main.Settings.OpenDatabaseCommand.ExecuteAsync(null);

	[RelayCommand]
	private void OpenRecent(RecentDatabase database)
	{
		if (database != null)
		{
			_main.Settings.OpenRecentCommand.Execute(database.Path);
		}
	}

	[RelayCommand]
	private Task New() => _main.Settings.NewDatabaseCommand.ExecuteAsync(null);

	/// <summary>Move or copy database…: asks which, then runs Settings' Move or Back up, or copies and opens the copy.</summary>
	[RelayCommand]
	private async Task MoveOrCopy()
	{
		MoveOrCopyChoice choice = await _app.Sheets.ShowAsync<MoveOrCopyChoice>(new MoveOrCopySheetViewModel(Name));
		switch (choice)
		{
			case MoveOrCopyChoice.Move:
				await _main.Settings.MoveDatabaseCommand.ExecuteAsync(null);
				break;
			case MoveOrCopyChoice.CopyAndOpen:
				await CopyAndOpenAsync();
				break;
			case MoveOrCopyChoice.Backup:
				await _main.Settings.BackUpCommand.ExecuteAsync(null);
				break;
		}
	}

	/// <summary>Copies the database (with its photos, documents and map layers) to a new place and opens the copy.</summary>
	private async Task CopyAndOpenAsync()
	{
		string current = _app.Store.Database.FilePath;
		string path = await _app.Dialogs.SaveFileAsync("Copy the database to", System.IO.Path.GetFileNameWithoutExtension(current) + " copy.sqlite", "SDE database", "sqlite");
		if (path == null)
		{
			return;
		}
		if (File.Exists(path))
		{
			_app.Dialogs.Notify("Choose another place", "There's already a file with that name there.", isError: true);
			return;
		}
		_main.CloseEditor();
		await _app.MoveOrCopyDatabaseAsync(path, move: false);
	}

	[RelayCommand]
	private void ShowInFinder() => _main.Settings.RevealDatabaseCommand.Execute(null);

	/// <summary>Backups…: the list of automatic backups, to restore one.</summary>
	[RelayCommand]
	private void Backups() => _main.RestoreBackupCommand.Execute(null);
}
