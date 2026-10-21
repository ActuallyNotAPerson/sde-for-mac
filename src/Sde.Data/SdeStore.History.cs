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
	/// Saves an assessment the way every save should: compares it with the stored copy, refuses the save when someone
	/// else saved since the writer loaded it (when the context says what it loaded), refuses a change to a Final or
	/// Letter sent assessment (unless the context allows it), saves, then records what changed and bumps the versions.
	/// Nothing is written when nothing changed, so viewing an assessment never moves its LastUpdate; a locked assessment
	/// that hasn't changed isn't written either, so its lock stays valid. The caller has already recalculated it. With
	/// <paramref name="shapes"/>, the structure's square-footage shapes are replaced too. Returns the versions after
	/// the save.
	/// </summary>
	internal VersionStamp SaveTracked(IDbConnection c, IDbTransaction tx, AssessmentFile file, SaveContext ctx, IReadOnlyList<ShapeRecord> shapes = null)
	{
		Guid id = file.Assessment.AssessmentID;
		AssessmentFile before = id == Guid.Empty ? null : LoadAssessment(c, tx, id);
		PropertyRecord storedProperty = before?.Property
			?? (file.Property.PropertyID == Guid.Empty ? null : TableMap<PropertyRecord>.Get(c, new { file.Property.PropertyID }, tx));
		CheckExpected(c, tx, id, before != null, file.Property.PropertyID, storedProperty != null, ctx);
		List<FieldDiff> diffs = before == null ? new List<FieldDiff>() : AssessmentDiff.Compare(before, file, Reference);
		if (before != null)
		{
			AssessmentStatusRecord status = GetStatus(c, tx, id);
			if (status.IsLocked)
			{
				if (diffs.Count == 0)
				{
					return GetStamp(c, tx, id, before.Property.PropertyID);
				}
				if (!ctx.AllowLocked)
				{
					throw new AssessmentLockedException(id, status.Value, diffs);
				}
			}
		}

		bool shapesChanged = shapes != null && (storedProperty == null || !SameShapes(c, tx, storedProperty.PropertyID, shapes));
		bool propertyChanged = storedProperty == null || shapesChanged || !SameProperty(storedProperty, file.Property);
		if (before != null && !propertyChanged && SameAssessment(c, tx, before, file))
		{
			// Nothing to save: keep the stored LastUpdate values, so the caller's copy matches the database.
			file.Property.LastUpdate = before.Property.LastUpdate;
			file.Assessment.LastUpdate = before.Assessment.LastUpdate;
			if (ctx.Event != null)
			{
				WriteEvent(c, tx, before.Property.PropertyID, id, ctx.Event, ctx);
			}
			return GetStamp(c, tx, id, before.Property.PropertyID);
		}

		SaveAssessment(c, tx, file, propertyChanged);
		Guid propertyId = file.Property.PropertyID;
		Guid assessmentId = file.Assessment.AssessmentID;
		if (shapesChanged)
		{
			ReplaceShapes(c, tx, propertyId, shapes);
		}
		if (propertyChanged)
		{
			BumpVersion(c, tx, propertyId, VersionKind.Property);
		}
		BumpVersion(c, tx, assessmentId, VersionKind.Assessment);
		TrashRemovedPhotos(c, tx, file, ctx);
		ReconcileDamageSuggestions(c, tx, file);

		if (before == null)
		{
			WriteEvent(c, tx, propertyId, assessmentId, ctx.Event ?? CreatedEvent, ctx);
		}
		else
		{
			if (ctx.Event != null)
			{
				WriteEvent(c, tx, propertyId, assessmentId, ctx.Event, ctx);
			}
			WriteDiffs(c, tx, propertyId, assessmentId, diffs, ctx);
		}
		return GetStamp(c, tx, assessmentId, propertyId);
	}

	/// <summary>
	/// Saves a structure on its own (no assessment) and records what changed. Refuses the save when someone else saved
	/// the structure since the writer loaded it (when the context says what it loaded). An unchanged structure isn't
	/// written. Returns whether anything was written.
	/// </summary>
	internal bool SavePropertyTracked(IDbConnection c, IDbTransaction tx, PropertyRecord p, SaveContext ctx)
	{
		PropertyRecord before = p.PropertyID == Guid.Empty ? null : TableMap<PropertyRecord>.Get(c, new { p.PropertyID }, tx);
		if (before != null)
		{
			CheckExpected(c, tx, Guid.Empty, false, p.PropertyID, true, ctx);
			if (SameProperty(before, p))
			{
				p.LastUpdate = before.LastUpdate;
				if (ctx.Event != null)
				{
					WriteEvent(c, tx, p.PropertyID, null, ctx.Event, ctx);
				}
				return false;
			}
		}
		SaveProperty(c, tx, p);
		BumpVersion(c, tx, p.PropertyID, VersionKind.Property);
		if (before == null)
		{
			WriteEvent(c, tx, p.PropertyID, null, ctx.Event ?? "Structure created", ctx);
			return true;
		}
		if (ctx.Event != null)
		{
			WriteEvent(c, tx, p.PropertyID, null, ctx.Event, ctx);
		}
		WriteDiffs(c, tx, p.PropertyID, null, AssessmentDiff.CompareProperty(before, p), ctx);
		return true;
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
