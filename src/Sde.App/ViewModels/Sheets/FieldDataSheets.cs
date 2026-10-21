using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.Online;
using Sde.Interop.Photos;

namespace Sde.App.ViewModels.Sheets;

/// <summary>The values the person ticked in the look-up sheet, and where they came from.</summary>
public sealed record AutoFillChoice(IReadOnlyList<ProposedValue> Values, bool FromNfhl, bool FromGeocoder, DateTime RetrievedOn);

/// <summary>One looked-up value: what the field holds now, what the service suggests, and a tick box.</summary>
public sealed partial class ProposalRowViewModel : ObservableObject
{
	public ProposalRowViewModel(OnlineProposal proposal, PropertyRecord property, ReferenceData reference)
	{
		Proposal = proposal;
		FieldInfo field = OnlineLookup.FieldFor(proposal.Value);
		object current = field?.RecordProperty.GetValue(property);
		string shown = field == null ? current?.ToString() : FieldCatalog.Display(field, current, reference);
		CurrentText = string.IsNullOrEmpty(shown) ? "None" : shown;
		IsSame = OnlineLookup.IsSame(field, current, proposal.Value);
		WouldOverwrite = !IsSame && !OnlineLookup.IsEmpty(current);
		// Ticked by default only when it fills an empty field: replacing what someone entered is a choice to make.
		IsChecked = !IsSame && !WouldOverwrite;
	}

	public OnlineProposal Proposal { get; }

	public string Label => Proposal.Value.Label;

	public string CurrentText { get; }

	public string ProposedText => Proposal.Value.Display;

	public string SourceText => Proposal.Source == OnlineSource.CensusGeocoder ? "Census geocoder" : "FEMA flood map";

	/// <summary>The field already has this value.</summary>
	public bool IsSame { get; }

	public bool CanCheck => !IsSame;

	/// <summary>The field has a different value now, which this would replace.</summary>
	public bool WouldOverwrite { get; }

	public string Note => IsSame ? "Already this value" : WouldOverwrite ? "Replaces the current value" : null;

	public bool HasNote => Note != null;

	public string CheckLabel => $"Use {Label}: {ProposedText}";

	[ObservableProperty]
	public partial bool IsChecked { get; set; }
}

/// <summary>
/// The editor's "Look up online": asks the Census geocoder (when the structure has an address but no coordinates) and
/// FEMA's National Flood Hazard Layer, then shows each value next to what is entered now, with tick boxes. Nothing is
/// changed until Apply; the editor then applies the ticked values as edits. Problems with the services are shown here
/// in plain English.
/// </summary>
public sealed partial class AutoFillSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly PropertyRecord _property;
	private CancellationTokenSource _cancel;
	private DateTime _retrievedOn;

	public AutoFillSheetViewModel(AppServices app, PropertyRecord property)
		: base("Look up online")
	{
		_app = app;
		_property = property;
		Address = EditorAddress(property, app.Store.Reference);
		// Closing the sheet (Cancel, Esc) stops a look-up still running.
		Closing += _ => _cancel?.Cancel();
	}

	private static string EditorAddress(PropertyRecord p, ReferenceData reference) => AssessmentEditorViewModel.Address(p, reference);

	public string Address { get; }

	public ObservableCollection<ProposalRowViewModel> Rows { get; } = new();

	/// <summary>Completes when the current look-up has finished (tests wait on it).</summary>
	public Task Lookup { get; private set; } = Task.CompletedTask;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RetryCommand))]
	public partial bool IsBusy { get; private set; }

	[ObservableProperty]
	public partial string StatusText { get; private set; }

	/// <summary>Why the look-up failed, in plain English (null when it didn't).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string ErrorText { get; private set; }

	public bool HasError => !string.IsNullOrEmpty(ErrorText);

	public bool HasRows => Rows.Count > 0;

	public string Caution => "The online flood map (NFHL) can differ from the effective FIRM your community adopted. Check the values against the FIRM before relying on them.";

	/// <summary>Starts (or restarts) the look-up.</summary>
	public void Start()
	{
		_cancel?.Cancel();
		_cancel = new CancellationTokenSource();
		Lookup = RunAsync(_cancel.Token);
	}

	private async Task RunAsync(CancellationToken ct)
	{
		IsBusy = true;
		ErrorText = null;
		Rows.Clear();
		OnPropertyChanged(nameof(HasRows));
		StatusText = "Asking the online services…";
		try
		{
			OnlineLookup lookup = new OnlineLookup(_app.HttpClientFactory(), _app.Store.Reference);
			OnlineLookupResult result = await lookup.LookupAsync(_property, ct);
			if (ct.IsCancellationRequested)
			{
				return;
			}
			_retrievedOn = result.RetrievedOn;
			foreach (OnlineProposal p in result.Proposals)
			{
				ProposalRowViewModel row = new ProposalRowViewModel(p, _property, _app.Store.Reference);
				row.PropertyChanged += (_, _) => ApplyCommand.NotifyCanExecuteChanged();
				Rows.Add(row);
			}
			string retrieved = result.RetrievedOn.ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"));
			StatusText = result.Note ?? (Rows.Count == 0 ? "Nothing was found." : $"Found {Rows.Count} values ({retrieved}). Tick the ones to use.");
			if (result.Note != null && Rows.Count > 0)
			{
				StatusText = result.Note + " The values found are below.";
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
		}
		catch (OnlineServiceException ex)
		{
			AppLog.Write("Online look-up failed: " + ex.Detail, ex);
			ErrorText = ex.Message;
			StatusText = null;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Online look-up failed", ex);
			ErrorText = "The look-up didn't work. Check the internet connection and try again. (" + ex.Message + ")";
			StatusText = null;
		}
		finally
		{
			IsBusy = false;
			OnPropertyChanged(nameof(HasRows));
			ApplyCommand.NotifyCanExecuteChanged();
		}
	}

	[RelayCommand(CanExecute = nameof(CanRetry))]
	private void Retry() => Start();

	private bool CanRetry() => !IsBusy;

	[RelayCommand(CanExecute = nameof(CanApply))]
	private void Apply()
	{
		List<ProposalRowViewModel> ticked = Rows.Where(r => r.IsChecked && r.CanCheck).ToList();
		Close(new AutoFillChoice(ticked.Select(r => r.Proposal.Value).ToList(),
			ticked.Any(r => r.Proposal.Source == OnlineSource.FemaNfhl),
			ticked.Any(r => r.Proposal.Source == OnlineSource.CensusGeocoder),
			_retrievedOn));
	}

	private bool CanApply() => !IsBusy && Rows.Any(r => r.IsChecked && r.CanCheck);
}

