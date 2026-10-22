using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>What FEMA's engine gives when a contractor's bid is the repair cost.</summary>
public sealed record BidWhatIf(decimal? Percent, string DamageMessage);

/// <summary>The what-if calculation behind the appeal sheet, kept apart so tests can check it against the engine.</summary>
public static class AppealMath
{
	/// <summary>
	/// Runs FEMA's engine on a deep copy of <paramref name="file"/> with the repair cost basis set to the contractor's
	/// estimate (CostOfRepairsID 394) and the estimate set to <paramref name="bidAmount"/>. The copy is thrown away:
	/// nothing is saved and <paramref name="file"/> is not changed.
	/// </summary>
	public static BidWhatIf WhatIf(AssessmentFile file, decimal bidAmount, AssessmentCalculator calculator)
	{
		ArgumentNullException.ThrowIfNull(file);
		ArgumentNullException.ThrowIfNull(calculator);
		AssessmentFile copy = UndoService.Read(UndoService.Snapshot(file));
		copy.Assessment.CostOfRepairsID = LookupIds.RepairContractorEstimate;
		copy.Assessment.ContractorEstimate = bidAmount;
		calculator.Recalculate(copy);
		return new BidWhatIf(copy.Assessment.PercentDamage, copy.Assessment.DamageMessage);
	}
}

/// <summary>One contractor bid in the appeal sheet, with its what-if result.</summary>
public sealed class BidRowViewModel(ContractorBidRecord bid, BidWhatIf whatIf, bool isInUse, IAsyncRelayCommand<BidRowViewModel> use, IAsyncRelayCommand<BidRowViewModel> remove)
{
	/// <summary>The sheet's Use this bid (the row is its parameter).</summary>
	public IAsyncRelayCommand<BidRowViewModel> Use { get; } = use;

	public IAsyncRelayCommand<BidRowViewModel> Remove { get; } = remove;

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public ContractorBidRecord Bid { get; } = bid;

	public BidWhatIf WhatIf { get; } = whatIf;

	public string Contractor => string.IsNullOrWhiteSpace(Bid.Contractor) ? "(no contractor named)" : Bid.Contractor.Trim();

	/// <summary>"$41,000.00 · Oct 5, 2026 · notes".</summary>
	public string Detail => string.Join(" · ", new[]
	{
		Bid.Amount?.ToString("C2", Us) ?? "No amount",
		Bid.BidDate?.ToString("MMM d, yyyy", Us),
		string.IsNullOrWhiteSpace(Bid.Notes) ? null : Bid.Notes.Trim(),
	}.Where(s => s != null));

	/// <summary>"52.4%: Substantially Damaged" with this bid as the repair cost.</summary>
	public string WhatIfText => WhatIf?.Percent is decimal p ? $"{p.ToString("0.0", Us)}%: {WhatIf.DamageMessage}" : WhatIf == null ? "Enter an amount" : WhatIf.DamageMessage ?? "Can't be worked out";

	/// <summary>True when the assessment already uses this bid as its repair cost.</summary>
	public bool IsInUse { get; } = isInUse;

	public bool CanUse => !IsInUse && Bid.Amount is > 0m;

	public string AccessibleUse => "Use the bid from " + Contractor;

	public string AccessibleRemove => "Remove the bid from " + Contractor;
}

