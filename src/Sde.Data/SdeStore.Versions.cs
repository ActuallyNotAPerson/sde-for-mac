using System.Data;
using Dapper;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>What a <see cref="RowVersionRecord"/> versions.</summary>
public enum VersionKind
{
	/// <summary>A structure (Property row), with its square-footage shapes.</summary>
	Property,
	/// <summary>An assessment, with its rows (elements, adjustments, photos, custom fields) and its status.</summary>
	Assessment,
}

/// <summary>A row of the RowVersion table (Mac-only, never exported).</summary>
[Table("RowVersion")]
public sealed class RowVersionRecord
{
	[Key] public Guid ObjectID { get; set; }

	/// <summary>'P' for a structure, 'A' for an assessment.</summary>
	public string Kind { get; set; }

	public long Version { get; set; }

	/// <summary>Orders every save in the database; the change watcher reads what came after the last one it saw.</summary>
	public long Seq { get; set; }

	/// <summary>The running copy of the app that saved (<see cref="SdeDatabase.WriterId"/>).</summary>
	public string WriterId { get; set; }

	public DateTime? ChangedOn { get; set; }

	[NotMapped] public VersionKind Value => Kind == "P" ? VersionKind.Property : VersionKind.Assessment;
}

/// <summary>
/// What a writer saw when it loaded an assessment: the assessment's version and its structure's, and both LastUpdate
/// values (writers older than v2.5 change those without versioning). A save that expects these values is refused with
/// a <see cref="ConcurrencyException"/> if any of them has moved since.
/// </summary>
public sealed record VersionStamp(long Assessment, long Property, DateTime? AssessmentLastUpdate, DateTime? PropertyLastUpdate);

/// <summary>
/// Thrown when a save finds that someone else (another window's import, the phone form, the AI assistant server, the
/// app on another Mac) saved the same assessment or structure after it was loaded. Nothing was written.
/// </summary>
public sealed class ConcurrencyException : InvalidOperationException
{
	public ConcurrencyException(Guid assessmentId, Guid propertyId, VersionStamp current)
		: base("Someone else saved changes to this assessment while it was open. Reload it to see their changes, then make yours again.")
	{
		AssessmentID = assessmentId;
		PropertyID = propertyId;
		Current = current;
	}

	public Guid AssessmentID { get; }

	public Guid PropertyID { get; }

	/// <summary>The versions in the database now.</summary>
	public VersionStamp Current { get; }
}

public sealed partial class SdeStore
{
	/// <summary>
	/// Records a change to a structure or an assessment: its version goes up by one and it gets the next sequence
	/// number. Every path that writes structure or assessment rows calls this inside its transaction.
	/// </summary>
	internal void BumpVersion(IDbConnection c, IDbTransaction tx, Guid objectId, VersionKind kind)
	{
		if (objectId == Guid.Empty)
		{
			return;
		}
		c.Execute("""
			INSERT INTO RowVersion (ObjectID, Kind, Version, Seq, WriterId, ChangedOn)
			VALUES (@objectId, @kind, 1, (SELECT COALESCE(MAX(Seq), 0) + 1 FROM RowVersion), @writer, @now)
			ON CONFLICT(ObjectID) DO UPDATE SET Version = Version + 1, Seq = excluded.Seq, WriterId = excluded.WriterId, ChangedOn = excluded.ChangedOn
			""", new { objectId, kind = kind == VersionKind.Property ? "P" : "A", writer = Database.WriterId, now = Clock() }, tx);
	}

	/// <summary>The version of a structure or assessment: 0 when v2.5 has never saved it.</summary>
	public long GetVersion(Guid objectId) => Database.Read(c => GetVersion(c, null, objectId));

	internal static long GetVersion(IDbConnection c, IDbTransaction tx, Guid objectId) =>
		c.ExecuteScalar<long?>("SELECT Version FROM RowVersion WHERE ObjectID = @objectId", new { objectId }, tx) ?? 0;