/// <summary>What the bulk look-up found for one structure.</summary>
public sealed partial class StructureProposalsViewModel : ObservableObject
{
	public StructureProposalsViewModel(Guid assessmentId, PropertyRecord property, string address, OnlineLookupResult result, string error, ReferenceData reference)
	{
		AssessmentId = assessmentId;
		Address = address;
		RetrievedOn = result?.RetrievedOn ?? DateTime.UtcNow;
		Message = error ?? result?.Note ?? (result == null || result.Proposals.Count == 0 ? "Nothing was found." : null);
		IsError = error != null;
		if (result != null)
		{
			foreach (OnlineProposal p in result.Proposals)
			{
				Rows.Add(new ProposalRowViewModel(p, property, reference));
			}
		}
	}

	public Guid AssessmentId { get; }

	public string Address { get; }

	public DateTime RetrievedOn { get; }

	/// <summary>Why nothing (or not everything) was found, or the service's error.</summary>
	public string Message { get; }

	public bool HasMessage => Message != null;

	public bool IsError { get; }

	public ObservableCollection<ProposalRowViewModel> Rows { get; } = new();
}

/// <summary>
/// Structures, Change, then Look up flood data online: the review of what the look-up found for each checked
/// structure, with tick boxes. Apply saves the ticked values in one batch of edit history (Final assessments are left
/// alone) and records where they came from.
/// </summary>
public sealed partial class BulkAutoFillSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;

	public BulkAutoFillSheetViewModel(AppServices app, IReadOnlyList<StructureProposalsViewModel> structures)
		: base("Flood data found online")
	{
		_app = app;
		Structures = structures;
		foreach (ProposalRowViewModel r in structures.SelectMany(s => s.Rows))
		{
			r.PropertyChanged += (_, _) =>
			{
				ApplyCommand.NotifyCanExecuteChanged();
				OnPropertyChanged(nameof(Summary));
			};
		}
	}

	public IReadOnlyList<StructureProposalsViewModel> Structures { get; }

	public string Caution => "The online flood map (NFHL) can differ from the effective FIRM your community adopted. Check the values against the FIRM before relying on them.";

	public string Summary
	{
		get
		{
			int found = Structures.Count(s => s.Rows.Count > 0);
			int failed = Structures.Count(s => s.IsError);
			int ticked = Structures.Sum(s => s.Rows.Count(r => r.IsChecked && r.CanCheck));
			string problems = failed == 0 ? "" : $" {failed} couldn't be looked up.";
			return $"Values found for {found} of {Structures.Count} structure{(Structures.Count == 1 ? "" : "s")}.{problems} {ticked} ticked to apply.";
		}
	}

	[ObservableProperty]
	public partial string ResultText { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
	public partial bool Applied { get; private set; }

	[RelayCommand(CanExecute = nameof(CanApply))]
	private void Apply()
	{
		List<(StructureProposalsViewModel Structure, List<ProposalRowViewModel> Rows)> chosen = Structures
			.Select(s => (s, s.Rows.Where(r => r.IsChecked && r.CanCheck).ToList()))
			.Where(x => x.Item2.Count > 0)
			.ToList();
		List<Guid> ids = chosen.Select(c => c.Structure.AssessmentId).ToList();
		string batch = Guid.NewGuid().ToString("N");
		int changed = 0;
		int locked = 0;
		try
		{
			_app.Writes.Write(ids, () =>
			{
				// One batch; the event says where the values came from, so structures are grouped by it.
				foreach (IGrouping<string, (StructureProposalsViewModel Structure, List<ProposalRowViewModel> Rows)> group in chosen.GroupBy(c =>
					OnlineLookup.EventLabel(c.Rows.Any(r => r.Proposal.Source == OnlineSource.FemaNfhl), c.Rows.Any(r => r.Proposal.Source == OnlineSource.CensusGeocoder), c.Structure.RetrievedOn)))
				{
					List<AssessmentFile> files = new List<AssessmentFile>();
					foreach ((StructureProposalsViewModel s, List<ProposalRowViewModel> rows) in group)
					{
						AssessmentFile f = _app.Store.LoadAssessment(s.AssessmentId);
						if (f != null)
						{
							FieldRules.Apply(f, rows.Select(r => OnlineLookup.Change(r.Proposal.Value)).ToList(), _app.Store.Calculator);
							files.Add(f);
						}
					}
					BulkResult r = _app.Store.SaveAssessments(files, new SaveContext { Via = SaveVia.Online, BatchId = batch, Event = group.Key });
					changed += r.Changed;
					locked += r.Locked.Count;
				}
			});
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the looked-up values", ex);
			return;
		}
		Applied = true;
		ResultText = $"Updated {changed} structure{(changed == 1 ? "" : "s")}." + (locked == 0 ? "" : $" {locked} Final {(locked == 1 ? "was" : "were")} left as {(locked == 1 ? "it was" : "they were")}.");
	}

	private bool CanApply() => !Applied && Structures.Any(s => s.Rows.Any(r => r.IsChecked && r.CanCheck));
}

