using System.Text.Json;
using System.Text.Json.Serialization;
using Sde.Data;

namespace Sde.App.Services;

/// <summary>File pickers, confirmations and Finder actions, implemented by the main window.</summary>
public interface IDialogs
{
	Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, string filterName, params string[] patterns);

	Task<string> PickFolderAsync(string title);

	Task<string> SaveFileAsync(string title, string suggestedName, string filterName, string extension);

	Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

	void Notify(string title, string message, bool isError = false);

	/// <summary>Opens a file with its default app (e.g. a PDF in Preview).</summary>
	void Open(string path);

	/// <summary>Shows a file or folder in Finder.</summary>
	void Reveal(string path);
}

/// <summary>Remembered between launches (~/Library/Application Support/SDE/app-settings.json).</summary>
public sealed class AppSettings
{
	public string DatabasePath { get; set; }

	public string LastExportFolder { get; set; }

	/// <summary>The import profile used last (its name), picked again for the next file with the same record ID column.</summary>
	public string LastImportProfile { get; set; }

	/// <summary>v1.5's name for <see cref="LastImportProfile"/>: read from an old settings file, never written.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string LastAccelaProfile
	{
		get => null;
		set => LastImportProfile ??= value;
	}

	/// <summary>The name written in edit history for changes made in this app. Defaults to the Mac account's full name.</summary>
	public string YourName { get; set; }

	/// <summary>Databases opened recently, newest first (File, then Open Recent).</summary>
	public List<string> RecentDatabases { get; set; } = new();

	/// <summary>The main window's position and size when it was last closed (null = centred, default size).</summary>
	public WindowPlacement Window { get; set; }

	/// <summary>The first-run walkthrough has been shown (Help, then Show the Walkthrough, shows it again).</summary>
	public bool SeenWalkthrough { get; set; }

	public const int MaxRecent = 8;

	/// <summary>
	/// ~/Library/Application Support/SDE/app-settings.json, or, when SDE_DATABASE chose the database (tests, training),
	/// a file beside that database, so a test or training session never changes the person's own settings.
	/// </summary>
	private static string FilePath => Environment.GetEnvironmentVariable("SDE_DATABASE") is string forced && forced.Length > 0
		? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(forced))!, "app-settings.json")
		: Path.Combine(Path.GetDirectoryName(SdeDatabase.DefaultPath)!, "app-settings.json");

	/// <summary>Keeps two saves in this app from writing the temporary file at the same time.</summary>
	private static readonly object SaveGate = new object();

	/// <summary>The remembered settings (see <see cref="Load(string)"/>).</summary>
	public static AppSettings Load() => Load(FilePath);

	/// <summary>
	/// Reads the settings at <paramref name="path"/>. When that file is missing, cut short or can't be read, the copy
	/// kept by the last save (the same name with ".bak" added) is read instead, and failing that the defaults are used.
	/// </summary>
	public static AppSettings Load(string path) => Read(path) ?? Read(path + ".bak") ?? new AppSettings();

	/// <summary>The settings in one file, or null when it is missing, cut short or can't be read.</summary>
	private static AppSettings Read(string path)
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) : null;
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Remembers the settings (see <see cref="Save(string)"/>).</summary>
	public void Save() => Save(FilePath);

	/// <summary>
	/// Writes the settings to a temporary file in the same folder, then moves it over <paramref name="path"/> in one
	/// step, so a crash or a full disk never leaves a half-written settings file. The previous file is kept with ".bak"
	/// added to its name, for <see cref="Load(string)"/> to fall back to.
	/// </summary>
	public void Save(string path)
	{
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true });
		lock (SaveGate)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			string temp = path + ".tmp";
			try
			{
				using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					stream.Write(json);
					stream.Flush(flushToDisk: true);
				}
				if (File.Exists(path))
				{
					File.Replace(temp, path, path + ".bak");
				}
				else
				{
					File.Move(temp, path);
				}
			}
			finally
			{
				// Gone already when the save worked; a save that failed part-way leaves nothing behind.
				File.Delete(temp);
			}
		}
	}
}