	/// <summary>The versions of an assessment and its structure as they are now, or null when the assessment doesn't exist.</summary>
	public VersionStamp GetStamp(Guid assessmentId) => Database.Read(c =>
	{
		Guid? propertyId = c.ExecuteScalar<Guid?>("SELECT PropertyID FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId });
		return propertyId == null ? null : GetStamp(c, null, assessmentId, propertyId.Value);
	});

	internal static VersionStamp GetStamp(IDbConnection c, IDbTransaction tx, Guid assessmentId, Guid propertyId) => new VersionStamp(
		GetVersion(c, tx, assessmentId),
		GetVersion(c, tx, propertyId),
		c.ExecuteScalar<DateTime?>("SELECT LastUpdate FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId }, tx),
		c.ExecuteScalar<DateTime?>("SELECT LastUpdate FROM Property WHERE PropertyID = @propertyId", new { propertyId }, tx));

	/// <summary>The newest sequence number in the database (0 when nothing has been versioned yet).</summary>
	public long LastSeq() => Database.Read(c => c.ExecuteScalar<long?>("SELECT MAX(Seq) FROM RowVersion") ?? 0);

	/// <summary>Every structure and assessment saved after sequence number <paramref name="seq"/>, oldest first.</summary>
	public List<RowVersionRecord> ChangesSince(long seq) =>
		Database.Read(c => TableMap<RowVersionRecord>.Select(c, "Seq > @seq", new { seq }, orderBy: "Seq").ToList());

	/// <summary>
	/// Refuses a save when the context says what versions the writer loaded and the database has moved on since. A
	/// structure or assessment that doesn't exist yet can't have been changed by anyone else.
	/// </summary>
	private static void CheckExpected(IDbConnection c, IDbTransaction tx, Guid assessmentId, bool assessmentExists, Guid propertyId, bool propertyExists, SaveContext ctx)
	{
		if (!ctx.HasExpectations || (!assessmentExists && !propertyExists))
		{
			return;
		}
		VersionStamp current = GetStamp(c, tx, assessmentId, propertyId);
		bool conflict = false;
		if (assessmentExists)
		{
			conflict |= ctx.ExpectedVersion is long v && v != current.Assessment;
			conflict |= ctx.ExpectedLastUpdate is DateTime t && !SameSecond(t, current.AssessmentLastUpdate);
		}
		if (propertyExists)
		{
			conflict |= ctx.ExpectedPropertyVersion is long pv && pv != current.Property;
			conflict |= ctx.ExpectedPropertyLastUpdate is DateTime pt && !SameSecond(pt, current.PropertyLastUpdate);
		}
		if (conflict)
		{
			throw new ConcurrencyException(assessmentId, propertyId, current);
		}
	}

	private static bool SameSecond(DateTime expected, DateTime? actual) =>
		actual is DateTime a && Math.Abs((a - expected).TotalSeconds) < 1;

	// ───────────── Comparing rows ─────────────

	/// <summary>
	/// True when a structure row would be saved unchanged (LastUpdate aside). Values are compared as stored: rounded to
	/// their Jet scale, dates to the second, an empty text the same as no text.
	/// </summary>
	internal static bool SameProperty(PropertyRecord stored, PropertyRecord next) =>
		SameRow(stored, JetScaling.Apply(next), nameof(PropertyRecord.LastUpdate));

	/// <summary>
	/// True when an assessment and its rows would be saved unchanged (LastUpdate aside): every column of the
	/// assessment, its results included, and the content of its element, adjustment, custom field and photo rows (their
	/// own IDs aside, since those are re-created on save).
	/// </summary>
	internal static bool SameAssessment(IDbConnection c, IDbTransaction tx, AssessmentFile stored, AssessmentFile next)
	{
		if (!SameRow(stored.Assessment, JetScaling.Apply(next.Assessment), nameof(AssessmentRecord.LastUpdate)))
		{
			return false;
		}
		if (!SameRows(stored.CostAdjustments, next.CostAdjustments, nameof(CostAdjustmentResultRecord.ResultID), nameof(CostAdjustmentResultRecord.AssessmentID))
			|| !SameRows(stored.AdditionalAdjustments, next.AdditionalAdjustments, nameof(AdditionalAdjustmentRecord.AdditionalAdjustmentID), nameof(AdditionalAdjustmentRecord.AssessmentID))
			|| !SameRows(stored.ElementPercentages, next.ElementPercentages, nameof(ElementPercentageRecord.ElementID), nameof(ElementPercentageRecord.AssessmentID))
			|| !SameRows(stored.CustomFields.OrderBy(r => r.CustomFieldID).ToList(), next.CustomFields.OrderBy(r => r.CustomFieldID).ToList(),
				nameof(CustomFieldResultRecord.ObjectIdentifier), nameof(CustomFieldResultRecord.ResultType)))
		{
			return false;
		}
		// Photos: the active ones in order. One the editor removed stays in its list, inactive; that's a change until
		// its row is inactive too.
		if (!SameRows(stored.Attachments.Where(a => a.Active).ToList(), next.Attachments.Where(a => a.Active).ToList(),
			nameof(AttachmentRecord.AttachmentID), nameof(AttachmentRecord.AssessmentID)))
		{
			return false;
		}
		foreach (AttachmentRecord removed in next.Attachments.Where(a => !a.Active && a.AttachmentID != 0))
		{
			if (c.ExecuteScalar<long?>("SELECT Active FROM Attachment WHERE AttachmentID = @AttachmentID", new { removed.AttachmentID }, tx) != 0)
			{
				return false;
			}
		}
		return true;
	}

	private static bool SameRows<T>(IReadOnlyList<T> stored, IReadOnlyList<T> next, params string[] ignore)
		where T : class, new()
	{
		if (stored.Count != next.Count)
		{
			return false;
		}
		for (int i = 0; i < stored.Count; i++)
		{
			if (!SameRow(stored[i], JetScaling.Apply(next[i]), ignore))
			{
				return false;
			}
		}
		return true;
	}

	private static bool SameRow<T>(T stored, T next, params string[] ignore)
		where T : class, new()
	{
		foreach (System.Reflection.PropertyInfo p in TableMap<T>.Columns)
		{
			if (Array.IndexOf(ignore, p.Name) < 0 && !SameValue(p.GetValue(stored), p.GetValue(next)))
			{
				return false;
			}
		}
		return true;
	}

	private static bool SameValue(object a, object b)
	{
		if (a is string sa && sa.Length == 0)
		{
			a = null;
		}
		if (b is string sb && sb.Length == 0)
		{
			b = null;
		}
		if (a is DateTime da && b is DateTime db)
		{
			return Math.Abs((da - db).TotalSeconds) < 1;
		}
		// [JetScale] text columns (ElementPercentage.ItemCost) are stored as numbers: "1234.5" and "1234.5000" match.
		if (a is string ta && b is string tb && JetScaling.TryParse(ta, out decimal na) && JetScaling.TryParse(tb, out decimal nb))
		{
			return na == nb;
		}
		return Equals(a, b);
	}
}
