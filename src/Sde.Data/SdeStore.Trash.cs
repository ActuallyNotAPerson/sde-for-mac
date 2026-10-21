using System.Data;
using Dapper;
using Newtonsoft.Json;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>What is in Recently deleted.</summary>
public enum DeletedKind
{
	Structure,
	Assessment,
	Photo,
}

/// <summary>A row of the DeletedItem table: something in Recently deleted, and when it goes for good.</summary>
[Table("DeletedItem")]
public sealed class DeletedItemRecord
{
	/// <summary>"S:&lt;PropertyID&gt;", "A:&lt;AssessmentID&gt;" or "P:&lt;FileName&gt;".</summary>
	[Key] public string ItemKey { get; set; }

	/// <summary>The stored kind; use <see cref="Value"/>.</summary>
	public string Kind { get; set; }

	public Guid? PropertyID { get; set; }

	public Guid? AssessmentID { get; set; }

	public string FileName { get; set; }

	/// <summary>What the list shows, e.g. "123 River Street".</summary>
	public string Label { get; set; }

	/// <summary>For a structure: JSON list of the assessment IDs that were deleted with it (and come back with it).</summary>
	public string Detail { get; set; }

	public DateTime DeletedOn { get; set; }

	public string DeletedBy { get; set; }

	public DateTime PurgeAfter { get; set; }

	[NotMapped] public DeletedKind Value => Enum.TryParse(Kind, out DeletedKind k) ? k : DeletedKind.Structure;
}

/// <summary>
/// Recently deleted. Deleting moves a structure, an assessment or a photo here instead of removing it: the rows stay
/// in FEMA's tables with Active = 0, which is how FEMA's own SDE deleted, so every list, report and export (and older
/// versions of this app) already leave them out. They can be restored for <see cref="KeepDays"/> days, then they are
/// removed for good (with their photo files) the next time the database is opened.
/// </summary>
public sealed partial class SdeStore
{
	public const int KeepDays = 30;

	private static string StructureKey(Guid id) => "S:" + id.ToString("D");

	private static string AssessmentKey(Guid id) => "A:" + id.ToString("D");

	private static string PhotoKey(string fileName) => "P:" + fileName;

	/// <summary>Moves a structure and its assessments to Recently deleted.</summary>
	public void TrashProperty(Guid propertyId, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.App;
		Database.InTransaction((c, tx) =>
		{
			PropertyRecord p = TableMap<PropertyRecord>.Get(c, new { PropertyID = propertyId }, tx) ?? throw new ArgumentException("That structure no longer exists.");
			List<Guid> assessments = c.Query<Guid>("SELECT AssessmentID FROM Assessment WHERE PropertyID = @propertyId AND Active = 1", new { propertyId }, tx).ToList();
			foreach (Guid aid in assessments)
			{
				AssessmentStatusRecord status = GetStatus(c, tx, aid);
				if (status.IsLocked)
				{
					throw new AssessmentLockedException(aid, status.Value, new List<FieldDiff>());
				}
			}
			c.Execute("UPDATE Assessment SET Active = 0 WHERE PropertyID = @propertyId AND Active = 1", new { propertyId }, tx);
			c.Execute("UPDATE Property SET Active = 0 WHERE PropertyID = @propertyId", new { propertyId }, tx);
			foreach (Guid aid in assessments)
			{
				BumpVersion(c, tx, aid, VersionKind.Assessment);
			}
			BumpVersion(c, tx, propertyId, VersionKind.Property);
			DateTime now = Clock();
			TableMap<DeletedItemRecord>.Upsert(c, new DeletedItemRecord
			{
				ItemKey = StructureKey(propertyId),
				Kind = nameof(DeletedKind.Structure),
				PropertyID = propertyId,
				Label = AddressLabel(p),
				Detail = JsonConvert.SerializeObject(assessments),
				DeletedOn = now,
				DeletedBy = ctx.User ?? UserName,
				PurgeAfter = now.AddDays(KeepDays),
			}, tx);
			WriteEvent(c, tx, propertyId, null, "Moved to Recently deleted", ctx);
		});
	}