/// <summary>Where the main window was.</summary>
public sealed class WindowPlacement
{
	public int X { get; set; }

	public int Y { get; set; }

	public double Width { get; set; }

	public double Height { get; set; }

	public bool Maximized { get; set; }
}

/// <summary>The open database and app-wide services.</summary>
public sealed class AppServices
{
	public AppServices(IDialogs dialogs)
	{
		Dialogs = dialogs;
		Sheets = new SheetHost();
		Errors = new ErrorPresenter(Sheets);
		Jobs = new JobRunner(Errors);
		Writes = new AssessmentWriteCoordinator(this);
		Changes = new ExternalChangeMonitor(this);
		PhoneForm = new PhoneFormService(this);
		Manual = new ManualService(dialogs);
		Settings = AppSettings.Load();
		// SDE_DATABASE=/path/x.sqlite opens a specific database without changing the remembered one (testing, training).
		string forced = Environment.GetEnvironmentVariable("SDE_DATABASE");
		if (!string.IsNullOrEmpty(forced))
		{
			Store = new SdeStore(SdeDatabase.OpenOrCreate(forced));
			StartWatching(Store);
			IsSample = Store.GetSetting<bool>(SampleDatabase.SettingKey);
			Tidy(Store);
			Store.UserName = UserName;
			// Keep everything a test or training session writes next to its database.
			ImportProfiles = new ImportProfiles(Path.Combine(Path.GetDirectoryName(Store.Database.FilePath)!, ImportProfiles.FolderName));
			DepthTables = new DepthTables(Path.Combine(Path.GetDirectoryName(Store.Database.FilePath)!, DepthTables.FolderName));
			return;
		}
		ImportProfiles = new ImportProfiles(Path.Combine(Path.GetDirectoryName(SdeDatabase.DefaultPath)!, ImportProfiles.FolderName));
		DepthTables = new DepthTables(Path.Combine(Path.GetDirectoryName(SdeDatabase.DefaultPath)!, DepthTables.FolderName));
		Open(Settings.DatabasePath is string p && File.Exists(p) ? p : SdeDatabase.DefaultPath);
	}

	public IDialogs Dialogs { get; }

	/// <summary>Panels shown over the window (see <see cref="SheetHost"/>).</summary>
	public SheetHost Sheets { get; }

	/// <summary>Shows and logs unexpected errors in plain English.</summary>
	public ErrorPresenter Errors { get; }

	/// <summary>Long tasks with progress and Stop.</summary>
	public JobRunner Jobs { get; }

	/// <summary>Changes to assessments made outside the editor.</summary>
	public AssessmentWriteCoordinator Writes { get; }

	/// <summary>Undo and redo for assessments.</summary>
	public UndoService Undo { get; } = new UndoService();

	/// <summary>
	/// This app's lease on the open database: shared while it is open, to itself during a restore or a move, which are
	/// refused while another copy of SDE holds one (see <see cref="DatabaseLease"/>). Null where leases aren't available.
	/// </summary>
	public DatabaseLease Lease { get; private set; }

	/// <summary>Notices saves made to the open database by other programs (polled by <see cref="Changes"/>).</summary>
	public ChangeWatcher Watcher { get; private set; }

	/// <summary>Tells the window about saves made by other programs while it is active.</summary>
	public ExternalChangeMonitor Changes { get; }

	/// <summary>
	/// Automatic backups of the open database. Off (null) when SDE_DATABASE chose the database, so tests and training
	/// sessions don't fill a folder with backups.
	/// </summary>
	public BackupService Backups { get; private set; }

	/// <summary>Completes when the automatic "opened today" backup has been written (or wasn't needed).</summary>
	public Task OpeningBackup { get; private set; } = Task.CompletedTask;

	public AppSettings Settings { get; }

	/// <summary>FEMA's SDE User Manual and where to read it.</summary>
	public ManualService Manual { get; }

	/// <summary>The open database is the practice one made by Help, then Open Sample Database.</summary>
	public bool IsSample { get; private set; }

