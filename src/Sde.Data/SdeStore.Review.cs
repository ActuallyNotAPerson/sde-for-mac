using Dapper;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>A reviewer's comment on an assessment, or a reply to one (Mac-only ReviewComment table).</summary>
[Table("ReviewComment")]
public sealed class ReviewCommentRecord
{
	[Key] public Guid CommentID { get; set; }

	public Guid AssessmentID { get; set; }

	/// <summary>The comment this one answers, or null for a new thread.</summary>
	public Guid? ParentID { get; set; }

	/// <summary>The field it's about (a FieldCatalog key), or null for the whole assessment.</summary>
	public string FieldKey { get; set; }

	public string Body { get; set; }

	public string Author { get; set; }

	/// <summary>App or AI assistant.</summary>
	public string Via { get; set; }

	public DateTime CreatedOn { get; set; }

	public DateTime? ResolvedOn { get; set; }

	public string ResolvedBy { get; set; }

	[NotMapped] public bool IsResolved => ResolvedOn != null;
}

/// <summary>A drawing on a photo, as JSON (Mac-only PhotoMarkup table). The photo file itself never changes.</summary>
[Table("PhotoMarkup")]
public sealed class PhotoMarkupRecord
{
	[Key] public string FileName { get; set; }

	public string Markup { get; set; }

	public DateTime? UpdatedOn { get; set; }

	public string UpdatedBy { get; set; }
}

public sealed partial class SdeStore
{
	public const int MaxCommentLength = 4000;

	/// <summary>An assessment's comments and replies, oldest first.</summary>
	public List<ReviewCommentRecord> GetReviewComments(Guid assessmentId) =>
		Database.Read(c => TableMap<ReviewCommentRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId }, orderBy: "CreatedOn, rowid").ToList());

	/// <summary>
	/// Adds a comment (or, with <paramref name="parentId"/>, a reply) to an assessment. Comments can be added to Final
	/// assessments too: they don't change the assessment.
	/// </summary>
	public ReviewCommentRecord AddReviewComment(Guid assessmentId, string body, string fieldKey = null, Guid? parentId = null, SaveContext context = null)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			throw new ArgumentException("Write the comment first.");
		}
		if (body.Length > MaxCommentLength)
		{
			throw new ArgumentException($"A comment can be at most {MaxCommentLength} characters.");
		}
		if (fieldKey != null && FieldCatalog.Get(fieldKey) == null && !fieldKey.StartsWith("Element.", StringComparison.Ordinal))
		{
			throw new ArgumentException($"There's no field called \"{fieldKey}\".");
		}
		SaveContext ctx = context ?? SaveContext.App;
		return Database.InTransaction((c, tx) =>
		{
			if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId }, tx) == 0)
			{
				throw new ArgumentException("That assessment no longer exists.");
			}
			if (parentId is Guid parent && c.ExecuteScalar<int>("SELECT COUNT(*) FROM ReviewComment WHERE CommentID = @parent AND AssessmentID = @assessmentId", new { parent, assessmentId }, tx) == 0)
			{
				throw new ArgumentException("The comment being answered no longer exists.");
			}
			ReviewCommentRecord r = new ReviewCommentRecord
			{
				CommentID = Guid.NewGuid(),
				AssessmentID = assessmentId,
				ParentID = parentId,
				FieldKey = fieldKey,
				Body = body.Trim(),
				Author = ctx.User ?? UserName,
				Via = ctx.Via,
				CreatedOn = Clock(),
			};
			TableMap<ReviewCommentRecord>.Insert(c, r, tx);
			return r;
		});
	}

	/// <summary>Marks a thread resolved (or open again). Replies follow their thread's state.</summary>
	public void ResolveReviewComment(Guid commentId, bool resolved = true, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.App;
		Database.InTransaction((c, tx) => c.Execute("UPDATE ReviewComment SET ResolvedOn = @on, ResolvedBy = @by WHERE CommentID = @commentId OR ParentID = @commentId",
			new { on = resolved ? Clock() : (DateTime?)null, by = resolved ? ctx.User ?? UserName : null, commentId }, tx));
	}

	/// <summary>How many open (unresolved) comment threads each assessment has.</summary>
	public Dictionary<Guid, int> OpenCommentCounts() => Database.Read(c => c.Query<OpenCountRow>(
		"SELECT AssessmentID, COUNT(*) AS Count FROM ReviewComment WHERE ParentID IS NULL AND ResolvedOn IS NULL GROUP BY AssessmentID")
		.ToDictionary(r => r.AssessmentID, r => r.Count));

	private sealed class OpenCountRow
	{
		public Guid AssessmentID { get; set; }

		public int Count { get; set; }
	}

	// ───────────── Photo markup ─────────────

	/// <summary>A photo's markup JSON, or null.</summary>
	public string GetPhotoMarkup(string fileName) =>
		Database.Read(c => TableMap<PhotoMarkupRecord>.Get(c, new { FileName = Path.GetFileName(fileName ?? "") })?.Markup);

	/// <summary>The markup of an assessment's photos, by file name.</summary>
	public Dictionary<string, string> GetPhotoMarkups(Guid assessmentId) => Database.Read(c => c.Query<PhotoMarkupRecord>(
		"SELECT m.FileName, m.Markup, m.UpdatedOn, m.UpdatedBy FROM PhotoMarkup m WHERE m.FileName IN (SELECT FileName FROM Attachment WHERE AssessmentID = @assessmentId)",
		new { assessmentId }).ToDictionary(m => m.FileName, m => m.Markup, StringComparer.Ordinal));

	/// <summary>
	/// Saves (or, with null or empty JSON, removes) a photo's markup. Refused when the photo belongs to a Final or Letter
	/// sent assessment: its reports must not change.
	/// </summary>
	public void SavePhotoMarkup(string fileName, string markupJson, SaveContext context = null)
	{
		string name = Path.GetFileName(fileName ?? "");
		if (name.Length == 0)
		{
			throw new ArgumentException("No photo was given.");
		}
		SaveContext ctx = context ?? SaveContext.App;
		Database.InTransaction((c, tx) =>
		{
			foreach (Guid owner in c.Query<Guid>("SELECT DISTINCT AssessmentID FROM Attachment WHERE FileName = @name", new { name }, tx).ToList())
			{
				AssessmentStatusRecord status = GetStatus(c, tx, owner);
				if (status.IsLocked && !ctx.AllowLocked)
				{
					throw new AssessmentLockedException(owner, status.Value, new List<FieldDiff> { new FieldDiff(AssessmentDiff.PhotoKey + ".Markup", "Photo markup", null, name) });
				}
			}
			if (string.IsNullOrWhiteSpace(markupJson))
			{
				TableMap<PhotoMarkupRecord>.Delete(c, "FileName = @name", new { name }, tx);
				return;
			}
			TableMap<PhotoMarkupRecord>.Upsert(c, new PhotoMarkupRecord { FileName = name, Markup = markupJson, UpdatedOn = Clock(), UpdatedBy = ctx.User ?? UserName }, tx);
		});
	}
}
