using CommunityToolkit.Mvvm.ComponentModel;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>The dashboard's compliance tiles: open and overdue cases and ICC claims, for the chosen event.</summary>
public sealed partial class DashboardViewModel
{
	/// <summary>True when there is at least one compliance case in scope (the tiles are hidden otherwise).</summary>
	[ObservableProperty]
	public partial bool HasCompliance { get; private set; }

	[ObservableProperty]
	public partial string ComplianceOpen { get; private set; }

	[ObservableProperty]
	public partial string ComplianceOpenNote { get; private set; }

	[ObservableProperty]
	public partial string ComplianceOverdue { get; private set; }

	[ObservableProperty]
	public partial bool HasOverdue { get; private set; }

	[ObservableProperty]
	public partial string IccInProgress { get; private set; }

	[ObservableProperty]
	public partial string IccPaid { get; private set; }

	[ObservableProperty]
	public partial string IccPaidNote { get; private set; }

	/// <summary>Counts the compliance cases in scope (the same numbers as the Compliance page's tiles and PDF).</summary>
	private void LoadCompliance()
	{
		List<ComplianceCaseRecord> cases = ComplianceViewModel.CasesInScope(_app);
		HasCompliance = cases.Count > 0;
		int open = cases.Count(c => c.IsOpen);
		int overdue = cases.Count(c => c.IsOverdue(DateTime.Today));
		List<ComplianceCaseRecord> paid = cases.Where(c => c.IccStatus == IccStatuses.Paid).ToList();
		ComplianceOpen = open.ToString("N0", Us);
		ComplianceOpenNote = $"{cases.Count - open:N0} closed";
		ComplianceOverdue = overdue.ToString("N0", Us);
		HasOverdue = overdue > 0;
		IccInProgress = cases.Count(c => c.IccStatus is IccStatuses.Filed or IccStatuses.Approved).ToString("N0", Us);
		IccPaid = paid.Sum(c => c.IccAmount ?? 0m).ToString("C0", Us);
		IccPaidNote = paid.Count == 1 ? "1 claim" : $"{paid.Count:N0} claims";
	}
}
