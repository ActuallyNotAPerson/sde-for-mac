using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports.Letters;

namespace Sde.App.ViewModels.Sheets;

/// <summary>A stored value and how it reads in a list (a compliance path, an ICC status).</summary>
public sealed record ChoiceItem(string Value, string Label)
{
	public override string ToString() => Label;
}

/// <summary>One line of a compliance case's timeline: what happened, when and who recorded it.</summary>
public sealed class ComplianceEntryViewModel(ComplianceEntryRecord entry)
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public ComplianceEntryRecord Entry { get; } = entry;

	public string Kind => Entry.Kind ?? "Note";

	public string Text => Entry.Text;

	/// <summary>"Oct 22, 2026 3:14 PM · Reviewer A".</summary>
	public string Byline => Entry.EntryOn.ToString("MMM d, yyyy h:mm tt", Us) + (string.IsNullOrWhiteSpace(Entry.CreatedBy) ? "" : " · " + Entry.CreatedBy.Trim());

	/// <summary>The icon beside the line, by its kind.</summary>
	public string Icon => Entry.Kind switch
	{
		"Letter" => "Letter",
		"Call" => "Phone",
		"Permit" => "Report",
		"Inspection" => "CheckCircle",
		"ICC" => "Cost",
		"Status" => "Flag",
		_ => "Comment",
	};
}

/// <summary>One of the three compliance letters, with the button that writes it.</summary>
public sealed record ComplianceLetterChoice(string Kind, string Label, IAsyncRelayCommand<string> Write)
{
	public string AccessibleName => "Write the " + Label.ToLowerInvariant() + " letter";
}

