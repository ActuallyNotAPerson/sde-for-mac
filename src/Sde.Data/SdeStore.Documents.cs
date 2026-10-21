using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// A document kept with a structure: an elevation certificate, a permit (Mac-only Document table). The file is stored in
/// the "documents" folder beside the database under a new name, so the original can be moved or deleted.
/// </summary>
[Table("Document")]
public sealed class DocumentRecord
{
	[Key] public Guid DocumentID { get; set; }

	public Guid PropertyID { get; set; }

	public Guid? AssessmentID { get; set; }

	/// <summary>See <see cref="DocumentKinds"/>.</summary>
	public string Kind { get; set; }

	/// <summary>The stored name in the documents folder.</summary>
	public string FileName { get; set; }

	public string OriginalName { get; set; }

	public string Title { get; set; }

	/// <summary>JSON: values read from it (an elevation certificate's A5, C2a, ...), or null.</summary>
	public string Fields { get; set; }

	public DateTime? AddedOn { get; set; }

	public string AddedBy { get; set; }
}

public static class DocumentKinds
{
	public const string ElevationCertificate = "ElevationCertificate";
	public const string Permit = "Permit";
	public const string Other = "Other";
}

public sealed partial class SdeStore
{
	/// <summary>A structure's documents, newest first.</summary>
	public List<DocumentRecord> GetDocuments(Guid propertyId) =>
		Database.Read(c => TableMap<DocumentRecord>.Select(c, "PropertyID = @propertyId", new { propertyId }, orderBy: "AddedOn DESC").ToList());

	public DocumentRecord GetDocument(Guid documentId) => Database.Read(c => TableMap<DocumentRecord>.Get(c, new { DocumentID = documentId }));

	/// <summary>Where a document's file is.</summary>
	public string DocumentPath(DocumentRecord d) => Path.Combine(Database.DocumentsFolder, Path.GetFileName(d.FileName ?? ""));

	/// <summary>
	/// Copies a file into the documents folder under a new name and records it with a structure (and optionally one of
	/// its assessments). <paramref name="fieldsJson"/> holds values read from it.
	/// </summary>
	public DocumentRecord AddDocument(Guid propertyId, string sourcePath, string kind, string title = null, Guid? assessmentId = null,
		string fieldsJson = null, SaveContext context = null)
	{
		if (!File.Exists(sourcePath))
		{
			throw new FileNotFoundException("The document isn't there any more.", sourcePath);
		}
		SaveContext ctx = context ?? SaveContext.App;
		Directory.CreateDirectory(Database.DocumentsFolder);
		string name = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant();
		string stored = Path.Combine(Database.DocumentsFolder, name);
		File.Copy(sourcePath, stored);
		DocumentRecord d = new DocumentRecord
		{
			DocumentID = Guid.NewGuid(),
			PropertyID = propertyId,
			AssessmentID = assessmentId,
			Kind = kind ?? DocumentKinds.Other,
			FileName = name,
			OriginalName = Path.GetFileName(sourcePath),
			Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(sourcePath) : title.Trim(),
			Fields = fieldsJson,
			AddedOn = Clock(),
			AddedBy = ctx.User ?? UserName,
		};
		try
		{
			Database.InTransaction((c, tx) =>
			{
				TableMap<DocumentRecord>.Insert(c, d, tx);
				WriteEvent(c, tx, propertyId, assessmentId, "Document added: " + d.Title, ctx);
			});
		}
		catch
		{
			File.Delete(stored);
			throw;
		}
		return d;
	}

	/// <summary>Removes a document and its stored file.</summary>
	public void RemoveDocument(Guid documentId, SaveContext context = null)
	{
		DocumentRecord d = GetDocument(documentId);
		if (d == null)
		{
			return;
		}
		Database.InTransaction((c, tx) =>
		{
			TableMap<DocumentRecord>.Delete(c, "DocumentID = @documentId", new { documentId }, tx);
			WriteEvent(c, tx, d.PropertyID, d.AssessmentID, "Document removed: " + d.Title, context ?? SaveContext.App);
		});
		string path = DocumentPath(d);
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}
}
