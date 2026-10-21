using System.Data;
using System.Globalization;
using Dapper;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// What a photo's embedded data said when it was added: when it was taken and where. Mac-only (never exported); keyed
/// by the photo's stored file name, because attachment IDs change whenever photos are put in a new order.
/// </summary>
[Table("PhotoInfo")]
public sealed class PhotoInfoRecord
{
	/// <summary>The photo's file name in the attachments folder (AttachmentRecord.FileName).</summary>
	[Key] public string FileName { get; set; }

	public Guid? AssessmentID { get; set; }

	public DateTime? TakenOn { get; set; }

	public double? Latitude { get; set; }

	public double? Longitude { get; set; }

	/// <summary>The file's name before it was copied in, e.g. "IMG_0412.HEIC".</summary>
	public string OriginalName { get; set; }

	[NotMapped] public bool HasPosition => Latitude != null && Longitude != null;
}

/// <summary>
/// An element's % damaged that came from a depth table and that nobody has accepted or changed yet. Mac-only. While
/// any of these remain, the assessment can't be made Final, because the value would reach FEMA's export as if an
/// inspector had entered it.
/// </summary>
[Table("DamageSuggestion")]
public sealed class DamageSuggestionRecord
{
	[Key] public Guid AssessmentID { get; set; }

	/// <summary>The element's name as the Damage step shows it (rows are matched by name; their IDs change on every save).</summary>
	[Key] public string ElementName { get; set; }

	public decimal SuggestedPercent { get; set; }

	/// <summary>The depth above the lowest floor the suggestion was worked out for, in feet.</summary>
	public decimal? DepthFt { get; set; }

	/// <summary>The depth table's name.</summary>
	public string TableName { get; set; }

	public DateTime? SuggestedOn { get; set; }
}

/// <summary>A photo already copied into the attachments folder (see <see cref="SdeStore.ImportPhoto"/>), to attach to an assessment.</summary>
public sealed record AttachedPhoto(Guid AssessmentID, AttachmentRecord Attachment, PhotoInfoRecord Info);

public sealed partial class SdeStore
{
	// ───────────────────────── Photo order ─────────────────────────

	/// <summary>
	/// The order of an assessment's photos is the order of its attachment list. FEMA's tables have no order column, and
	/// everything (loading, reports, Export.json) lists photos by AttachmentID, so when the active photos are no longer
	/// in ID order they are given new IDs in list order: their rows are deleted here and inserted again by the save that
	/// follows. AttachmentID is AUTOINCREMENT, so new IDs always sort after the old ones, and nothing Mac-only is keyed on
	/// it. Photos in Recently deleted keep their IDs.
	/// </summary>
	internal static void RenumberPhotosInOrder(IDbConnection c, IDbTransaction tx, AssessmentFile file)
	{
		List<AttachmentRecord> active = file.Attachments.Where(x => x.Active).ToList();
		long previous = 0;
		bool inOrder = true;
		foreach (AttachmentRecord a in active)
		{
			// A new photo (no ID yet) gets the next ID, after every existing one.
			long id = a.AttachmentID == 0 ? long.MaxValue : a.AttachmentID;
			if (id < previous || (id == previous && id != long.MaxValue))
			{
				inOrder = false;
				break;
			}
			previous = id;
		}
		if (inOrder)
		{
			return;
		}
		foreach (AttachmentRecord a in active.Where(x => x.AttachmentID != 0))
		{
			c.Execute("DELETE FROM Attachment WHERE AttachmentID = @AttachmentID AND AssessmentID = @AssessmentID",
				new { a.AttachmentID, file.Assessment.AssessmentID }, tx);
			a.AttachmentID = 0;
		}
	}

	// ───────────────────────── Photo details (EXIF) ─────────────────────────

	/// <summary>Records (or replaces) what a photo's embedded data said.</summary>
	public void SavePhotoInfo(PhotoInfoRecord info) => Database.InTransaction((c, tx) => TableMap<PhotoInfoRecord>.Upsert(c, info, tx));

	/// <summary>A photo's recorded details, or null.</summary>
	public PhotoInfoRecord GetPhotoInfo(string fileName) =>
		Database.Read(c => TableMap<PhotoInfoRecord>.Get(c, new { FileName = Path.GetFileName(fileName ?? "") }));