	/// <summary>Moves one assessment to Recently deleted (its structure stays).</summary>
	public void TrashAssessment(Guid assessmentId, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.App;
		Database.InTransaction((c, tx) =>
		{
			AssessmentRecord a = TableMap<AssessmentRecord>.Get(c, new { AssessmentID = assessmentId }, tx) ?? throw new ArgumentException("That assessment no longer exists.");
			if (GetStatus(c, tx, assessmentId).IsLocked)
			{
				throw new AssessmentLockedException(assessmentId, GetStatus(c, tx, assessmentId).Value, new List<FieldDiff>());
			}
			PropertyRecord p = TableMap<PropertyRecord>.Get(c, new { a.PropertyID }, tx);
			c.Execute("UPDATE Assessment SET Active = 0 WHERE AssessmentID = @assessmentId", new { assessmentId }, tx);
			BumpVersion(c, tx, assessmentId, VersionKind.Assessment);
			DateTime now = Clock();
			TableMap<DeletedItemRecord>.Upsert(c, new DeletedItemRecord
			{
				ItemKey = AssessmentKey(assessmentId),
				Kind = nameof(DeletedKind.Assessment),
				PropertyID = a.PropertyID,
				AssessmentID = assessmentId,
				Label = AddressLabel(p) + (a.DateofInspection is DateTime d ? ", inspected " + d.ToString("MMM d, yyyy") : ""),
				DeletedOn = now,
				DeletedBy = ctx.User ?? UserName,
				PurgeAfter = now.AddDays(KeepDays),
			}, tx);
			WriteEvent(c, tx, a.PropertyID, assessmentId, "Moved to Recently deleted", ctx);
		});
	}

	/// <summary>
	/// Records photos the editor removed (their attachment rows were saved with Active = 0) in Recently deleted. Called
	/// inside a save.
	/// </summary>
	internal void TrashRemovedPhotos(IDbConnection c, IDbTransaction tx, AssessmentFile file, SaveContext ctx)
	{
		foreach (AttachmentRecord a in file.Attachments.Where(a => !a.Active && !string.IsNullOrEmpty(a.FileName)))
		{
			string key = PhotoKey(a.FileName);
			if (TableMap<DeletedItemRecord>.Get(c, new { ItemKey = key }, tx) != null)
			{
				continue;
			}
			DateTime now = Clock();
			TableMap<DeletedItemRecord>.Insert(c, new DeletedItemRecord
			{
				ItemKey = key,
				Kind = nameof(DeletedKind.Photo),
				PropertyID = file.Property.PropertyID,
				AssessmentID = file.Assessment.AssessmentID,
				FileName = a.FileName,
				Label = (string.IsNullOrWhiteSpace(a.Description) ? "Photo" : a.Description) + " (" + AddressLabel(file.Property) + ")",
				DeletedOn = now,
				DeletedBy = ctx.User ?? UserName,
				PurgeAfter = now.AddDays(KeepDays),
			}, tx);
		}
	}

	/// <summary>Everything in Recently deleted, most recent first.</summary>
	public List<DeletedItemRecord> GetDeletedItems() =>
		Database.Read(c => TableMap<DeletedItemRecord>.Select(c, orderBy: "DeletedOn DESC").ToList());

