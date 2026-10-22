using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Quality;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>One assessment in the review queue: its result, status, open quality findings and open comment threads.</summary>
public sealed partial class ReviewRowViewModel : ObservableObject
{
	public ReviewRowViewModel(StructureSummary summary, ReferenceData reference, AssessmentStatus status, int findings, int errors, int warnings, int comments)
	{
		Structure = new StructureRowViewModel(summary, reference);
		StatusValue = status;
		FindingCount = findings;
		ErrorCount = errors;
		WarningCount = warnings;
		CommentCount = comments;
	}

	/// <summary>The same row as on Structures (address, result and its colours).</summary>
	public StructureRowViewModel Structure { get; }

	public StructureSummary Summary => Structure.Summary;

	public Guid AssessmentID => Summary.AssessmentID!.Value;

	public string Address => Structure.Address;

	/// <summary>The owner and source record, for the address's tooltip.</summary>
	public string Detail => Structure.Detail;

	public AssessmentStatus StatusValue { get; }

	public string Status => AssessmentStatuses.Label(StatusValue);

	/// <summary>Where the status is in the review workflow, for sorting: Draft, Appeal, Reviewed, Final, Letter sent.</summary>
	public int StatusOrder => StatusValue switch
	{
		AssessmentStatus.Draft => 0,
		AssessmentStatus.Appeal => 1,
		AssessmentStatus.Reviewed => 2,
		AssessmentStatus.Final => 3,
		_ => 4,
	};

	public bool IsLocked => AssessmentStatuses.IsLocked(StatusValue);

	public string Inspected => Structure.Inspected;

	public DateTime InspectedOn => Summary.DateofInspection ?? DateTime.MinValue;

	public string Inspector => Structure.Inspector;

	public ResultState State => Structure.State;

	public string PercentText => Structure.PercentText;

	/// <summary>The percent damaged for sorting (-1 when there is none).</summary>
	public decimal PercentSort => Structure.HasPercent ? Summary.PercentDamage ?? -1 : -1;

	public string Result => Structure.Result;

	/// <summary>Open (not set aside) quality findings about this assessment or its structure.</summary>
	public int FindingCount { get; }

	public int ErrorCount { get; }

	public int WarningCount { get; }

	public bool HasFindings => FindingCount > 0;

	public string FindingText => FindingCount.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

	/// <summary>Red when one of the findings is an error, amber for warnings, grey for notes.</summary>
	public Tone FindingTone => ErrorCount > 0 ? Tone.Danger : WarningCount > 0 ? Tone.Warning : Tone.Neutral;

	public string FindingTip => FindingCount == 1 ? "1 open check" : $"{FindingCount} open checks" + (ErrorCount > 0 ? $", {ErrorCount} of them errors" : "");

	/// <summary>Open comment threads.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasComments), nameof(CommentText))]
	public partial int CommentCount { get; set; }

	public bool HasComments => CommentCount > 0;

	public string CommentText => CommentCount.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

	[ObservableProperty]
	public partial bool IsChecked { get; set; }

	public string AccessibleCheck => "Include " + Address;
}

