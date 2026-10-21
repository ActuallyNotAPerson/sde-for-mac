using System.Data;
using Dapper;
using Sde.Data.Infrastructure;

namespace Sde.Data;

public sealed partial class SdeStore
{
	/// <summary>An assessment's status; one with no status row is a Draft.</summary>
	public AssessmentStatusRecord GetStatus(Guid assessmentId) => Database.Read(c => GetStatus(c, null, assessmentId));

	internal static AssessmentStatusRecord GetStatus(IDbConnection c, IDbTransaction tx, Guid assessmentId) =>
		TableMap<AssessmentStatusRecord>.Get(c, new { AssessmentID = assessmentId }, tx) ?? new AssessmentStatusRecord { AssessmentID = assessmentId };

	/// <summary>Every assessment that isn't a Draft, by assessment ID (for the Structures list).</summary>
	public Dictionary<Guid, AssessmentStatusRecord> GetStatuses() =>
		Database.Read(c => TableMap<AssessmentStatusRecord>.Select(c).ToDictionary(s => s.AssessmentID));

	public bool IsLocked(Guid assessmentId) => GetStatus(assessmentId).IsLocked;

	/// <summary>
	/// True when a locked assessment was changed after it was locked, which only an older version of this app (which
	/// doesn't know about locks) can do. The app shows "changed in an older version".
	/// </summary>
	public bool ChangedSinceLocked(Guid assessmentId) => Database.Read(c =>
	{
		AssessmentStatusRecord s = GetStatus(c, null, assessmentId);
		if (!s.IsLocked || s.LockedLastUpdate == null)
		{
			return false;
		}
		DateTime? last = c.ExecuteScalar<DateTime?>("SELECT LastUpdate FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId });
		return last != s.LockedLastUpdate;
	});

	/// <summary>
	/// Moves an assessment to a new status and records it in its history. Reopening a Final or Letter sent assessment
	/// needs a reason. An assessment can't be made Final while it has depth-table suggestions nobody has accepted,
	/// because those would reach FEMA's export as if an inspector had entered them.
	/// </summary>
	public AssessmentStatusRecord SetStatus(Guid assessmentId, AssessmentStatus status, string reason = null, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.App;
		return Database.InTransaction((c, tx) => SetStatus(c, tx, assessmentId, status, reason, ctx));
	}

	/// <summary><see cref="SetStatus(Guid, AssessmentStatus, string, SaveContext)"/> inside a caller's transaction.</summary>
	internal AssessmentStatusRecord SetStatus(IDbConnection c, IDbTransaction tx, Guid assessmentId, AssessmentStatus status, string reason, SaveContext ctx)
	{
		Guid? propertyId = c.ExecuteScalar<Guid?>("SELECT PropertyID FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId }, tx);
		if (propertyId == null)
		{
			throw new ArgumentException("There's no assessment " + assessmentId + ".");
		}
		AssessmentStatusRecord current = GetStatus(c, tx, assessmentId);
		if (current.Value == status)
		{
			return current;
		}
		bool reopening = current.IsLocked && !AssessmentStatuses.IsLocked(status);
		if (reopening && string.IsNullOrWhiteSpace(reason))
		{
			throw new ArgumentException("Say why this assessment is being reopened.");
		}
		if (status == AssessmentStatus.Final
			&& c.ExecuteScalar<int>("SELECT COUNT(*) FROM DamageSuggestion WHERE AssessmentID = @assessmentId", new { assessmentId }, tx) > 0)
		{
			throw new InvalidOperationException("Some damage percentages were suggested from the depth table and haven't been accepted yet. Accept or change them before making this assessment Final.");
		}

		AssessmentStatusRecord next = new AssessmentStatusRecord
		{
			AssessmentID = assessmentId,
			Value = status,
			ChangedOn = Clock(),
			ChangedBy = ctx.User ?? UserName,
			Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
			LockedLastUpdate = AssessmentStatuses.IsLocked(status)
				? c.ExecuteScalar<DateTime?>("SELECT LastUpdate FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId }, tx)
				: null,
		};
		TableMap<AssessmentStatusRecord>.Upsert(c, next, tx);
		string label = reopening ? "Reopened: " + next.Reason : "Status: " + AssessmentStatuses.Label(status);
		WriteEvent(c, tx, propertyId.Value, assessmentId, label, ctx, "Status", AssessmentStatuses.Label(current.Value), AssessmentStatuses.Label(status));
		return next;
	}

	/// <summary>
	/// How many of these assessments aren't Final yet (Draft, Reviewed or Appeal; Letter sent counts as Final). Null
	/// means every active assessment of an active structure. Used to warn before exporting work nobody has signed off.
	/// </summary>
	public int CountNotFinal(IReadOnlyCollection<Guid> assessmentIds = null) => Database.Read(c =>
	{
		List<Guid> ids = assessmentIds?.Distinct().ToList() ?? c.Query<Guid>(
			"SELECT a.AssessmentID FROM Assessment a JOIN Property p ON p.PropertyID = a.PropertyID WHERE a.Active = 1 AND p.Active = 1").ToList();
		HashSet<Guid> final = c.Query<Guid>("SELECT AssessmentID FROM AssessmentStatus WHERE Status IN ('Final', 'LetterSent')").ToHashSet();
		return ids.Count(id => !final.Contains(id));
	});
}
