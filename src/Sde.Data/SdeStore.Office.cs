using Dapper;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// Reads for the office pages (Review) and their sidebar counts: small, quick queries, so the counts can be read
/// again whenever the sidebar refreshes.
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

	private sealed class ActiveStatusRow
	{
		public Guid AssessmentID { get; set; }

		public string Status { get; set; }
	}
}
