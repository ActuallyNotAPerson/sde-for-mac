using Dapper;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// Reads for the office pages (Review and Compliance) and their sidebar counts: small, quick queries, so the counts
/// can be read again whenever the sidebar refreshes.
/// </summary>
public sealed partial class SdeStore
{
	/// <summary>
	/// The review status of every active assessment of an active structure, by assessment ID. One with no status row is
	/// a Draft.
	/// </summary>
	public Dictionary<Guid, AssessmentStatus> ActiveAssessmentStatuses() => Database.Read(c => c.Query<ActiveStatusRow>("""
		SELECT a.AssessmentID, s.Status FROM Assessment a
		JOIN Property p ON p.PropertyID = a.PropertyID AND p.Active = 1
		LEFT JOIN AssessmentStatus s ON s.AssessmentID = a.AssessmentID
		WHERE a.Active = 1
		""").ToDictionary(r => r.AssessmentID, r => AssessmentStatuses.Parse(r.Status)));

	/// <summary>The structures that have at least one active assessment in an event.</summary>
	public HashSet<Guid> PropertiesInEvent(Guid eventId) => Database.Read(c => c.Query<Guid>("""
		SELECT DISTINCT a.PropertyID FROM EventAssessment e
		JOIN Assessment a ON a.AssessmentID = e.AssessmentID AND a.Active = 1
		WHERE e.EventID = @eventId
		""", new { eventId }).ToHashSet());

	/// <summary>
	/// The compliance cases of structures that are not in Recently deleted (and, for a substantial damage case, whose
	/// assessment isn't either), open ones first, then by deadline. A deleted structure's case comes back with it.
	/// </summary>
	public List<ComplianceCaseRecord> GetActiveComplianceCases() => Database.Read(c => TableMap<ComplianceCaseRecord>.Select(c,
		"PropertyID IN (SELECT PropertyID FROM Property WHERE Active = 1) AND (AssessmentID IS NULL OR AssessmentID IN (SELECT AssessmentID FROM Assessment WHERE Active = 1))",
		orderBy: "CASE WHEN Status = 'Closed' THEN 1 ELSE 0 END, COALESCE(Deadline, '9999-12-31'), CreatedOn").ToList());

	/// <summary>The compliance case for a substantial improvement determination, or null.</summary>
	public ComplianceCaseRecord GetComplianceCaseForImprovement(Guid determinationId) =>
		Database.Read(c => TableMap<ComplianceCaseRecord>.Select(c, "DeterminationID = @determinationId", new { determinationId }).FirstOrDefault());

	private sealed class ActiveStatusRow
	{
		public Guid AssessmentID { get; set; }

		public string Status { get; set; }
	}
}
