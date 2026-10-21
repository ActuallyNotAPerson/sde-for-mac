using System.Data;
using Dapper;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

public sealed partial class SdeStore
{
	/// <summary>Edits of one field by one person within this long are kept as one history row.</summary>
	public static readonly TimeSpan HistoryMergeWindow = TimeSpan.FromMinutes(10);

	public const string CreatedEvent = "Assessment created";

	/// <summary>The name written to edit history when a save doesn't name anyone. The app sets it from Settings.</summary>
	public string UserName { get; set; } = Environment.UserName;

	/// <summary>The time used for edit history, statuses and Recently deleted (tests replace it).</summary>
	public Func<DateTime> Clock { get; set; } = Now;

	/// <summary>
	/// Saves an assessment the way every save should: compares it with the stored copy, refuses a change to a Final
	/// or Letter sent assessment (unless the context allows it), saves, then records what changed. A locked assessment
	/// that hasn't changed isn't written at all, so its lock stays valid. The caller has already recalculated it.
	/// </summary>
	internal void SaveTracked(IDbConnection c, IDbTransaction tx, AssessmentFile file, SaveContext ctx)
	{
		Guid id = file.Assessment.AssessmentID;
		AssessmentFile before = id == Guid.Empty ? null : LoadAssessment(c, tx, id);
		List<FieldDiff> diffs = before == null ? new List<FieldDiff>() : AssessmentDiff.Compare(before, file, Reference);
		if (before != null)
		{
			AssessmentStatusRecord status = GetStatus(c, tx, id);
			if (status.IsLocked)
			{
				if (diffs.Count == 0)
				{
					return;
				}
				if (!ctx.AllowLocked)
				{
					throw new AssessmentLockedException(id, status.Value, diffs);
				}
			}
		}

		SaveAssessment(c, tx, file);
		TrashRemovedPhotos(c, tx, file, ctx);
		ReconcileDamageSuggestions(c, tx, file);

		Guid propertyId = file.Property.PropertyID;
		Guid assessmentId = file.Assessment.AssessmentID;
		if (before == null)
		{
			WriteEvent(c, tx, propertyId, assessmentId, ctx.Event ?? CreatedEvent, ctx);
			return;
		}
		if (ctx.Event != null)
		{
			WriteEvent(c, tx, propertyId, assessmentId, ctx.Event, ctx);
		}
		WriteDiffs(c, tx, propertyId, assessmentId, diffs, ctx);
	}

	/// <summary>Saves a structure on its own (no assessment) and records what changed.</summary>
	internal void SavePropertyTracked(IDbConnection c, IDbTransaction tx, PropertyRecord p, SaveContext ctx)
	{
		PropertyRecord before = p.PropertyID == Guid.Empty ? null : TableMap<PropertyRecord>.Get(c, new { p.PropertyID }, tx);
		SaveProperty(c, tx, p);
		if (before == null)
		{
			WriteEvent(c, tx, p.PropertyID, null, ctx.Event ?? "Structure created", ctx);
			return;
		}
		if (ctx.Event != null)
		{
			WriteEvent(c, tx, p.PropertyID, null, ctx.Event, ctx);
		}
		WriteDiffs(c, tx, p.PropertyID, null, AssessmentDiff.CompareProperty(before, p), ctx);
	}

	/// <summary>Records an event (no field) such as "Assessment created", "Imported from SDE_1" or "Reopened".</summary>
	public void WriteEvent(Guid propertyId, Guid? assessmentId, string label, SaveContext context = null) =>
		Database.InTransaction((c, tx) => WriteEvent(c, tx, propertyId, assessmentId, label, context ?? SaveContext.App));