	/// <summary>The recorded details of an assessment's photos.</summary>
	public List<PhotoInfoRecord> GetPhotoInfos(Guid assessmentId) =>
		Database.Read(c => TableMap<PhotoInfoRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId }).ToList());

	/// <summary>
	/// Attaches photos (already copied into the attachments folder) to their assessments in one transaction and one
	/// batch of edit history, with their details. The first photo of an assessment that has none becomes its report
	/// photo. Locked (Final, Letter sent) assessments are skipped and listed in the result; the caller removes the files
	/// it copied for them.
	/// </summary>
	public BulkResult AttachPhotos(IReadOnlyList<AttachedPhoto> photos, SaveContext context)
	{
		SaveContext ctx = context.AsBatch();
		BulkResult result = new BulkResult { BatchId = ctx.BatchId };
		Database.InTransaction((c, tx) =>
		{
			foreach (IGrouping<Guid, AttachedPhoto> group in photos.GroupBy(p => p.AssessmentID))
			{
				AssessmentFile file = LoadAssessment(c, tx, group.Key);
				if (file == null)
				{
					continue;
				}
				if (!ctx.AllowLocked && (GetStatus(c, tx, group.Key).IsLocked || IsCheckedOut(c, tx, group.Key)))
				{
					result.Locked.Add(group.Key);
					continue;
				}
				foreach (AttachedPhoto p in group)
				{
					p.Attachment.AssessmentID = group.Key;
					p.Attachment.Active = true;
					p.Attachment.DefaultImage = !file.Attachments.Any(x => x.Active);
					file.Attachments.Add(p.Attachment);
				}
				Calculator.Recalculate(file);
				SaveTracked(c, tx, file, ctx);
				foreach (AttachedPhoto p in group.Where(x => x.Info != null))
				{
					p.Info.FileName = p.Attachment.FileName;
					p.Info.AssessmentID = group.Key;
					TableMap<PhotoInfoRecord>.Upsert(c, p.Info, tx);
				}
				result.Changed++;
			}
		});
		return result;
	}

	// ───────────────────────── Depth-table suggestions ─────────────────────────

	/// <summary>The suggestions on an assessment that nobody has accepted or changed yet.</summary>
	public List<DamageSuggestionRecord> GetDamageSuggestions(Guid assessmentId) =>
		Database.Read(c => TableMap<DamageSuggestionRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId }).ToList());

	/// <summary>
	/// Records element percentages that were just filled in from a depth table (and saved). Only elements whose saved
	/// value is the suggested one are recorded. Writes one history event. Returns how many were recorded.
	/// </summary>
	public int AddDamageSuggestions(Guid assessmentId, IReadOnlyList<DamageSuggestionRecord> suggestions, SaveContext context = null)
	{
		SaveContext ctx = context ?? new SaveContext { Via = SaveVia.DepthTable };
		return Database.InTransaction((c, tx) =>
		{
			AssessmentFile file = LoadAssessment(c, tx, assessmentId) ?? throw new ArgumentException("That assessment no longer exists.");
			int added = 0;
			foreach (DamageSuggestionRecord s in suggestions)
			{
				ElementPercentageRecord e = file.ElementPercentages.FirstOrDefault(x => x.ElementName == s.ElementName);
				if (e == null || e.UserDamage != s.SuggestedPercent)
				{
					continue;
				}
				TableMap<DamageSuggestionRecord>.Delete(c, "AssessmentID = @assessmentId AND ElementName = @ElementName", new { assessmentId, s.ElementName }, tx);
				s.AssessmentID = assessmentId;
				s.SuggestedOn ??= Clock();
				TableMap<DamageSuggestionRecord>.Insert(c, s, tx);
				added++;
			}
			if (added > 0)
			{
				DamageSuggestionRecord first = suggestions[0];
				string depth = first.DepthFt is decimal d ? " at " + d.ToString("0.##", CultureInfo.InvariantCulture) + " ft" : "";
				WriteEvent(c, tx, file.Property.PropertyID, assessmentId,
					$"Damage suggested from depth table \"{first.TableName}\"{depth} ({added} element{(added == 1 ? "" : "s")}, not yet accepted)", ctx);
			}
			return added;
		});
	}

	/// <summary>
	/// The inspector accepts suggested values as their own: the suggestions are removed (the values stay) and the
	/// acceptance is recorded in history. <paramref name="elementNames"/> null accepts all. Returns how many were accepted.
	/// </summary>
	public int AcceptDamageSuggestions(Guid assessmentId, IReadOnlyCollection<string> elementNames = null, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.App;
		return Database.InTransaction((c, tx) =>
		{
			List<DamageSuggestionRecord> rows = TableMap<DamageSuggestionRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId }, tx)
				.Where(r => elementNames == null || elementNames.Contains(r.ElementName)).ToList();
			if (rows.Count == 0)
			{
				return 0;
			}
			foreach (DamageSuggestionRecord r in rows)
			{
				TableMap<DamageSuggestionRecord>.Delete(c, "AssessmentID = @assessmentId AND ElementName = @ElementName", new { assessmentId, r.ElementName }, tx);
			}
			Guid propertyId = c.ExecuteScalar<Guid>("SELECT PropertyID FROM Assessment WHERE AssessmentID = @assessmentId", new { assessmentId }, tx);
			string what = string.Join(", ", rows.Select(r => r.ElementName + " " + r.SuggestedPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%"));
			WriteEvent(c, tx, propertyId, assessmentId, "Accepted suggested damage: " + what, ctx);
			return rows.Count;
		});
	}

	/// <summary>
	/// On every save: a suggestion only stands while the element still has the suggested value. One the inspector
	/// changed (or that an undo, copy or import changed) is removed, since the value is now someone's own.
	/// </summary>
	internal static void ReconcileDamageSuggestions(IDbConnection c, IDbTransaction tx, AssessmentFile file)
	{
		Guid assessmentId = file.Assessment.AssessmentID;
		foreach (DamageSuggestionRecord s in TableMap<DamageSuggestionRecord>.Select(c, "AssessmentID = @assessmentId", new { assessmentId }, tx).ToList())
		{
			ElementPercentageRecord e = file.ElementPercentages.FirstOrDefault(x => x.ElementName == s.ElementName);
			if (e == null || e.UserDamage != s.SuggestedPercent)
			{
				TableMap<DamageSuggestionRecord>.Delete(c, "AssessmentID = @assessmentId AND ElementName = @ElementName", new { assessmentId, s.ElementName }, tx);
			}
		}
	}
}
