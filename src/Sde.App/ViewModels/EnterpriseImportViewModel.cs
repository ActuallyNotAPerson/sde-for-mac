using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core;
using Sde.Core.Records;
using Sde.Interop;

namespace Sde.App.ViewModels;

/// <summary>A choice in a small drop-down (a parsing mode).</summary>
public sealed record ModeOption(int Value, string Label);

/// <summary>One SDE field and the spreadsheet column that fills it.</summary>
public sealed partial class EnterpriseFieldRowViewModel(EnterpriseFieldInfo info, EnterpriseImportViewModel owner) : ObservableObject
{
	public const string NotImported = "(not imported)";

	public EnterpriseFieldInfo Info { get; } = info;

	public string Label => Info.Label;

	public IReadOnlyList<string> Columns => Owner.ColumnChoices;

	/// <summary>Called when the sheet (and so the column list) changes.</summary>
	public void RefreshColumns() => OnPropertyChanged(nameof(Columns));

	[ObservableProperty]
	public partial string Column { get; set; } = NotImported;

	public bool IsMapped => Column != null && Column != NotImported;

	/// <summary>How the column's text is split (owner names and address parts only).</summary>
	public IReadOnlyList<ModeOption> Modes { get; init; }

	public bool HasModes => Modes != null;

	[ObservableProperty]
	public partial ModeOption Mode { get; set; }

	/// <summary>The Residential / Non-Residential words (structure type only).</summary>
	public bool HasTypeTexts => Info.Field == EnterpriseField.StructureType;

	public EnterpriseImportViewModel Owner { get; } = owner;

	partial void OnColumnChanged(string value) => Owner.MappingEdited();

	partial void OnModeChanged(ModeOption value) => Owner.MappingEdited();
}

/// <summary>The fields of one group on the original screen (Owner/Address, Community, Subdivision, Structure/NFIP).</summary>
public sealed record EnterpriseFieldGroup(string Title, IReadOnlyList<EnterpriseFieldRowViewModel> Rows);

/// <summary>One sheet row as it would be imported.</summary>
public sealed partial class EnterprisePreviewRowViewModel(EnterpriseRow row, ReferenceData reference) : ObservableObject
{
	public EnterpriseRow Row { get; } = row;

	public int RowNumber => Row.RowNumber;

	public string Address
	{
		get
		{
			PropertyRecord p = Row.Property;
			string street = Editor.AssessmentEditorViewModel.Address(p, reference, "(no address)");
			return string.IsNullOrWhiteSpace(p.City) ? street : street + ", " + p.City;
		}
	}

	public string Owner => string.Join(" ", new[] { Row.Property.OwnerFirstName, Row.Property.OwnerLastName }.Where(x => !string.IsNullOrWhiteSpace(x)));

	public string Note => Row.IsValid ? string.Join(" ", Row.Warnings) : string.Join(" ", Row.Problems);

	public bool IsProblem => !Row.IsValid;

	public bool CanInclude => Row.IsValid;

	[ObservableProperty]
	public partial bool Include { get; set; } = row.Selected;
}

/// <summary>
/// Enterprise Import: create structures from a parcel spreadsheet (e.g. the county assessor's) by choosing which column
/// fills each SDE field. Wraps Sde.Interop.EnterpriseImport; the original's flow (pick file → sheet → header row → map
/// columns → import, then the mapping is remembered) is kept, with a preview of every row before anything is created.
/// </summary>
public sealed partial class EnterpriseImportViewModel : ObservableObject
{
	private readonly AppServices _app;
	private readonly Action _imported;
	private SourceTable _table;
	private EnterprisePreview _preview;
	private bool _loadingMapping;

	public EnterpriseImportViewModel(AppServices app, Action imported)
	{
		_app = app;
		_imported = imported;
		Groups = EnterpriseMapping.Fields
			.GroupBy(f => f.Group)
			.Select(g => new EnterpriseFieldGroup(g.Key, g.Select(f => new EnterpriseFieldRowViewModel(f, this) { Modes = ModesFor(f.Field) }).ToList()))
			.ToList();
		foreach (EnterpriseFieldRowViewModel r in AllRows.Where(r => r.HasModes))
		{
			r.Mode = r.Modes[0];
		}
		ReloadSavedMappings();
	}