	internal void WriteEvent(IDbConnection c, IDbTransaction tx, Guid propertyId, Guid? assessmentId, string label, SaveContext ctx,
		string field = null, string oldValue = null, string newValue = null)
	{
		TableMap<EditHistoryRecord>.Insert(c, new EditHistoryRecord
		{
			PropertyID = propertyId,
			AssessmentID = assessmentId,
			ChangedOn = Clock(),
			ChangedBy = ctx.User ?? UserName,
			Via = ctx.Via,
			BatchId = ctx.BatchId,
			Field = field,
			Label = label,
			OldValue = oldValue,
			NewValue = newValue,
		}, tx);
	}

	/// <summary>
	/// Writes one row per changed field. An app edit of a field the same person changed within
	/// <see cref="HistoryMergeWindow"/> updates that row instead (typing a value is one change, not one per autosave);
	/// if the field ends up back where it started, the row is removed. Batch rows are never merged.
	/// </summary>
	private void WriteDiffs(IDbConnection c, IDbTransaction tx, Guid propertyId, Guid? assessmentId, IReadOnlyList<FieldDiff> diffs, SaveContext ctx)
	{
		DateTime now = Clock();
		string user = ctx.User ?? UserName;
		foreach (FieldDiff d in diffs)
		{
			EditHistoryRecord recent = ctx.BatchId != null ? null : c.QueryFirstOrDefault<EditHistoryRecord>("""
				SELECT * FROM EditHistory
				WHERE PropertyID = @propertyId AND AssessmentID IS @assessmentId AND Field = @Key
				ORDER BY ChangedOn DESC, HistoryID DESC LIMIT 1
				""", new { propertyId, assessmentId, d.Key }, tx);
			bool merge = recent != null && recent.BatchId == null && recent.ChangedBy == user && recent.Via == ctx.Via
				&& now - recent.ChangedOn <= HistoryMergeWindow && recent.NewValue == d.OldValue && d.Key != AssessmentDiff.PhotoKey;
			if (!merge)
			{
				TableMap<EditHistoryRecord>.Insert(c, new EditHistoryRecord
				{
					PropertyID = propertyId,
					AssessmentID = assessmentId,
					ChangedOn = now,
					ChangedBy = user,
					Via = ctx.Via,
					BatchId = ctx.BatchId,
					Field = d.Key,
					Label = d.Label,
					OldValue = d.OldValue,
					NewValue = d.NewValue,
				}, tx);
			}
			else if (recent.OldValue == d.NewValue)
			{
				c.Execute("DELETE FROM EditHistory WHERE HistoryID = @HistoryID", new { recent.HistoryID }, tx);
			}
			else
			{
				c.Execute("UPDATE EditHistory SET NewValue = @NewValue, ChangedOn = @now WHERE HistoryID = @HistoryID",
					new { d.NewValue, now, recent.HistoryID }, tx);
			}
		}
	}

	/// <summary>
	/// An assessment's history, newest first: its own rows plus changes made to its structure on their own (an import
	/// of coordinates, say).
	/// </summary>
	public List<EditHistoryRecord> GetHistory(Guid assessmentId) =>
		Database.Read(c => TableMap<EditHistoryRecord>.Select(c,
			"AssessmentID = @assessmentId OR (AssessmentID IS NULL AND PropertyID = (SELECT PropertyID FROM Assessment WHERE AssessmentID = @assessmentId))",
			new { assessmentId }, orderBy: "ChangedOn DESC, HistoryID DESC").ToList());

	/// <summary>A structure's history (all its assessments and its own fields), newest first.</summary>
	public List<EditHistoryRecord> GetPropertyHistory(Guid propertyId) =>
		Database.Read(c => TableMap<EditHistoryRecord>.Select(c, "PropertyID = @propertyId", new { propertyId }, orderBy: "ChangedOn DESC, HistoryID DESC").ToList());

	/// <summary>Every row written by one bulk edit or import, in the order written.</summary>
	public List<EditHistoryRecord> GetBatch(string batchId) =>
		Database.Read(c => TableMap<EditHistoryRecord>.Select(c, "BatchId = @batchId", new { batchId }, orderBy: "HistoryID").ToList());
}
