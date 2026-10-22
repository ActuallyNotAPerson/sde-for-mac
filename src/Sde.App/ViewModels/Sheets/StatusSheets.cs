using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>Asks why a Final or Letter sent assessment is being reopened (the reason goes into its history).</summary>
public sealed partial class ReopenSheetViewModel(string address, AssessmentStatus current) : SheetViewModel("Reopen this assessment?")
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Small;

	public string Message => $"{address} is {AssessmentStatuses.Label(current)}. Say why it needs to change; the reason is kept in its history.";

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(ReopenCommand))]
	public partial string Reason { get; set; }

	[RelayCommand(CanExecute = nameof(HasReason))]
	private void Reopen() => Close(Reason.Trim());

	private bool HasReason() => !string.IsNullOrWhiteSpace(Reason);
}

/// <summary>One row of an assessment's history.</summary>
public sealed class HistoryRowViewModel(EditHistoryRecord row)
{
	public EditHistoryRecord Row { get; } = row;

	public string When => Row.ChangedOn.ToString("MMM d, yyyy h:mm tt");

	public string Who => string.Join(" · ", new[] { Row.ChangedBy, Row.Via == SaveVia.App ? null : Row.Via }.Where(s => !string.IsNullOrEmpty(s)));

	public string What => Row.Label;

	/// <summary>"old → new" for a field change; empty for an event.</summary>
	public string Change => Row.IsEvent && Row.Field == null ? "" : $"{Show(Row.OldValue)} → {Show(Row.NewValue)}";

	public bool HasChange => Change.Length > 0;

	private static string Show(string v) => string.IsNullOrEmpty(v) ? "(blank)" : v.Length > 60 ? v[..57] + "…" : v;
}

/// <summary>Who changed what on an assessment, and when.</summary>
public sealed class HistorySheetViewModel : SheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Large;

	public HistorySheetViewModel(string address, IEnumerable<EditHistoryRecord> rows)
		: base("History of " + address)
	{
		Rows = rows.Select(r => new HistoryRowViewModel(r)).ToList();
	}

	public IReadOnlyList<HistoryRowViewModel> Rows { get; }

	public bool IsEmpty => Rows.Count == 0;
}