	public IReadOnlyList<EnterpriseFieldGroup> Groups { get; }

	/// <summary>Owner/Address and Community (left column on screen).</summary>
	public IReadOnlyList<EnterpriseFieldGroup> LeftGroups => new[] { Groups[0], Groups[1] };

	/// <summary>Subdivision and Structure/NFIP (right column on screen).</summary>
	public IReadOnlyList<EnterpriseFieldGroup> RightGroups => new[] { Groups[2], Groups[3] };

	private IEnumerable<EnterpriseFieldRowViewModel> AllRows => Groups.SelectMany(g => g.Rows);

	// ───────────── Source file ─────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasFile), nameof(FileName))]
	public partial string FilePath { get; set; }

	public bool HasFile => FilePath != null;

	public string FileName => Path.GetFileName(FilePath);

	[ObservableProperty]
	public partial IReadOnlyList<string> SheetNames { get; set; } = Array.Empty<string>();

	public bool HasSeveralSheets => SheetNames.Count > 1;

	[ObservableProperty]
	public partial string SelectedSheet { get; set; }

	[ObservableProperty]
	public partial bool FirstRowIsHeader { get; set; } = true;

	[ObservableProperty]
	public partial IReadOnlyList<string> ColumnChoices { get; set; } = new[] { EnterpriseFieldRowViewModel.NotImported };

	[ObservableProperty]
	public partial string TableSummary { get; set; }

	[ObservableProperty]
	public partial string ResidentialText { get; set; } = "Residential";

	[ObservableProperty]
	public partial string NonResidentialText { get; set; } = "Non-Residential";

