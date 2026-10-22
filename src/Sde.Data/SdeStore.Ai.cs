using Dapper;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// What the AI assistant server (Sde.Mcp) needs beyond the other store methods: a record of a change that edit history
/// can't describe on its own (an event or a review comment an assistant added, the teams structures had before an
/// assistant moved them), and taking back an assistant's comment when its change is reverted.
/// </summary>
public sealed partial class SdeStore
{
	/// <summary>
	/// Writes one edit history row that belongs to no structure or assessment, so no assessment's history shows it,
	/// under the context's batch: a record of a change made outside FEMA's fields, which <see cref="GetBatch"/> returns
	/// with the batch's other rows. <paramref name="value"/> holds what a revert needs (the caller's own format).
	/// </summary>
	/// <param name="field">What kind of record this is (never a FieldCatalog key, so field reverts pass it by).</param>
	/// <param name="label">The change in a few words, as lists of recent changes show it.</param>
	/// <param name="value">The details, stored as the row's new value.</param>
	/// <param name="context">The change's context; it must have a batch.</param>
	public void WriteBatchRecord(string field, string label, string value, SaveContext context)
	{
		ArgumentException.ThrowIfNullOrEmpty(field);
		ArgumentNullException.ThrowIfNull(context);
		if (context.BatchId == null)
		{
			throw new ArgumentException("A batch record needs a batch.", nameof(context));
		}
		Database.InTransaction((c, tx) => TableMap<EditHistoryRecord>.Insert(c, new EditHistoryRecord
		{
			ChangedOn = Clock(),
			ChangedBy = context.User ?? UserName,
			Via = context.Via,
			BatchId = context.BatchId,
			Field = field,
			Label = label,
			NewValue = value,
		}, tx));
	}

	/// <summary>
	/// Removes a review comment that nobody has answered or resolved (reverting an AI assistant's comment). Returns
	/// false, removing nothing, when it has replies, is resolved, or is already gone.
	/// </summary>
	public bool RemoveReviewComment(Guid commentId) => Database.InTransaction((c, tx) => c.Execute("""
		DELETE FROM ReviewComment
		WHERE CommentID = @commentId AND ResolvedOn IS NULL
		  AND NOT EXISTS (SELECT 1 FROM ReviewComment r WHERE r.ParentID = @commentId)
		""", new { commentId }, tx) > 0);
}
