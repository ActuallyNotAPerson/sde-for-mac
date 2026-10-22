using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.Core;
using Sde.Core.Records;

namespace Sde.App.ViewModels;

/// <summary>A renamable custom field slot.</summary>
public sealed partial class CustomFieldNameViewModel(CustomFieldRecord field, Action<CustomFieldNameViewModel> rename) : ObservableObject
{
	public CustomFieldRecord Field { get; } = field;

	public string Slot => $"Field {Field.zOrder}";

	public string Name
	{
		get => Field.CustomField;
		set
		{
			if (Field.CustomField != value && !string.IsNullOrWhiteSpace(value))
			{
				Field.CustomField = value.Trim();
				OnPropertyChanged();
				rename(this);
			}
		}
	}
}

/// <summary>A saved note that can be inserted into the editor's notes fields; edits are saved as they're typed.</summary>
public sealed partial class SnippetViewModel(NoteRecord note, Action<NoteRecord> save) : ObservableObject
{
	public NoteRecord Note { get; } = note;

	public string Name
	{
		get => Note.NoteName;
		set
		{
			if (Note.NoteName != value)
			{
				Note.NoteName = value;
				OnPropertyChanged();
				save(Note);
			}
		}
	}

	public string Text
	{
		get => Note.Notes;
		set
		{
			if (Note.Notes != value)
			{
				Note.Notes = value;
				OnPropertyChanged();
				save(Note);
			}
		}
	}
}

