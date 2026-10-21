using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;

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
}

/// <summary>The window: a sidebar (Structures, Import &amp; export, Settings) and the current page.</summary>
public sealed partial class MainViewModel : ViewModelBase, IEditorHost
{
	private readonly AppServices _app;

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
		app.DataChanged += () =>
		{
			Structures.Reload();
			RefreshStatus();
		};
		app.DatabaseChanged += () =>
		{
			Editor = null;
			Structures.DatabaseChanged();
			Settings = new SettingsViewModel(app);
			Structures.ShowOnly(null, null);
			SourceImport.DatabaseChanged();
			RefreshStatus();
			OnPropertyChanged(nameof(RecentDatabases));
			OnPropertyChanged(nameof(IsSampleDatabase));
			Show(Page.Structures);
		};
		CurrentPage = Structures;
		RefreshStatus();
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
	[NotifyPropertyChangedFor(nameof(IsStructuresPage), nameof(IsImportExportPage), nameof(IsSettingsPage), nameof(IsTrashPage), nameof(IsMapPage), nameof(IsDashboardPage))]
	public partial Page ActivePage { get; set; }

	public bool IsStructuresPage => ActivePage is Page.Structures or Page.Editor;

	public bool IsImportExportPage => ActivePage is Page.ImportExport or Page.SourceImport;

	public bool IsSettingsPage => ActivePage == Page.Settings;

	public bool IsTrashPage => ActivePage == Page.Trash;

	public bool IsMapPage => ActivePage == Page.Map;

	public bool IsDashboardPage => ActivePage == Page.Dashboard;

	[ObservableProperty]
	public partial string DatabaseName { get; set; }

	[ObservableProperty]
	public partial string CountsText { get; set; }

	public void RefreshStatus()
	{
		DatabaseName = Path.GetFileName(_app.Store.Database.FilePath);
		(int p, int a) = _app.Store.Counts();
		CountsText = $"{p} structure{(p == 1 ? "" : "s")} · {a} assessment{(a == 1 ? "" : "s")}";
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
			_ => Structures,
		};
		Map.IsActive = page == Page.Map;
		Dashboard.IsActive = page == Page.Dashboard;
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
		if (page == Page.Structures)
		{
			Structures.Reload();
			RefreshStatus();
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

	public void OpenAssessment(Guid assessmentId)
	{
		Editor?.Save();
		AssessmentFile file = _app.Store.LoadAssessment(assessmentId);
		if (file == null)
		{
			_app.Dialogs.Notify("Not found", "That assessment no longer exists.", isError: true);
			ShowStructures(saveEditor: false);
			return;
		}
		Editor = new AssessmentEditorViewModel(_app, this, file);
		ActivePage = Page.Editor;
		CurrentPage = Editor;
		RefreshStatus();
	}

	/// <summary>Opens a new assessment that isn't saved yet (see <see cref="Sde.Data.SdeStore.NewDraft"/>).</summary>
	public void OpenDraft(AssessmentFile draft)
	{
		Editor?.Save();
		Editor = new AssessmentEditorViewModel(_app, this, draft, isDraft: true);
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

	public void ShowStructures(bool saveEditor = true)
	{
		if (!saveEditor)
		{
			Editor = null;
		}
		Show(Page.Structures);
		Editor = null;
	}

	Guid? IEditorHost.OpenAssessmentId => Editor?.Assessment.AssessmentID;

	void IEditorHost.FlushEditor() => Editor?.Save();

	void IEditorHost.ReloadEditor()
	{
		if (Editor == null)
		{
			return;
		}
		int step = Editor.Sections.IndexOf(Editor.SelectedSection);
		bool showing = CurrentPage == Editor;
		AssessmentFile file = _app.Store.LoadAssessment(Editor.Assessment.AssessmentID);
		if (file == null || !file.Assessment.Active || !file.Property.Active)
		{
			Editor = null;
			if (showing)
			{
				Show(Page.Structures);
			}
			return;
		}
		Editor = new AssessmentEditorViewModel(_app, this, file);
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