/// <summary>One quality finding, with what can be done about it: open the assessment at its field, set it aside, bring it back.</summary>
public sealed class QaFindingViewModel(QaFinding finding, string address)
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>Opens the assessment at the finding's field (the Review page's command).</summary>
	public IRelayCommand<QaFindingViewModel> Open { get; init; }

	/// <summary>Sets the finding aside with a reason.</summary>
	public IAsyncRelayCommand<QaFindingViewModel> Dismiss { get; init; }

	/// <summary>Brings a finding that was set aside back.</summary>
	public IRelayCommand<QaFindingViewModel> BringBack { get; init; }

	public bool IsError => Finding.Severity == QaSeverity.Error;

	public bool IsWarning => Finding.Severity == QaSeverity.Warning;

	public bool IsNote => Finding.Severity == QaSeverity.Info;

	/// <summary>The icon beside the finding, by its severity.</summary>
	public string Icon => Finding.Severity switch
	{
		QaSeverity.Error => "ErrorCircle",
		QaSeverity.Warning => "Warning",
		_ => "Info",
	};

	public QaFinding Finding { get; } = finding;

	public QaRuleInfo Rule { get; } = QaRules.Rule(finding.RuleId);

	/// <summary>The structure's address, or the event's name for a finding about a whole event.</summary>
	public string Address { get; } = address;

	public string Message => Finding.Message;

	public bool IsDismissed => Finding.IsDismissed;

	public bool IsOpen => !IsDismissed;

	/// <summary>"Set aside by Reviewer A on Oct 22, 2026: two units on one parcel".</summary>
	public string DismissedText
	{
		get
		{
			QaDismissalRecord d = Finding.Dismissal;
			if (d == null)
			{
				return null;
			}
			string who = string.IsNullOrWhiteSpace(d.DismissedBy) ? "" : " by " + d.DismissedBy.Trim();
			string when = d.DismissedOn is DateTime on ? " on " + on.ToString("MMM d, yyyy", Us) : "";
			return $"Set aside{who}{when}: {d.Reason}";
		}
	}

	public bool CanDismiss => Rule?.CanDismiss == true && !IsDismissed;

	public bool CanOpen => Finding.AssessmentId != null;

	public string FieldLabel => FieldCatalog.Get(Finding.FieldKey)?.Label;

	public bool HasField => FieldLabel != null;

	/// <summary>"Go to field" when the finding names a field, else "Open".</summary>
	public string OpenText => HasField ? "Go to field" : "Open";

	public string OpenTip => HasField ? $"Open the assessment at {FieldLabel}" : "Open the assessment";

	public string AccessibleOpen => HasField ? $"Go to {FieldLabel} of {Address}" : "Open " + Address;

	public string AccessibleDismiss => $"Set aside {Rule?.Title ?? Finding.RuleId} for {Address}";

	public string AccessibleBringBack => $"Bring back {Rule?.Title ?? Finding.RuleId} for {Address}";
}

/// <summary>The findings of one quality check, under its title, severity and description.</summary>
public sealed class QaGroupViewModel(QaRuleInfo rule, IReadOnlyList<QaFindingViewModel> findings)
{
	public QaRuleInfo Rule { get; } = rule;

	public IReadOnlyList<QaFindingViewModel> Findings { get; } = findings;

	public string Title => Rule.Title;

	public string Description => Rule.Description;

	public string SeverityText => Rule.Severity switch
	{
		QaSeverity.Error => "Error",
		QaSeverity.Warning => "Warning",
		_ => "Note",
	};

	public Tone Tone => Rule.Severity switch
	{
		QaSeverity.Error => Tone.Danger,
		QaSeverity.Warning => Tone.Warning,
		_ => Tone.Info,
	};

	/// <summary>"3 open, 1 set aside".</summary>
	public string CountText
	{
		get
		{
			int open = Findings.Count(f => f.IsOpen);
			int aside = Findings.Count - open;
			return $"{open} open" + (aside > 0 ? $", {aside} set aside" : "");
		}
	}

	/// <summary>Said under checks that block Final and so can't be set aside.</summary>
	public string NoDismissNote => Rule.CanDismiss ? null : "This check can't be set aside. Fix it instead.";

	public bool HasNoDismissNote => NoDismissNote != null;
}

/// <summary>
/// The Review page: the assessments waiting for review (Draft and Reviewed unless another status is chosen) with their
/// results, open quality findings and comment threads; the quality findings grouped by check, to open, set aside or
/// bring back; the selected assessment's comments; and Mark reviewed. It follows the event chosen in the sidebar.
/// </summary>
public sealed partial class ReviewViewModel : ViewModelBase
{
	/// <summary>The status filter's choices; the first is the default.</summary>
	public static IReadOnlyList<string> StatusChoices { get; } = new[] { "Draft and Reviewed", "Draft", "Reviewed", "Appeal", "Final", "Letter sent", "Every status" };

	/// <summary>The severity filter's choices on the Quality checks tab.</summary>
	public static IReadOnlyList<string> SeverityChoices { get; } = new[] { "Every check", "Errors and warnings", "Errors only" };

