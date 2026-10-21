using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>One structure/assessment found in a file being imported.</summary>
public sealed partial class ImportRowViewModel : ObservableObject
{
	public Guid PropertyID { get; init; }

	public Guid? AssessmentID { get; init; }

	public string Address { get; init; }

	public string Detail { get; init; }

	public bool AlreadyInDatabase { get; init; }

	public string Status => AlreadyInDatabase ? "Already in your database" : "New";

	[ObservableProperty]
	public partial bool Include { get; set; } = true;
}

/// <summary>
/// Exchange with FEMA's SDE and other tools. SDE data (Export.json folders, SDE 2.x
/// folders and FEMA .mdb databases) is handled here; spreadsheets, maps, coordinates and settings files are in
/// ImportExportViewModel.Interop.cs, and Enterprise Import in EnterpriseImportViewModel.
/// </summary>
public sealed partial class ImportExportViewModel : ViewModelBase
{
	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private SdeDataset _pending;
	private string _pendingPhotos;
	private IDisposable _pendingOwner; // an SDE 2.x read's staged photos, deleted when the import is done or cancelled

	public ImportExportViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
		Enterprise = new EnterpriseImportViewModel(app, () =>
		{
			_app.RaiseDataChanged();
			_main.RefreshStatus();
		});
	}

	public EnterpriseImportViewModel Enterprise { get; }

	public ObservableCollection<ImportRowViewModel> PendingRows { get; } = new();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPending))]
	public partial string PendingSource { get; set; }

	public bool HasPending => PendingSource != null;

	/// <summary>Things worth knowing about the file before importing (e.g. SDE 2.x values that couldn't be converted).</summary>
	[ObservableProperty]
	public partial string PendingNotes { get; set; }

	/// <summary>0 = skip existing, 1 = replace existing, 2 = keep both (import a copy).</summary>
	[ObservableProperty]
	public partial int ExistingChoice { get; set; }

	[ObservableProperty]
	public partial string LastResult { get; set; }

	public int ExistingCount => PendingRows.Count(r => r.AlreadyInDatabase);

	[RelayCommand]
	private void DismissResult() => LastResult = null;

	/// <summary>Opens the page that imports inspections or parcels.</summary>
	[RelayCommand]
	private Task OpenSourceImport() => _main.OpenSourceImportCommand.ExecuteAsync(null);

	// ───────────── Export ─────────────

	[RelayCommand]
	private Task ExportAll() => ExportAsync(null);

	/// <summary>Writes an SDE_n folder (Export.json + photos) that FEMA's SDE can import.</summary>
	public async Task ExportAsync(IReadOnlyList<Guid> assessmentIds)
	{
		if (!await SampleWarning.ConfirmAsync(_app, "an export for FEMA SDE"))
		{
			return;
		}
		string folder = await _app.Dialogs.PickFolderAsync("Choose where to save the SDE export");
		if (folder == null)
		{
			return;
		}
		_main.Editor?.Save();   // export what's on screen, not the last autosave
		if (!await ExportWarnings.ConfirmNotFinalAsync(_app, assessmentIds?.ToList()))
		{
			return;
		}
		ExchangeService x = new ExchangeService(_app.Store);
		JobOutcome<(SdeDataset Data, string Written, IReadOnlyList<string> Missing)> outcome = await _app.Jobs.RunAsync("Exporting to SDE", (progress, ct) =>
		{
			SdeDataset data = assessmentIds == null ? x.ReadDataset() : x.ReadDataset(assessmentIds);
			(string written, IReadOnlyList<string> missing) = x.Export(data, folder);
			return (data, written, missing);
		}, "Couldn't export to SDE", canStop: false);
		if (!outcome.Completed)
		{
			return;
		}
		(SdeDataset exported, string folderWritten, IReadOnlyList<string> missingPhotos) = outcome.Value;
		_app.Settings.LastExportFolder = folder;
		_app.Settings.Save();
		LastResult = $"Exported {exported.Properties.Count} structures and {exported.Assessments.Count} assessments to {folderWritten}" +
			(missingPhotos.Count > 0 ? $" ({missingPhotos.Count} photo files were missing)" : "");
		_app.Dialogs.Notify("Export complete", LastResult);
		_app.Dialogs.Reveal(folderWritten);
	}

	// ───────────── Import ─────────────

	[RelayCommand]
	private async Task ChooseSdeFolder()
	{
		string folder = await _app.Dialogs.PickFolderAsync("Choose an SDE export folder (it contains Export.json)");
		if (folder != null)
		{
			await LoadPendingAsync(folder);
		}
	}

	[RelayCommand]
	private async Task ChooseMdb()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Choose a FEMA SDE database", false, "SDE database", "*.mdb");
		if (files.Count == 1)
		{
			await LoadPendingAsync(files[0]);
		}
	}

	/// <summary>Reads a folder or .mdb into the preview list (nothing is written yet). Also used by drag and drop.</summary>
	public async Task LoadPendingAsync(string path)
	{
		try
		{
			LoadedSource loaded = await Task.Run(() => ReadSource(path));
			CancelImport();
			_pending = loaded.Data;
			_pendingPhotos = loaded.Photos;
			_pendingOwner = loaded.Owner;
			ShowPending(path, loaded.Data);
			PendingNotes = loaded.Notes is { Count: > 0 } notes
				? string.Join("\n", notes.Take(8)) + (notes.Count > 8 ? $"\n…and {notes.Count - 8} more" : "")
				: null;
		}
		catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or Newtonsoft.Json.JsonException)
		{
			_app.Dialogs.Notify("Can't import that", ex.Message, isError: true);
		}
	}

	private sealed record LoadedSource(SdeDataset Data, string Photos, IDisposable Owner = null, IReadOnlyList<string> Notes = null);

	private LoadedSource ReadSource(string path)
	{
		if (path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase))
		{
			(SdeDataset data, string photos) = ExchangeService.ReadMdb(path);
			return new LoadedSource(data, photos);
		}
		if (SdeExchangeFolder.IsExchangeFolder(path))
		{
			return new LoadedSource(SdeExchangeFolder.Read(path), SdeExchangeFolder.PhotosFolder(path));
		}
		if (SdeExchangeFolder.IsLegacyFolder(path))
		{
			return ReadLegacy(path);
		}
		throw new InvalidDataException("That folder doesn't contain an SDE export (Export.json) or an SDE 2.x export (SummaryData.xml).");
	}

	private void ShowPending(string source, SdeDataset data)
	{
		HashSet<Guid> existingAssessments = _app.Store.ListStructures().Where(s => s.AssessmentID != null).Select(s => s.AssessmentID!.Value).ToHashSet();
		HashSet<Guid> existingProperties = _app.Store.ListStructures().Select(s => s.PropertyID).ToHashSet();
		PendingRows.Clear();
		foreach (PropertyRecord p in data.Properties)
		{
			List<AssessmentRecord> assessments = data.Assessments.Where(a => a.PropertyID == p.PropertyID).ToList();
			string address = Editor.AssessmentEditorViewModel.Address(p, _app.Store.Reference, "(no address)") + (string.IsNullOrWhiteSpace(p.City) ? "" : ", " + p.City);
			if (assessments.Count == 0)
			{
				PendingRows.Add(new ImportRowViewModel { PropertyID = p.PropertyID, Address = address, Detail = "Structure only", AlreadyInDatabase = existingProperties.Contains(p.PropertyID) });
			}
			foreach (AssessmentRecord a in assessments)
			{
				PendingRows.Add(new ImportRowViewModel
				{
					PropertyID = p.PropertyID,
					AssessmentID = a.AssessmentID,
					Address = address,
					Detail = (a.DateofInspection?.ToString("MMM d, yyyy") ?? "No inspection date") + (a.DamageMessage switch
					{
						null or "" => "",
						"Substantially Damaged" or "Not Substantially Damaged" => $" · {a.PercentDamage:0.0}% · {a.DamageMessage}",
						_ => " · " + a.DamageMessage,
					}),
					AlreadyInDatabase = existingAssessments.Contains(a.AssessmentID),
				});
			}
		}
		PendingSource = source;
		OnPropertyChanged(nameof(ExistingCount));
		LastResult = null;
	}

	[RelayCommand]
	private void CancelImport()
	{
		_pending = null;
		_pendingOwner?.Dispose();
		_pendingOwner = null;
		PendingRows.Clear();
		PendingSource = null;
		PendingNotes = null;
	}

	[RelayCommand]
	private async Task RunImport()
	{
		if (_pending == null)
		{
			return;
		}
		HashSet<Guid> assessments = PendingRows.Where(r => r.Include && r.AssessmentID != null).Select(r => r.AssessmentID!.Value).ToHashSet();
		HashSet<Guid> properties = PendingRows.Where(r => r.Include).Select(r => r.PropertyID).ToHashSet();
		SdeDataset selected = _pending.Filter(properties, assessments);
		ExistingRecordAction action = ExistingChoice switch
		{
			1 => ExistingRecordAction.Overwrite,
			2 => ExistingRecordAction.Copy,
			_ => ExistingRecordAction.Skip,
		};
		string photos = _pendingPhotos;
		string sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(PendingSource ?? ""));
		JobOutcome<ImportResult> outcome = await _app.Writes.WriteAsync(null, () => _app.Jobs.RunAsync("Importing SDE data",
			(progress, ct) => new ExchangeService(_app.Store).Import(selected, photos, action, sourceName), "Couldn't import", canStop: false));
		if (!outcome.Completed)
		{
			return;
		}
		ImportResult result = outcome.Value;
		LastResult = "Imported: " + result + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings.Take(10)) : "");
		CancelImport();
		_main.RefreshStatus();
		_app.Dialogs.Notify("Import complete", result.ToString());
	}
}