	/// <summary>The database that was open when the sample was opened, which "Back to my database" returns to.</summary>
	public string SampleReturnPath { get; private set; }

	/// <summary>Where the sample database is made (a fresh copy each time it is opened).</summary>
	public string SampleFolder { get; set; } = Path.Combine(Path.GetDirectoryName(SdeDatabase.DefaultPath)!, SampleDatabase.FolderName);

	/// <summary>
	/// The first-run walkthrough opens on the first launch, but never when SDE_DATABASE is set (tests and training
	/// sessions), so nothing unexpected covers the window.
	/// </summary>
	public bool ShouldShowWalkthrough => !Settings.SeenWalkthrough && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDE_DATABASE"));

	/// <summary>The phone form's web server (off until started in Settings).</summary>
	public PhoneFormService PhoneForm { get; }

	public SdeStore Store { get; private set; }

	/// <summary>Saved import profiles. Kept outside the database, so they carry over to a new database.</summary>
	public ImportProfiles ImportProfiles { get; }

	/// <summary>The community's depth-damage tables (files, like import profiles; SDE ships none).</summary>
	public DepthTables DepthTables { get; }

	/// <summary>
	/// The HTTP client the online look-ups (Census geocoder, FEMA flood map) use. Tests replace it with one whose
	/// handler answers from recorded files, so they never touch the network.
	/// </summary>
	public Func<HttpClient> HttpClientFactory { get; set; } = () => OnlineLookup.SharedClient.Value;

	/// <summary>Raised after a different database was opened.</summary>
	public event Action DatabaseChanged;

	/// <summary>Raised when structures were added, changed or removed (e.g. by an import), so lists can reload.</summary>
	public event Action DataChanged;

	/// <summary>Opens (or creates) a database and makes it the one SDE opens next time. Throws if the file isn't an SDE database.</summary>
	public void Open(string path)
	{
		SdeStore store = new SdeStore(SdeDatabase.OpenOrCreate(path));
		Tidy(store);
		store.UserName = UserName;
		string previous = Store?.Database.FilePath;
		bool previousWasSample = IsSample;
		Store = store;
		StartWatching(store);
		IsSample = store.GetSetting<bool>(SampleDatabase.SettingKey);
		if (IsSample && !previousWasSample)
		{
			SampleReturnPath = previous;
		}
		// The sample is practice: no backups of it, and it isn't what SDE opens next time.
		Backups = IsSample ? null : new BackupService(store.Database);
		BackupService backups = Backups;
		OpeningBackup = backups == null ? Task.CompletedTask : Task.Run(() =>
		{
			try
			{
				backups.BackUpIfNoneToday();
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				AppLog.Write("Couldn't make the automatic backup", ex);
			}
		});
		if (!IsSample)
		{
			Settings.DatabasePath = store.Database.FilePath;
			Settings.RecentDatabases.RemoveAll(p => string.Equals(p, store.Database.FilePath, StringComparison.Ordinal));
			Settings.RecentDatabases.Insert(0, store.Database.FilePath);
			if (Settings.RecentDatabases.Count > AppSettings.MaxRecent)
			{
				Settings.RecentDatabases.RemoveRange(AppSettings.MaxRecent, Settings.RecentDatabases.Count - AppSettings.MaxRecent);
			}
			Settings.Save();
		}
		Undo.Clear();
		DatabaseChanged?.Invoke();
	}

	/// <summary>The name for edit history: <see cref="AppSettings.YourName"/>, or the account's full name.</summary>
	public string UserName => string.IsNullOrWhiteSpace(Settings.YourName) ? AccountName.Value : Settings.YourName.Trim();

	/// <summary>Changes the name used in edit history from now on.</summary>
	public void SetYourName(string name)
	{
		Settings.YourName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
		Settings.Save();
		Store.UserName = UserName;
	}