/// <summary>
/// ⋯, then Appeal: the owner's appeal (received, basis, outcome, notes) and the contractor bids sent with it. For each
/// bid it shows what FEMA's engine gives with that bid as the repair cost, beside the current result. "Use this bid"
/// changes the real assessment (after a confirm, and after reopening it if it is locked); nothing else here does.
/// </summary>
public sealed partial class AppealSheetViewModel : SheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Large;

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
	private readonly AppServices _app;
	private readonly Guid _assessmentId;
	private readonly string _address;
	private bool _hasAppeal;

	public AppealSheetViewModel(AppServices app, Guid assessmentId, string address)
		: base("Appeal")
	{
		_app = app;
		_assessmentId = assessmentId;
		_address = address;
		AppealRecord appeal = app.Store.GetAppeal(assessmentId);
		_hasAppeal = appeal != null;
		ReceivedOn = appeal?.ReceivedOn ?? DateTime.Today;
		Basis = appeal?.Basis;
		Outcome = appeal?.Outcome ?? AppealOutcomes.Pending;
		Notes = appeal?.Notes;
		NewBidDate = DateTime.Today;
		Refresh();
	}

	public string Address => _address;

	public static IReadOnlyList<string> Outcomes => AppealOutcomes.All;

	// ───────────── The appeal ─────────────

	[ObservableProperty]
	public partial DateTime? ReceivedOn { get; set; }

	[ObservableProperty]
	public partial string Basis { get; set; }

	[ObservableProperty]
	public partial string Outcome { get; set; }

	[ObservableProperty]
	public partial string Notes { get; set; }

	[ObservableProperty]
	public partial string SavedText { get; private set; }

	/// <summary>
	/// Saves the appeal. The first time, asks whether to set the assessment's status to Appeal (a Final or Letter sent
	/// assessment is reopened for it, with "Appeal received" as the reason).
	/// </summary>
	[RelayCommand]
	public async Task SaveAppeal()
	{
		bool first = !_hasAppeal;
		try
		{
			_app.Store.SaveAppeal(new AppealRecord
			{
				AssessmentID = _assessmentId,
				ReceivedOn = ReceivedOn?.Date,
				Basis = Blank(Basis),
				Outcome = Outcome ?? AppealOutcomes.Pending,
				Notes = Blank(Notes),
				DecidedOn = _app.Store.GetAppeal(_assessmentId)?.DecidedOn,
				DecidedBy = _app.Store.GetAppeal(_assessmentId)?.DecidedBy,
			});
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the appeal", ex);
			return;
		}
		_hasAppeal = true;
		SavedText = "Appeal saved.";
		AssessmentStatusRecord status = _app.Store.GetStatus(_assessmentId);
		if (first && Outcome == AppealOutcomes.Pending && status.Value != AssessmentStatus.Appeal)
		{
			string locked = status.IsLocked ? $" It is {AssessmentStatuses.Label(status.Value)} now, so it will be reopened, with \"Appeal received\" as the reason." : "";
			if (await _app.Dialogs.ConfirmAsync("Set the status to Appeal?", "Shows this assessment as under appeal in the Structures list." + locked, "Set to Appeal"))
			{
				string when = ReceivedOn is DateTime d ? " " + d.ToString("MM/dd/yyyy", Us) : "";
				try
				{
					_app.Writes.Write(new[] { _assessmentId }, () => _app.Store.SetStatus(_assessmentId, AssessmentStatus.Appeal, "Appeal received" + when));
					SavedText = "Appeal saved. The status is now Appeal.";
				}
				catch (Exception ex) when (ex is not OutOfMemoryException)
				{
					_app.Errors.Show("Couldn't change the status", ex);
				}
			}
		}
		Refresh();
	}

	// ───────────── Current result and bids ─────────────

	/// <summary>"75.0%: Substantially Damaged (repair cost: computed damages, $82,345.00)".</summary>
	[ObservableProperty]
	public partial string CurrentText { get; private set; }

	[ObservableProperty]
	public partial string StatusText { get; private set; }

	public ObservableCollection<BidRowViewModel> Bids { get; } = new();

	public bool HasBids => Bids.Count > 0;

	[ObservableProperty]
	public partial string NewContractor { get; set; }

	[ObservableProperty]
	public partial decimal? NewAmount { get; set; }

	[ObservableProperty]
	public partial DateTime? NewBidDate { get; set; }

	[ObservableProperty]
	public partial string NewNotes { get; set; }

	/// <summary>Reads the assessment, its status and its bids again, and works out each bid's what-if result.</summary>
	private void Refresh()
	{
		AssessmentFile file = _app.Store.LoadAssessment(_assessmentId);
		if (file == null)
		{
			Close();
			return;
		}
		Sde.Core.Records.AssessmentRecord a = file.Assessment;
		string basis = _app.Store.Reference.LookupName(a.CostOfRepairsID) ?? "no basis chosen";
		string cost = CumulativeDamage.RepairCost(a)?.ToString("C2", Us) ?? "no amount";
		CurrentText = (a.PercentDamage is decimal p ? $"{p.ToString("0.0", Us)}%: " : "") + (a.DamageMessage ?? "Data Entry Incomplete") + $" (repair cost: {basis}, {cost})";
		AssessmentStatusRecord status = _app.Store.GetStatus(_assessmentId);
		StatusText = "Status: " + AssessmentStatuses.Label(status.Value) + (status.IsLocked ? " (locked; using a bid reopens it first)" : "");

		AssessmentCalculator calc = _app.Store.Calculator;
		Bids.Clear();
		foreach (ContractorBidRecord bid in _app.Store.GetBids(_assessmentId))
		{
			BidWhatIf whatIf = bid.Amount is decimal amount ? AppealMath.WhatIf(file, amount, calc) : null;
			bool inUse = a.CostOfRepairsID == LookupIds.RepairContractorEstimate && a.ContractorEstimate == bid.Amount;
			Bids.Add(new BidRowViewModel(bid, whatIf, inUse, UseBidCommand, RemoveBidCommand));
		}
		OnPropertyChanged(nameof(HasBids));
	}

	[RelayCommand]
	public void AddBid()
	{
		if (NewAmount is not decimal amount || amount <= 0m)
		{
			_app.Dialogs.Notify("Enter the bid amount", "Enter the total repair cost from the contractor's bid.");
			return;
		}
		try
		{
			_app.Store.SaveBid(new ContractorBidRecord
			{
				AssessmentID = _assessmentId,
				Contractor = Blank(NewContractor),
				Amount = amount,
				BidDate = NewBidDate?.Date,
				Notes = Blank(NewNotes),
			});
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't add the bid", ex);
			return;
		}
		NewContractor = null;
		NewAmount = null;
		NewNotes = null;
		NewBidDate = DateTime.Today;
		Refresh();
	}

	[RelayCommand]
	private async Task RemoveBid(BidRowViewModel row)
	{
		if (row == null || !await _app.Dialogs.ConfirmAsync("Remove this bid?", $"The bid from {row.Contractor} ({row.Detail}) will be removed. The assessment doesn't change.", "Remove", destructive: true))
		{
			return;
		}
		try
		{
			_app.Store.DeleteBid(row.Bid.BidID);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't remove the bid", ex);
			return;
		}
		Refresh();
	}

	/// <summary>
	/// Makes the bid the assessment's repair cost (contractor's estimate). Asks first, saying what the result becomes.
	/// A Final or Letter sent assessment is reopened first, and the person is asked why; locks are never bypassed.
	/// </summary>
	[RelayCommand]
	public async Task UseBid(BidRowViewModel row)
	{
		if (row?.Bid.Amount is not decimal amount || amount <= 0m)
		{
			return;
		}
		AssessmentStatusRecord status = _app.Store.GetStatus(_assessmentId);
		string after = row.WhatIf?.Percent is decimal p ? $"{p.ToString("0.0", Us)}% ({row.WhatIf.DamageMessage})" : row.WhatIf?.DamageMessage ?? "not worked out";
		string locked = status.IsLocked
			? $" This assessment is {AssessmentStatuses.Label(status.Value)}, so it has to be reopened first: you'll be asked why."
			: "";
		string message = $"The repair cost becomes {row.Contractor}'s bid of {amount.ToString("C2", Us)} (contractor's estimate). Now: {CurrentText}. With this bid: {after}. The change is kept in the assessment's history.{locked}";
		if (!await _app.Dialogs.ConfirmAsync("Use this bid?", message, "Use this bid"))
		{
			return;
		}
		if (status.IsLocked)
		{
			string reason = await _app.Sheets.ShowAsync<string>(new ReopenSheetViewModel(_address, status.Value));
			if (reason == null)
			{
				return;
			}
			try
			{
				_app.Writes.Write(new[] { _assessmentId }, () => _app.Store.SetStatus(_assessmentId, AssessmentStatus.Appeal, reason));
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_app.Errors.Show("Couldn't reopen the assessment", ex);
				Refresh();
				return;
			}
		}
		string who = row.Contractor;
		try
		{
			_app.Writes.Apply(_assessmentId, f =>
			{
				f.Assessment.CostOfRepairsID = LookupIds.RepairContractorEstimate;
				f.Assessment.ContractorEstimate = amount;
			}, new SaveContext { Event = $"Repair cost from contractor bid: {who}, {amount.ToString("C2", Us)}" });
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't use the bid", ex);
			Refresh();
			return;
		}
		SavedText = $"The repair cost is now {who}'s bid of {amount.ToString("C2", Us)}.";
		Refresh();
	}

	private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