	private const int EveryStatus = 6;
	private const int ErrorsOnly = 2;

	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private List<ReviewRowViewModel> _all = new();
	private List<QaFinding> _findings = new();
	private Dictionary<Guid, string> _addresses = new();
	private HashSet<Guid> _only;
	private bool _filling;

	public ReviewViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
	}

	/// <summary>Set by the window while this is the page showing (it then reloads when the data changes).</summary>
	public bool IsActive { get; set; }

	/// <summary>The queue: the assessments that pass the filters, problems first.</summary>
	public ObservableCollection<ReviewRowViewModel> Rows { get; } = new();

	/// <summary>The quality findings that pass the filters, by check.</summary>
	public ObservableCollection<QaGroupViewModel> Groups { get; } = new();

	/// <summary>The selected assessment's open findings, beside the queue.</summary>
	public ObservableCollection<QaFindingViewModel> SelectedFindings { get; } = new();

	/// <summary>Every finding of the last run, open and set aside.</summary>
	public IReadOnlyList<QaFinding> Findings => _findings;

	/// <summary>0 = the queue, 1 = the quality checks.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsQueueTab), nameof(IsChecksTab))]
	public partial int TabIndex { get; set; }

	public bool IsQueueTab => TabIndex == 0;

	public bool IsChecksTab => TabIndex == 1;

	/// <summary>Which statuses the queue shows (an index into <see cref="StatusChoices"/>).</summary>
	[ObservableProperty]
	public partial int StatusFilter { get; set; }

	/// <summary>Which checks are listed (an index into <see cref="SeverityChoices"/>).</summary>
	[ObservableProperty]
	public partial int SeverityFilter { get; set; }

	/// <summary>Whether findings someone set aside are listed too.</summary>
	[ObservableProperty]
	public partial bool ShowDismissed { get; set; }

	partial void OnStatusFilterChanged(int value) => FillRows();

	partial void OnSeverityFilterChanged(int value) => FillGroups();

	partial void OnShowDismissedChanged(bool value) => FillGroups();

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(MarkReviewedCommand), nameof(OpenSelectedCommand))]
	[NotifyPropertyChangedFor(nameof(SelectedTitle), nameof(SelectedDetail))]
	public partial ReviewRowViewModel Selected { get; set; }

	partial void OnSelectedChanged(ReviewRowViewModel value) => LoadSelected();

	/// <summary>The selected assessment's comment threads (null when none is selected).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection))]
	public partial CommentThreadsViewModel SelectedComments { get; private set; }

	public bool HasSelection => SelectedComments != null;

	/// <summary>The selected assessment's address, for the panel beside the queue.</summary>
	public string SelectedTitle => Selected?.Address;

	/// <summary>"Draft · Substantially Damaged".</summary>
	public string SelectedDetail => Selected == null ? null : Selected.Status + " · " + Selected.Result;

	public bool HasSelectedFindings => SelectedFindings.Count > 0;

	/// <summary>"Showing only the 2 assessments being exported", while the lists are limited to some assessments.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasOnly))]
	public partial string OnlyLabel { get; private set; }

	public bool HasOnly => OnlyLabel != null;

	// ───────────── Headline numbers ─────────────

	/// <summary>Draft and Reviewed assessments in scope.</summary>
	[ObservableProperty]
	public partial int WaitingCount { get; private set; }

	[ObservableProperty]
	public partial int OpenFindingCount { get; private set; }

	[ObservableProperty]
	public partial int ErrorCount { get; private set; }

	/// <summary>Open comment threads on the assessments in scope.</summary>
	[ObservableProperty]
	public partial int OpenCommentCount { get; private set; }

	public string QueueTabText => $"Queue ({Rows.Count:N0})";

	public string ChecksTabText => $"Quality checks ({OpenFindingCount:N0})";

	public string Subtitle => _app.Scope.IsAll
		? "Check assessments before they are final: the queue, the quality checks and reviewers' comments."
		: $"Check the assessments in {_app.Scope.Event.Label} before they are final.";

	public string CountText => Rows.Count == 1 ? "1 assessment" : $"{Rows.Count:N0} assessments";

	public bool HasRows => Rows.Count > 0;

	/// <summary>Nothing to review at all in scope.</summary>
	public bool IsQueueEmpty => _all.Count == 0;

	/// <summary>There are assessments, but the filters hide every one.</summary>
	public bool NoRowsMatch => _all.Count > 0 && Rows.Count == 0;

	public string QueueEmptyTitle => _app.Scope.IsAll ? "No assessments yet" : $"Nothing in {_app.Scope.Event.Label} yet";

	public string QueueEmptyText => "Assessments appear here for review as soon as they are entered or imported.";

	public bool HasGroups => Groups.Count > 0;

	public bool ChecksEmpty => Groups.Count == 0;

	public string ChecksEmptyTitle => _findings.Count == 0 ? "No problems found" : "Nothing matches";

	public string ChecksEmptyText => _findings.Count == 0
		? "The quality checks found nothing to look at in these assessments."
		: "Show every check, or the ones set aside, to see more.";

	public int CheckedCount => _all.Count(r => r.IsChecked);

	public bool HasChecked => CheckedCount > 0;

	public string CheckedText => CheckedCount == 1 ? "1 checked" : $"{CheckedCount:N0} checked";

	/// <summary>The sidebar's count: Draft and Reviewed assessments in the chosen event (or the whole database).</summary>
	public static int CountWaiting(AppServices app) =>
		app.Store.ActiveAssessmentStatuses().Count(s => s.Value is AssessmentStatus.Draft or AssessmentStatus.Reviewed && app.Scope.Includes(s.Key));

	/// <summary>Reads the assessments in scope, runs the quality checks and counts the comments again.</summary>
	public void Reload()
	{
		HashSet<Guid> checkedIds = _all.Where(r => r.IsChecked).Select(r => r.AssessmentID).ToHashSet();
		Guid? selected = Selected?.AssessmentID;
		ReferenceData reference = _app.Store.Reference;
		List<StructureSummary> list = _app.Store.ListStructures().Where(s => s.AssessmentID != null && _app.Scope.Includes(s.AssessmentID)).ToList();
		List<Guid> ids = list.Select(s => s.AssessmentID!.Value).ToList();
		try
		{
			_findings = QualityChecks.Run(_app.Store, ids);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't run the quality checks", ex);
			_findings = new List<QaFinding>();
		}
		_addresses = list.GroupBy(s => s.PropertyID).ToDictionary(g => g.Key, g => new StructureRowViewModel(g.First()).Address);
		Dictionary<Guid, int> comments = _app.Store.OpenCommentCounts();
		ILookup<Guid, QaFinding> open = _findings.Where(f => !f.IsDismissed && f.AssessmentId != null).ToLookup(f => f.AssessmentId!.Value);
		Dictionary<Guid, AssessmentStatus> statuses = _app.Store.ActiveAssessmentStatuses();
		_all = list.Select(s =>
		{
			Guid id = s.AssessmentID!.Value;
			List<QaFinding> mine = open[id].ToList();
			return new ReviewRowViewModel(s, reference, statuses.GetValueOrDefault(id), mine.Count, mine.Count(f => f.Severity == QaSeverity.Error),
				mine.Count(f => f.Severity == QaSeverity.Warning), comments.GetValueOrDefault(id)) { IsChecked = checkedIds.Contains(id) };
		}).ToList();
		foreach (ReviewRowViewModel r in _all)
		{
			r.PropertyChanged += OnRowChanged;
		}

		WaitingCount = _all.Count(r => r.StatusValue is AssessmentStatus.Draft or AssessmentStatus.Reviewed);
		OpenFindingCount = _findings.Count(f => !f.IsDismissed);
		ErrorCount = _findings.Count(f => !f.IsDismissed && f.Severity == QaSeverity.Error);
		OpenCommentCount = _all.Sum(r => r.CommentCount);
		FillRows();
		FillGroups();
		Selected = selected is Guid keep ? Rows.FirstOrDefault(r => r.AssessmentID == keep) : null;
		LoadSelected();
		OnPropertyChanged(nameof(Subtitle));
		OnPropertyChanged(nameof(QueueEmptyTitle));
		OnPropertyChanged(nameof(ChecksTabText));
		RefreshChecked();
	}

	private void OnRowChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ReviewRowViewModel.IsChecked) && !_filling)
		{
			RefreshChecked();
		}
	}

	private void RefreshChecked()
	{
		OnPropertyChanged(nameof(CheckedCount));
		OnPropertyChanged(nameof(HasChecked));
		OnPropertyChanged(nameof(CheckedText));
		MarkReviewedCommand.NotifyCanExecuteChanged();
	}

	/// <summary>The rows that pass the status filter (and the "only" limit), problems first, then by address.</summary>
	private void FillRows()
	{
		IEnumerable<ReviewRowViewModel> rows = _all.Where(r => StatusMatches(r.StatusValue) && (_only == null || _only.Contains(r.AssessmentID)));
		Guid? selected = Selected?.AssessmentID;
		_filling = true;
		Rows.Clear();
		foreach (ReviewRowViewModel r in rows.OrderByDescending(r => r.ErrorCount).ThenByDescending(r => r.FindingCount).ThenByDescending(r => r.CommentCount)
			.ThenBy(r => r.Address, StringComparer.OrdinalIgnoreCase))
		{
			Rows.Add(r);
		}
		_filling = false;
		if (selected is Guid id && Rows.FirstOrDefault(r => r.AssessmentID == id) is ReviewRowViewModel again)
		{
			Selected = again;
		}
		OnPropertyChanged(nameof(HasRows));
		OnPropertyChanged(nameof(IsQueueEmpty));
		OnPropertyChanged(nameof(NoRowsMatch));
		OnPropertyChanged(nameof(CountText));
		OnPropertyChanged(nameof(QueueTabText));
	}

	private bool StatusMatches(AssessmentStatus s) => StatusFilter switch
	{
		0 => s is AssessmentStatus.Draft or AssessmentStatus.Reviewed,
		1 => s == AssessmentStatus.Draft,
		2 => s == AssessmentStatus.Reviewed,
		3 => s == AssessmentStatus.Appeal,
		4 => s == AssessmentStatus.Final,
		5 => s == AssessmentStatus.LetterSent,
		_ => true,
	};

	/// <summary>The findings that pass the severity filter (and the "only" limit), grouped by check in the catalog's order.</summary>
	private void FillGroups()
	{
		Groups.Clear();
		IEnumerable<QaFinding> shown = _findings.Where(f => (ShowDismissed || !f.IsDismissed)
			&& (SeverityFilter == 0 || f.Severity == QaSeverity.Error || SeverityFilter == 1 && f.Severity == QaSeverity.Warning)
			&& (_only == null || f.AssessmentId is Guid id && _only.Contains(id)));
		foreach (IGrouping<string, QaFinding> g in shown.GroupBy(f => f.RuleId).OrderBy(g => QaRules.Catalog.ToList().FindIndex(r => r.Id == g.Key)))
		{
			QaRuleInfo rule = QaRules.Rule(g.Key);
			if (rule != null)
			{
				Groups.Add(new QaGroupViewModel(rule, g.OrderBy(f => f.IsDismissed).Select(Describe).ToList()));
			}
		}
		OnPropertyChanged(nameof(HasGroups));
		OnPropertyChanged(nameof(ChecksEmpty));
		OnPropertyChanged(nameof(ChecksEmptyTitle));
		OnPropertyChanged(nameof(ChecksEmptyText));
	}

	private QaFindingViewModel Describe(QaFinding f)
	{
		string where = f.PropertyId is Guid p && _addresses.TryGetValue(p, out string address) ? address
			: f.PropertyId == null ? _app.Store.GetEvent(f.ObjectId)?.Label ?? "This event"
			: "(no address)";
		return new QaFindingViewModel(f, where) { Open = OpenFindingCommand, Dismiss = DismissFindingCommand, BringBack = BringBackCommand };
	}

	/// <summary>The selected assessment's comments and open findings, for the panel beside the queue.</summary>
	private void LoadSelected()
	{
		if (SelectedComments != null)
		{
			SelectedComments.Changed -= OnCommentsChanged;
		}
		SelectedFindings.Clear();
		if (Selected == null)
		{
			SelectedComments = null;
			OnPropertyChanged(nameof(HasSelectedFindings));
			return;
		}
		Guid id = Selected.AssessmentID;
		foreach (QaFinding f in _findings.Where(f => f.AssessmentId == id && !f.IsDismissed))
		{
			SelectedFindings.Add(Describe(f));
		}
		SelectedComments = new CommentThreadsViewModel(_app, id);
		SelectedComments.Changed += OnCommentsChanged;
		OnPropertyChanged(nameof(HasSelectedFindings));
	}

	/// <summary>A comment was added, answered or resolved beside the queue: its counts follow.</summary>
	private void OnCommentsChanged()
	{
		if (Selected != null && SelectedComments != null)
		{
			Selected.CommentCount = SelectedComments.OpenCount;
			OpenCommentCount = _all.Sum(r => r.CommentCount);
		}
	}

	// ───────────── Filters and the headline tiles ─────────────

	/// <summary>The Waiting tile: the queue with its default filter.</summary>
	[RelayCommand]
	private void ShowWaiting()
	{
		ClearOnly();
		StatusFilter = 0;
		TabIndex = 0;
	}

	/// <summary>The Open checks tile: every open finding.</summary>
	[RelayCommand]
	private void ShowAllFindings()
	{
		ClearOnly();
		SeverityFilter = 0;
		ShowDismissed = false;
		TabIndex = 1;
	}

	/// <summary>The Errors tile: only findings of error severity.</summary>
	[RelayCommand]
	private void ShowErrors()
	{
		ClearOnly();
		SeverityFilter = ErrorsOnly;
		ShowDismissed = false;
		TabIndex = 1;
	}

	/// <summary>The Comments tile: the assessments (of any status) with open comment threads.</summary>
	[RelayCommand]
	private void ShowCommented()
	{
		List<Guid> ids = _all.Where(r => r.HasComments).Select(r => r.AssessmentID).ToList();
		SetOnly(ids, ids.Count == 1 ? "Showing the 1 assessment with open comments" : $"Showing the {ids.Count:N0} assessments with open comments");
		StatusFilter = EveryStatus;
		TabIndex = 0;
	}

	/// <summary>
	/// Shows the error findings of these assessments (from the question before an export): the Review page, the
	/// Quality checks tab, errors only, limited to them.
	/// </summary>
	public void ShowErrorsFor(IReadOnlyCollection<Guid> assessmentIds)
	{
		_main.ShowCommand.Execute(Page.Review);
		SetOnly(assessmentIds, assessmentIds.Count == 1 ? "Showing the 1 assessment being exported with errors" : $"Showing the {assessmentIds.Count:N0} assessments being exported with errors");
		SeverityFilter = ErrorsOnly;
		ShowDismissed = false;
		StatusFilter = EveryStatus;
		TabIndex = 1;
	}

	private void SetOnly(IEnumerable<Guid> ids, string label)
	{
		_only = ids.ToHashSet();
		OnlyLabel = label;
		FillRows();
		FillGroups();
	}

	/// <summary>The chip's close button: every assessment in scope again.</summary>
	[RelayCommand]
	private void ClearOnly()
	{
		if (_only == null)
		{
			return;
		}
		_only = null;
		OnlyLabel = null;
		FillRows();
		FillGroups();
	}

	[RelayCommand]
	private void ClearChecked()
	{
		foreach (ReviewRowViewModel r in _all)
		{
			r.IsChecked = false;
		}
	}

	/// <summary>Check again: reads everything and runs the checks again.</summary>
	[RelayCommand]
	private void RunChecks() => Reload();

	// ───────────── Opening, setting aside, marking reviewed ─────────────

	/// <summary>Opens an assessment from the queue; closing it comes back here.</summary>
	[RelayCommand]
	private void Open(ReviewRowViewModel row)
	{
		if (row != null)
		{
			_main.OpenAssessmentFrom(row.AssessmentID, Page.Review);
		}
	}

	[RelayCommand(CanExecute = nameof(CanOpenSelected))]
	private void OpenSelected() => Open(Selected);

	private bool CanOpenSelected() => Selected != null;

	/// <summary>Opens the assessment a finding is about, at its field when it names one.</summary>
	[RelayCommand]
	private void OpenFinding(QaFindingViewModel finding)
	{
		if (finding?.Finding.AssessmentId is not Guid id)
		{
			return;
		}
		_main.OpenAssessmentFrom(id, Page.Review);
		if (finding.Finding.FieldKey != null && _main.Editor?.Assessment.AssessmentID == id)
		{
			_main.Editor.GoToField(finding.Finding.FieldKey);
		}
	}

	/// <summary>Sets a finding aside with a reason (asked in a small sheet), recorded in edit history.</summary>
	[RelayCommand]
	private async Task DismissFinding(QaFindingViewModel finding)
	{
		if (finding == null || !finding.CanDismiss)
		{
			return;
		}
		string reason = await _app.Sheets.ShowAsync<string>(new DismissFindingSheetViewModel(finding.Finding, finding.Address));
		if (reason == null)
		{
			return;
		}
		QaDismissalRecord dismissal;
		try
		{
			dismissal = _app.Store.DismissQaFinding(finding.Finding, reason);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't set the check aside", ex);
			return;
		}
		Reload();
		_app.Dialogs.Notify("Check set aside", $"{finding.Rule?.Title ?? finding.Finding.RuleId}: {finding.Address}.", "Undo",
			() => BringBackDismissal(dismissal.RuleId, dismissal.ObjectID, dismissal.Detail));
	}

	/// <summary>Brings a finding that was set aside back into the open list (recorded in edit history).</summary>
	[RelayCommand]
	private void BringBack(QaFindingViewModel finding)
	{
		if (finding?.Finding.Dismissal is QaDismissalRecord d)
		{
			BringBackDismissal(d.RuleId, d.ObjectID, d.Detail);
		}
	}

	private void BringBackDismissal(string ruleId, Guid objectId, string detail)
	{
		try
		{
			_app.Store.RemoveQaDismissal(ruleId, objectId, detail);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't bring the check back", ex);
			return;
		}
		Reload();
	}

	/// <summary>
	/// Sets the checked assessments (or the selected one, when none are checked) to Reviewed. Final and Letter sent
	/// assessments are left alone, and Reviewed ones stay as they are.
	/// </summary>
	[RelayCommand(CanExecute = nameof(CanMarkReviewed))]
	private void MarkReviewed()
	{
		List<ReviewRowViewModel> targets = _all.Where(r => r.IsChecked).ToList();
		if (targets.Count == 0 && Selected != null)
		{
			targets.Add(Selected);
		}
		if (targets.Count == 0)
		{
			return;
		}
		List<ReviewRowViewModel> change = targets.Where(r => r.StatusValue is AssessmentStatus.Draft or AssessmentStatus.Appeal).ToList();
		int locked = targets.Count(r => r.IsLocked);
		int already = targets.Count(r => r.StatusValue == AssessmentStatus.Reviewed);
		List<string> problems = new();
		int marked = 0;
		ClearChecked();
		if (change.Count > 0)
		{
			_app.Writes.Write(change.Select(r => r.AssessmentID).ToList(), () =>
			{
				foreach (ReviewRowViewModel r in change)
				{
					try
					{
						_app.Store.SetStatus(r.AssessmentID, AssessmentStatus.Reviewed);
						marked++;
					}
					catch (Exception ex) when (ex is not OutOfMemoryException)
					{
						problems.Add($"{r.Address}: {ErrorPresenter.Describe(null, ex).Message}");
					}
				}
				return marked;
			});
		}
		List<string> parts = new() { marked == 1 ? "Marked 1 assessment reviewed." : $"Marked {marked:N0} assessments reviewed." };
		if (already > 0)
		{
			parts.Add(already == 1 ? "1 already was." : $"{already:N0} already were.");
		}
		if (locked > 0)
		{
			parts.Add(locked == 1 ? "1 Final or Letter sent assessment was left alone." : $"{locked:N0} Final or Letter sent assessments were left alone.");
		}
		if (problems.Count > 0)
		{
			parts.Add("Not marked: " + string.Join(" ", problems));
		}
		_app.Dialogs.Notify("Marked reviewed", string.Join(" ", parts), isError: problems.Count > 0);
	}

	private bool CanMarkReviewed() => HasChecked || Selected != null;
}