	/// <summary>The Mac account's full name ("id -F"), or the short user name elsewhere.</summary>
	public static readonly Lazy<string> AccountName = new Lazy<string>(() =>
	{
		if (OperatingSystem.IsMacOS())
		{
			try
			{
				System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/id") { RedirectStandardOutput = true, UseShellExecute = false };
				psi.ArgumentList.Add("-F");
				using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
				string full = p?.StandardOutput.ReadToEnd().Trim();
				p?.WaitForExit(2000);
				if (!string.IsNullOrWhiteSpace(full))
				{
					return full;
				}
			}
			catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
			{
			}
		}
		return Environment.UserName;
	});

	/// <summary>On quitting: a backup if anything changed since the last one. Never stops the app from closing.</summary>
	public void BackUpOnQuit()
	{
		try
		{
			OpeningBackup.Wait(TimeSpan.FromSeconds(30));
			Backups?.BackUpIfChanged();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't make the backup on quitting", ex);
		}
	}

	/// <summary>
	/// Replaces the open database with a backup (after backing up the current state) and reopens it. Refused while
	/// another copy of SDE has the database open.
	/// </summary>
	public void RestoreBackup(BackupInfo backup)
	{
		string path = Store.Database.FilePath;
		OpeningBackup.Wait(TimeSpan.FromSeconds(30));
		Watcher?.Dispose();
		Watcher = null;
		try
		{
			new BackupService(Store.Database).Restore(backup, Lease);
		}
		catch
		{
			Watcher = new ChangeWatcher(Store.Database);
			throw;
		}
		Open(path);
	}

	/// <summary>
	/// Moves the open database (with its photos, documents and map layers) to <paramref name="newPath"/>, or copies it
	/// there, as a job with progress, then opens the new one. A move is refused while another copy of SDE has the
	/// database open. Returns false when it didn't happen (the job runner has shown why).
	/// </summary>
	public async Task<bool> MoveOrCopyDatabaseAsync(string newPath, bool move)
	{
		OpeningBackup.Wait(TimeSpan.FromSeconds(30));
		SdeDatabase database = Store.Database;
		DatabaseLease lease = Lease;
		if (move)
		{
			Watcher?.Dispose();
			Watcher = null;
		}
		JobOutcome<bool> outcome = await Jobs.RunAsync(move ? "Moving the database" : "Copying the database", (progress, ct) =>
		{
			IProgress<(int Done, int Total)> files = new Progress<(int Done, int Total)>(x => progress.Report(new JobProgress(x.Done, x.Total, "Copying photos and documents")));
			if (move)
			{
				DatabaseFiles.Move(database, newPath, lease, files);
			}
			else
			{
				DatabaseFiles.Copy(database, newPath, files);
			}
			return true;
		}, move ? "Couldn't move the database" : "Couldn't copy the database", canStop: false);
		if (!outcome.Completed)
		{
			if (Watcher == null && File.Exists(database.FilePath))
			{
				Watcher = new ChangeWatcher(database);
			}
			return false;
		}
		if (move)
		{
			Settings.RecentDatabases.RemoveAll(p => string.Equals(p, database.FilePath, StringComparison.Ordinal));
		}
		Open(newPath);
		return true;
	}

	/// <summary>Takes this app's lease on a newly opened database and starts noticing others' saves to it.</summary>
	private void StartWatching(SdeStore store)
	{
		Watcher?.Dispose();
		Lease?.Dispose();
		Lease = DatabaseLease.TryShared(store.Database.LockPath);
		Watcher = new ChangeWatcher(store.Database);
	}

	/// <summary>Lets go of the open database's lease and connection (on quitting; tests call it for each window).</summary>
	public void Close()
	{
		Changes.IsActive = false;
		Watcher?.Dispose();
		Watcher = null;
		Lease?.Dispose();
		Lease = null;
	}

	/// <summary>On opening a database: removes what has been in Recently deleted for more than 30 days.</summary>
	private static void Tidy(SdeStore store)
	{
		try
		{
			store.PurgeExpired();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't empty old items from Recently deleted", ex);
		}
	}

	/// <summary>Recent databases that still exist, newest first.</summary>
	public IReadOnlyList<string> RecentDatabases() => Settings.RecentDatabases.Where(File.Exists).ToList();

	public void RaiseDataChanged() => DataChanged?.Invoke();
}
