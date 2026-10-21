using Dapper;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// Links a structure to a record in another system: an assessor's parcel, a permit, a row of a building inventory.
/// Written by imports that create or update structures (no assessment). Mac-only: StructureLink isn't part of FEMA's
/// database and never reaches Export.json. Deleting the structure deletes its links.
/// </summary>
[Table("StructureLink")]
public sealed class StructureLinkRecord
{
	/// <summary>The import profile's source name, e.g. "County assessor".</summary>
	[Key] public string Source { get; set; }

	/// <summary>The record's ID in that source, e.g. a parcel number (matched ignoring case).</summary>
	[Key] public string RecordId { get; set; }

	public Guid PropertyID { get; set; }

	/// <summary>JSON object {field key: source text} as last imported, so a re-import only applies what changed.</summary>
	public string SourceValues { get; set; }

	/// <summary>The file it was last imported from (name only).</summary>
	public string SourceFile { get; set; }

	public DateTime? ImportedOn { get; set; }
}

/// <summary>One structure to save in <see cref="SdeStore.SaveStructureImport"/>.</summary>
public sealed class ImportedStructure
{
	/// <summary>The structure; a new one has PropertyID = Guid.Empty.</summary>
	public PropertyRecord Property { get; init; }

	/// <summary>The link to write (its PropertyID is filled in).</summary>
	public StructureLinkRecord Link { get; init; }

	/// <summary>When set, the structure's square-footage shapes are replaced by one total ('c') shape of this size.</summary>
	public decimal? ShapeTotal { get; init; }
}

public sealed partial class SdeStore
{
	/// <summary>
	/// Saves imported structures (new or updated) with their links, all in one transaction: if one fails, nothing is
	/// saved. Each structure goes through the tracked save, so the import is one batch in edit history. Structure
	/// fields aren't locked (a Final assessment keeps its own record of what it was based on in its history).
	/// </summary>
	public void SaveStructureImport(IReadOnlyCollection<ImportedStructure> items, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.Batch(SaveVia.Import);
		DateTime now = Now();
		Database.InTransaction((c, tx) =>
		{
			foreach (ImportedStructure item in items)
			{
				SavePropertyTracked(c, tx, item.Property, ctx);
				StructureLinkRecord link = item.Link;
				link.PropertyID = item.Property.PropertyID;
				link.ImportedOn ??= now;
				TableMap<StructureLinkRecord>.Upsert(c, link, tx);
				if (item.ShapeTotal is decimal total)
				{
					Guid propertyId = item.Property.PropertyID;
					TableMap<ShapeRecord>.Delete(c, "PropertyID = @propertyId", new { propertyId }, tx);
					TableMap<ShapeRecord>.Insert(c, new ShapeRecord { ShapeType = "c", PropertyID = propertyId, TotalSqFt = total }, tx);
				}
			}
		});
	}

	/// <summary>The link of a source's record (ignoring case), or null.</summary>
	public StructureLinkRecord GetStructureLink(string source, string recordId) =>
		Database.Read(c => TableMap<StructureLinkRecord>.Select(c, "Source = @source AND RecordId = @recordId", new { source, recordId }).FirstOrDefault());

	/// <summary>Every link from a source (null = every source) to an active structure, newest import first.</summary>
	public List<StructureLinkRecord> GetStructureLinks(string source = null) =>
		Database.Read(c => c.Query<StructureLinkRecord>("""
			SELECT l.Source, l.RecordId, l.PropertyID, l.SourceValues, l.SourceFile, l.ImportedOn
			FROM StructureLink l
			JOIN Property p ON p.PropertyID = l.PropertyID AND p.Active = 1
			WHERE @source IS NULL OR l.Source = @source
			ORDER BY l.ImportedOn DESC, l.RecordId
			""", new { source }).ToList());

	/// <summary>The links of one structure (it may be in several sources), newest import first.</summary>
	public List<StructureLinkRecord> GetStructureLinksFor(Guid propertyId) =>
		Database.Read(c => TableMap<StructureLinkRecord>.Select(c, "PropertyID = @propertyId", new { propertyId }, orderBy: "ImportedOn DESC").ToList());
}