	partial void OnSheetNamesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasSeveralSheets));

	partial void OnSelectedSheetChanged(string value) => LoadTable();

	partial void OnFirstRowIsHeaderChanged(bool value) => LoadTable();

	partial void OnResidentialTextChanged(string value) => MappingEdited();

	partial void OnNonResidentialTextChanged(string value) => MappingEdited();

	[RelayCommand]
	private async Task ChooseFile()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Choose a parcel spreadsheet", false, "Spreadsheet", "*.xlsx", "*.xlsm", "*.xls", "*.csv", "*.dbf", "*.shp");
		if (files.Count == 1)
		{
			OpenFile(files[0]);
		}
	}

	/// <summary>Opens a workbook or .csv and reads its first sheet. Also used by tests.</summary>
	public void OpenFile(string path)
	{
		try
		{
			IReadOnlyList<string> sheets = EnterpriseImport.SheetNames(path);
			FilePath = path;
			_table = null;
			SheetNames = sheets;
			if (SelectedSheet == sheets.FirstOrDefault())
			{
				LoadTable();
			}
			else
			{
				SelectedSheet = sheets.FirstOrDefault(); // loads the table
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// ClosedXML and the CSV reader throw various exceptions for files they can't read.
			_app.Dialogs.Notify("Can't read that spreadsheet", ex.Message, isError: true);
		}
	}

	private void LoadTable()
	{
		if (FilePath == null || SelectedSheet == null)
		{
			return;
		}
		try
		{
			_table = EnterpriseImport.ReadTable(FilePath, SelectedSheet, FirstRowIsHeader);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Dialogs.Notify("Can't read that sheet", ex.Message, isError: true);
			return;
		}
		List<string> choices = new() { EnterpriseFieldRowViewModel.NotImported };
		choices.AddRange(_table.Columns);
		ColumnChoices = choices;
		foreach (EnterpriseFieldRowViewModel r in AllRows)
		{
			r.RefreshColumns();
			if (r.IsMapped && _table.IndexOf(r.Column) < 0)
			{
				r.Column = EnterpriseFieldRowViewModel.NotImported;
			}
		}
		TableSummary = $"{_table.Rows.Count} row{(_table.Rows.Count == 1 ? "" : "s")} · {_table.Columns.Count} column{(_table.Columns.Count == 1 ? "" : "s")}";
		if (!AllRows.Any(r => r.IsMapped))
		{
			GuessColumns();
		}
		MappingEdited();
	}

	/// <summary>Maps columns whose header is the field's name (e.g. "City", "Zip Code", "Parcel Number"). The original made you pick every one.</summary>
	private void GuessColumns()
	{
		_loadingMapping = true;
		foreach (EnterpriseFieldRowViewModel r in AllRows)
		{
			string[] names = new[] { r.Info.Label, r.Info.Field.ToString(), r.Info.SettingsColumn }
				.Concat(Synonyms.TryGetValue(r.Info.Field, out string[] more) ? more : Array.Empty<string>()).ToArray();
			string match = _table.Columns.FirstOrDefault(c => names.Any(n => Normalize(n) == Normalize(c)));
			if (match != null)
			{
				r.Column = match;
			}
		}
		// A single "Address" / "Site Address" column usually holds the whole street address.
		EnterpriseFieldRowViewModel number = AllRows.Single(r => r.Info.Field == EnterpriseField.StreetNumber);
		EnterpriseFieldRowViewModel name = AllRows.Single(r => r.Info.Field == EnterpriseField.StreetName);
		if (!number.IsMapped && !name.IsMapped &&
			_table.Columns.FirstOrDefault(c => Normalize(c) is "address" or "siteaddress" or "situsaddress" or "streetaddress" or "propertyaddress") is string whole)
		{
			name.Column = whole;
			name.Mode = name.Modes.Single(o => o.Value == (int)StreetNameParsing.NumberNameSuffix);
		}
		_loadingMapping = false;
	}

	/// <summary>Common assessor / GIS column names for a few fields.</summary>
	private static readonly Dictionary<EnterpriseField, string[]> Synonyms = new()
	{
		[EnterpriseField.YearOfConstruction] = new[] { "Year Built", "YearBuilt", "Yr Built", "Built" },
		[EnterpriseField.TotalSquareFootage] = new[] { "Square Feet", "Sq Ft", "SqFt", "Living Area", "Building Area", "Finished Area" },
		[EnterpriseField.ParcelNumber] = new[] { "Parcel", "Parcel ID", "ParcelID", "PIN", "APN", "Parcel No" },
		[EnterpriseField.Zip] = new[] { "ZIP", "Zip5", "Postal Code" },
		[EnterpriseField.Latitude] = new[] { "Lat", "Y" },
		[EnterpriseField.Longitude] = new[] { "Lon", "Long", "Lng", "X" },
		[EnterpriseField.FirmZone] = new[] { "Flood Zone", "FLD_ZONE" },
		[EnterpriseField.County] = new[] { "Parish" },
	};

	private static string Normalize(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

	// ───────────── Saved mappings ─────────────

	public ObservableCollection<EnterpriseMapping> SavedMappings { get; } = new();

	[ObservableProperty]
	public partial EnterpriseMapping SelectedSaved { get; set; }

	public void ReloadSavedMappings()
	{
		SavedMappings.Clear();
		foreach (EnterpriseImportSettingsRecord r in _app.Store.GetEnterpriseMappings())
		{
			SavedMappings.Add(EnterpriseMapping.FromRecord(r));
		}
	}

	partial void OnSelectedSavedChanged(EnterpriseMapping value)
	{
		if (value != null)
		{
			Apply(value);
		}
	}

	private void Apply(EnterpriseMapping m)
	{
		_loadingMapping = true;
		foreach (EnterpriseFieldRowViewModel r in AllRows)
		{
			string column = m[r.Info.Field];
			r.Column = column != null && (_table == null || _table.IndexOf(column) >= 0) ? column : EnterpriseFieldRowViewModel.NotImported;
			if (r.HasModes)
			{
				int mode = r.Info.Field switch
				{
					EnterpriseField.OwnerFirstName => (int)m.FirstNameParsing,
					EnterpriseField.OwnerLastName => (int)m.LastNameParsing,
					EnterpriseField.StreetNumber => (int)m.StreetNumberParsing,
					EnterpriseField.StreetName => (int)m.StreetNameParsing,
					_ => (int)m.SuffixParsing,
				};
				if (mode == 0 && r.Info.Field == EnterpriseField.StreetName)
				{
					mode = (int)StreetNameParsing.Name; // "Make Selection..." takes the street name as is
				}
				r.Mode = r.Modes.FirstOrDefault(o => o.Value == mode) ?? r.Modes[0];
			}
		}
		_loadingMapping = false;
		MappingEdited();
	}

	/// <summary>The mapping as currently shown.</summary>
	public EnterpriseMapping CurrentMapping()
	{
		EnterpriseMapping m = new EnterpriseMapping
		{
			Name = SelectedSaved?.Name,
			Id = SelectedSaved?.Id ?? 0,
			Guid = SelectedSaved?.Guid,
			ResidentialText = ResidentialText,
			NonResidentialText = NonResidentialText,
		};
		foreach (EnterpriseFieldRowViewModel r in AllRows)
		{
			m[r.Info.Field] = r.IsMapped ? r.Column : null;
			int mode = r.Mode?.Value ?? 0;
			switch (r.Info.Field)
			{
				case EnterpriseField.OwnerFirstName: m.FirstNameParsing = (NameParsing)mode; break;
				case EnterpriseField.OwnerLastName: m.LastNameParsing = (NameParsing)mode; break;
				case EnterpriseField.StreetNumber: m.StreetNumberParsing = (StreetNumberParsing)mode; break;
				case EnterpriseField.StreetName: m.StreetNameParsing = (StreetNameParsing)mode; break;
				case EnterpriseField.StreetSuffix: m.SuffixParsing = (SuffixParsing)mode; break;
			}
		}
		return m;
	}

	[RelayCommand]
	private void SaveMapping()
	{
		EnterpriseMapping m = CurrentMapping();
		m.Name ??= EnterpriseMapping.DefaultName(DateTime.Now);
		EnterpriseImportSettingsRecord record = m.ToRecord();
		_app.Store.SaveEnterpriseMapping(record);
		ReloadSavedMappings();
		_loadingMapping = true;
		SelectedSaved = SavedMappings.FirstOrDefault(x => x.Id == record.EIS_ID);
		_loadingMapping = false;
		_app.Dialogs.Notify("Mapping saved", m.Name);
	}

	[RelayCommand]
	private async Task DeleteMapping()
	{
		if (SelectedSaved is not EnterpriseMapping m || !await _app.Dialogs.ConfirmAsync("Delete this mapping?", m.Name, "Delete", destructive: true))
		{
			return;
		}
		_app.Store.DeleteEnterpriseMapping(m.Id);
		SelectedSaved = null;
		ReloadSavedMappings();
	}

	// ───────────── Preview and import ─────────────

	public ObservableCollection<EnterprisePreviewRowViewModel> PreviewRows { get; } = new();

	[ObservableProperty]
	public partial string MappingProblems { get; set; }

	[ObservableProperty]
	public partial string PreviewSummary { get; set; }

	[ObservableProperty]
	public partial bool CanImport { get; set; }

	[ObservableProperty]
	public partial bool RememberMapping { get; set; } = true;

	/// <summary>Re-runs the preview whenever the mapping changes, so the rows below always show what would be imported.</summary>
	public void MappingEdited()
	{
		if (_loadingMapping || _table == null)
		{
			return;
		}
		_preview = EnterpriseImport.Preview(_table, CurrentMapping(), _app.Store.Reference, _app.Store.GetProperties());
		PreviewRows.Clear();
		foreach (EnterpriseRow r in _preview.Rows.Take(MaxPreviewRows))
		{
			PreviewRows.Add(new EnterprisePreviewRowViewModel(r, _app.Store.Reference));
		}
		MappingProblems = _preview.MappingProblems.Count > 0 ? string.Join("\n", _preview.MappingProblems) : null;
		int valid = _preview.ValidCount, total = _preview.Rows.Count;
		int warned = _preview.Rows.Count(r => r.IsValid && r.Warnings.Count > 0);
		PreviewSummary = $"{valid} of {total} row{(total == 1 ? "" : "s")} can be imported" +
			(warned > 0 ? $" · {warned} with values left blank" : "") +
			(total > MaxPreviewRows ? $" · showing the first {MaxPreviewRows}" : "");
		CanImport = _preview.MappingProblems.Count == 0 && valid > 0;
	}

	/// <summary>Rows shown in the preview (all rows are imported; this only keeps the screen quick for big county files).</summary>
	public const int MaxPreviewRows = 200;

	[RelayCommand]
	private async Task Import()
	{
		if (_preview == null || !CanImport)
		{
			return;
		}
		foreach (EnterprisePreviewRowViewModel r in PreviewRows)
		{
			r.Row.Selected = r.Include && r.CanInclude;
		}
		int count = _preview.Rows.Count(r => r.IsValid && r.Selected);
		if (!await _app.Dialogs.ConfirmAsync("Create structures?", $"{count} new structure{(count == 1 ? "" : "s")} will be added to your database.", "Import"))
		{
			return;
		}
		EnterprisePreview preview = _preview;
		JobOutcome<int> outcome = await _app.Jobs.RunAsync("Creating structures", (progress, ct) => EnterpriseImport.Import(_app.Store, preview),
			"Couldn't create the structures", canStop: false);
		if (!outcome.Completed)
		{
			return;
		}
		int created = outcome.Value;
		if (RememberMapping && SelectedSaved == null)
		{
			EnterpriseMapping m = CurrentMapping();
			m.Name = EnterpriseMapping.DefaultName(DateTime.Now);
			_app.Store.SaveEnterpriseMapping(m.ToRecord());
			ReloadSavedMappings();
		}
		_app.Dialogs.Notify("Import complete", $"Created {created} structure{(created == 1 ? "" : "s")} from {FileName}. Open one from the Structures list to start its assessment.");
		Close();
		_imported();
	}

	[RelayCommand]
	private void Close()
	{
		FilePath = null;
		_table = null;
		_preview = null;
		SheetNames = Array.Empty<string>();
		SelectedSheet = null;
		ColumnChoices = new[] { EnterpriseFieldRowViewModel.NotImported };
		PreviewRows.Clear();
		MappingProblems = null;
		PreviewSummary = null;
		TableSummary = null;
		CanImport = false;
	}

	/// <summary>The parsing choices (FEMA's lookup types 30–33; the first is the default and means "use the column as is").</summary>
	internal static IReadOnlyList<ModeOption> ModesFor(EnterpriseField f) => f switch
	{
		EnterpriseField.OwnerFirstName => new ModeOption[] { new((int)NameParsing.AsIs, "Just the first name"), new((int)NameParsing.FirstLast, "Full name: John Doe"), new((int)NameParsing.LastCommaFirst, "Full name: Doe, John") },
		EnterpriseField.OwnerLastName => new ModeOption[] { new((int)NameParsing.AsIs, "Just the last name"), new((int)NameParsing.FirstLast, "Full name: John Doe"), new((int)NameParsing.LastCommaFirst, "Full name: Doe, John") },
		EnterpriseField.StreetNumber => new ModeOption[] { new((int)StreetNumberParsing.Number, "Just the number: 123"), new((int)StreetNumberParsing.NumberNameSuffix, "Whole address: 123 Main St"), new((int)StreetNumberParsing.NumberName, "Number and name: 123 Main") },
		EnterpriseField.StreetName => new ModeOption[] { new((int)StreetNameParsing.NameSuffix, "Name and suffix: Main St"), new((int)StreetNameParsing.Name, "Just the name: Main"), new((int)StreetNameParsing.NumberNameSuffix, "Whole address: 123 Main St"), new((int)StreetNameParsing.NumberName, "Number and name: 123 Main") },
		EnterpriseField.StreetSuffix => new ModeOption[] { new((int)SuffixParsing.Suffix, "Just the suffix: St"), new((int)SuffixParsing.NameSuffix, "Name and suffix: Main St"), new((int)SuffixParsing.NumberNameSuffix, "Whole address: 123 Main St") },
		_ => null,
	};
}
