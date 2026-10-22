using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Interop;

namespace Sde.App.ViewModels;

/// <summary>One row of a Lat/Long spreadsheet being imported.</summary>
public sealed partial class LatLongRowViewModel : ObservableObject
{
	public LatLongRowViewModel(LatLongRow row)
	{
		Row = row;
		Include = row.Selected;
	}

	public LatLongRow Row { get; }

	public string Address => string.IsNullOrWhiteSpace(Row.Address) ? Row.PropertyIdText : Row.Address;

	public string Current => Coordinates(Row.CurrentLatitude, Row.CurrentLongitude) ?? "None";

	public string New => Coordinates(Row.NewLatitude, Row.NewLongitude) ?? "None";

	public bool CanInclude => Row.IsValid && Row.IsChanged;

	/// <summary>Why the row can't be used, a warning, or whether it changes anything.</summary>
	public string Note => !Row.IsValid ? string.Join(" ", Row.Problems)
		: Row.Warning ?? (Row.IsChanged ? null : "No change");

	public bool IsProblem => !Row.IsValid;

	[ObservableProperty]
	public partial bool Include { get; set; }

	private static string Coordinates(decimal? lat, decimal? lon) =>
		lat == null && lon == null ? null : $"{lat?.ToString("0.######") ?? "–"}, {lon?.ToString("0.######") ?? "–"}";
}

/// <summary>Spreadsheets (Excel, coordinates), Google Earth maps, SDE 2.x folders and user-settings files (src/Sde.Interop).</summary>
public sealed partial class ImportExportViewModel
{
	private LoadedSource ReadLegacy(string folder)
	{
		LegacyReadResult legacy = LegacySde2Import.Read(folder, _app.Store.Reference);
		return new LoadedSource(legacy.Data, legacy.PhotosFolder, legacy, legacy.Warnings);
	}

	/// <summary>Runs a file job off the UI thread and reports a failure instead of crashing.</summary>
	private async Task<bool> RunFileJob(string failureTitle, Action job)
	{
		try
		{
			await Task.Run(job);
			return true;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// Third-party file readers (ClosedXML, XML, zip) throw many kinds of exceptions for damaged or unexpected
			// files; all of them mean "that file can't be used", which is what the user needs to hear.
			AppLog.Write(failureTitle, ex);
			_app.Dialogs.Notify(failureTitle, ex.Message, isError: true);
			return false;
		}
	}

	// ───────────── Excel ─────────────

	[RelayCommand]
	private Task ExportExcel() => ExportExcelAsync(_app.Scope.ExportIds(_app.Store));

	/// <summary>FEMA's "Export to Excel": one row per assessment, 218 columns, as SDE showed them.</summary>
	public async Task ExportExcelAsync(IReadOnlyList<Guid> assessmentIds)
	{
		string path = await _app.Dialogs.SaveFileAsync("Export assessments to Excel", "SDE assessments.xlsx", "Excel workbook", "xlsx");
		if (path == null)
		{
			return;
		}
		_main.Editor?.Save();
		int count = 0;
		bool ok = await RunFileJob("Couldn't export to Excel", () =>
		{
			List<AssessmentFile> files = assessmentIds == null ? _app.Store.LoadAllAssessments() : _app.Store.LoadAssessments(assessmentIds);
			count = files.Count;
			ExcelExport.Write(files, _app.Store.Reference, path);
		});
		if (ok)
		{
			LastResult = $"Exported {count} assessment{(count == 1 ? "" : "s")} to {Path.GetFileName(path)}.";
			_app.Dialogs.Notify("Excel export complete", LastResult);
			_app.Dialogs.Open(path);
		}
	}

	// ───────────── Google Earth ─────────────

	/// <summary>FEMA's "Generate Geo Reference File": a pin per assessment, coloured by type and % damaged.</summary>
	[RelayCommand]
	private async Task ExportMap()
	{
		string path = await _app.Dialogs.SaveFileAsync("Save the Google Earth map", "SDE map.kmz", "Google Earth file", "kmz");
		if (path == null)
		{
			return;
		}
		_main.Editor?.Save();
		GeoFileResult result = null;
		if (await RunFileJob("Couldn't create the map", () => result = GeoFileExport.WriteKmz(_app.Scope.ExportIds(_app.Store) is IReadOnlyList<Guid> ids ? _app.Store.LoadAssessments(ids) : _app.Store.LoadAllAssessments(), _app.Store.Reference, path)))
		{
			LastResult = $"Map saved with {result.Placemarks} pin{(result.Placemarks == 1 ? "" : "s")}." +
				(result.Skipped.Count > 0 ? $" {result.Skipped.Count} assessment{(result.Skipped.Count == 1 ? " has" : "s have")} no latitude/longitude and {(result.Skipped.Count == 1 ? "was" : "were")} left out." : "");
			_app.Dialogs.Notify("Map saved", LastResult);
			_app.Dialogs.Reveal(path);
		}
	}

	// ───────────── Latitude / longitude ─────────────