/// <summary>Default values for new records, preferences, custom field names, and the database file.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
	private readonly AppServices _app;
	private readonly DefaultValuesRecord _defaults;
	private readonly UserPreferencesRecord _prefs;

	public SettingsViewModel(AppServices app)
	{
		_app = app;
		_defaults = app.Store.GetDefaults() ?? new DefaultValuesRecord();
		_prefs = app.Store.GetPreferences();
		ReferenceData r = app.Store.Reference;
		Func<object> D = () => _defaults;
		Type dt = typeof(DefaultValuesRecord);
		IReadOnlyList<ChoiceOption> Lookup(int type) => r.OfType(type).Select(l => new ChoiceOption(l.LookupID, l.LookupFieldName)).ToList();

		DefaultGroups.Add(new FieldGroupViewModel("Location", null, new FieldViewModel[]
		{
			new TextFieldViewModel("City", D, dt, nameof(DefaultValuesRecord.City)),
			new ChoiceFieldViewModel("State", D, dt, nameof(DefaultValuesRecord.State), () => r.States.OrderBy(s => s.State).Select(s => new ChoiceOption(s.StateID, s.State)).ToList()),
			new ChoiceFieldViewModel("County / parish", D, dt, nameof(DefaultValuesRecord.County), () => r.CountiesIn(_defaults.State).Select(c => new ChoiceOption(c.CountyID, c.County)).ToList()),
			new TextFieldViewModel("ZIP code", D, dt, nameof(DefaultValuesRecord.Zip)),
			new TextFieldViewModel("Datum", D, dt, nameof(DefaultValuesRecord.Datum)),
			new NumberFieldViewModel("Year built", D, dt, nameof(DefaultValuesRecord.YearOfConstruction)) { Format = "0" },
		}));
		DefaultGroups.Add(new FieldGroupViewModel("NFIP and flood map", null, new FieldViewModel[]
		{
			new TextFieldViewModel("NFIP community ID", D, dt, nameof(DefaultValuesRecord.NFIPCommunityID)) { MaxLength = 6 },
			new TextFieldViewModel("Community name", D, dt, nameof(DefaultValuesRecord.NFIPCommunityName)),
			new TextFieldViewModel("FIRM panel number", D, dt, nameof(DefaultValuesRecord.FirmPanelNumber)),
			new TextFieldViewModel("Panel suffix", D, dt, nameof(DefaultValuesRecord.Suffix)) { MaxLength = 1 },
			new DateFieldViewModel("FIRM panel date", D, dt, nameof(DefaultValuesRecord.DateOfFirmPanel)),
			new ChoiceFieldViewModel("FIRM zone", D, dt, nameof(DefaultValuesRecord.FirmZone), () => Lookup(LookupTypes.FirmZone)),
			new NumberFieldViewModel("Base flood elevation", D, dt, nameof(DefaultValuesRecord.BFE)) { Format = "0.##", Suffix = "ft" },
			new ChoiceFieldViewModel("Regulatory floodway", D, dt, nameof(DefaultValuesRecord.RegulatoryFloodway), () => Lookup(LookupTypes.RegulatoryFloodway)),
			new TextFieldViewModel("Community information", D, dt, nameof(DefaultValuesRecord.CommunityUse)) { IsWide = true },
		}));
		DefaultGroups.Add(new FieldGroupViewModel("Event and inspection", null, new FieldViewModel[]
		{
			new DateFieldViewModel("Date damage occurred", D, dt, nameof(DefaultValuesRecord.DateDamagedOccurred)),
			new ChoiceFieldViewModel("Cause of damage", D, dt, nameof(DefaultValuesRecord.CauseOfDamage), () => Lookup(LookupTypes.CauseOfDamage)),
			new NumberFieldViewModel("Flood duration", D, dt, nameof(DefaultValuesRecord.DurationOfFlood)) { Format = "0" },
			new ChoiceFieldViewModel("Duration units", D, dt, nameof(DefaultValuesRecord.DurationOfFloodUnit), () => Lookup(LookupTypes.DurationUnits)),
			new TextFieldViewModel("Inspector", D, dt, nameof(DefaultValuesRecord.InspectedBy)),
			new TextFieldViewModel("Inspector phone", D, dt, nameof(DefaultValuesRecord.InspectedPhone)),
			new DateFieldViewModel("Inspection date", D, dt, nameof(DefaultValuesRecord.DateOfInspection)),
		}));
		DefaultGroups.Add(new FieldGroupViewModel("Cost", null, new FieldViewModel[]
		{
			new NumberFieldViewModel("Base cost per sq ft", D, dt, nameof(DefaultValuesRecord.BaseCost)) { Format = "N2", Prefix = "$" },
			new NumberFieldViewModel("Geographic adjustment", D, dt, nameof(DefaultValuesRecord.LocalMultiplier)) { Format = "0.###" },
			new TextFieldViewModel("Cost data source", D, dt, nameof(DefaultValuesRecord.CostDataRef)),
			new DateFieldViewModel("Cost data date", D, dt, nameof(DefaultValuesRecord.CostDataDate)),
		}));
		foreach (FieldViewModel f in DefaultGroups.SelectMany(g => g.Fields))
		{
			f.Load();
			f.Changed += DefaultChanged;
		}

		foreach (CustomFieldRecord cf in r.CustomFields.OrderBy(c => c.zOrder))
		{
			CustomFieldNames.Add(new CustomFieldNameViewModel(cf, n => _app.Store.RenameCustomField(n.Field.CustomFieldID, n.Name)));
		}
		_checkLatLong = _prefs.LatLong;
		_useDefaults = _prefs.UseDefaults;
		LoadSnippets();
		SelectedCategory = Categories[0];
	}

	public ObservableCollection<FieldGroupViewModel> DefaultGroups { get; } = new();

	public ObservableCollection<CustomFieldNameViewModel> CustomFieldNames { get; } = new();

	public string DatabasePath => _app.Store.Database.FilePath;

	/// <summary>Note snippets (FEMA's saved notes): text to insert into an assessment's notes fields.</summary>
	public ObservableCollection<SnippetViewModel> Snippets { get; } = new();

	private void LoadSnippets()
	{
		Snippets.Clear();
		foreach (NoteRecord n in _app.Store.GetNotes())
		{
			Snippets.Add(new SnippetViewModel(n, _app.Store.SaveNote));
		}
	}

	[RelayCommand]
	private void AddSnippet()
	{
		NoteRecord n = new NoteRecord { NoteName = "New snippet", Notes = "" };
		_app.Store.SaveNote(n);
		Snippets.Add(new SnippetViewModel(n, _app.Store.SaveNote));
	}

	[RelayCommand]
	private void DeleteSnippet(SnippetViewModel snippet)
	{
		_app.Store.DeleteNote(snippet.Note.NoteID);
		Snippets.Remove(snippet);
	}

	/// <summary>The address of USGS's high-water mark service; blank uses the built-in one.</summary>
	public string UsgsStnUrl
	{
		get => _app.Settings.UsgsStnUrl ?? "";
		set
		{
			string clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
			if (clean != _app.Settings.UsgsStnUrl)
			{
				_app.Settings.UsgsStnUrl = clean;
				_app.Settings.Save();
				OnPropertyChanged();
			}
		}
	}

	public string UsgsStnDefault => Sde.Interop.Online.StnClient.DefaultBaseUrl;

	/// <summary>The name written in edit history (Settings, Preferences).</summary>
	public string YourName
	{
		get => _app.UserName;
		set
		{
			_app.SetYourName(value);
			OnPropertyChanged();
		}
	}

	public string BackupsText => _app.Backups == null
		? "Automatic backups are off for this database."
		: $"Backed up automatically into the Backups folder next to the database, once a day when it's opened and when you quit (the newest {Sde.Data.BackupService.Keep} are kept).";

	[RelayCommand]
	private void RestoreBackup() => _ = _app.Sheets.ShowAsync(new Sheets.RestoreBackupSheetViewModel(_app));

	/// <summary>The phone form (Settings, Phone form).</summary>
	public PhoneFormService PhoneForm => _app.PhoneForm;

	public string AttachmentsPath => _app.Store.AttachmentsFolder;

	public string Version => typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1";

	private bool _checkLatLong;

	/// <summary>FEMA's "Lat/Long Validation": check coordinates fall inside the structure's state.</summary>
	public bool CheckLatLong
	{
		get => _checkLatLong;
		set
		{
			if (SetProperty(ref _checkLatLong, value))
			{
				_prefs.LatLong = value;
				_app.Store.SavePreferences(_prefs);
			}
		}
	}

	private bool _useDefaults;

	/// <summary>FEMA's "Auto Check Default Values": fill new structures with the defaults below.</summary>
	public bool UseDefaults
	{
		get => _useDefaults;
		set
		{
			if (SetProperty(ref _useDefaults, value))
			{
				_prefs.UseDefaults = value;
				_app.Store.SavePreferences(_prefs);
			}
		}
	}

	private void DefaultChanged(FieldViewModel field)
	{
		if (field.Member == nameof(DefaultValuesRecord.State))
		{
			foreach (ChoiceFieldViewModel c in DefaultGroups.SelectMany(g => g.Fields).OfType<ChoiceFieldViewModel>())
			{
				c.RefreshOptions();
			}
		}
		_app.Store.SaveDefaults(_defaults);
	}

	[RelayCommand]
	private void RevealDatabase() => _app.Dialogs.Reveal(DatabasePath);

	[RelayCommand]
	private async Task OpenDatabase()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Open an SDE database", false, "SDE database", "*.sqlite");
		if (files.Count == 1)
		{
			OpenPath(files[0]);
		}
	}

	/// <summary>File, then Open Recent.</summary>
	[RelayCommand]
	private void OpenRecent(string path)
	{
		if (!File.Exists(path))
		{
			_app.Dialogs.Notify("Not found", $"\"{Path.GetFileName(path)}\" isn't there any more. It may have been moved or deleted.", isError: true);
			return;
		}
		OpenPath(path);
	}

	private void OpenPath(string path)
	{
		try
		{
			_app.Open(path);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't open " + Path.GetFileName(path), ex);
		}
	}

	[RelayCommand]
	private async Task NewDatabase()
	{
		string path = await _app.Dialogs.SaveFileAsync("Create a new, empty SDE database", "SDE " + DateTime.Today.ToString("yyyy-MM-dd") + ".sqlite", "SDE database", "sqlite");
		if (path == null)
		{
			return;
		}
		if (File.Exists(path))
		{
			_app.Dialogs.Notify("Choose another name", "A file with that name already exists.", isError: true);
			return;
		}
		OpenPath(path);
	}

	/// <summary>
	/// Saves a copy of the database in a new folder, with its photos, documents and map layers (a consistent copy: SQLite
	/// VACUUM INTO). The copy isn't opened.
	/// </summary>
	[RelayCommand]
	private async Task BackUp()
	{
		string folder = await _app.Dialogs.PickFolderAsync("Choose where to save the backup");
		if (folder == null)
		{
			return;
		}
		Sde.Data.SdeDatabase database = _app.Store.Database;
		JobOutcome<string> outcome = await _app.Jobs.RunAsync("Backing up", (progress, ct) =>
		{
			string target = Path.Combine(folder, "SDE backup " + DateTime.Now.ToString("yyyy-MM-dd HHmm"));
			IProgress<(int Done, int Total)> files = new Progress<(int Done, int Total)>(x => progress.Report(new JobProgress(x.Done, x.Total, "Copying photos and documents")));
			Sde.Data.DatabaseFiles.Copy(database, Path.Combine(target, "sde.sqlite"), files);
			return target;
		}, "Couldn't back up", canStop: false);
		if (outcome.Completed)
		{
			_app.Dialogs.Notify("Backup saved", outcome.Value);
			_app.Dialogs.Reveal(outcome.Value);
		}
	}

	/// <summary>
	/// Moves the database, with its photos, documents and map layers, to another folder (a shared drive, say) and opens
	/// it from there.
	/// </summary>
	[RelayCommand]
	private async Task MoveDatabase()
	{
		string current = _app.Store.Database.FilePath;
		string path = await _app.Dialogs.SaveFileAsync("Move the database to", Path.GetFileName(current), "SDE database", "sqlite");
		if (path == null)
		{
			return;
		}
		if (File.Exists(path))
		{
			_app.Dialogs.Notify("Choose another place", "There's already a file with that name there.", isError: true);
			return;
		}
		if (!await _app.Dialogs.ConfirmAsync("Move the database?",
			$"SDE moves \"{Path.GetFileName(current)}\", its photos, documents and map layers to {Path.GetDirectoryName(path)}, then opens it from there.", "Move"))
		{
			return;
		}
		await _app.MoveOrCopyDatabaseAsync(path, move: true);
	}
}
