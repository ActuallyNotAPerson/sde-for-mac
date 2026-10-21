using Dapper;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// Compliance after a substantial damage (SD) or substantial improvement (SI) determination: the path the owner takes,
/// the permit and deadline, the ICC claim, the elevation certificate, the final inspection (Mac-only ComplianceCase table).
/// </summary>
[Table("ComplianceCase")]
public sealed class ComplianceCaseRecord
{
	[Key] public Guid CaseID { get; set; }

	public Guid PropertyID { get; set; }

	/// <summary>The SD determination (an assessment), for an SD case.</summary>
	public Guid? AssessmentID { get; set; }

	/// <summary>The SI determination, for an SI case.</summary>
	public Guid? DeterminationID { get; set; }

	/// <summary>SD or SI.</summary>
	public string Kind { get; set; }

	/// <summary>See <see cref="CompliancePaths"/>; null until chosen.</summary>
	public string Path { get; set; }

	public string PermitNumber { get; set; }

	public DateTime? PermitIssuedOn { get; set; }

	public DateTime? Deadline { get; set; }

	public string IccClaimNumber { get; set; }

	/// <summary>See <see cref="IccStatuses"/>.</summary>
	public string IccStatus { get; set; }

	public decimal? IccAmount { get; set; }

	public DateTime? EcReceivedOn { get; set; }

	public DateTime? FinalInspectionOn { get; set; }

	/// <summary>The certificate of compliance or occupancy.</summary>
	public DateTime? ComplianceCertOn { get; set; }

	/// <summary>Open or Closed.</summary>
	public string Status { get; set; } = ComplianceStatuses.Open;

	public DateTime? ClosedOn { get; set; }

	public string ClosedReason { get; set; }

	public string Notes { get; set; }

	public DateTime? CreatedOn { get; set; }

	public string CreatedBy { get; set; }

	public DateTime? UpdatedOn { get; set; }

	[NotMapped] public bool IsOpen => Status != ComplianceStatuses.Closed;

	/// <summary>Open with a deadline before <paramref name="today"/>.</summary>
	public bool IsOverdue(DateTime today) => IsOpen && Deadline is DateTime d && d.Date < today.Date;
}

/// <summary>One line of a compliance case's timeline (Mac-only ComplianceEntry table).</summary>
[Table("ComplianceEntry")]
public sealed class ComplianceEntryRecord
{
	[Key] public Guid EntryID { get; set; }

	public Guid CaseID { get; set; }

	public DateTime EntryOn { get; set; }

	/// <summary>Note, Letter, Call, Permit, Inspection, ICC or Status.</summary>
	public string Kind { get; set; }

	public string Text { get; set; }

	/// <summary>The LetterLog row, for a letter.</summary>
	public string LetterID { get; set; }

	public string CreatedBy { get; set; }
}

/// <summary>How an owner brings a substantially damaged or improved building into compliance.</summary>
public static class CompliancePaths
{
	public const string Elevate = "Elevate";
	public const string Relocate = "Relocate";
	public const string Demolish = "Demolish";
	/// <summary>Dry floodproofing: non-residential buildings only.</summary>
	public const string Floodproof = "Floodproof";
	/// <summary>Repair below the threshold after an appeal changed the determination.</summary>
	public const string RepairBelowThreshold = "RepairBelowThreshold";

	public static readonly IReadOnlyList<string> All = new[] { Elevate, Relocate, Demolish, Floodproof, RepairBelowThreshold };

	public static string Label(string path) => path switch
	{
		Floodproof => "Floodproof (non-residential)",
		RepairBelowThreshold => "Repair below the threshold (after an appeal)",
		null => "Not chosen yet",
		_ => path,
	};
}

/// <summary>The state of an Increased Cost of Compliance (ICC) claim.</summary>
public static class IccStatuses
{
	public const string NotFiled = "NotFiled";
	public const string Filed = "Filed";
	public const string Approved = "Approved";
	public const string Paid = "Paid";
	public const string Denied = "Denied";

	public static readonly IReadOnlyList<string> All = new[] { NotFiled, Filed, Approved, Paid, Denied };

	public static string Label(string status) => status switch
	{
		NotFiled or null => "Not filed",
		_ => status,
	};
}

public static class ComplianceStatuses
{
	public const string Open = "Open";
	public const string Closed = "Closed";
}

public sealed partial class SdeStore
{
	/// <summary>Every compliance case (open ones first, then by deadline).</summary>
	public List<ComplianceCaseRecord> GetComplianceCases(bool openOnly = false) => Database.Read(c => TableMap<ComplianceCaseRecord>.Select(c,
		openOnly ? "Status <> 'Closed'" : null, orderBy: "CASE WHEN Status = 'Closed' THEN 1 ELSE 0 END, COALESCE(Deadline, '9999-12-31'), CreatedOn").ToList());

	public ComplianceCaseRecord GetComplianceCase(Guid caseId) =>
		Database.Read(c => TableMap<ComplianceCaseRecord>.Get(c, new { CaseID = caseId }));