	/// <summary>
	/// Puts something back. A restored assessment whose structure is also in Recently deleted brings the structure back
	/// too; a restored structure brings back the assessments that were deleted with it. A photo goes back only to the
	/// assessment it was removed from, and not while that assessment is Final or Letter sent.
	/// </summary>
	public void Restore(string itemKey, SaveContext context = null)
	{
		SaveContext ctx = context ?? new SaveContext { Via = SaveVia.Restore };
		Database.InTransaction((c, tx) =>
		{
			DeletedItemRecord item = TableMap<DeletedItemRecord>.Get(c, new { ItemKey = itemKey }, tx) ?? throw new ArgumentException("That's no longer in Recently deleted.");
			switch (item.Value)
			{
				case DeletedKind.Structure:
					RestoreStructure(c, tx, item);
					WriteEvent(c, tx, item.PropertyID!.Value, null, "Restored from Recently deleted", ctx);
					break;
				case DeletedKind.Assessment:
					c.Execute("UPDATE Assessment SET Active = 1 WHERE AssessmentID = @AssessmentID", new { item.AssessmentID }, tx);
					BumpVersion(c, tx, item.AssessmentID!.Value, VersionKind.Assessment);
					if (c.ExecuteScalar<long>("SELECT Active FROM Property WHERE PropertyID = @PropertyID", new { item.PropertyID }, tx) == 0
						&& TableMap<DeletedItemRecord>.Get(c, new { ItemKey = StructureKey(item.PropertyID!.Value) }, tx) is DeletedItemRecord structure)
					{
						// The structure comes back as it went: with the assessments that were deleted with it.
						RestoreStructure(c, tx, structure);
						TableMap<DeletedItemRecord>.Delete(c, "ItemKey = @ItemKey", new { structure.ItemKey }, tx);
					}
					WriteEvent(c, tx, item.PropertyID!.Value, item.AssessmentID, "Restored from Recently deleted", ctx);
					break;
				case DeletedKind.Photo:
					if (item.AssessmentID is Guid owner)
					{
						AssessmentStatusRecord status = GetStatus(c, tx, owner);
						if (status.IsLocked && !ctx.AllowLocked)
						{
							throw new AssessmentLockedException(owner, status.Value,
								new List<FieldDiff> { new FieldDiff(AssessmentDiff.PhotoKey, "Photo restored", null, item.FileName) });
						}
						c.Execute("UPDATE Attachment SET Active = 1 WHERE FileName = @FileName AND AssessmentID = @owner AND Active = 0", new { item.FileName, owner }, tx);
						BumpVersion(c, tx, owner, VersionKind.Assessment);
					}
					else
					{
						c.Execute("UPDATE Attachment SET Active = 1 WHERE FileName = @FileName AND Active = 0", new { item.FileName }, tx);
					}
					WriteEvent(c, tx, item.PropertyID!.Value, item.AssessmentID, "Photo restored", ctx);
					break;
			}
			TableMap<DeletedItemRecord>.Delete(c, "ItemKey = @itemKey", new { itemKey }, tx);
		});
	}

	private void RestoreStructure(IDbConnection c, IDbTransaction tx, DeletedItemRecord item)
	{
		c.Execute("UPDATE Property SET Active = 1 WHERE PropertyID = @PropertyID", new { item.PropertyID }, tx);
		BumpVersion(c, tx, item.PropertyID!.Value, VersionKind.Property);
		List<Guid> assessments = string.IsNullOrEmpty(item.Detail) ? new List<Guid>() : JsonConvert.DeserializeObject<List<Guid>>(item.Detail);
		foreach (Guid id in assessments)
		{
			c.Execute("UPDATE Assessment SET Active = 1 WHERE AssessmentID = @id", new { id }, tx);
			BumpVersion(c, tx, id, VersionKind.Assessment);
		}
	}

	/// <summary>Removes one item for good now (its rows and photo files).</summary>
	public void Purge(string itemKey) => PurgeWhere("ItemKey = @itemKey", new { itemKey });

	/// <summary>Removes everything in Recently deleted for good.</summary>
	public void EmptyTrash() => PurgeWhere(null, null);