	public ObservableCollection<LatLongRowViewModel> LatLongRows { get; } = new();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasLatLong))]
	public partial string LatLongSource { get; set; }

	public bool HasLatLong => LatLongSource != null;

	[ObservableProperty]
	public partial string LatLongSummary { get; set; }

	[RelayCommand]
	private async Task ExportLatLong()
	{
		string path = await _app.Dialogs.SaveFileAsync("Save the coordinates spreadsheet", "SDE coordinates.xlsx", "Excel workbook", "xlsx");
		if (path == null)
		{
			return;
		}
		int rows = 0;
		if (await RunFileJob("Couldn't create the spreadsheet", () => rows = LatLongSpreadsheet.Export(_app.Store, path)))
		{
			LastResult = $"Saved {rows} structure{(rows == 1 ? "" : "s")} to {Path.GetFileName(path)}. Fill in Latitude and Longitude, save, then use “Read coordinates…”.";
			_app.Dialogs.Notify("Spreadsheet saved", LastResult);
			_app.Dialogs.Open(path);
		}
	}

	[RelayCommand]
	private async Task ChooseLatLong()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Choose the coordinates spreadsheet", false, "Excel workbook", "*.xlsx");
		if (files.Count == 1)
		{
			await LoadLatLongAsync(files[0]);
		}
	}

	/// <summary>Reads a filled-in coordinates sheet into the preview (nothing is saved yet).</summary>
	public async Task LoadLatLongAsync(string path)
	{
		List<LatLongRow> rows = null;
		if (!await RunFileJob("Can't read that spreadsheet", () => rows = LatLongSpreadsheet.Read(_app.Store, path)))
		{
			return;
		}
		LatLongRows.Clear();
		foreach (LatLongRow r in rows)
		{
			LatLongRows.Add(new LatLongRowViewModel(r));
		}
		int changed = rows.Count(r => r.IsChanged), problems = rows.Count(r => !r.IsValid);
		LatLongSummary = $"{changed} structure{(changed == 1 ? "" : "s")} will get new coordinates" +
			(problems > 0 ? $" · {problems} row{(problems == 1 ? "" : "s")} can't be used" : "") +
			(rows.Count - changed - problems > 0 ? $" · {rows.Count - changed - problems} unchanged" : "");
		LatLongSource = path;
	}

	[RelayCommand]
	private void CancelLatLong()
	{
		LatLongRows.Clear();
		LatLongSource = null;
		LatLongSummary = null;
	}

	[RelayCommand]
	private void ApplyLatLong()
	{
		foreach (LatLongRowViewModel r in LatLongRows)
		{
			r.Row.Selected = r.Include && r.CanInclude;
		}
		int updated;
		try
		{
			updated = _app.Writes.Write(null, () => LatLongSpreadsheet.Apply(_app.Store, LatLongRows.Select(r => r.Row)));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the coordinates", ex);
			return;
		}
		CancelLatLong();
		LastResult = $"Updated the coordinates of {updated} structure{(updated == 1 ? "" : "s")}.";
		_app.Dialogs.Notify("Coordinates saved", LastResult);
		_app.RaiseDataChanged();
	}

	// ───────────── User settings file ─────────────

	[ObservableProperty]
	public partial bool SettingsIncludeNotes { get; set; } = true;

	[ObservableProperty]
	public partial bool SettingsIncludeMappings { get; set; } = true;

	[ObservableProperty]
	public partial bool SettingsIncludeDefaults { get; set; } = true;

	/// <summary>FEMA's "Export User Settings": saved notes, Enterprise Import mappings and default values, to share with colleagues.</summary>
	[RelayCommand]
	private async Task ExportSettingsFile()
	{
		if (!SettingsIncludeNotes && !SettingsIncludeMappings && !SettingsIncludeDefaults)
		{
			_app.Dialogs.Notify("Nothing to export", "Tick at least one of notes, import mappings or default values.");
			return;
		}
		string path = await _app.Dialogs.SaveFileAsync("Save your SDE settings", "SDE settings.json", "SDE settings", "json");
		if (path == null)
		{
			return;
		}
		if (await RunFileJob("Couldn't save the settings", () => UserSettingsFile.Export(_app.Store, path, SettingsIncludeNotes, SettingsIncludeMappings, SettingsIncludeDefaults)))
		{
			LastResult = $"Settings saved to {Path.GetFileName(path)}.";
			_app.Dialogs.Notify("Settings exported", LastResult);
			_app.Dialogs.Reveal(path);
		}
	}

	[RelayCommand]
	private async Task ImportSettingsFile()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Choose an SDE settings file", false, "SDE settings", "*.json");
		if (files.Count != 1)
		{
			return;
		}
		UserSettingsContents contents = null;
		if (!await RunFileJob("Can't read that file", () => contents = UserSettingsFile.Inspect(files[0])))
		{
			return;
		}
		List<string> parts = new();
		if (contents.Notes is > 0)
		{
			parts.Add($"{contents.Notes} saved note{(contents.Notes == 1 ? "" : "s")} will be added");
		}
		if (contents.EnterpriseSettings is > 0)
		{
			parts.Add($"{contents.EnterpriseSettings} import mapping{(contents.EnterpriseSettings == 1 ? "" : "s")} will be added");
		}
		if (contents.DefaultValues)
		{
			parts.Add("your default values will be replaced");
		}
		if (parts.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to import", "That settings file is empty.");
			return;
		}
		string message = char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..] + ".";
		if (!await _app.Dialogs.ConfirmAsync("Import settings?", message, "Import"))
		{
			return;
		}
		UserSettingsImportResult result = null;
		if (await RunFileJob("Couldn't import the settings", () => result = UserSettingsFile.Import(_app.Store, files[0])))
		{
			LastResult = $"Added {result.NotesAdded} note{(result.NotesAdded == 1 ? "" : "s")} and {result.MappingsAdded} import mapping{(result.MappingsAdded == 1 ? "" : "s")}" +
				(result.DefaultsReplaced ? "; default values replaced." : ".");
			_app.Dialogs.Notify("Settings imported", LastResult);
			_main.Settings = new SettingsViewModel(_app);
			Enterprise.ReloadSavedMappings();
		}
	}
}
