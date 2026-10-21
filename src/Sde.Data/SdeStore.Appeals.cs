using System.Globalization;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// A contractor's bid sent in by an owner who disputes a determination (Mac-only table ContractorBid). The app shows
/// what FEMA's engine would give with each bid as the repair cost; a bid only reaches the assessment when someone
/// chooses "Use this bid".
/// </summary>
[Table("ContractorBid")]
public sealed class ContractorBidRecord
{
	[Key] public Guid BidID { get; set; }

	public Guid AssessmentID { get; set; }

	public string Contractor { get; set; }

	/// <summary>The bid's total repair cost.</summary>
	public decimal? Amount { get; set; }

	public DateTime? BidDate { get; set; }

	public string Notes { get; set; }
}

/// <summary>An owner's appeal of an assessment's determination (Mac-only table Appeal, one per assessment).</summary>
[Table("Appeal")]
public sealed class AppealRecord
{
	[Key] public Guid AssessmentID { get; set; }

	public DateTime? ReceivedOn { get; set; }

	/// <summary>What the owner disputes and why, e.g. "Repair estimate too high; contractor bids attached".</summary>
	public string Basis { get; set; }

	/// <summary>One of <see cref="AppealOutcomes"/>.</summary>
	public string Outcome { get; set; } = AppealOutcomes.Pending;

	public DateTime? DecidedOn { get; set; }

	public string DecidedBy { get; set; }

	public string Notes { get; set; }
}

/// <summary>The outcomes of an appeal (stored as text).</summary>
public static class AppealOutcomes
{
	public const string Pending = "Pending";

	/// <summary>The determination stands.</summary>
	public const string Upheld = "Upheld";

	/// <summary>The assessment was changed (for example, a contractor's bid was used).</summary>
	public const string Revised = "Revised";

	public const string Withdrawn = "Withdrawn";

	public static IReadOnlyList<string> All { get; } = new[] { Pending, Upheld, Revised, Withdrawn };
}

public sealed partial class SdeStore
{
	/// <summary>The MacSetting key of the community's cumulative damage rule (<see cref="Sde.Core.Assessments.CumulativeSettings"/>).</summary>
	public const string CumulativeSettingsKey = "Cumulative.Settings";

	/// <summary>This database's cumulative damage rule; off when none is saved (the NFIP minimum looks at each event on its own).</summary>
	public Sde.Core.Assessments.CumulativeSettings GetCumulativeSettings() =>
		GetSetting<Sde.Core.Assessments.CumulativeSettings>(CumulativeSettingsKey) ?? new Sde.Core.Assessments.CumulativeSettings();

	public void SetCumulativeSettings(Sde.Core.Assessments.CumulativeSettings settings) => SetSetting(CumulativeSettingsKey, settings);

	/// <summary>An assessment's contractor bids, oldest first (by bid date, then amount).</summary>
	public List<ContractorBidRecord> GetBids(Guid assessmentId) =>
		Database.Read(c => TableMap<ContractorBidRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId },
			orderBy: "BidDate IS NULL, BidDate, Amount").ToList());

	/// <summary>
	/// Adds a bid (BidID = Guid.Empty gets a new ID) or saves changes to one, and records it in the assessment's
	/// history. Bids are notes beside the assessment, so they can be added while it is Final or Letter sent.
	/// </summary>
	public ContractorBidRecord SaveBid(ContractorBidRecord bid, SaveContext context = null)
	{
		ArgumentNullException.ThrowIfNull(bid);
		SaveContext ctx = context ?? SaveContext.App;
		Database.InTransaction((c, tx) =>
		{
			Guid propertyId = PropertyOf(c, tx, bid.AssessmentID);
			bool isNew = bid.BidID == Guid.Empty || TableMap<ContractorBidRecord>.Get(c, new { bid.BidID }, tx) == null;
			if (bid.BidID == Guid.Empty)
			{
				bid.BidID = Guid.NewGuid();
			}
			TableMap<ContractorBidRecord>.Upsert(c, bid, tx);
			WriteEvent(c, tx, propertyId, bid.AssessmentID, (isNew ? "Contractor bid added: " : "Contractor bid changed: ") + DescribeBid(bid), ctx);
		});
		return bid;
	}

	public void DeleteBid(Guid bidId, SaveContext context = null) => Database.InTransaction((c, tx) =>
	{
		ContractorBidRecord bid = TableMap<ContractorBidRecord>.Get(c, new { BidID = bidId }, tx);
		if (bid == null)
		{
			return;
		}
		Guid propertyId = PropertyOf(c, tx, bid.AssessmentID);
		TableMap<ContractorBidRecord>.Delete(c, "BidID = @bidId", new { bidId }, tx);
		WriteEvent(c, tx, propertyId, bid.AssessmentID, "Contractor bid removed: " + DescribeBid(bid), context ?? SaveContext.App);
	});

	/// <summary>An assessment's appeal, or null when none has been recorded.</summary>
	public AppealRecord GetAppeal(Guid assessmentId) => Database.Read(c => TableMap<AppealRecord>.Get(c, new { AssessmentID = assessmentId }));

	/// <summary>
	/// Records or updates an assessment's appeal. History gets "Appeal received" the first time and "Appeal outcome:
	/// Upheld" (and so on) when the outcome changes. Doesn't change the assessment or its status.
	/// </summary>
	public AppealRecord SaveAppeal(AppealRecord appeal, SaveContext context = null)
	{
		ArgumentNullException.ThrowIfNull(appeal);
		SaveContext ctx = context ?? SaveContext.App;
		if (!AppealOutcomes.All.Contains(appeal.Outcome ?? ""))
		{
			appeal.Outcome = AppealOutcomes.Pending;
		}
		Database.InTransaction((c, tx) =>
		{
			Guid propertyId = PropertyOf(c, tx, appeal.AssessmentID);
			AppealRecord before = TableMap<AppealRecord>.Get(c, new { appeal.AssessmentID }, tx);
			if (appeal.Outcome != AppealOutcomes.Pending && before?.Outcome != appeal.Outcome)
			{
				appeal.DecidedOn ??= Clock();
				appeal.DecidedBy ??= ctx.User ?? UserName;
			}
			TableMap<AppealRecord>.Upsert(c, appeal, tx);
			if (before == null)
			{
				string when = appeal.ReceivedOn is DateTime d ? " " + d.ToString("MM/dd/yyyy", CultureInfo.GetCultureInfo("en-US")) : "";
				WriteEvent(c, tx, propertyId, appeal.AssessmentID, "Appeal received" + when + (string.IsNullOrWhiteSpace(appeal.Basis) ? "" : ": " + appeal.Basis.Trim()), ctx);
			}
			if ((before?.Outcome ?? AppealOutcomes.Pending) != appeal.Outcome)
			{
				WriteEvent(c, tx, propertyId, appeal.AssessmentID, "Appeal outcome: " + appeal.Outcome, ctx, "Appeal.Outcome", before?.Outcome, appeal.Outcome);
			}
		});
		return appeal;
	}

	private static Guid PropertyOf(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid assessmentId) =>
		Dapper.SqlMapper.ExecuteScalar<Guid?>(c, "SELECT PropertyID FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId }, tx)
			?? throw new ArgumentException("That assessment no longer exists.");

	private static string DescribeBid(ContractorBidRecord bid)
	{
		string who = string.IsNullOrWhiteSpace(bid.Contractor) ? "(no contractor named)" : bid.Contractor.Trim();
		return bid.Amount is decimal a ? $"{who}, {a.ToString("C2", CultureInfo.GetCultureInfo("en-US"))}" : who;
	}
}