	/// <summary>
	/// Removes items older than <see cref="KeepDays"/> for good, and drops Recently deleted, photo markup and dismissed
	/// check rows whose target is gone (an older version of this app may have deleted it). Called when the database is
	/// opened.
	/// </summary>
	public int PurgeExpired()
	{
		Database.InTransaction((c, tx) => c.Execute("""
			DELETE FROM DeletedItem WHERE
			  (Kind = 'Structure' AND NOT EXISTS (SELECT 1 FROM Property p WHERE p.PropertyID = DeletedItem.PropertyID)) OR
			  (Kind = 'Assessment' AND NOT EXISTS (SELECT 1 FROM Assessment a WHERE a.AssessmentID = DeletedItem.AssessmentID)) OR
			  (Kind = 'Photo' AND NOT EXISTS (SELECT 1 FROM Attachment t WHERE t.FileName = DeletedItem.FileName));
			DELETE FROM PhotoMarkup WHERE NOT EXISTS (SELECT 1 FROM Attachment t WHERE t.FileName = PhotoMarkup.FileName);
			DELETE FROM QaDismissal WHERE
			  NOT EXISTS (SELECT 1 FROM Assessment a WHERE a.AssessmentID = QaDismissal.ObjectID) AND
			  NOT EXISTS (SELECT 1 FROM Property p WHERE p.PropertyID = QaDismissal.ObjectID) AND
			  NOT EXISTS (SELECT 1 FROM Event e WHERE e.EventID = QaDismissal.ObjectID);
			""", transaction: tx));
		return PurgeWhere("PurgeAfter <= @now", new { now = Clock() });
	}

	private int PurgeWhere(string where, object args)
	{
		List<string> files = new List<string>();
		int count = Database.InTransaction((c, tx) =>
		{
			List<DeletedItemRecord> items = TableMap<DeletedItemRecord>.Select(c, where, args, tx).ToList();
			foreach (DeletedItemRecord item in items)
			{
				switch (item.Value)
				{
					case DeletedKind.Structure:
						foreach (Guid aid in c.Query<Guid>("SELECT AssessmentID FROM Assessment WHERE PropertyID = @PropertyID", new { item.PropertyID }, tx).ToList())
						{
							files.AddRange(DeleteAssessmentRows(c, tx, aid));
							BumpVersion(c, tx, aid, VersionKind.Assessment);
							TableMap<DeletedItemRecord>.Delete(c, "ItemKey = @key", new { key = AssessmentKey(aid) }, tx);
						}
						BumpVersion(c, tx, item.PropertyID!.Value, VersionKind.Property);
						object pid = new { propertyId = item.PropertyID };
						TableMap<ShapeRecord>.Delete(c, "PropertyID = @propertyId", pid, tx);
						TableMap<CustomFieldResultRecord>.Delete(c, "ObjectIdentifier = @propertyId", pid, tx);
						TableMap<RecentAssessmentRecord>.Delete(c, "PropertyID = @propertyId", pid, tx);
						TableMap<PropertyRecord>.Delete(c, "PropertyID = @propertyId", pid, tx);
						break;
					case DeletedKind.Assessment:
						files.AddRange(DeleteAssessmentRows(c, tx, item.AssessmentID!.Value));
						BumpVersion(c, tx, item.AssessmentID!.Value, VersionKind.Assessment);
						break;
					case DeletedKind.Photo:
						c.Execute("DELETE FROM Attachment WHERE FileName = @FileName AND Active = 0", new { item.FileName }, tx);
						files.Add(item.FileName);
						break;
				}
				TableMap<DeletedItemRecord>.Delete(c, "ItemKey = @ItemKey", new { item.ItemKey }, tx);
			}
			// A photo file another (active) attachment still uses is kept: an import can share file names.
			HashSet<string> inUse = c.Query<string>("SELECT FileName FROM Attachment WHERE FileName IS NOT NULL", transaction: tx).ToHashSet(StringComparer.Ordinal);
			files.RemoveAll(inUse.Contains);
			return items.Count;
		});
		DeleteFiles(files);
		return count;
	}

