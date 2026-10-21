using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core;
using Sde.Core.Records;
using Sde.Interop;

namespace Sde.App.ViewModels;

/// <summary>One SDE field and where its value comes from: a column of the file, or the same value for every record.</summary>
public sealed partial class SourceFieldRowViewModel : ObservableObject
{
	public const string NotImported = "(not imported)";

	public SourceFieldRowViewModel(SourceFieldInfo info, string label, SourceImportViewModel owner)
	{
		Info = info;
		Label = label;
		Owner = owner;
		Modes = info.Property is EnterpriseField f ? EnterpriseImportViewModel.ModesFor(f) : null;
		Mode = Modes?[0];
	}

	public SourceFieldInfo Info { get; }

	public string Label { get; }

	public SourceImportViewModel Owner { get; }

	public IReadOnlyList<string> Columns => Owner.FieldColumnChoices;

	public void RefreshColumns()
	{
		OnPropertyChanged(nameof(Columns));
		OnPropertyChanged(nameof(Sample));
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMapped), nameof(HasColumn), nameof(Sample))]
	public partial string Column { get; set; } = NotImported;

	/// <summary>Used for every record (or when the column is blank), e.g. "Flood" or "1.15".</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMapped))]
	public partial string Constant { get; set; }

	public bool HasColumn => Column != null && Column != NotImported;

	public bool IsMapped => HasColumn || !string.IsNullOrWhiteSpace(Constant);

	/// <summary>The first value in the column, so you can see what it holds ("e.g. 3 ft").</summary>
	public string Sample => HasColumn && Owner.SampleFor(Column) is string s ? "e.g. " + s : null;

	public string ConstantHint => Info.Kind switch
	{
		SourceFieldKind.Lookup or SourceFieldKind.Stories => "or always…",
		SourceFieldKind.StructureType => "or always, e.g. Residential",
		SourceFieldKind.Number or SourceFieldKind.Percent => "or always, e.g. 1.15",
		SourceFieldKind.Date => "or always, e.g. 9/14/2026",
		_ => "or always…",
	};

	/// <summary>How an owner-name or address column is split (as in Enterprise Import).</summary>
	public IReadOnlyList<ModeOption> Modes { get; }

	public bool HasModes => Modes != null;

	[ObservableProperty]
	public partial ModeOption Mode { get; set; }

	partial void OnColumnChanged(string value) => Owner.FieldEdited(this);

	partial void OnConstantChanged(string value) => Owner.FieldEdited(this);

	partial void OnModeChanged(ModeOption value) => Owner.FieldEdited(this);
}

/// <summary>The fields of one editor step, shown as a collapsible section.</summary>
public sealed partial class SourceFieldGroupViewModel(string title, IReadOnlyList<SourceFieldRowViewModel> rows) : ObservableObject
{
	public string Title { get; } = title;

	public IReadOnlyList<SourceFieldRowViewModel> Rows { get; } = rows;

	[ObservableProperty]
	public partial bool IsExpanded { get; set; }

	/// <summary>Whether the group's fields belong to the structure, so they're used when importing structures.</summary>
	public bool AppliesToStructures => Rows.All(r => r.Info.AppliesToStructures);

	/// <summary>Hidden when importing structures and the fields are about an inspection.</summary>
	[ObservableProperty]
	public partial bool IsShown { get; set; } = true;

	public string Header => $"{Title} · {Rows.Count(r => r.IsMapped)} of {Rows.Count} from the file";

	public void Refresh() => OnPropertyChanged(nameof(Header));
}

/// <summary>One value of the filter column (e.g. an inspection type), ticked to import it.</summary>
public sealed partial class SourceFilterValueViewModel(string value, int count, bool include, Action changed) : ObservableObject
{
	public string Value { get; } = value;

	public string Label { get; } = $"{value} ({count})";

	[ObservableProperty]
	public partial bool Include { get; set; } = include;

	partial void OnIncludeChanged(bool value) => changed();
}

/// <summary>Text in the file the import can't use as it is, and what it should mean in SDE.</summary>
public sealed partial class SourceValueRowViewModel : ObservableObject
{
	public const string LeaveAsIs = "(leave as it was)";
	private readonly Action<SourceValueRowViewModel> _changed;
	private readonly bool _loading;

	public SourceValueRowViewModel(SourceFieldInfo field, string label, string text, int count, IReadOnlyList<string> choices, string translation, Action<SourceValueRowViewModel> changed)
	{
		_loading = true;
		Field = field;
		FieldLabel = label;
		Text = text;
		CountText = count switch { 0 => "not in this file", 1 => "1 record", _ => $"{count} records" };
		Choices = choices == null ? null : new[] { LeaveAsIs }.Concat(choices).ToList();
		Choice = Choices == null ? null : translation ?? LeaveAsIs;
		Number = Choices == null ? translation : null;
		_changed = changed;
		_loading = false;
	}

	public SourceFieldInfo Field { get; }

	public string FieldLabel { get; }

	/// <summary>The text in the file.</summary>
	public string Text { get; }

	public string CountText { get; }

	public string AccessibleName => $"{FieldLabel}: what “{Text}” means in SDE";

	/// <summary>FEMA's choices for the field, or null when it takes a number.</summary>
	public IReadOnlyList<string> Choices { get; }

	public bool HasChoices => Choices != null;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMatched))]
	public partial string Choice { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMatched))]
	public partial string Number { get; set; }

	public bool IsMatched => HasChoices ? Choice is not null and not LeaveAsIs : !string.IsNullOrWhiteSpace(Number);

	/// <summary>The translation to save, or null to remove it.</summary>
	public string Translation => HasChoices ? (IsMatched ? Choice : null) : string.IsNullOrWhiteSpace(Number) ? null : Number.Trim();

	partial void OnChoiceChanged(string value)
	{
		if (!_loading)
		{
			_changed(this);
		}
	}

	partial void OnNumberChanged(string value)
	{
		if (!_loading)
		{
			_changed(this);
		}
	}
}

