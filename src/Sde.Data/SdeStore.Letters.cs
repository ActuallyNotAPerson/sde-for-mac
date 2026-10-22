using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>A letter written to an owner (Mac-only table LetterLog).</summary>
[Table("LetterLog")]
public sealed class LetterLogRecord
{
	[Key] public Guid LetterID { get; set; }

	public Guid AssessmentID { get; set; }

	/// <summary>"Substantial damage", "Not substantially damaged", "Substantial improvement", or a compliance letter ("Compliance options", "ICC support", "Deadline reminder").</summary>
	public string Kind { get; set; }

	/// <summary>The template's file name.</summary>
	public string Template { get; set; }

	public DateTime? GeneratedOn { get; set; }

	/// <summary>When it was sent (null until someone marks it as sent).</summary>
	public DateTime? SentOn { get; set; }

	/// <summary>Where the letter was written (full path).</summary>
	public string FileName { get; set; }
}

public sealed partial class SdeStore
{
	/// <summary>Records letters just written (one row each, LetterID and GeneratedOn filled in when missing), with a history event per letter.</summary>
	public void AddLetters(IEnumerable<LetterLogRecord> letters, SaveContext context = null)
	{
		ArgumentNullException.ThrowIfNull(letters);
		SaveContext ctx = context ?? SaveContext.App;
		Database.InTransaction((c, tx) =>
		{
			foreach (LetterLogRecord l in letters)
			{
				Guid propertyId = PropertyOf(c, tx, l.AssessmentID);
				if (l.LetterID == Guid.Empty)
				{
					l.LetterID = Guid.NewGuid();
				}
				l.GeneratedOn ??= Clock();
				TableMap<LetterLogRecord>.Insert(c, l, tx);
				WriteEvent(c, tx, propertyId, l.AssessmentID, $"Letter written: {l.Kind ?? "letter"} ({Path.GetFileName(l.FileName ?? "")})", ctx);
			}
		});
	}

	/// <summary>An assessment's letters, newest first.</summary>
	public List<LetterLogRecord> GetLetters(Guid assessmentId) =>
		Database.Read(c => TableMap<LetterLogRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId }, orderBy: "GeneratedOn DESC").ToList());

	/// <summary>
	/// Marks an assessment's letter as sent: every letter of it not yet marked gets <paramref name="sentOn"/> (today
	/// when null), and the assessment's status becomes Letter sent, which locks it. Both happen together or not at all.
	/// Returns how many letters were marked (0 when none had been written in SDE; the status still changes).
	/// </summary>
	public int MarkLetterSent(Guid assessmentId, DateTime? sentOn = null, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.App;
		return Database.InTransaction((c, tx) =>
		{
			Guid propertyId = PropertyOf(c, tx, assessmentId);
			DateTime when = sentOn ?? Clock().Date;
			List<LetterLogRecord> unsent = TableMap<LetterLogRecord>.Select(c, "AssessmentID = @assessmentId AND SentOn IS NULL", new { assessmentId }, tx).ToList();
			foreach (LetterLogRecord l in unsent)
			{
				l.SentOn = when;
				TableMap<LetterLogRecord>.Update(c, l, tx);
				WriteEvent(c, tx, propertyId, assessmentId, $"Letter sent: {l.Kind ?? "letter"} ({when.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)})", ctx);
			}
			SetStatus(c, tx, assessmentId, AssessmentStatus.LetterSent, null, ctx);
			return unsent.Count;
		});
	}
}
