using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Shell;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

public enum Page
{
	Structures,
	Editor,
	ImportExport,
	Settings,
	/// <summary>Import inspections or parcels: a sub-page of Import &amp; export.</summary>
	SourceImport,
	/// <summary>Recently deleted.</summary>
	Trash,
	/// <summary>Where the structures are, coloured by result.</summary>
	Map,
	/// <summary>Event dashboard: numbers, bars, summary PDF and GIS files.</summary>
	Dashboard,
	/// <summary>Assessments waiting for review, and the checks that flag problems.</summary>
	Review,
	/// <summary>Follow-up on substantially damaged structures: permits, deadlines, ICC claims.</summary>
	Compliance,
	/// <summary>Teams and the structures assigned to them.</summary>
	Assignments,
}

/// <summary>
/// The window: the sidebar (pages grouped as Work, Data and Manage, with the event and database switchers), the title
/// band (page title, ⌘K search, New), and the current page.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IEditorHost
{
	private readonly AppServices _app;

	/// <summary>The sidebar page the open assessment was opened from (Structures unless Review or Compliance opened it).</summary>
	private Page _editorParent = Page.Structures;

	public MainViewModel(AppServices app)
	{
		_app = app;
		app.Writes.Attach(this);
		Structures = new StructuresViewModel(app, this);
		ImportExport = new ImportExportViewModel(app, this);
		SourceImport = new SourceImportViewModel(app, this);
		Settings = new SettingsViewModel(app);
		Trash = new TrashViewModel(app);
		Map = new MapViewModel(app, this);
		Dashboard = new DashboardViewModel(app);
		Review = new ReviewViewModel(app, this);
		Compliance = new ComplianceViewModel(app, this);
		Assignments = new AssignmentsViewModel(app, this);
		SidebarGroups = BuildSidebar();
		Events = new EventSwitcherViewModel { NewEventCommand = NewEventCommand, ManageEventsCommand = ManageEventsCommand };
		Events.SelectionChanged += choice => _app.Scope.Set(_app.Store, choice.Id);
		app.Scope.Changed += OnScopeChanged;
		DatabaseSwitcher = new DatabaseSwitcherViewModel(app, this);
		Palette = new CommandPaletteViewModel(app, this);
		app.DataChanged += () =>
		{
			// An assessment may have joined or left the chosen event, or the event itself changed.
			app.Scope.Refresh(app.Store);
			RefreshEvents();
			Structures.Reload();
			ReloadOfficePages();
			RefreshStatus();
		};
		app.Changes.Changed += OnExternalChanges;
		app.DatabaseChanged += () =>
		{
			Editor = null;
			Structures.DatabaseChanged();
			Settings = new SettingsViewModel(app);
			Structures.ShowOnly(null, null);
			SourceImport.DatabaseChanged();
			RefreshStatus();
			DatabaseSwitcher.Refresh();
			RefreshEvents();
			OnPropertyChanged(nameof(RecentDatabases));
			OnPropertyChanged(nameof(IsSampleDatabase));
			OnPropertyChanged(nameof(IsWorkPackage));
			OnPropertyChanged(nameof(WorkPackageText));
			Show(Page.Structures);
		};
		CurrentPage = Structures;
		RefreshEvents();
		RefreshStatus();
		UpdateSidebar();
	}

	/// <summary>Fills the event switcher from the database and shows the chosen event (the scope's).</summary>
	public void RefreshEvents()
	{
		Dictionary<Guid, int> counts = _app.Store.EventCounts();
		List<EventChoice> choices = _app.Store.GetEvents().Select(e =>
		{
			int n = counts.GetValueOrDefault(e.EventID);
			string detail = string.Join(" · ", new[] { e.ToDetails().DateRange, $"{n:N0} assessment{(n == 1 ? "" : "s")}" }.Where(x => !string.IsNullOrWhiteSpace(x)));
			return new EventChoice(e.EventID, e.Label, detail);
		}).ToList();
		Events.SetEvents(choices);
		Events.ShowChosen(_app.Scope.EventId);
	}

	/// <summary>Another event was chosen: the pages it scopes show only its assessments.</summary>
	private void OnScopeChanged()
	{
		Events.ShowChosen(_app.Scope.EventId);
		Structures.Reload();
		if (Dashboard.IsActive)
		{
			Dashboard.Reload();
		}
		ReloadOfficePages();
		RefreshStatus();
	}

	/// <summary>Review, Compliance and Assignments catch up with the database while one of them is showing.</summary>
	private void ReloadOfficePages()
	{
		if (Review.IsActive)
		{
			Review.Reload();
		}
		if (Compliance.IsActive)
		{
			Compliance.Reload();
		}
		if (Assignments.IsActive)
		{
			Assignments.Reload();
		}
	}

	/// <summary>Adds an event and chooses it in the sidebar.</summary>
	[RelayCommand]
	private async Task NewEvent()
	{
		if (await Sheets.ShowAsync(new EventSheetViewModel(_app)) is EventRecord added)
		{
			RefreshEvents();
			_app.Scope.Set(_app.Store, added.EventID);
		}
	}

	/// <summary>The list of events, to add, rename or delete them.</summary>
	[RelayCommand]
	private async Task ManageEvents()
	{
		if (await Sheets.ShowAsync(new ManageEventsSheetViewModel(_app)) is true)
		{
			_app.RaiseDataChanged();
		}
	}

	/// <summary>The sidebar's pages, in order; View › ⌘1 to ⌘9 follow this order.</summary>
	public IReadOnlyList<NavGroupViewModel> SidebarGroups { get; }

	/// <summary>Every sidebar page in order (the first is ⌘1).</summary>
	public IEnumerable<NavItemViewModel> SidebarItems => SidebarGroups.SelectMany(g => g.Items);

	/// <summary>The event switcher at the top of the sidebar.</summary>
	public EventSwitcherViewModel Events { get; }

	/// <summary>The database switcher at the bottom of the sidebar.</summary>
	public DatabaseSwitcherViewModel DatabaseSwitcher { get; }

	/// <summary>The ⌘K command palette.</summary>
	public CommandPaletteViewModel Palette { get; }

	/// <summary>The review queue, the quality checks and reviewers' comments.</summary>
	public ReviewViewModel Review { get; }

	/// <summary>The compliance and ICC tracker.</summary>
	public ComplianceViewModel Compliance { get; }

	/// <summary>Teams, their structures in route order, and the field kit.</summary>
	public AssignmentsViewModel Assignments { get; }

	private IReadOnlyList<NavGroupViewModel> BuildSidebar()
	{
		int n = 0;
		NavItemViewModel Item(Page page, string title, string icon) => new NavItemViewModel(page, title, icon, ++n, p => Show(p));
		return new[]
		{
			new NavGroupViewModel("WORK", new[]
			{
				Item(Page.Structures, "Structures", "Structures"),
				Item(Page.Map, "Map", "Map"),
				Item(Page.Dashboard, "Dashboard", "Dashboard"),
				Item(Page.Review, "Review", "Review"),
				Item(Page.Compliance, "Compliance", "Compliance"),
				Item(Page.Assignments, "Assignments", "Assignments"),
			}),
			new NavGroupViewModel("DATA", new[] { Item(Page.ImportExport, "Import & export", "ImportExport") }),
			new NavGroupViewModel("MANAGE", new[]
			{
				Item(Page.Settings, "Settings", "Settings"),
				Item(Page.Trash, "Recently deleted", "Trash"),
			}),
		};
	}

	/// <summary>The sidebar page a page belongs to (the editor belongs to Structures; import to Import &amp; export).</summary>
	public static Page SidebarPage(Page page) => page switch
	{
		Page.Editor => Page.Structures,
		Page.SourceImport => Page.ImportExport,
		_ => page,
	};

	private void UpdateSidebar()
	{
		Page active = ActivePage == Page.Editor ? _editorParent : SidebarPage(ActivePage);
		foreach (NavItemViewModel item in SidebarItems)
		{
			item.IsActive = item.Page == active;
		}
		OnPropertyChanged(nameof(PageTitle));
		OnPropertyChanged(nameof(PageParent));
		OnPropertyChanged(nameof(HasPageParent));
	}

	partial void OnActivePageChanged(Page value) => UpdateSidebar();

	partial void OnEditorChanged(AssessmentEditorViewModel oldValue, AssessmentEditorViewModel newValue)
	{
		if (oldValue != null)
		{
			oldValue.PropertyChanged -= OnEditorPropertyChanged;
		}
		if (newValue != null)
		{
			newValue.PropertyChanged += OnEditorPropertyChanged;
		}
		OnPropertyChanged(nameof(PageTitle));
		OnPropertyChanged(nameof(PageParent));
		OnPropertyChanged(nameof(HasPageParent));
	}

	/// <summary>The title band follows the open assessment's address as it is typed.</summary>
	private void OnEditorPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(AssessmentEditorViewModel.Title))
		{
			OnPropertyChanged(nameof(PageTitle));
		}
	}

	/// <summary>The title band's text: the page's name, or the open assessment's address.</summary>
	public string PageTitle => ActivePage switch
	{
		Page.Editor when Editor != null => Editor.Title,
		Page.SourceImport => "Import inspections or parcels",
		_ => SidebarItems.FirstOrDefault(i => i.Page == SidebarPage(ActivePage))?.Title ?? "Structures",
	};

	/// <summary>
	/// The page the title band's breadcrumb leads back to ("Structures" above an open assessment, "Import &amp; export"
	/// above the import page), or null on a page of its own.
	/// </summary>
	public string PageParent => ActivePage switch
	{
		Page.Editor when Editor != null => SidebarItems.FirstOrDefault(i => i.Page == _editorParent)?.Title ?? "Structures",
		Page.SourceImport => "Import & export",
		_ => null,
	};

	public bool HasPageParent => PageParent != null;

	/// <summary>The breadcrumb: back to the page this one belongs to (saving the open assessment first).</summary>
	[RelayCommand]
	private void GoToParent()
	{
		if (ActivePage == Page.Editor && Editor != null)
		{
			Editor.CloseCommand.Execute(null);
		}
		else if (ActivePage == Page.SourceImport)
		{
			Show(Page.ImportExport);
		}
	}

	/// <summary>Esc: closes the command palette if it is open, otherwise cancels the sheet on top.</summary>
	[RelayCommand]
	private void Escape()
	{
		if (Palette.IsOpen)
		{
			Palette.Close();
		}
		else
		{
			Sheets.CancelCurrentCommand.Execute(null);
		}
	}

	/// <summary>Opens a structure from a search result: its newest assessment, or a first one when it has none.</summary>
	public void OpenStructure(StructureSummary structure)
	{
		if (structure.AssessmentID is Guid id)
		{
			OpenAssessment(id);
		}
		else if (_app.Store.GetProperty(structure.PropertyID) is Sde.Core.Records.PropertyRecord p)
		{
			OpenDraft(_app.Store.NewDraft(p, _app.Store.GetPreferences().UseDefaults));
		}
	}

	public StructuresViewModel Structures { get; }

	/// <summary>The sheet shown over the window, if any.</summary>
	public SheetHost Sheets => _app.Sheets;

	/// <summary>The long task running, if any (shown with a progress bar and Stop).</summary>
	public JobRunner Jobs => _app.Jobs;

	public ImportExportViewModel ImportExport { get; }

	public SourceImportViewModel SourceImport { get; }

	public TrashViewModel Trash { get; }

	public MapViewModel Map { get; }

	public DashboardViewModel Dashboard { get; }

	[ObservableProperty]
	public partial SettingsViewModel Settings { get; set; }

	[ObservableProperty]
	public partial AssessmentEditorViewModel Editor { get; set; }

	[ObservableProperty]
	public partial ViewModelBase CurrentPage { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsStructuresPage), nameof(IsImportExportPage), nameof(IsSettingsPage), nameof(IsTrashPage), nameof(IsMapPage), nameof(IsDashboardPage),
		nameof(IsReviewPage), nameof(IsCompliancePage), nameof(IsAssignmentsPage))]
	public partial Page ActivePage { get; set; }

	public bool IsStructuresPage => ActivePage is Page.Structures or Page.Editor;

	public bool IsImportExportPage => ActivePage is Page.ImportExport or Page.SourceImport;

	public bool IsSettingsPage => ActivePage == Page.Settings;

	public bool IsTrashPage => ActivePage == Page.Trash;

	public bool IsMapPage => ActivePage == Page.Map;

	public bool IsDashboardPage => ActivePage == Page.Dashboard;

	public bool IsReviewPage => ActivePage == Page.Review;

	public bool IsCompliancePage => ActivePage == Page.Compliance;

	public bool IsAssignmentsPage => ActivePage == Page.Assignments;

	[ObservableProperty]
	public partial string DatabaseName { get; set; }

	/// <summary>"3 structures · 4 assessments", for the database switcher's tooltip.</summary>
	[ObservableProperty]
	public partial string CountsText { get; set; }

	/// <summary>"3 structures", under the database's name in the switcher.</summary>
	[ObservableProperty]
	public partial string StructureCountText { get; set; }

	/// <summary>The sidebar's database name, counts and badges, read again from the database.</summary>
	public void RefreshStatus()
	{
		DatabaseName = Path.GetFileName(_app.Store.Database.FilePath);
		(int p, int a) = _app.Store.Counts();
		CountsText = $"{p} structure{(p == 1 ? "" : "s")} · {a} assessment{(a == 1 ? "" : "s")}";
		StructureCountText = p.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")) + (p == 1 ? " structure" : " structures");
		int deleted = 0;
		try
		{
			deleted = _app.Store.GetDeletedItems().Count;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't count Recently deleted", ex);
		}
		// In an event, the count is the event's structures.
		int structures = _app.Scope.IsAll ? p : Structures.StructureCount;
		// Review counts the assessments waiting (Draft and Reviewed); Compliance, the open cases past their deadline.
		int waiting = Count("Couldn't count the assessments waiting for review", () => ReviewViewModel.CountWaiting(_app));
		int overdue = Count("Couldn't count the overdue compliance cases", () => ComplianceViewModel.CountOverdue(_app));
		foreach (NavItemViewModel item in SidebarItems)
		{
			item.Count = item.Page switch
			{
				Page.Structures => structures,
				Page.Trash => deleted,
				Page.Review => waiting,
				Page.Compliance => overdue,
				_ => 0,
			};
			item.CountTone = item.Page switch
			{
				Page.Review => Tone.Accent,
				Page.Compliance => Tone.Danger,
				_ => Tone.Neutral,
			};
		}
	}

	private static int Count(string failure, Func<int> count)
	{
		try
		{
			return count();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write(failure, ex);
			return 0;
		}
	}

	[RelayCommand]
	private void Show(Page page)
	{
		if (page != Page.Editor && Editor != null)
		{
			Editor.Save();
		}
		ActivePage = page;
		CurrentPage = page switch
		{
			Page.Editor => (ViewModelBase)Editor ?? Structures,
			Page.ImportExport => ImportExport,
			Page.SourceImport => SourceImport,
			Page.Settings => Settings,
			Page.Trash => Trash,
			Page.Map => Map,
			Page.Dashboard => Dashboard,
			Page.Review => Review,
			Page.Compliance => Compliance,
			Page.Assignments => Assignments,
			_ => Structures,
		};
		Map.IsActive = page == Page.Map;
		Dashboard.IsActive = page == Page.Dashboard;
		Review.IsActive = page == Page.Review;
		Compliance.IsActive = page == Page.Compliance;
		Assignments.IsActive = page == Page.Assignments;
		RefreshStatus();
		if (page == Page.Map)
		{
			Map.Reload();
			Map.RequestFit();
		}
		if (page == Page.Dashboard)
		{
			Dashboard.Reload();
		}
		if (page == Page.Trash)
		{
			Trash.Reload();
		}
		if (page == Page.Review)
		{
			Review.Reload();
		}
		if (page == Page.Compliance)
		{
			Compliance.Reload();
		}
		if (page == Page.Assignments)
		{
			Assignments.Reload();
		}
		if (page == Page.Structures)
		{
			Structures.Reload();
		}
	}

	/// <summary>The window became active (check for others' saves now, then every 1.5 seconds) or inactive (stop checking).</summary>
	public void WindowActivated(bool active) => _app.Changes.IsActive = active;

	/// <summary>Another program saved something: lists catch up, and so does the open assessment if it was one of them.</summary>
	private void OnExternalChanges(ExternalChanges changes)
	{
		Structures.Reload();
		RefreshStatus();
		if (Map.IsActive)
		{
			Map.Reload();
		}
		if (Dashboard.IsActive)
		{
			Dashboard.Reload();
		}
		if (ActivePage == Page.Trash)
		{
			Trash.Reload();
		}
		ReloadOfficePages();
		if (Editor != null && (changes.Unversioned || changes.Assessments.Contains(Editor.Assessment.AssessmentID) || changes.Properties.Contains(Editor.Property.PropertyID)))
		{
			RefreshEditor();
		}
	}

	/// <summary>File → Import Inspections or Parcels…: the import page, asking for the file when none is open.</summary>
	[RelayCommand]
	private async Task OpenSourceImport()
	{
		Show(Page.SourceImport);
		if (!SourceImport.HasFile)
		{
			await SourceImport.ChooseFileCommand.ExecuteAsync(null);
		}
	}

	// ───────────── Menu and keyboard commands ─────────────

	/// <summary>File, then Save (Cmd+S): saves the open assessment now.</summary>
	[RelayCommand]
	private void Save() => Editor?.Save();

	/// <summary>Edit, then Undo when the cursor isn't in a text box (which undoes its own typing).</summary>
	[RelayCommand]
	private void Undo()
	{
		if (CurrentPage == Editor && Editor != null)
		{
			Editor.Undo();
		}
	}

	[RelayCommand]
	private void Redo()
	{
		if (CurrentPage == Editor && Editor != null)
		{
			Editor.Redo();
		}
	}

	/// <summary>Edit, then Find (Cmd+F): the Structures search box.</summary>
	[RelayCommand]
	private void Find()
	{
		Show(Page.Structures);
		Avalonia.Threading.Dispatcher.UIThread.Post(Structures.FocusSearch, Avalonia.Threading.DispatcherPriority.Background);
	}

	/// <summary>View, then Next Step (Cmd+]).</summary>
	[RelayCommand]
	private void NextStep()
	{
		if (CurrentPage == Editor && Editor != null)
		{
			Editor.NextCommand.Execute(null);
		}
	}

	/// <summary>View, then Previous Step (Cmd+[).</summary>
	[RelayCommand]
	private void PreviousStep()
	{
		if (CurrentPage == Editor && Editor != null)
		{
			Editor.BackCommand.Execute(null);
		}
	}

	/// <summary>File, then Restore from Backup.</summary>
	[RelayCommand]
	private void RestoreBackup()
	{
		Editor?.Save();
		_ = Sheets.ShowAsync(new RestoreBackupSheetViewModel(_app));
	}

	[RelayCommand]
	private void ShowShortcuts() => _ = Sheets.ShowAsync(new ShortcutsSheetViewModel());

	/// <summary>Help, then Show Log Folder: what to send when reporting a problem.</summary>
	[RelayCommand]
	private void ShowLogs()
	{
		Directory.CreateDirectory(AppLog.Folder);
		_app.Dialogs.Reveal(AppLog.Folder);
	}

	/// <summary>Recent databases for File, then Open Recent.</summary>
	public IReadOnlyList<string> RecentDatabases => _app.RecentDatabases();

	/// <summary>
	/// Opens an assessment in the editor. From another assessment (the editor's own switcher, ⌘K while editing) the
	/// editor keeps the page it was opened from; otherwise closing it leads back to Structures.
	/// </summary>
	public void OpenAssessment(Guid assessmentId) => OpenAssessmentFrom(assessmentId, ActivePage == Page.Editor ? _editorParent : Page.Structures);

	/// <summary>Opens an assessment from a page (Review, Compliance), which the breadcrumb and closing the editor lead back to.</summary>
	public void OpenAssessmentFrom(Guid assessmentId, Page parent)
	{
		Editor?.Save();
		(AssessmentFile file, VersionStamp stamp) = _app.Store.LoadAssessmentWithStamp(assessmentId);
		if (file == null)
		{
			_app.Dialogs.Notify("Not found", "That assessment no longer exists.", isError: true);
			ShowStructures(saveEditor: false);
			return;
		}
		_editorParent = parent;
		Editor = new AssessmentEditorViewModel(_app, this, file, stamp: stamp);
		ActivePage = Page.Editor;
		CurrentPage = Editor;
		RefreshStatus();
	}

	/// <summary>Opens a new assessment that isn't saved yet (see <see cref="Sde.Data.SdeStore.NewDraft"/>).</summary>
	public void OpenDraft(AssessmentFile draft)
	{
		if (ActivePage != Page.Editor)
		{
			_editorParent = Page.Structures;
		}
		Editor?.Save();
		// A new assessment of an existing structure still expects that structure as it was loaded.
		Guid propertyId = draft.Property.PropertyID;
		VersionStamp stamp = propertyId == Guid.Empty ? null
			: new VersionStamp(0, _app.Store.GetVersion(propertyId), null, draft.Property.LastUpdate);
		Editor = new AssessmentEditorViewModel(_app, this, draft, isDraft: true, stamp: stamp);
		ActivePage = Page.Editor;
		CurrentPage = Editor;
	}

	/// <summary>
	/// Saves and closes the open assessment. Called before bulk changes (imports, coordinates) so the editor's
	/// in-memory copy can't overwrite them when it next saves (on quit, or on leaving the editor).
	/// </summary>
	public void CloseEditor()
	{
		Editor?.Save();
		Editor = null;
	}

	/// <summary>
	/// Leaves the editor for the page it was opened from (Structures, unless Review or Compliance opened it), saving
	/// the assessment first unless <paramref name="saveEditor"/> is false. From any other page, shows Structures.
	/// </summary>
	public void ShowStructures(bool saveEditor = true)
	{
		Page back = ActivePage == Page.Editor ? _editorParent : Page.Structures;
		if (!saveEditor)
		{
			Editor = null;
		}
		Show(back);
		Editor = null;
		_editorParent = Page.Structures;
	}

	Guid? IEditorHost.OpenAssessmentId => Editor?.Assessment.AssessmentID;

	Guid? IEditorHost.OpenPropertyId => Editor?.Property.PropertyID;

	void IEditorHost.FlushEditor() => Editor?.Save();

	void IEditorHost.ReloadEditor() => RefreshEditor();

	/// <summary>
	/// Catches the open editor up with the database. Nothing happens when its assessment and structure haven't changed
	/// since it loaded them (so its undo steps survive writes to other assessments). An editor with unsaved changes
	/// saves them, which finds the newer copy and merges onto it; a clean one reloads, and its undo steps are dropped,
	/// since undoing would put someone else's change back. One deleted elsewhere is closed.
	/// </summary>
	public void RefreshEditor()
	{
		if (Editor == null)
		{
			return;
		}
		if (Editor.IsDraft)
		{
			// Not in the database yet; its first save checks its structure's version.
			return;
		}
		Guid id = Editor.Assessment.AssessmentID;
		VersionStamp now = _app.Store.GetStamp(id);
		if (now != null && now == Editor.Stamp)
		{
			return;
		}
		if (now != null && Editor.IsDirty)
		{
			Editor.Save();
			return;
		}
		_app.Undo.Forget(id);
		(AssessmentFile file, VersionStamp stamp) = _app.Store.LoadAssessmentWithStamp(id);
		ReplaceEditor(file, stamp);
	}

	/// <summary>
	/// Shows a new copy of the open assessment (after it was reloaded, merged or undone), keeping the step that was
	/// showing. <paramref name="conflict"/> is what the user is asked about after a merge. A copy that is gone or
	/// deleted closes the editor.
	/// </summary>
	public void ReplaceEditor(AssessmentFile file, VersionStamp stamp, EditConflict conflict = null)
	{
		if (Editor == null)
		{
			return;
		}
		int step = Editor.Sections.IndexOf(Editor.SelectedSection);
		bool showing = CurrentPage == Editor;
		if (file == null || !file.Assessment.Active || !file.Property.Active)
		{
			Editor = null;
			if (showing)
			{
				Show(Page.Structures);
			}
			return;
		}
		Editor = new AssessmentEditorViewModel(_app, this, file, stamp: stamp) { Conflict = conflict };
		if (step > 0 && step < Editor.Sections.Count)
		{
			Editor.SelectedSection = Editor.Sections[step];
		}
		if (showing)
		{
			CurrentPage = Editor;
		}
	}

	/// <summary>Opens a PDF report for the given assessments. Implemented in MainViewModel.Reports.cs.</summary>
	public Task PrintAsync(string kind, IReadOnlyList<Guid> assessmentIds) => PrintReportAsync(kind, assessmentIds);
}