/// <summary>One record as it would be imported.</summary>
public sealed partial class SourcePreviewRowViewModel(ImportRow row, ReferenceData reference) : ObservableObject
{
	public ImportRow Row { get; } = row;

	public string Record => Row.RecordId == null ? "(no record ID)" : Row.InspectionId is { Length: > 0 } i ? $"{Row.RecordId} · {i}" : Row.RecordId;

	public string Address
	{
		get
		{
			if (Row.File?.Property is not PropertyRecord p)
			{
				return "";
			}
			string street = AssessmentEditorViewModel.Address(p, reference, "(no address)");
			return string.IsNullOrWhiteSpace(p.City) ? street : street + ", " + p.City;
		}
	}

	public string Status => Row.IsLocked ? "Final, won't change" : Row.Status switch
	{
		ImportRowStatus.NewStructure => "New structure",
		ImportRowStatus.NewInspection => "New inspection",
		ImportRowStatus.Attach => Row.File == null ? "Link to existing structure" : "Add to existing structure",
		ImportRowStatus.Update => Row.Changes.Count == 1 ? "Update 1 field" : $"Update {Row.Changes.Count} fields",
		ImportRowStatus.Unchanged => "Unchanged",
		ImportRowStatus.Ambiguous => "Ambiguous",
		_ => "Can't import",
	};

	/// <summary>A structures import has no assessment, so no result.</summary>
	public bool IsStructureOnly => Row.File == null && Row.Property != null;

	public ResultState State => !Row.CanImport || Row.DamageMessage is null or "Data Entry Incomplete" ? ResultState.Incomplete
		: Row.DamageMessage is "Substantially Damaged" or "Not Substantially Damaged"
			? (Row.PercentDamage >= 50m ? ResultState.Substantial : Row.PercentDamage >= 40m ? ResultState.Close : ResultState.NotSubstantial)
		: ResultState.Undetermined;

	public string Result => IsStructureOnly && Row.CanImport ? "Structure only"
		: !Row.CanImport ? "–"
		: State is ResultState.Incomplete or ResultState.Undetermined ? Row.DamageMessage ?? "Data Entry Incomplete"
		: $"{Row.PercentDamage:0.0}% · {Row.DamageMessage}";

	/// <summary>Why it can't be imported, or what changes, what was left out and what is still missing.</summary>
	public string Note
	{
		get
		{
			if (Row.IsLocked)
			{
				return "Final: reopen it in SDE to take these changes · " + string.Join(" · ", Row.Changes);
			}
			if (!Row.CanImport)
			{
				return string.Join(" ", Row.Problems);
			}
			List<string> parts = new(Row.Changes);
			parts.AddRange(Row.Warnings);
			if (Row.MissingRequired > 0)
			{
				parts.Add(Row.MissingRequired == 1 ? "1 required field to fill in" : $"{Row.MissingRequired} required fields to fill in");
			}
			return string.Join(" · ", parts);
		}
	}

	public bool IsProblem => !Row.CanImport;

	public bool CanInclude => Row.CanImport && Row.Status != ImportRowStatus.Unchanged;

	[ObservableProperty]
	public partial bool Include { get; set; } = row.Selected && row.Status != ImportRowStatus.Unchanged;
}

/// <summary>
/// Import inspections or parcels (a sub-page of Import &amp; export): choose the file, name its source, say what each
/// record becomes (an assessment, or just a structure) and how new records find their structure, say how the file is
/// laid out, map the fields (with a saved profile per source), match the file's words to FEMA's choices, preview what
/// each record will do, then import and review the result in the Structures list. Wraps <see cref="SourceImport"/>.
/// </summary>
public sealed partial class SourceImportViewModel : ViewModelBase
{
	public const string None = "(none)";
	public const int MaxPreviewRows = 200;

	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private readonly DispatcherTimer _previewTimer;
	private SourceProfile _profile = new();
	private SourceTable _raw;
	private SourceShaped _shaped;
	private SourcePreview _preview;
	private CancellationTokenSource _previewCancel;
	private bool _suspend;