/// <summary>
/// A compliance case in full: every field (path, permit and deadline, ICC claim, elevation certificate, final
/// inspection, certificate of compliance, open or closed), its timeline newest first with Add note, and the three
/// compliance letters, each written from its template with the case's fields and recorded on the timeline.
/// </summary>
public sealed partial class ComplianceCaseSheetViewModel : SheetViewModel
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>The kinds of line a person can add to the timeline.</summary>
	public static IReadOnlyList<string> NoteKinds { get; } = new[] { "Note", "Call", "Inspection", "Permit", "ICC" };

	public static IReadOnlyList<ChoiceItem> IccChoices { get; } = IccStatuses.All.Select(s => new ChoiceItem(s, IccStatuses.Label(s))).ToList();

	public static IReadOnlyList<ChoiceItem> StatusChoices { get; } = new[]
	{
		new ChoiceItem(ComplianceStatuses.Open, "Open"),
		new ChoiceItem(ComplianceStatuses.Closed, "Closed"),
	};

	private readonly AppServices _app;
	private readonly Action<Guid> _openAssessment;
	private readonly PropertyRecord _property;
	private ComplianceCaseRecord _case;
	private bool _loading;

	/// <param name="openAssessment">Opens the case's assessment (from the Compliance page); null where it is already open.</param>
	public ComplianceCaseSheetViewModel(AppServices app, Guid caseId, Action<Guid> openAssessment = null)
		: base("Compliance case")
	{
		_app = app;
		_openAssessment = openAssessment;
		_case = app.Store.GetComplianceCase(caseId) ?? throw new ArgumentException("That compliance case no longer exists.");
		_property = app.Store.GetProperty(_case.PropertyID) ?? new PropertyRecord();
		Address = AssessmentEditorViewModel.Address(_property, app.Store.Reference, "(no address)");
		PathChoices = new[] { new ChoiceItem(null, CompliancePaths.Label(null)) }
			.Concat(CompliancePaths.All.Where(p => p != CompliancePaths.Floodproof || _property.StructureTypeID != 1).Select(p => new ChoiceItem(p, CompliancePaths.Label(p))))
			.ToList();
		Letters = LetterKinds.Compliance.Select(k => new ComplianceLetterChoice(k, LetterLabel(k), WriteLetterCommand)).ToList();
		Show(_case);
		LoadTimeline();
	}

	public override SheetSize Size => SheetSize.Large;

	public Guid CaseId => _case.CaseID;

	public string Address { get; }

	/// <summary>"Pat Example · Substantial damage (SD) · opened Oct 22, 2026 by Reviewer A".</summary>
	public string Detail
	{
		get
		{
			string owner = string.Join(" ", new[] { _property.OwnerFirstName, _property.OwnerLastName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
			string kind = _case.Kind == "SI" ? "Substantial improvement (SI)" : "Substantial damage (SD)";
			string opened = _case.CreatedOn is DateTime on ? "opened " + on.ToString("MMM d, yyyy", Us) + (string.IsNullOrWhiteSpace(_case.CreatedBy) ? "" : " by " + _case.CreatedBy.Trim()) : null;
			return string.Join(" · ", new[] { owner, kind, opened }.Where(s => !string.IsNullOrEmpty(s)));
		}
	}

	/// <summary>True for a substantial damage case opened from this window's lists, which can open its assessment.</summary>
	public bool CanOpenAssessment => _openAssessment != null && _case.AssessmentID != null;

	public IReadOnlyList<ChoiceItem> PathChoices { get; }

	// ───────────── The case's fields ─────────────

	[ObservableProperty]
	public partial ChoiceItem Path { get; set; }

	[ObservableProperty]
	public partial string PermitNumber { get; set; }

	[ObservableProperty]
	public partial DateTime? PermitIssuedOn { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(OverdueText), nameof(IsOverdue))]
	public partial DateTime? Deadline { get; set; }

	[ObservableProperty]
	public partial string IccClaimNumber { get; set; }

	[ObservableProperty]
	public partial ChoiceItem IccStatus { get; set; }

	[ObservableProperty]
	public partial decimal? IccAmount { get; set; }

	[ObservableProperty]
	public partial DateTime? EcReceivedOn { get; set; }

	[ObservableProperty]
	public partial DateTime? FinalInspectionOn { get; set; }

	[ObservableProperty]
	public partial DateTime? ComplianceCertOn { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsCaseClosed), nameof(OverdueText), nameof(IsOverdue))]
	public partial ChoiceItem Status { get; set; }

	[ObservableProperty]
	public partial string ClosedReason { get; set; }

	[ObservableProperty]
	public partial string Notes { get; set; }

	public bool IsCaseClosed => Status?.Value == ComplianceStatuses.Closed;

	public bool IsOverdue => !IsCaseClosed && Deadline is DateTime d && d.Date < DateTime.Today;

	/// <summary>"The deadline passed 12 days ago and the case is still open."</summary>
	public string OverdueText
	{
		get
		{
			if (!IsOverdue)
			{
				return null;
			}
			int days = (DateTime.Today - Deadline.Value.Date).Days;
			return days == 1 ? "The deadline passed yesterday and the case is still open." : $"The deadline passed {days} days ago and the case is still open.";
		}
	}

	/// <summary>True when a field was changed and not saved yet.</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	public partial bool IsDirty { get; private set; }

	/// <summary>Why the last save was refused, or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; private set; }

	public bool HasError => Error != null;

	/// <summary>"Saved." after a save.</summary>
	[ObservableProperty]
	public partial string SavedText { get; private set; }

	partial void OnPathChanged(ChoiceItem value) => Edited();

	partial void OnPermitNumberChanged(string value) => Edited();

	partial void OnPermitIssuedOnChanged(DateTime? value) => Edited();

	partial void OnDeadlineChanged(DateTime? value) => Edited();

	partial void OnIccClaimNumberChanged(string value) => Edited();

	partial void OnIccStatusChanged(ChoiceItem value) => Edited();

	partial void OnIccAmountChanged(decimal? value) => Edited();

	partial void OnEcReceivedOnChanged(DateTime? value) => Edited();

	partial void OnFinalInspectionOnChanged(DateTime? value) => Edited();

	partial void OnComplianceCertOnChanged(DateTime? value) => Edited();

	partial void OnStatusChanged(ChoiceItem value) => Edited();

	partial void OnClosedReasonChanged(string value) => Edited();

	partial void OnNotesChanged(string value) => Edited();

	private void Edited()
	{
		if (!_loading)
		{
			IsDirty = true;
			SavedText = null;
		}
	}

	private void Show(ComplianceCaseRecord c)
	{
		_loading = true;
		Path = PathChoices.FirstOrDefault(p => p.Value == c.Path) ?? PathChoices[0];
		PermitNumber = c.PermitNumber;
		PermitIssuedOn = c.PermitIssuedOn;
		Deadline = c.Deadline;
		IccClaimNumber = c.IccClaimNumber;
		IccStatus = IccChoices.FirstOrDefault(s => s.Value == (c.IccStatus ?? IccStatuses.NotFiled)) ?? IccChoices[0];
		IccAmount = c.IccAmount;
		EcReceivedOn = c.EcReceivedOn;
		FinalInspectionOn = c.FinalInspectionOn;
		ComplianceCertOn = c.ComplianceCertOn;
		Status = c.IsOpen ? StatusChoices[0] : StatusChoices[1];
		ClosedReason = c.ClosedReason;
		Notes = c.Notes;
		_loading = false;
		IsDirty = false;
	}

	/// <summary>The case as the form shows it (a copy; the stored case changes only on Save).</summary>
	private ComplianceCaseRecord FormCase() => new ComplianceCaseRecord
	{
		CaseID = _case.CaseID,
		PropertyID = _case.PropertyID,
		AssessmentID = _case.AssessmentID,
		DeterminationID = _case.DeterminationID,
		Kind = _case.Kind,
		Path = Path?.Value,
		PermitNumber = Blank(PermitNumber),
		PermitIssuedOn = PermitIssuedOn?.Date,
		Deadline = Deadline?.Date,
		IccClaimNumber = Blank(IccClaimNumber),
		IccStatus = IccStatus?.Value ?? IccStatuses.NotFiled,
		IccAmount = IccAmount,
		EcReceivedOn = EcReceivedOn?.Date,
		FinalInspectionOn = FinalInspectionOn?.Date,
		ComplianceCertOn = ComplianceCertOn?.Date,
		Status = Status?.Value ?? ComplianceStatuses.Open,
		ClosedOn = _case.ClosedOn,
		ClosedReason = IsCaseClosed ? Blank(ClosedReason) : null,
		Notes = Blank(Notes),
		CreatedOn = _case.CreatedOn,
		CreatedBy = _case.CreatedBy,
		UpdatedOn = _case.UpdatedOn,
	};

	/// <summary>Saves the form. Changes to the path, ICC status, deadline and open or closed go on the timeline.</summary>
	[RelayCommand(CanExecute = nameof(IsDirty))]
	public void Save() => SaveCase();

	private bool SaveCase()
	{
		try
		{
			_case = _app.Store.SaveComplianceCase(FormCase());
		}
		catch (ArgumentException ex)
		{
			Error = ex.Message;
			return false;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the compliance case", ex);
			return false;
		}
		Error = null;
		Show(_case);
		SavedText = "Saved.";
		LoadTimeline();
		return true;
	}

	/// <summary>Done: saves any changes, then closes.</summary>
	[RelayCommand]
	private void Done()
	{
		if (IsDirty && !SaveCase())
		{
			return;
		}
		Close(true);
	}

	[RelayCommand]
	private void OpenAssessment()
	{
		if (_case.AssessmentID is Guid id && _openAssessment != null && (!IsDirty || SaveCase()))
		{
			Close(true);
			_openAssessment(id);
		}
	}

	// ───────────── Timeline ─────────────

	/// <summary>The case's timeline, newest first.</summary>
	public ObservableCollection<ComplianceEntryViewModel> Entries { get; } = new();

	[ObservableProperty]
	public partial string NoteKind { get; set; } = "Note";

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(AddNoteCommand))]
	public partial string NoteText { get; set; }

	private void LoadTimeline()
	{
		Entries.Clear();
		foreach (ComplianceEntryRecord e in _app.Store.GetComplianceEntries(_case.CaseID).AsEnumerable().Reverse())
		{
			Entries.Add(new ComplianceEntryViewModel(e));
		}
	}

	/// <summary>Adds a line to the timeline (a note, a call, an inspection) straight away.</summary>
	[RelayCommand(CanExecute = nameof(CanAddNote))]
	public void AddNote()
	{
		try
		{
			_app.Store.AddComplianceEntry(_case.CaseID, NoteKind ?? "Note", NoteText.Trim());
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't add the note", ex);
			return;
		}
		NoteText = null;
		LoadTimeline();
	}

	private bool CanAddNote() => !string.IsNullOrWhiteSpace(NoteText);

	// ───────────── Letters ─────────────

	/// <summary>The three compliance letters, in the order they are usually sent.</summary>
	public IReadOnlyList<ComplianceLetterChoice> Letters { get; }

	/// <summary>What the last letter did.</summary>
	[ObservableProperty]
	public partial string LetterText { get; private set; }

	/// <summary>Placeholders in the template that aren't fields (left in the letter as typed).</summary>
	[ObservableProperty]
	public partial string LetterWarning { get; private set; }

	public string TemplatesFolder => LetterTemplateLocation.Folder(_app);

	private static string LetterLabel(string kind) => kind switch
	{
		LetterKinds.ComplianceOptions => "Options and deadline",
		LetterKinds.IccSupport => "ICC support",
		_ => "Deadline reminder",
	};

	/// <summary>
	/// Writes one letter from its template with this case's fields (saving the case first), records it in the letter
	/// log and on the timeline, and opens it. When the template is missing, offers to create the starter compliance
	/// letters first.
	/// </summary>
	[RelayCommand]
	public async Task WriteLetter(string kind)
	{
		if (IsDirty && !SaveCase())
		{
			return;
		}
		string folder = TemplatesFolder;
		string template = System.IO.Path.Combine(folder, LetterTemplates.FileName(kind));
		if (!File.Exists(template))
		{
			if (!await _app.Dialogs.ConfirmAsync("Create the compliance letters?",
				"There's no template for this letter yet. SDE can put its three starter compliance letters in your letter templates folder. Review and adapt them with your community's counsel before you send any.",
				"Create them"))
			{
				return;
			}
			try
			{
				LetterTemplates.WriteComplianceTemplates(folder);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_app.Errors.Show("Couldn't create the compliance letters", ex);
				return;
			}
		}
		string suggested = string.Concat($"{Address} ({kind}).docx".Split(System.IO.Path.GetInvalidFileNameChars())).Replace(':', '-');
		string path = await _app.Dialogs.SaveFileAsync("Save the letter", suggested, "Word document", "docx");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		AssessmentRecord assessment = _case.AssessmentID is Guid id ? _app.Store.LoadAssessment(id)?.Assessment : null;
		LetterSender s = _app.Store.GetSetting<LetterSender>(LetterSender.SettingKey) ?? new LetterSender();
		LetterContext context = new LetterContext
		{
			CommunityName = s.CommunityName, OfficeName = s.OfficeName, OfficeAddress = s.OfficeAddress, OfficePhone = s.OfficePhone,
			OfficeEmail = s.OfficeEmail, OfficialName = s.OfficialName, OfficialTitle = s.OfficialTitle,
			Status = assessment == null ? null : AssessmentStatuses.Label(_app.Store.GetStatus(assessment.AssessmentID).Value),
			Compliance = _case,
		};
		LetterMergeResult result;
		try
		{
			result = LetterMerge.Merge(template, LetterFields.For(_property, assessment, _app.Store.Reference, context), path);
			_app.Store.LogComplianceLetter(_case.CaseID, kind, path);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't write the letter", ex);
			return;
		}
		LoadTimeline();
		LetterText = $"Wrote \"{System.IO.Path.GetFileName(path)}\" and added it to the timeline.";
		LetterWarning = result.UnknownPlaceholders.Count == 0 ? null
			: "These placeholders aren't fields, so they were left in the letter as typed: " + string.Join(", ", result.UnknownPlaceholders.Select(p => "«" + p + "»")) + ".";
		_app.Dialogs.Open(path);
	}

	private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