	/// <summary>The case for an SD determination (an assessment), or null.</summary>
	public ComplianceCaseRecord GetComplianceCaseFor(Guid assessmentId) =>
		Database.Read(c => TableMap<ComplianceCaseRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId }).FirstOrDefault());

	/// <summary>
	/// Opens the compliance case for a substantially damaged assessment (50% or more), or returns the one it already has.
	/// Throws for an assessment that isn't substantially damaged.
	/// </summary>
	public ComplianceCaseRecord OpenComplianceCase(Guid assessmentId, SaveContext context = null) => Database.InTransaction((c, tx) =>
	{
		ComplianceCaseRecord existing = TableMap<ComplianceCaseRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId }, tx).FirstOrDefault();
		if (existing != null)
		{
			return existing;
		}
		AssessmentRecord a = TableMap<AssessmentRecord>.Get(c, new { AssessmentID = assessmentId }, tx) ?? throw new ArgumentException("That assessment no longer exists.");
		if (!(a.PercentDamage >= 50m) || a.PhysicalDamage)
		{
			throw new InvalidOperationException("Only a substantially damaged building (50% or more) needs a compliance case.");
		}
		ComplianceCaseRecord created = NewCase(a.PropertyID, "SD", context);
		created.AssessmentID = assessmentId;
		TableMap<ComplianceCaseRecord>.Insert(c, created, tx);
		AddEntry(c, tx, created.CaseID, "Status", "Case opened for the substantial damage determination", null, context);
		return created;
	});

	/// <summary>Opens the compliance case for a substantial improvement determination, or returns the one it already has.</summary>
	public ComplianceCaseRecord OpenComplianceCaseForImprovement(Guid determinationId, SaveContext context = null) => Database.InTransaction((c, tx) =>
	{
		ComplianceCaseRecord existing = TableMap<ComplianceCaseRecord>.Select(c, "DeterminationID = @determinationId", new { determinationId }, tx).FirstOrDefault();
		if (existing != null)
		{
			return existing;
		}
		Guid propertyId = c.ExecuteScalar<Guid?>("SELECT PropertyID FROM SIDetermination WHERE DeterminationID = @determinationId", new { determinationId }, tx)
			?? throw new ArgumentException("That determination no longer exists.");
		ComplianceCaseRecord created = NewCase(propertyId, "SI", context);
		created.DeterminationID = determinationId;
		TableMap<ComplianceCaseRecord>.Insert(c, created, tx);
		AddEntry(c, tx, created.CaseID, "Status", "Case opened for the substantial improvement determination", null, context);
		return created;
	});

	private ComplianceCaseRecord NewCase(Guid propertyId, string kind, SaveContext context) => new ComplianceCaseRecord
	{
		CaseID = Guid.NewGuid(),
		PropertyID = propertyId,
		Kind = kind,
		IccStatus = IccStatuses.NotFiled,
		CreatedOn = Clock(),
		CreatedBy = (context ?? SaveContext.App).User ?? UserName,
		UpdatedOn = Clock(),
	};

	/// <summary>
	/// Saves a case. Changes to its path, ICC status, deadline and open/closed state are written to its timeline.
	/// Floodproofing is only for non-residential buildings.
	/// </summary>
	public ComplianceCaseRecord SaveComplianceCase(ComplianceCaseRecord updated, SaveContext context = null) => Database.InTransaction((c, tx) =>
	{
		ComplianceCaseRecord before = TableMap<ComplianceCaseRecord>.Get(c, new { updated.CaseID }, tx) ?? throw new ArgumentException("That case no longer exists.");
		if (updated.Path == CompliancePaths.Floodproof
			&& c.ExecuteScalar<int?>("SELECT StructureTypeID FROM Property WHERE PropertyID = @PropertyID", new { updated.PropertyID }, tx) == 1)
		{
			throw new ArgumentException("Dry floodproofing is only allowed for non-residential buildings.");
		}
		if (updated.Status == ComplianceStatuses.Closed && before.Status != ComplianceStatuses.Closed)
		{
			updated.ClosedOn ??= Clock();
		}
		else if (updated.Status != ComplianceStatuses.Closed)
		{
			updated.ClosedOn = null;
		}
		updated.UpdatedOn = Clock();
		TableMap<ComplianceCaseRecord>.Update(c, updated, tx);
		if (before.Path != updated.Path)
		{
			AddEntry(c, tx, updated.CaseID, "Status", "Path: " + CompliancePaths.Label(updated.Path), null, context);
		}
		if (before.IccStatus != updated.IccStatus)
		{
			AddEntry(c, tx, updated.CaseID, "ICC", "ICC claim: " + IccStatuses.Label(updated.IccStatus), null, context);
		}
		if (before.Deadline != updated.Deadline)
		{
			AddEntry(c, tx, updated.CaseID, "Status", updated.Deadline is DateTime d ? "Deadline: " + d.ToString("MMM d, yyyy") : "Deadline removed", null, context);
		}
		if (before.Status != updated.Status)
		{
			AddEntry(c, tx, updated.CaseID, "Status", updated.Status == ComplianceStatuses.Closed
				? "Case closed" + (string.IsNullOrWhiteSpace(updated.ClosedReason) ? "" : ": " + updated.ClosedReason.Trim())
				: "Case reopened", null, context);
		}
		return updated;
	});

	/// <summary>Adds a line to a case's timeline (a note, a call, a letter sent).</summary>
	public ComplianceEntryRecord AddComplianceEntry(Guid caseId, string kind, string text, string letterId = null, SaveContext context = null) =>
		Database.InTransaction((c, tx) => AddEntry(c, tx, caseId, kind, text, letterId, context));

	private ComplianceEntryRecord AddEntry(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid caseId, string kind, string text, string letterId, SaveContext context)
	{
		ComplianceEntryRecord e = new ComplianceEntryRecord
		{
			EntryID = Guid.NewGuid(),
			CaseID = caseId,
			EntryOn = Clock(),
			Kind = kind,
			Text = text,
			LetterID = letterId,
			CreatedBy = (context ?? SaveContext.App).User ?? UserName,
		};
		TableMap<ComplianceEntryRecord>.Insert(c, e, tx);
		return e;
	}

	/// <summary>A case's timeline, oldest first.</summary>
	public List<ComplianceEntryRecord> GetComplianceEntries(Guid caseId) =>
		Database.Read(c => TableMap<ComplianceEntryRecord>.Select(c, "CaseID = @caseId", new { caseId }, orderBy: "EntryOn, rowid").ToList());
}
