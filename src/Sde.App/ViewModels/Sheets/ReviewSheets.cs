using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Quality;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Review, Set aside: why a quality finding doesn't need fixing (two units on one parcel, a date checked with the
/// owner). Closes with the reason; the Review page records it with who and when.
/// </summary>
public sealed partial class DismissFindingSheetViewModel : SheetViewModel
{
	public DismissFindingSheetViewModel(QaFinding finding, string address)
		: base("Set this check aside")
	{
		Finding = finding;
		Address = address;
		QaRuleInfo rule = QaRules.Rule(finding.RuleId);
		RuleTitle = rule?.Title ?? finding.RuleId;
		Note = rule is { Pairwise: true }
			? "It is set aside for every structure in the group. If anything it checks changes, the check shows again."
			: "If anything it checks changes, the check shows again.";
	}

	public override SheetSize Size => SheetSize.Small;

	public QaFinding Finding { get; }

	public string Address { get; }

	public string RuleTitle { get; }

	public string Message => Finding.Message;

	/// <summary>What setting it aside covers, in one sentence.</summary>
	public string Note { get; }

	[ObservableProperty]
	public partial string Reason { get; set; }

	/// <summary>Shown when Set aside is pressed without a reason.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; private set; }

	public bool HasError => Error != null;

	partial void OnReasonChanged(string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			Error = null;
		}
	}

	[RelayCommand]
	public void Dismiss()
	{
		if (string.IsNullOrWhiteSpace(Reason))
		{
			Error = "Say why this check doesn't need fixing. The reason is kept with it.";
			return;
		}
		Close(Reason.Trim());
	}
}

/// <summary>The editor's Comments sheet: the assessment's review comments, with reply and resolve.</summary>
public sealed class CommentsSheetViewModel(CommentThreadsViewModel threads, string address) : SheetViewModel("Comments")
{
	public override SheetSize Size => SheetSize.Medium;

	public CommentThreadsViewModel Threads { get; } = threads;

	public string Address { get; } = address;
}

/// <summary>What to do when assessments about to be exported have open quality errors.</summary>
public enum QaExportChoice
{
	Cancel,
	/// <summary>Show them on the Review page instead of exporting.</summary>
	Review,
	ExportAnyway,
}

/// <summary>
/// Asked before an SDE export or a submission folder when some of the assessments have open quality findings of
/// error severity: look at them on the Review page, or export anyway.
/// </summary>
public sealed partial class QaBeforeExportSheetViewModel : SheetViewModel
{
	public QaBeforeExportSheetViewModel(int findings, int assessments, IReadOnlyList<string> examples)
		: base("Fix these first?")
	{
		FindingCount = findings;
		string problems = findings == 1 ? "1 problem" : $"{findings} problems";
		string which = assessments == 1 ? "1 of these assessments has" : $"{assessments} of these assessments have";
		Message = $"{which} {problems} that the quality checks count as errors, such as an inspection dated before the damage. "
			+ "They should be fixed before the data goes to FEMA or your state.";
		Examples = examples;
	}

	public override SheetSize Size => SheetSize.Medium;

	public int FindingCount { get; }

	public string Message { get; }

	/// <summary>A few of the findings, "123 River Street: Inspected on …".</summary>
	public IReadOnlyList<string> Examples { get; }

	[RelayCommand]
	private void ShowOnReview() => Close(QaExportChoice.Review);

	[RelayCommand]
	private void ExportAnyway() => Close(QaExportChoice.ExportAnyway);
}