	/// <summary>Assessment and structure IDs in Recently deleted (an import of the same IDs replaces them).</summary>
	public HashSet<Guid> TrashedIds() => Database.Read(c => c.Query<Guid>(
		"SELECT PropertyID FROM Property WHERE Active = 0 UNION SELECT AssessmentID FROM Assessment WHERE Active = 0").ToHashSet());

	/// <summary>
	/// Removes trashed rows for good when an import brings in the same IDs, so the import isn't blocked by something
	/// nobody can see. Returns what was removed (for the import's notes). Runs inside the import's transaction.
	/// </summary>
	internal List<string> PurgeTrashedCollisions(IDbConnection c, IDbTransaction tx, IEnumerable<Guid> propertyIds, IEnumerable<Guid> assessmentIds)
	{
		List<string> removed = new List<string>();
		List<string> files = new List<string>();
		foreach (Guid aid in assessmentIds.Distinct())
		{
			if (c.ExecuteScalar<long?>("SELECT Active FROM Assessment WHERE AssessmentID = @aid", new { aid }, tx) == 0)
			{
				files.AddRange(DeleteAssessmentRows(c, tx, aid));
				BumpVersion(c, tx, aid, VersionKind.Assessment);
				TableMap<DeletedItemRecord>.Delete(c, "ItemKey = @key", new { key = AssessmentKey(aid) }, tx);
				removed.Add($"Assessment {aid} was in Recently deleted and has been replaced by the imported one.");
			}
		}
		foreach (Guid pid in propertyIds.Distinct())
		{
			if (c.ExecuteScalar<long?>("SELECT Active FROM Property WHERE PropertyID = @pid", new { pid }, tx) == 0)
			{
				foreach (Guid aid in c.Query<Guid>("SELECT AssessmentID FROM Assessment WHERE PropertyID = @pid", new { pid }, tx).ToList())
				{
					files.AddRange(DeleteAssessmentRows(c, tx, aid));
					BumpVersion(c, tx, aid, VersionKind.Assessment);
					TableMap<DeletedItemRecord>.Delete(c, "ItemKey = @key", new { key = AssessmentKey(aid) }, tx);
				}
				BumpVersion(c, tx, pid, VersionKind.Property);
				object args = new { propertyId = pid };
				TableMap<ShapeRecord>.Delete(c, "PropertyID = @propertyId", args, tx);
				TableMap<CustomFieldResultRecord>.Delete(c, "ObjectIdentifier = @propertyId", args, tx);
				TableMap<RecentAssessmentRecord>.Delete(c, "PropertyID = @propertyId", args, tx);
				TableMap<PropertyRecord>.Delete(c, "PropertyID = @propertyId", args, tx);
				TableMap<DeletedItemRecord>.Delete(c, "ItemKey = @key", new { key = StructureKey(pid) }, tx);
				removed.Add($"Structure {pid} was in Recently deleted and has been replaced by the imported one.");
			}
		}
		_pendingFileDeletes.AddRange(files);
		return removed;
	}

	/// <summary>Photo files to delete once the import that purged their rows has committed.</summary>
	private readonly List<string> _pendingFileDeletes = new List<string>();

	internal void DeletePendingFiles()
	{
		HashSet<string> inUse = Database.Read(c => c.Query<string>("SELECT FileName FROM Attachment WHERE FileName IS NOT NULL").ToHashSet(StringComparer.Ordinal));
		DeleteFiles(_pendingFileDeletes.Where(f => !inUse.Contains(f)).ToList());
		_pendingFileDeletes.Clear();
	}

	private string AddressLabel(PropertyRecord p)
	{
		if (p == null)
		{
			return "Structure";
		}
		string street = string.Join(" ", new[] { p.StreetNumber, p.StreetName, Reference.LookupName(p.StreetSuffixID) }.Where(s => !string.IsNullOrWhiteSpace(s)));
		return string.IsNullOrWhiteSpace(street) ? "Structure with no address" : street;
	}
}