	public SourceImportViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
		_previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
		_previewTimer.Tick += async (_, _) => await RefreshPreviewAsync();
		ReferenceData reference = app.Store.Reference;
		Groups = SourceFields.Groups.Select(g => new SourceFieldGroupViewModel(g, SourceFields.All.Where(f => f.Group == g)
			.Select(f => new SourceFieldRowViewModel(f, f.CustomField > 0 ? CustomFieldName(reference, f.CustomField) : f.Label, this)).ToList())).ToList();
		ReloadProfiles();
		ExampleProfiles = ShippedProfiles.Names;
	}

	public IReadOnlyList<SourceFieldGroupViewModel> Groups { get; }

	private IEnumerable<SourceFieldRowViewModel> AllRows => Groups.SelectMany(g => g.Rows);

	[RelayCommand]
	private void Back() => _main.ShowCommand.Execute(Page.ImportExport);

	// ───────────── File ─────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasFile), nameof(FileName))]
	public partial string FilePath { get; set; }

	public bool HasFile => FilePath != null;

	public string FileName => Path.GetFileName(FilePath);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSeveralSheets))]
	public partial IReadOnlyList<string> SheetNames { get; set; } = Array.Empty<string>();

	public bool HasSeveralSheets => SheetNames.Count > 1;

	[ObservableProperty]
	public partial string SelectedSheet { get; set; }

	[ObservableProperty]
	public partial string TableSummary { get; set; }

	partial void OnSelectedSheetChanged(string value)
	{
		if (!_suspend)
		{
			LoadSheet();
		}
	}

	[RelayCommand]
	private async Task ChooseFile()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Choose a file to import", false, "Spreadsheet", "*.xlsx", "*.xlsm", "*.xls", "*.csv", "*.dbf", "*.shp");
		if (files.Count == 1)
		{
			OpenFile(files[0]);
		}
	}

	/// <summary>Opens a file: the last-used profile if it fits the file, otherwise a guessed one. Also used by tests.</summary>
	public void OpenFile(string path)
	{
		IReadOnlyList<string> sheets;
		try
		{
			sheets = SourceTable.SheetNames(path);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// ClosedXML and the CSV reader throw various exceptions for files they can't read.
			_app.Dialogs.Notify("Can't read that file", ex.Message, isError: true);
			return;
		}
		_suspend = true;
		FilePath = path;
		SheetNames = sheets;
		SelectedSheet = sheets.FirstOrDefault();
		_suspend = false;
		LastResult = null;
		LoadSheet();
	}

	private void LoadSheet()
	{
		if (FilePath == null)
		{
			return;
		}
		try
		{
			_raw = SourceTable.Read(FilePath, SelectedSheet);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Dialogs.Notify("Can't read that sheet", ex.Message, isError: true);
			return;
		}
		RawColumns = new[] { None }.Concat(_raw.Columns).ToList();
		string last = _app.Settings.LastImportProfile;
		SourceProfile saved = _app.ImportProfiles.Exists(last) ? LoadProfile(last) : null;
		if (saved != null && _raw.IndexOf(saved.RecordIdColumn) >= 0)
		{
			ApplyProfile(saved);
			return;
		}
		SourceProfile guessed = SourceFields.Guess(_raw);
		guessed.Name = UnusedProfileName();
		ApplyProfile(guessed);
		ProfileNotes = saved == null ? null
			: $"Your profile “{saved.Name}” doesn't fit this file (it has no “{saved.RecordIdColumn}” column), so the columns were guessed. Choose a saved profile above to use one anyway.";
	}

	[RelayCommand]
	private void Close()
	{
		_previewTimer.Stop();
		_previewCancel?.Cancel();
		FilePath = null;
		_raw = null;
		_shaped = null;
		_preview = null;
		SheetNames = Array.Empty<string>();
		RawColumns = new[] { None };
		FieldColumnChoices = new[] { SourceFieldRowViewModel.NotImported };
		PreviewRows.Clear();
		Values.Clear();
		FilterValues.Clear();
		MappingProblems = PreviewSummary = ShapeNotes = TableSummary = BuildingValueQuestion = null;
		CanImport = false;
		OnPropertyChanged(nameof(HasValues));
	}

	/// <summary>A different database was opened: the preview (what's new, what's linked) must be worked out again.</summary>
	public void DatabaseChanged()
	{
		LastResult = null;
		LastImportIds = null;
		if (HasFile)
		{
			SchedulePreview();
		}
	}

	// ───────────── Profiles ─────────────

	public ObservableCollection<string> SavedProfiles { get; } = new();

	[ObservableProperty]
	public partial string SelectedProfile { get; set; }

	/// <summary>The name the profile is saved under (on import, or with Save profile).</summary>
	[ObservableProperty]
	public partial string ProfileName { get; set; } = ImportProfiles.DefaultName;

	[ObservableProperty]
	public partial string ProfileNotes { get; set; }

	public string ProfilesFolder => _app.ImportProfiles.Folder;

	/// <summary>The example profiles that come with SDE (see <see cref="ShippedProfiles"/>).</summary>
	public IReadOnlyList<string> ExampleProfiles { get; }

	/// <summary>Picking an example applies it to the open file; importing (or Save profile) keeps a copy under its name.</summary>
	[ObservableProperty]
	public partial string SelectedExample { get; set; }

	partial void OnSelectedExampleChanged(string value)
	{
		if (!_suspend && value != null && _raw != null && ShippedProfiles.Get(value) is SourceProfile p)
		{
			ApplyProfile(p);
			ProfileNotes = $"Started from the example “{value}”. Check each column against your file; the profile is saved under its name when you import.";
		}
	}

	partial void OnSelectedProfileChanged(string value)
	{
		if (!_suspend && value != null && _raw != null && LoadProfile(value) is SourceProfile p)
		{
			ApplyProfile(p);
		}
	}

	partial void OnProfileNameChanged(string value)
	{
		if (!_suspend)
		{
			_profile.Name = value;
		}
	}

	public void ReloadProfiles()
	{
		_suspend = true;
		SavedProfiles.Clear();
		foreach (string n in _app.ImportProfiles.Names())
		{
			SavedProfiles.Add(n);
		}
		_suspend = false;
	}

	private SourceProfile LoadProfile(string name)
	{
		try
		{
			SourceProfile p = _app.ImportProfiles.Load(name, out List<string> warnings);
			ProfileNotes = warnings.Count > 0 ? string.Join(" ", warnings) : null;
			return p;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			_app.Dialogs.Notify("Can't read that profile", ex.Message, isError: true);
			return null;
		}
	}

	[RelayCommand]
	private void SaveProfile()
	{
		string name = SaveCurrentProfile();
		_app.Dialogs.Notify("Profile saved", $"“{name}” will be used for the next file you open with the same record ID column.");
	}

	private string SaveCurrentProfile()
	{
		_profile.Name = ProfileName;
		string name = _app.ImportProfiles.Save(_profile.Clone());
		_app.Settings.LastImportProfile = name;
		_app.Settings.Save();
		ReloadProfiles();
		_suspend = true;
		ProfileName = name;
		SelectedProfile = name;
		_suspend = false;
		return name;
	}

	[RelayCommand]
	private async Task DeleteProfile()
	{
		if (SelectedProfile is not string name || !await _app.Dialogs.ConfirmAsync("Delete this profile?", name, "Delete", destructive: true))
		{
			return;
		}
		_app.ImportProfiles.Delete(name);
		ReloadProfiles();
		_suspend = true;
		SelectedProfile = null;
		_suspend = false;
	}

	[RelayCommand]
	private async Task ImportProfile()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Choose an import profile someone shared", false, "Import profile", "*.json");
		if (files.Count != 1)
		{
			return;
		}
		try
		{
			string name = _app.ImportProfiles.Import(files[0], out List<string> warnings);
			ReloadProfiles();
			ProfileNotes = warnings.Count > 0 ? string.Join(" ", warnings) : null;
			SelectedProfile = name;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			_app.Dialogs.Notify("Can't use that profile", ex.Message, isError: true);
		}
	}

	[RelayCommand]
	private void ShowProfilesFolder()
	{
		Directory.CreateDirectory(ProfilesFolder);
		_app.Dialogs.Reveal(ProfilesFolder);
	}

	/// <summary>Shows a profile on screen and works out the export with it.</summary>
	private void ApplyProfile(SourceProfile p)
	{
		_profile = p;
		_suspend = true;
		ProfileName = p.Name;
		SelectedProfile = SavedProfiles.Contains(p.Name) ? p.Name : null;
		SelectedExample = null;
		SourceName = p.SourceName;
		ModeIndex = (int)p.Mode;
		MatchByIndex = (int)p.MatchBy;
		ParcelIgnoreLeadingZeros = p.ParcelIgnoreLeadingZeros;
		AddressMatchUnit = p.AddressMatchUnit;
		LayoutIndex = (int)p.Layout;
		RecordIdColumn = Choice(p.RecordIdColumn);
		InspectionIdColumn = Choice(p.InspectionIdColumn);
		ItemColumn = Choice(p.ItemColumn);
		ValueColumn = Choice(p.ValueColumn);
		FilterColumn = Choice(p.FilterColumn);
		UseDefaultValues = p.UseDefaultValues;
		RecordIdCustomField = p.RecordIdCustomField;
		_suspend = false;
		Reshape();
		_suspend = true;
		foreach (SourceFieldRowViewModel r in AllRows)
		{
			SourceFieldMap m = p.Fields.GetValueOrDefault(r.Info.Key);
			r.Column = m?.Column is string c && FieldColumnChoices.Contains(c) ? c : SourceFieldRowViewModel.NotImported;
			r.Constant = m?.Constant;
			if (r.HasModes)
			{
				int mode = r.Info.Property switch
				{
					EnterpriseField.OwnerFirstName => (int)p.FirstNameParsing,
					EnterpriseField.OwnerLastName => (int)p.LastNameParsing,
					EnterpriseField.StreetNumber => (int)p.StreetNumberParsing,
					EnterpriseField.StreetName => (int)p.StreetNameParsing,
					_ => (int)p.SuffixParsing,
				};
				if (mode == 0 && r.Info.Property == EnterpriseField.StreetName)
				{
					mode = (int)StreetNameParsing.Name; // "as is" is the plain name
				}
				r.Mode = r.Modes.FirstOrDefault(o => o.Value == mode) ?? r.Modes[0];
			}
		}
		_suspend = false;
		foreach (SourceFieldRowViewModel r in AllRows)
		{
			WriteField(r);
		}
		foreach (SourceFieldGroupViewModel g in Groups)
		{
			g.Refresh();
			g.IsExpanded = g.Rows.Any(r => r.IsMapped);
		}
		ShowGroupsForMode();
		SchedulePreview();
	}

	// ───────────── Source, mode and matching ─────────────

	/// <summary>The system or list the file comes from ("Permits", "County assessor"); written to every link.</summary>
	[ObservableProperty]
	public partial string SourceName { get; set; } = SourceProfile.DefaultSourceName;

	/// <summary>0 = each record becomes an assessment (inspections), 1 = a structure (parcels).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsStructuresMode), nameof(IsInspectionsMode), nameof(RecordWord))]
	public partial int ModeIndex { get; set; }

	public bool IsStructuresMode => ModeIndex == (int)SourceMode.Structures;

	public bool IsInspectionsMode => !IsStructuresMode;

	/// <summary>0 = record ID only, 1 = parcel number, 2 = address.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMatchByParcel), nameof(IsMatchByAddress))]
	public partial int MatchByIndex { get; set; }

	public bool IsMatchByParcel => MatchByIndex == (int)SourceMatchBy.Parcel;

	public bool IsMatchByAddress => MatchByIndex == (int)SourceMatchBy.Address;

	[ObservableProperty]
	public partial bool ParcelIgnoreLeadingZeros { get; set; }

	[ObservableProperty]
	public partial bool AddressMatchUnit { get; set; }

	/// <summary>"inspection" or "structure", for the wording of counts and the Import button.</summary>
	public string RecordWord => IsStructuresMode ? "structure" : "inspection";

	partial void OnSourceNameChanged(string value) => SourceEdited();

	partial void OnModeIndexChanged(int value)
	{
		SourceEdited();
		ShowGroupsForMode();
	}

	partial void OnMatchByIndexChanged(int value) => SourceEdited();

	partial void OnParcelIgnoreLeadingZerosChanged(bool value) => SourceEdited();

	partial void OnAddressMatchUnitChanged(bool value) => SourceEdited();

	private void SourceEdited()
	{
		if (_suspend)
		{
			return;
		}
		_profile.SourceName = SourceName?.Trim();
		_profile.Mode = (SourceMode)Math.Max(0, ModeIndex);
		_profile.MatchBy = (SourceMatchBy)Math.Max(0, MatchByIndex);
		_profile.ParcelIgnoreLeadingZeros = ParcelIgnoreLeadingZeros;
		_profile.AddressMatchUnit = AddressMatchUnit;
		SchedulePreview();
	}

	/// <summary>Importing structures uses only the structure's fields, so the other groups are hidden.</summary>
	private void ShowGroupsForMode()
	{
		foreach (SourceFieldGroupViewModel g in Groups)
		{
			g.IsShown = !IsStructuresMode || g.AppliesToStructures;
		}
	}

	// ───────────── Layout ─────────────

	/// <summary>"(none)" plus the file's own columns (before checklist items are combined).</summary>
	[ObservableProperty]
	public partial IReadOnlyList<string> RawColumns { get; set; } = new[] { None };

	/// <summary>0 = one row per record, 1 = one row per checklist item.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsChecklistLayout))]
	public partial int LayoutIndex { get; set; }

	public bool IsChecklistLayout => LayoutIndex == 1;

	[ObservableProperty]
	public partial string RecordIdColumn { get; set; } = None;

	[ObservableProperty]
	public partial string InspectionIdColumn { get; set; } = None;

	[ObservableProperty]
	public partial string ItemColumn { get; set; } = None;

	[ObservableProperty]
	public partial string ValueColumn { get; set; } = None;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasFilter))]
	public partial string FilterColumn { get; set; } = None;

	public bool HasFilter => FilterColumn is not null and not None;

	public ObservableCollection<SourceFilterValueViewModel> FilterValues { get; } = new();

	partial void OnLayoutIndexChanged(int value) => LayoutEdited();

	partial void OnRecordIdColumnChanged(string value) => LayoutEdited();

	partial void OnInspectionIdColumnChanged(string value) => LayoutEdited();

	partial void OnItemColumnChanged(string value) => LayoutEdited();

	partial void OnValueColumnChanged(string value) => LayoutEdited();

	partial void OnFilterColumnChanged(string value)
	{
		if (!_suspend)
		{
			_profile.FilterValues.Clear();
		}
		LayoutEdited();
	}

	private void LayoutEdited()
	{
		if (_suspend)
		{
			return;
		}
		_profile.Layout = (SourceLayout)LayoutIndex;
		_profile.RecordIdColumn = Real(RecordIdColumn);
		_profile.InspectionIdColumn = Real(InspectionIdColumn);
		_profile.ItemColumn = Real(ItemColumn);
		_profile.ValueColumn = Real(ValueColumn);
		_profile.FilterColumn = Real(FilterColumn);
		Reshape();
		SchedulePreview();
	}

	/// <summary>Filters and combines the file with the current layout, and refreshes the column lists.</summary>
	private void Reshape()
	{
		if (_raw == null)
		{
			return;
		}
		_shaped = SourceTable.Shape(_raw, _profile);
		FieldColumnChoices = new[] { SourceFieldRowViewModel.NotImported }.Concat(_shaped.Table.Columns).ToList();
		bool wasSuspended = _suspend;
		_suspend = true;
		foreach (SourceFieldRowViewModel r in AllRows)
		{
			r.RefreshColumns();
			if (r.HasColumn && !FieldColumnChoices.Contains(r.Column))
			{
				r.Column = SourceFieldRowViewModel.NotImported;
				WriteField(r);
			}
		}
		_suspend = wasSuspended;

		FilterValues.Clear();
		if (_profile.FilterColumn != null)
		{
			HashSet<string> chosen = _profile.FilterValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach ((string value, int count) in SourceTable.DistinctValues(_raw, _profile.FilterColumn, 30))
			{
				FilterValues.Add(new SourceFilterValueViewModel(value, count, chosen.Contains(value), FilterEdited));
			}
		}
		int records = _shaped.Table.Rows.Count;
		TableSummary = $"{_raw.Rows.Count} row{(_raw.Rows.Count == 1 ? "" : "s")} · {_raw.Columns.Count} columns"
			+ (_profile.Layout == SourceLayout.OneRowPerChecklistItem || _shaped.FilteredOut > 0 ? $" → {records} {Plural(records, RecordWord)}" : "")
			+ (_shaped.FilteredOut > 0 ? $" ({_shaped.FilteredOut} left out by the filter)" : "");
	}

	private void FilterEdited()
	{
		_profile.FilterValues = FilterValues.Where(v => v.Include).Select(v => v.Value).ToList();
		_shaped = SourceTable.Shape(_raw, _profile);
		int records = _shaped.Table.Rows.Count;
		TableSummary = $"{_raw.Rows.Count} row{(_raw.Rows.Count == 1 ? "" : "s")} · {_raw.Columns.Count} columns → {records} {Plural(records, RecordWord)}"
			+ (_shaped.FilteredOut > 0 ? $" ({_shaped.FilteredOut} left out by the filter)" : "");
		SchedulePreview();
	}

	// ───────────── Fields ─────────────

	/// <summary>"(not imported)" plus the columns after checklist items are combined.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<string> FieldColumnChoices { get; set; } = new[] { SourceFieldRowViewModel.NotImported };

	[ObservableProperty]
	public partial bool UseDefaultValues { get; set; } = true;

	/// <summary>0 = don't copy the record ID; 1–3 = the assessment custom field that gets it.</summary>
	[ObservableProperty]
	public partial int RecordIdCustomField { get; set; }

	public IReadOnlyList<string> CustomFieldChoices =>
		new[] { "Don't copy it" }.Concat(Enumerable.Range(1, 3).Select(z => CustomFieldName(_app.Store.Reference, z))).ToList();

	partial void OnUseDefaultValuesChanged(bool value)
	{
		if (!_suspend)
		{
			_profile.UseDefaultValues = value;
			SchedulePreview();
		}
	}

	partial void OnRecordIdCustomFieldChanged(int value)
	{
		if (!_suspend)
		{
			_profile.RecordIdCustomField = Math.Max(0, value);
			SchedulePreview();
		}
	}

	/// <summary>A field row changed: store it in the profile and preview again.</summary>
	public void FieldEdited(SourceFieldRowViewModel row)
	{
		if (_suspend || Groups == null) // Groups is null while the rows are being created
		{
			return;
		}
		WriteField(row);
		if (row.Info.Key == "TaxAssessedValue")
		{
			_profile.ValueIsBuildingOnly = false; // a different value: ask again
		}
		Groups.First(g => g.Rows.Contains(row)).Refresh();
		SchedulePreview();
	}

	private void WriteField(SourceFieldRowViewModel r)
	{
		SourceFieldMap m = _profile.Map(r.Info.Key);
		m.Column = r.HasColumn ? r.Column : null;
		m.Constant = string.IsNullOrWhiteSpace(r.Constant) ? null : r.Constant.Trim();
		if (r.HasModes && r.Mode != null)
		{
			switch (r.Info.Property)
			{
				case EnterpriseField.OwnerFirstName: _profile.FirstNameParsing = (NameParsing)r.Mode.Value; break;
				case EnterpriseField.OwnerLastName: _profile.LastNameParsing = (NameParsing)r.Mode.Value; break;
				case EnterpriseField.StreetNumber: _profile.StreetNumberParsing = (StreetNumberParsing)r.Mode.Value; break;
				case EnterpriseField.StreetName: _profile.StreetNameParsing = (StreetNameParsing)r.Mode.Value; break;
				case EnterpriseField.StreetSuffix: _profile.SuffixParsing = (SuffixParsing)r.Mode.Value; break;
			}
		}
	}

	/// <summary>The first non-blank value of a column (after combining), for the "e.g." hint.</summary>
	public string SampleFor(string column)
	{
		int i = _shaped?.Table.IndexOf(column) ?? -1;
		return i < 0 ? null : _shaped.Table.Rows.Select(r => r[i]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
	}

	/// <summary>Collapses every field group when any is open (to get to the preview), otherwise opens them all.</summary>
	[RelayCommand]
	private void ExpandAll()
	{
		bool expand = Groups.All(g => !g.IsExpanded);
		foreach (SourceFieldGroupViewModel g in Groups)
		{
			g.IsExpanded = expand;
		}
	}

	// ───────────── Values to match ─────────────

	public ObservableCollection<SourceValueRowViewModel> Values { get; } = new();

	public bool HasValues => Values.Count > 0;

	[ObservableProperty]
	public partial string ValuesSummary { get; set; }

	private void ValueEdited(SourceValueRowViewModel row)
	{
		Dictionary<string, string> translate = _profile.Map(row.Field.Key).Translate;
		if (row.Translation is string t)
		{
			translate[row.Text] = t;
		}
		else
		{
			translate.Remove(row.Text);
		}
		SchedulePreview();
	}

	/// <summary>Values that need matching, then the ones already translated (so they can be changed), in field order.</summary>
	private void ShowValues(SourcePreview preview)
	{
		ReferenceData reference = _app.Store.Reference;
		Dictionary<(string, string), int> counts = new();
		foreach (SourceFieldInfo f in SourceFields.All.Where(f => f.Translatable))
		{
			if (_profile[f.Key] is SourceFieldMap m && m.Column != null && m.Translate.Count > 0)
			{
				foreach ((string value, int count) in SourceTable.DistinctValues(_shaped.Table, m.Column, int.MaxValue))
				{
					counts[(f.Key, value.ToLowerInvariant())] = count;
				}
			}
		}
		List<SourceValueRowViewModel> rows = new();
		foreach (SourceFieldInfo f in SourceFields.All.Where(f => f.Translatable))
		{
			SourceFieldMap map = _profile[f.Key];
			if (map == null)
			{
				continue;
			}
			string label = AllRows.First(r => r.Info == f).Label;
			IReadOnlyList<string> choices = SourceValues.Choices(f, reference);
			List<SourceUnmatched> unmatched = preview.Unmatched.Where(u => u.FieldKey == f.Key).ToList();
			foreach (SourceUnmatched u in unmatched)
			{
				string translation = map.Translate.GetValueOrDefault(u.Text);
				rows.Add(new SourceValueRowViewModel(f, label, u.Text, u.Count, choices, choices == null || choices.Contains(translation) ? translation : null, ValueEdited));
			}
			foreach ((string text, string translation) in map.Translate.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (unmatched.Any(u => string.Equals(u.Text, text, StringComparison.OrdinalIgnoreCase)))
				{
					continue; // translated to something unusable: shown above as still to match
				}
				string shown = choices == null || choices.Contains(translation) ? translation : null;
				rows.Add(new SourceValueRowViewModel(f, label, text, counts.GetValueOrDefault((f.Key, text.ToLowerInvariant())), choices, shown, ValueEdited));
			}
		}
		Values.Clear();
		foreach (SourceValueRowViewModel r in rows)
		{
			Values.Add(r);
		}
		int open = preview.Unmatched.Count;
		ValuesSummary = open == 0
			? "Every value in the file matches one of SDE's choices."
			: $"{open} value{(open == 1 ? " in the file doesn't" : "s in the file don't")} match SDE's choices. Pick what each means; until then that field is left as it was.";
		OnPropertyChanged(nameof(HasValues));
	}

	// ───────────── Preview ─────────────

	public ObservableCollection<SourcePreviewRowViewModel> PreviewRows { get; } = new();

	[ObservableProperty]
	public partial string PreviewSummary { get; set; }

	[ObservableProperty]
	public partial string MappingProblems { get; set; }

	/// <summary>Notes from combining checklist rows (e.g. an item answered twice) and about fields the mode doesn't use.</summary>
	[ObservableProperty]
	public partial string ShapeNotes { get; set; }

	[ObservableProperty]
	public partial bool IsBusy { get; set; }

	[ObservableProperty]
	public partial bool CanImport { get; set; }

	[ObservableProperty]
	public partial string ImportText { get; set; } = "Import";

	private void SchedulePreview()
	{
		if (_suspend || _shaped == null)
		{
			return;
		}
		IsBusy = true;
		_previewTimer.Stop();
		_previewTimer.Start();
	}

	/// <summary>Works out every record with the current settings (in the background) and shows the result.</summary>
	public async Task RefreshPreviewAsync()
	{
		_previewTimer.Stop();
		if (_shaped == null)
		{
			return;
		}
		_previewCancel?.Cancel();
		CancellationTokenSource cancel = _previewCancel = new CancellationTokenSource();
		SourceProfile profile = _profile.Clone();
		SourceShaped shaped = _shaped;
		IsBusy = true;
		SourcePreview preview;
		try
		{
			preview = await Task.Run(() => SourceImport.Preview(shaped, profile, _app.Store, cancel.Token), cancel.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			if (cancel == _previewCancel)
			{
				AppLog.Write("Couldn't work out the import preview", ex);
				IsBusy = false;
				_preview = null;
				MappingProblems = "Couldn't work out the import: " + ex.Message;
				CanImport = false;
			}
			return;
		}
		if (cancel != _previewCancel)
		{
			return; // a newer preview is on its way
		}
		IsBusy = false;
		ShowPreview(preview);
	}

	private void ShowPreview(SourcePreview preview)
	{
		_preview = preview;
		ReferenceData reference = _app.Store.Reference;
		PreviewRows.Clear();
		foreach (ImportRow r in preview.Rows.Take(MaxPreviewRows))
		{
			SourcePreviewRowViewModel vm = new SourcePreviewRowViewModel(r, reference);
			vm.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(SourcePreviewRowViewModel.Include))
				{
					UpdateImportText();
				}
			};
			PreviewRows.Add(vm);
		}
		List<string> problems = preview.MappingProblems.ToList();
		MappingProblems = problems.Count > 0 ? string.Join("\n", problems) : null;
		List<string> notes = preview.Notes.Concat(preview.Shaped.Warnings.Take(10)).ToList();
		if (preview.Shaped.Warnings.Count > 10)
		{
			notes.Add($"…and {preview.Shaped.Warnings.Count - 10} more");
		}
		ShapeNotes = notes.Count > 0 ? string.Join("\n", notes) : null;
		BuildingValueQuestion = preview.NeedsBuildingValueConfirmation
			? $"Is {preview.BuildingValueSource} the value of the building only, without the land?"
			: null;
		List<string> parts = new();
		void Part(ImportRowStatus s, string one, string many)
		{
			int n = preview.Count(s);
			if (n > 0)
			{
				parts.Add(n == 1 ? "1 " + one : $"{n} {many}");
			}
		}
		Part(ImportRowStatus.NewStructure, "new structure", "new structures");
		Part(ImportRowStatus.NewInspection, "new inspection of a known structure", "new inspections of known structures");
		if (IsStructuresMode)
		{
			Part(ImportRowStatus.Attach, "linked to an existing structure", "linked to existing structures");
		}
		else
		{
			Part(ImportRowStatus.Attach, "added to an existing structure", "added to existing structures");
		}
		int locked = preview.LockedCount;
		int updates = preview.Count(ImportRowStatus.Update) - locked;
		if (updates > 0)
		{
			parts.Add(updates == 1 ? "1 update" : $"{updates} updates");
		}
		if (locked > 0)
		{
			parts.Add(locked == 1 ? "1 Final, won't change" : $"{locked} Final, won't change");
		}
		Part(ImportRowStatus.Unchanged, "unchanged", "unchanged");
		Part(ImportRowStatus.Ambiguous, "ambiguous match", "ambiguous matches");
		Part(ImportRowStatus.Problem, "can't be imported", "can't be imported");
		PreviewSummary = (parts.Count == 0 ? (IsStructuresMode ? "No structures" : "No inspections") : string.Join(" · ", parts))
			+ (preview.Rows.Count > MaxPreviewRows ? $" · showing the first {MaxPreviewRows}" : "");
		ShowValues(preview);
		UpdateImportText();
	}

	private void UpdateImportText()
	{
		if (_preview == null || _preview.MappingProblems.Count > 0)
		{
			CanImport = false;
			ImportText = "Import";
			return;
		}
		int shown = PreviewRows.Count(r => r.Include && r.CanInclude);
		int beyond = _preview.Rows.Skip(MaxPreviewRows).Count(r => r.Selected && r.CanImport && r.Status != ImportRowStatus.Unchanged);
		int n = shown + beyond;
		CanImport = n > 0;
		ImportText = n == 0 ? "Nothing to import" : $"Import {n} {Plural(n, RecordWord)}";
	}

	/// <summary>Set when the building value needs the person's word that it excludes the land (see <see cref="ConfirmBuildingValue"/>).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(NeedsBuildingValueConfirmation))]
	public partial string BuildingValueQuestion { get; set; }

	public bool NeedsBuildingValueConfirmation => BuildingValueQuestion != null;

	/// <summary>
	/// Flag 4: asks whether the value used as the building value (Adj. Tax Assessed Value) is the building only. FEMA's
	/// market value never includes the land, and an assessor's total value would make every percentage too low.
	/// </summary>
	[RelayCommand]
	private async Task ConfirmBuildingValue()
	{
		if (_preview is not { NeedsBuildingValueConfirmation: true } preview)
		{
			return;
		}
		bool yes = await _app.Dialogs.ConfirmAsync("Is this the building value only?",
			$"SDE will use {preview.BuildingValueSource} as the value of the building (Adj. Tax Assessed Value). FEMA counts the "
			+ "building only, never the land, so a total of land and building would make every percent damaged too low. "
			+ "Choose “Building only” only if it is the building (improvement) value.", "Building only");
		if (!yes)
		{
			return;
		}
		_profile.ValueIsBuildingOnly = true;
		await RefreshPreviewAsync();
	}

	[RelayCommand]
	private async Task Import()
	{
		if (_previewTimer.IsEnabled || _preview == null)
		{
			await RefreshPreviewAsync();
		}
		if (_preview is { NeedsBuildingValueConfirmation: true })
		{
			await ConfirmBuildingValue();
		}
		if (_preview == null || !CanImport)
		{
			return;
		}
		Dictionary<int, bool> include = PreviewRows.ToDictionary(r => r.Row.RowNumber, r => r.Include && r.CanInclude);
		int created = _preview.Rows.Count(r => r.Selected && r.CanImport && include.GetValueOrDefault(r.RowNumber, true) && r.Status is not (ImportRowStatus.Update or ImportRowStatus.Unchanged));
		int updated = _preview.Rows.Count(r => r.Selected && r.CanImport && include.GetValueOrDefault(r.RowNumber, true) && r.Status == ImportRowStatus.Update);
		string what = IsStructuresMode ? "structure" : "assessment";
		string message = string.Join(" and ", new[]
		{
			created > 0 ? $"{created} {Plural(created, IsStructuresMode ? "structure" : "new assessment")} will be {(IsStructuresMode ? "created or linked" : "created")}" : null,
			updated > 0 ? $"{updated} {Plural(updated, what)} will be updated with what changed in {_profile.EffectiveSourceName}" : null,
		}.Where(x => x != null)) + ".";
		if (!await _app.Dialogs.ConfirmAsync(IsStructuresMode ? "Import structures?" : "Import inspections?", message, "Import"))
		{
			return;
		}
		string source = FilePath;
		JobOutcome<SourceImportResult> outcome = await _app.Writes.WriteAsync(null, async () =>
		{
			// The editor has just been saved: work the preview out again so nothing it saved is overwritten.
			await RefreshPreviewAsync();
			SourcePreview preview = _preview;
			if (preview == null || preview.MappingProblems.Count > 0)
			{
				return new JobOutcome<SourceImportResult>(JobOutcomeKind.Stopped, null, null);
			}
			foreach (SourcePreviewRowViewModel r in PreviewRows)
			{
				r.Row.Selected = include.GetValueOrDefault(r.Row.RowNumber, r.Include) && r.CanInclude;
			}
			return await _app.Jobs.RunAsync($"Importing {Path.GetFileName(source)}",
				(_, _) => SourceImport.Import(_app.Store, preview, source), "Couldn't import (nothing was changed)", canStop: false);
		});
		if (!outcome.Completed)
		{
			return;
		}
		SourceImportResult result = outcome.Value;
		SaveCurrentProfile();
		LastImportIds = IsStructuresMode ? result.StructureIds : result.AssessmentIds;
		LastResult = string.Join(" · ", new[]
		{
			result.Created > 0 ? $"{result.Created} created" : null,
			result.Linked > 0 ? $"{result.Linked} linked" : null,
			result.Updated > 0 ? $"{result.Updated} updated" : null,
			result.Unchanged > 0 ? $"{result.Unchanged} unchanged" : null,
			result.Locked > 0 ? $"{result.Locked} Final, not changed" : null,
		}.Where(x => x != null)) + $" from {FileName}.";
		_main.RefreshStatus();
		_app.Dialogs.Notify("Import complete", LastResult);
		await RefreshPreviewAsync();
	}

	// ───────────── Result ─────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasResult))]
	public partial string LastResult { get; set; }

	public bool HasResult => LastResult != null;

	/// <summary>What the last import brought in: assessments (inspections) or structures (structures).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ReviewText))]
	public partial IReadOnlyList<Guid> LastImportIds { get; set; }

	public string ReviewText => LastImportIds is { Count: var n } ? (n == 1 ? "Review it in Structures →" : $"Review the {n} in Structures →") : null;

	/// <summary>Shows the Structures list narrowed to what the last import brought in.</summary>
	[RelayCommand]
	private void Review()
	{
		if (LastImportIds is { Count: > 0 } ids)
		{
			_main.Structures.ShowOnly(ids, $"Last import · {ids.Count}");
			_main.ShowCommand.Execute(Page.Structures);
		}
	}

	// ───────────── Helpers ─────────────

	/// <summary>"Import", or "Import 2", "Import 3"… when that name is taken.</summary>
	private string UnusedProfileName()
	{
		string name = ImportProfiles.DefaultName;
		for (int n = 2; SavedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
		{
			name = ImportProfiles.DefaultName + " " + n;
		}
		return name;
	}

	private static string Plural(int n, string word) => n == 1 ? word : word + "s";

	private string Choice(string column) => column != null && _raw != null && _raw.IndexOf(column) >= 0 ? _raw.Columns[_raw.IndexOf(column)] : None;

	private static string Real(string choice) => choice is null or None ? null : choice;

	private static string CustomFieldName(ReferenceData reference, int zOrder) =>
		reference.CustomFields.FirstOrDefault(c => c.zOrder == zOrder)?.CustomField ?? "Custom field " + zOrder;
}