/// <summary>One photo of a folder being matched to structures.</summary>
public sealed partial class PhotoMatchRowViewModel : ObservableObject
{
	public PhotoMatchRowViewModel(PhotoMatch match, Guid? assessmentId)
	{
		Match = match;
		AssessmentId = match.Candidate == null ? null : assessmentId;
		Include = CanInclude;
	}

	public PhotoMatch Match { get; }

	public Guid? AssessmentId { get; }

	public string Path => Match.PhotoPath;

	public string FileName => System.IO.Path.GetFileName(Match.PhotoPath);

	public string Structure => Match.Candidate?.Address ?? "No structure";

	public string Reason => Match.Reason;

	public string Distance => Match.DistanceMeters is double d ? d.ToString("0", CultureInfo.InvariantCulture) + " m" : "";

	/// <summary>Only a photo matched to one structure can be attached; ambiguous and unmatched photos are left out.</summary>
	public bool CanInclude => Match.Candidate != null && AssessmentId != null;

	public bool IsProblem => !CanInclude;

	public string IncludeLabel => $"Attach {FileName} to {Structure}";

	[ObservableProperty]
	public partial bool Include { get; set; }
}

/// <summary>
/// Import and export, then Match a photo folder to structures: each photo with the structure it was matched to (by
/// file name or GPS) and why. The ticked photos are attached to each structure's latest assessment in one batch.
/// </summary>
public sealed partial class PhotoFolderSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly string _folder;

	public PhotoFolderSheetViewModel(AppServices app, string folder, IReadOnlyList<PhotoMatch> matches, IReadOnlyDictionary<Guid, Guid> latestAssessmentByStructure)
		: base("Match photos to structures")
	{
		_app = app;
		_folder = folder;
		Rows = matches.Select(m => new PhotoMatchRowViewModel(m,
			m.Candidate != null && latestAssessmentByStructure.TryGetValue(m.Candidate.Id, out Guid a) ? a : null)).ToList();
		foreach (PhotoMatchRowViewModel r in Rows)
		{
			r.PropertyChanged += (_, _) =>
			{
				AttachCommand.NotifyCanExecuteChanged();
				OnPropertyChanged(nameof(Summary));
			};
		}
	}

	public string FolderName => Path.GetFileName(_folder.TrimEnd(Path.DirectorySeparatorChar));

	public IReadOnlyList<PhotoMatchRowViewModel> Rows { get; }

	public string Summary
	{
		get
		{
			int matched = Rows.Count(r => r.CanInclude);
			int ticked = Rows.Count(r => r.Include && r.CanInclude);
			return $"{Rows.Count} photo{(Rows.Count == 1 ? "" : "s")} in {FolderName}: {matched} matched to a structure, {Rows.Count - matched} not. "
				+ $"{ticked} ticked to attach to each structure's latest assessment.";
		}
	}

	[RelayCommand(CanExecute = nameof(CanAttach))]
	private async Task Attach()
	{
		List<PhotoMatchRowViewModel> chosen = Rows.Where(r => r.Include && r.CanInclude).ToList();
		List<Guid> ids = chosen.Select(r => r.AssessmentId!.Value).Distinct().ToList();
		SdeStore store = _app.Store;
		string temp = Path.Combine(Path.GetTempPath(), "sde-photos");
		SaveContext ctx = SaveContext.Batch(SaveVia.PhotoFolder, "Photos added from the folder " + FolderName);
		JobOutcome<BulkResult> outcome = await _app.Writes.WriteAsync(ids, () => _app.Jobs.RunAsync("Attaching photos", (progress, ct) =>
		{
			List<AttachedPhoto> photos = new List<AttachedPhoto>();
			try
			{
				for (int i = 0; i < chosen.Count; i++)
				{
					ct.ThrowIfCancellationRequested();
					PhotoMatchRowViewModel row = chosen[i];
					progress.Report(new JobProgress(i, chosen.Count, "Copying " + row.FileName));
					Sde.Interop.Photos.PhotoInfo info = ReadDetails(row.Path);
					string stored = PhotoConversion.Compatible(row.Path, temp);
					AttachmentRecord a = store.ImportPhoto(row.AssessmentId!.Value, stored, Path.GetFileNameWithoutExtension(row.Path));
					if (stored != row.Path)
					{
						File.Delete(stored);
					}
					photos.Add(new AttachedPhoto(row.AssessmentId.Value, a, new PhotoInfoRecord
					{
						TakenOn = info.TakenOn,
						Latitude = info.Latitude,
						Longitude = info.Longitude,
						OriginalName = row.FileName,
					}));
				}
				progress.Report(new JobProgress(chosen.Count, chosen.Count, "Saving"));
				BulkResult result = store.AttachPhotos(photos, ctx);
				DeleteCopies(store, photos.Where(p => result.Locked.Contains(p.AssessmentID)));
				return result;
			}
			catch
			{
				DeleteCopies(store, photos);
				throw;
			}
		}, "Couldn't attach the photos"));
		if (!outcome.Completed)
		{
			return;
		}
		BulkResult r = outcome.Value;
		int attached = chosen.Count(c => !r.Locked.Contains(c.AssessmentId!.Value));
		Close(r);
		_app.Dialogs.Notify("Photos attached",
			$"Attached {attached} photo{(attached == 1 ? "" : "s")} to {r.Changed} structure{(r.Changed == 1 ? "" : "s")}."
			+ (r.Locked.Count == 0 ? "" : $" {r.Locked.Count} Final assessment{(r.Locked.Count == 1 ? " was" : "s were")} left as {(r.Locked.Count == 1 ? "it was" : "they were")}."));
	}

	private bool CanAttach() => Rows.Any(r => r.Include && r.CanInclude);

	private static Sde.Interop.Photos.PhotoInfo ReadDetails(string path)
	{
		try
		{
			return PhotoMetadata.Read(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return new Sde.Interop.Photos.PhotoInfo(null, null, null);
		}
	}

	private static void DeleteCopies(SdeStore store, IEnumerable<AttachedPhoto> photos)
	{
		foreach (AttachedPhoto p in photos)
		{
			try
			{
				File.Delete(store.PhotoPath(p.Attachment));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				AppLog.Write("Couldn't remove a copied photo", ex);
			}
		}
	}
}
