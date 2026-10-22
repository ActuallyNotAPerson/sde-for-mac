using System.Data;
using Dapper;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// Links an assessment to the record it was imported from (an inspection in a permit system, a row of a spreadsheet).
/// Mac-only: the ExternalLink table isn't part of FEMA's database and never reaches Export.json. Deleting the
/// assessment deletes its link.
/// </summary>
[Table("ExternalLink")]
public sealed class ExternalLinkRecord
{
	[Key] public Guid AssessmentID { get; set; }

	/// <summary>Where the record came from: the import profile's source name (v1.5 always wrote "Accela").</summary>
	public string Source { get; set; }

	/// <summary>The source system's record ID, e.g. "BLD-2026-00123" (matched ignoring case).</summary>
	public string RecordId { get; set; }

	/// <summary>The inspection within the record; "" when there is one assessment per record.</summary>
	public string InspectionId { get; set; } = "";

	/// <summary>JSON object {field key: source text} as last imported, so a re-import only applies what changed.</summary>
	public string SourceValues { get; set; }

	/// <summary>The file it was last imported from (name only).</summary>
	public string SourceFile { get; set; }

	public DateTime? ImportedOn { get; set; }

	/// <summary>The assessment's structure (filled in by <see cref="SdeStore.GetExternalLinks"/>; not a column).</summary>
	[NotMapped] public Guid PropertyID { get; set; }
}

/// <summary>One assessment to save in <see cref="SdeStore.SaveImport"/>.</summary>
public sealed class ImportedAssessment
{
	/// <summary>The assessment and its structure. A new structure has PropertyID = Guid.Empty; several items may share one.</summary>
	public AssessmentFile File { get; init; }

	/// <summary>The link to write (its AssessmentID is filled in).</summary>
	public ExternalLinkRecord Link { get; init; }

	/// <summary>When set, the structure's square-footage shapes are replaced by one total ('c') shape of this size.</summary>
	public decimal? ShapeTotal { get; init; }
}

public sealed partial class SdeStore
{
	/// <summary>
	/// Saves imported assessments (new or updated) with their structures and links, all in one transaction: if one
	/// fails, nothing is saved. Each assessment is recalculated first. The import is one batch in edit history.
	/// Assessments that are Final or Letter sent aren't changed; their IDs are returned so the import can say so.
	/// </summary>
	public IReadOnlyList<Guid> SaveImport(IReadOnlyCollection<ImportedAssessment> items, SaveContext context = null)
	{
		AssessmentCalculator calc = Calculator;
		foreach (ImportedAssessment item in items)
		{
			calc.Recalculate(item.File);
		}
		SaveContext ctx = context ?? SaveContext.Batch(SaveVia.Import);
		DateTime now = Now();
		List<Guid> locked = new List<Guid>();
		Database.InTransaction((c, tx) =>
		{
			foreach (ImportedAssessment item in items)
			{
				Guid id = item.File.Assessment.AssessmentID;
				if (!ctx.AllowLocked && id != Guid.Empty && (GetStatus(c, tx, id).IsLocked || IsCheckedOut(c, tx, id)))
				{
					locked.Add(id);
					continue;
				}
				SaveTracked(c, tx, item.File, ctx,
					item.ShapeTotal is decimal total ? new[] { new ShapeRecord { ShapeType = "c", TotalSqFt = total } } : null);
				ExternalLinkRecord link = item.Link;
				link.AssessmentID = item.File.Assessment.AssessmentID;
				link.InspectionId ??= "";
				link.ImportedOn ??= now;
				// The same record may still be linked to an assessment in Recently deleted; the new one takes the link.
				c.Execute("""
					DELETE FROM ExternalLink WHERE Source = @Source AND RecordId = @RecordId AND InspectionId = @InspectionId
					AND AssessmentID <> @AssessmentID AND AssessmentID IN (SELECT AssessmentID FROM Assessment WHERE Active = 0)
					""", new { link.Source, link.RecordId, link.InspectionId, link.AssessmentID }, tx);
				TableMap<ExternalLinkRecord>.Upsert(c, link, tx);
			}
		});
		return locked;
	}

	public ExternalLinkRecord GetExternalLink(Guid assessmentId) =>
		Database.Read(c => TableMap<ExternalLinkRecord>.Get(c, new { AssessmentID = assessmentId }));

	/// <summary>
	/// Every link from a source (null = every source), with its structure, for active assessments of active structures
	/// (newest import first).
	/// </summary>
	public List<ExternalLinkRecord> GetExternalLinks(string source = null) =>
		Database.Read(c => c.Query<ExternalLinkRecord>("""
			SELECT x.AssessmentID, x.Source, x.RecordId, x.InspectionId, x.SourceValues, x.SourceFile, x.ImportedOn, a.PropertyID
			FROM ExternalLink x
			JOIN Assessment a ON a.AssessmentID = x.AssessmentID AND a.Active = 1
			JOIN Property p ON p.PropertyID = a.PropertyID AND p.Active = 1
			WHERE @source IS NULL OR x.Source = @source
			ORDER BY x.ImportedOn DESC
			""", new { source }).ToList());

	/// <summary>Active structures with this parcel number (exact, ignoring case and surrounding spaces).</summary>
	public List<PropertyRecord> FindPropertiesByParcel(string parcelNumber) =>
		string.IsNullOrWhiteSpace(parcelNumber)
			? new List<PropertyRecord>()
			: Database.Read(c => TableMap<PropertyRecord>.Select(c, "Active = 1 AND TRIM(ParcelNumber) = TRIM(@parcelNumber) COLLATE NOCASE", new { parcelNumber }).ToList());

	/// <summary>Structures that have a linked assessment (from any source).</summary>
	public HashSet<Guid> LinkedPropertyIds() =>
		Database.Read(c => c.Query<Guid>("SELECT DISTINCT a.PropertyID FROM ExternalLink x JOIN Assessment a ON a.AssessmentID = x.AssessmentID").ToHashSet());

	private static void DeleteLinks(IDbConnection c, IDbTransaction tx, Guid assessmentId) =>
		TableMap<ExternalLinkRecord>.Delete(c, "AssessmentID = @assessmentId", new { assessmentId }, tx);
}
