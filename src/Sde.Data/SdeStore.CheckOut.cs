using System.Data;
using Dapper;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>An assessment checked out to another Mac in a work package (Mac-only CheckOut table).</summary>
[Table("CheckOut")]
public sealed class CheckOutRecord
{
	[Key] public Guid AssessmentID { get; set; }

	public Guid PackageID { get; set; }

	public DateTime CheckedOutOn { get; set; }

	public string CheckedOutBy { get; set; }

	/// <summary>Who, or which Mac, took it.</summary>
	public string CheckedOutTo { get; set; }

	/// <summary>The assessment's version when it left.</summary>
	public long? Version { get; set; }

	/// <summary>Its structure's version when it left.</summary>
	public long? PropertyVersion { get; set; }
}

/// <summary>Thrown when something tries to change an assessment that is checked out to another Mac.</summary>
public sealed class AssessmentCheckedOutException : InvalidOperationException
{
	public AssessmentCheckedOutException(Guid assessmentId, CheckOutRecord record)
		: base($"This assessment is checked out to {(string.IsNullOrWhiteSpace(record.CheckedOutTo) ? "another Mac" : record.CheckedOutTo)} in a work package. Check the package back in, or take the assessment back, to change it here.")
	{
		AssessmentID = assessmentId;
		CheckOut = record;
	}

	public Guid AssessmentID { get; }

	public CheckOutRecord CheckOut { get; }
}

public sealed partial class SdeStore
{
	/// <summary>The check-out of an assessment, or null when it's here to edit.</summary>
	public CheckOutRecord GetCheckOut(Guid assessmentId) => Database.Read(c => TableMap<CheckOutRecord>.Get(c, new { AssessmentID = assessmentId }));

	/// <summary>Every assessment checked out now, by package.</summary>
	public List<CheckOutRecord> GetCheckOuts() => Database.Read(c => TableMap<CheckOutRecord>.Select(c, orderBy: "CheckedOutOn, PackageID").ToList());

	internal static bool IsCheckedOut(IDbConnection c, IDbTransaction tx, Guid assessmentId) =>
		c.ExecuteScalar<int>("SELECT COUNT(*) FROM CheckOut WHERE AssessmentID = @assessmentId", new { assessmentId }, tx) > 0;

	/// <summary>Refuses to change an assessment that is checked out to another Mac.</summary>
	internal static void EnsureNotCheckedOut(IDbConnection c, IDbTransaction tx, Guid assessmentId)
	{
		CheckOutRecord r = TableMap<CheckOutRecord>.Get(c, new { AssessmentID = assessmentId }, tx);
		if (r != null)
		{
			throw new AssessmentCheckedOutException(assessmentId, r);
		}
	}

	/// <summary>
	/// Takes a checked-out assessment back early, so it can be changed here again. Its work package can no longer be
	/// checked in for it; what was done in the field can still be copied over field by field (see
	/// <see cref="WorkPackages.Compare"/>).
	/// </summary>
	public void TakeBack(Guid assessmentId, SaveContext context = null) => Database.InTransaction((c, tx) =>
	{
		CheckOutRecord r = TableMap<CheckOutRecord>.Get(c, new { AssessmentID = assessmentId }, tx);
		if (r == null)
		{
			return;
		}
		TableMap<CheckOutRecord>.Delete(c, "AssessmentID = @assessmentId", new { assessmentId }, tx);
		Guid propertyId = c.ExecuteScalar<Guid>("SELECT PropertyID FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId }, tx);
		WriteEvent(c, tx, propertyId, assessmentId, "Taken back from a work package" + (string.IsNullOrWhiteSpace(r.CheckedOutTo) ? "" : " (checked out to " + r.CheckedOutTo + ")"),
			context ?? SaveContext.App);
	});
}
