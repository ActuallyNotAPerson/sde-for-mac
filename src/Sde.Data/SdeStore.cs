using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>One row of the structure list (the original's View/Search cards).</summary>
public sealed record StructureSummary(
	Guid PropertyID,
	Guid? AssessmentID,
	int? StructureTypeID,
	string Owner,
	string Address,
	string City,
	string NFIPCID,
	string NFIPCommunityName,
	DateTime? DateofInspection,
	string InspectorName,
	decimal? PercentDamage,
	string DamageMessage,
	DateTime? LastUpdate,
	string ExternalSource = null,
	string ExternalId = null,
	string StreetName = null,
	string ZipCode = null,
	string ParcelNumber = null,
	int? FIRMZone = null,
	string Status = null);

/// <summary>
/// Everything the app reads and writes. Replaces FEMA's 25 DAL services and the parts of the BLL that called them.
/// All SQL is parameterized; every multi-row save runs in one transaction.
/// </summary>
public sealed partial class SdeStore
{
	private readonly Lazy<ReferenceData> _reference;

	public SdeStore(SdeDatabase database)
	{
		Database = database;
		_reference = new Lazy<ReferenceData>(LoadReferenceData);
	}

	public SdeDatabase Database { get; }

	public string AttachmentsFolder => Database.AttachmentsFolder;

	public ReferenceData Reference => _reference.Value;

	public AssessmentCalculator Calculator => new AssessmentCalculator(Reference);

	private ReferenceData LoadReferenceData() => Database.Read(c => new ReferenceData(
		TableMap<LookupRecord>.Select(c).ToList(),
		TableMap<StateRecord>.Select(c, orderBy: "State").ToList(),
		TableMap<CountyRecord>.Select(c, orderBy: "County").ToList(),
		TableMap<StreetSuffixRecord>.Select(c).ToList(),
		TableMap<DepreciationValueRecord>.Select(c).ToList(),
		TableMap<CostAdjustmentResidenceTypeRecord>.Select(c).ToList(),
		TableMap<ValidationRuleRecord>.Select(c).ToList(),
		TableMap<LatLongValidationRecord>.Select(c).ToList(),
		TableMap<CustomFieldRecord>.Select(c, orderBy: "zOrder").ToList()));

	// ───────────────────────── Structures (properties) ─────────────────────────

	public PropertyRecord GetProperty(Guid id) => Database.Read(c => TableMap<PropertyRecord>.Get(c, new { PropertyID = id }));

	public List<PropertyRecord> GetProperties(bool activeOnly = true) =>
		Database.Read(c => TableMap<PropertyRecord>.Select(c, activeOnly ? "Active = 1" : null).ToList());

	/// <summary>
	/// Inserts or updates a property, writing edit history. New properties get an ID, Active = true and ImportDate = now.
	/// Structure fields aren't locked: a Final assessment keeps its own copy of what it was based on in its history.
	/// </summary>
	public PropertyRecord SaveProperty(PropertyRecord p, SaveContext context = null)
	{
		Database.InTransaction((c, tx) => SavePropertyTracked(c, tx, p, context ?? SaveContext.App));
		return p;
	}

	private static void SaveProperty(IDbConnection c, IDbTransaction tx, PropertyRecord p)
	{
		if (p.PropertyID == Guid.Empty)
		{
			p.PropertyID = Guid.NewGuid();
			p.Active = true;
			if (p.ImportDate == default)
			{
				p.ImportDate = DateTime.Now;
			}
		}
		p.LastUpdate = Now();
		TableMap<PropertyRecord>.Upsert(c, p, tx);
	}

	/// <summary>Saves many properties in one transaction (Enterprise Import, Lat/Long import), as one batch in edit history.</summary>
	public void SaveProperties(IEnumerable<PropertyRecord> properties, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.Batch(SaveVia.Import);
		Database.InTransaction((c, tx) =>
		{
			foreach (PropertyRecord p in properties)
			{
				SavePropertyTracked(c, tx, p, ctx);
			}
		});
	}

	/// <summary>
	/// Permanently deletes a property, its assessments and all their rows and photo files.
	/// Port of PropertyService.DeleteStructureData (decomp/DAL/DAL.Services/PropertyService.cs:60-105).
	/// </summary>
	public void DeleteProperty(Guid propertyId)
	{
		List<string> files = new();
		Database.InTransaction((c, tx) =>
		{
			foreach (Guid aid in c.Query<Guid>("SELECT AssessmentID FROM Assessment WHERE PropertyID = @propertyId", new { propertyId }, tx).ToList())
			{
				files.AddRange(DeleteAssessmentRows(c, tx, aid));
				BumpVersion(c, tx, aid, VersionKind.Assessment);
			}
			BumpVersion(c, tx, propertyId, VersionKind.Property);
			TableMap<ShapeRecord>.Delete(c, "PropertyID = @propertyId", new { propertyId }, tx);
			TableMap<CustomFieldResultRecord>.Delete(c, "ObjectIdentifier = @propertyId", new { propertyId }, tx);
			TableMap<RecentAssessmentRecord>.Delete(c, "PropertyID = @propertyId", new { propertyId }, tx);
			TableMap<PropertyRecord>.Delete(c, "PropertyID = @propertyId", new { propertyId }, tx);
		});
		DeleteFiles(files);
	}

	// ───────────────────────── Assessments ─────────────────────────

	/// <summary>
	/// Creates a new assessment for a property, as AssessmentService.CreateNewInspection + InspectionService.AddInspection did:
	/// Active, inspection date and cost-data date = today, value basis = Computed ACV (390), repair basis = Computed Damages (393).
	/// With <paramref name="applyDefaults"/>, the "Enter Default Data" values are filled in (PropertyService.CreateNewInspection).
	/// The new assessment is calculated and saved.
	/// </summary>
	public AssessmentFile CreateAssessment(Guid propertyId, bool applyDefaults)
	{
		PropertyRecord p = GetProperty(propertyId) ?? throw new ArgumentException("No property " + propertyId);
		AssessmentFile file = NewDraft(p, applyDefaults);
		SaveAssessment(file);
		return file;
	}

	/// <summary>
	/// A new assessment that isn't saved yet: for a new structure (<paramref name="property"/> with PropertyID
	/// Guid.Empty) or an existing one. The editor saves it on the first edit, so opening "New structure" and closing it
	/// again leaves nothing behind. With <paramref name="applyDefaults"/>, the "Enter Default Data" values are filled in.
	/// </summary>
	public AssessmentFile NewDraft(PropertyRecord property, bool applyDefaults)
	{
		DefaultValuesRecord defaults = applyDefaults ? GetDefaults() : null;
		if (property.PropertyID == Guid.Empty)
		{
			property.Active = true;
			if (defaults != null)
			{
				DefaultValues.ApplyToProperty(defaults, property);
			}
		}
		AssessmentFile file = AssessmentFile.CreateNew(property, defaults);
		Calculator.Recalculate(file);
		return file;
	}

	public AssessmentFile LoadAssessment(Guid assessmentId) => Database.Read(c => LoadAssessment(c, null, assessmentId));

	/// <summary>An assessment with the versions it was loaded at (in one read, so they belong together), or null.</summary>
	public (AssessmentFile File, VersionStamp Stamp) LoadAssessmentWithStamp(Guid assessmentId) => Database.Read(c =>
	{
		using IDbTransaction tx = c.BeginTransaction(deferred: true);
		AssessmentFile file = LoadAssessment(c, tx, assessmentId);
		return file == null ? (null, null) : (file, GetStamp(c, tx, assessmentId, file.Property.PropertyID));
	});

	internal static AssessmentFile LoadAssessment(IDbConnection c, IDbTransaction tx, Guid assessmentId)
	{
		AssessmentRecord a = TableMap<AssessmentRecord>.Get(c, new { AssessmentID = assessmentId }, tx);
		if (a == null)
		{
			return null;
		}
		object args = new { id = assessmentId };
		return new AssessmentFile
		{
			Assessment = a,
			Property = TableMap<PropertyRecord>.Get(c, new { a.PropertyID }, tx),
			CostAdjustments = TableMap<CostAdjustmentResultRecord>.Select(c, "AssessmentID = @id", args, tx).ToList(),
			AdditionalAdjustments = TableMap<AdditionalAdjustmentRecord>.Select(c, "AssessmentID = @id", args, tx, orderBy: "zOrder").ToList(),
			ElementPercentages = TableMap<ElementPercentageRecord>.Select(c, "AssessmentID = @id", args, tx).ToList(),
			Attachments = TableMap<AttachmentRecord>.Select(c, "AssessmentID = @id AND Active = 1", args, tx, orderBy: "AttachmentID").ToList(),
			CustomFields = TableMap<CustomFieldResultRecord>.Select(c, "ObjectIdentifier = @id", args, tx, orderBy: "zOrder").ToList(),
		};
	}

	/// <summary>Loads several assessments (skipping IDs that don't exist), in the order given, over one connection.</summary>
	public List<AssessmentFile> LoadAssessments(IEnumerable<Guid> assessmentIds) =>
		Database.Read(c => assessmentIds.Select(id => LoadAssessment(c, null, id)).Where(f => f != null).ToList());

	/// <summary>All active assessments of active structures.</summary>
	public List<AssessmentFile> LoadAllAssessments() => Database.Read(c => c.Query<Guid>(
			"SELECT a.AssessmentID FROM Assessment a JOIN Property p ON p.PropertyID = a.PropertyID WHERE a.Active = 1 AND p.Active = 1 ORDER BY a.DateofInspection DESC")
		.ToList().Select(id => LoadAssessment(c, null, id)).Where(f => f != null).ToList());

	public List<AssessmentRecord> GetAssessmentsForProperty(Guid propertyId) =>
		Database.Read(c => TableMap<AssessmentRecord>.Select(c, "PropertyID = @propertyId AND Active = 1", new { propertyId }, orderBy: "DateofInspection DESC").ToList());

	/// <summary>
	/// Recalculates and saves an assessment and its property in one transaction. Child rows are replaced
	/// (the original deleted and re-inserted them per tab). Also updates the recent-assessments list, writes edit
	/// history and refuses to change a locked assessment (see <see cref="SaveTracked"/>). Nothing is written when
	/// nothing changed. With <paramref name="shapes"/>, the structure's square-footage shapes are replaced in the same
	/// transaction. Returns the versions after the save, which a writer that keeps the assessment open expects next time.
	/// </summary>
	public VersionStamp SaveAssessment(AssessmentFile file, SaveContext context = null, IReadOnlyList<ShapeRecord> shapes = null)
	{
		Calculator.Recalculate(file);
		return Database.InTransaction((c, tx) => SaveTracked(c, tx, file, context ?? SaveContext.App, shapes));
	}

	/// <summary>
	/// Loads one assessment, changes it and saves it, all in one transaction, so nothing another program saves can
	/// slip in between (the phone form, the AI assistant server, the map's spatial join). The change runs before the
	/// assessment is recalculated; locks, history and versions are as for any save. Returns the saved assessment.
	/// </summary>
	public AssessmentFile Apply(Guid assessmentId, Action<AssessmentFile> change, SaveContext context = null)
	{
		AssessmentCalculator calc = Calculator;
		return Database.InTransaction((c, tx) =>
		{
			AssessmentFile file = LoadAssessment(c, tx, assessmentId) ?? throw new ArgumentException("That assessment no longer exists.");
			change(file);
			calc.Recalculate(file);
			SaveTracked(c, tx, file, context ?? SaveContext.App);
			return file;
		});
	}

	/// <summary>Writes an assessment's rows: the structure row only when it changed, then the assessment and its child rows.</summary>
	internal static void SaveAssessment(IDbConnection c, IDbTransaction tx, AssessmentFile file, bool writeProperty)
	{
		AssessmentRecord a = file.Assessment;
		if (writeProperty || file.Property.PropertyID == Guid.Empty)
		{
			SaveProperty(c, tx, file.Property);
		}
		a.PropertyID = file.Property.PropertyID;
		a.LastUpdate = Now();
		TableMap<AssessmentRecord>.Upsert(c, a, tx);
		object args = new { id = a.AssessmentID };
		TableMap<CostAdjustmentResultRecord>.Delete(c, "AssessmentID = @id", args, tx);
		TableMap<AdditionalAdjustmentRecord>.Delete(c, "AssessmentID = @id", args, tx);
		TableMap<ElementPercentageRecord>.Delete(c, "AssessmentID = @id", args, tx);
		TableMap<CustomFieldResultRecord>.Delete(c, "ObjectIdentifier = @id", args, tx);
		foreach (CostAdjustmentResultRecord r in file.CostAdjustments)
		{
			r.AssessmentID = a.AssessmentID;
			TableMap<CostAdjustmentResultRecord>.Insert(c, r, tx);
		}
		foreach (AdditionalAdjustmentRecord r in file.AdditionalAdjustments)
		{
			r.AssessmentID = a.AssessmentID;
			TableMap<AdditionalAdjustmentRecord>.Insert(c, r, tx);
		}
		foreach (ElementPercentageRecord r in file.ElementPercentages)
		{
			r.AssessmentID = a.AssessmentID;
			TableMap<ElementPercentageRecord>.Insert(c, r, tx);
		}
		foreach (CustomFieldResultRecord r in file.CustomFields)
		{
			r.ObjectIdentifier = a.AssessmentID;
			r.ResultType = "assessment";
			TableMap<CustomFieldResultRecord>.Insert(c, r, tx);
		}
		RenumberPhotosInOrder(c, tx, file);
		foreach (AttachmentRecord r in file.Attachments)
		{
			r.AssessmentID = a.AssessmentID;
			TableMap<AttachmentRecord>.Upsert(c, r, tx);
		}
		// Attachments no longer in the file are removed from the table (the caller deletes the photo files). Photos the
		// editor removed stay in the file with Active = 0 (Recently deleted); inactive rows are never removed here.
		List<int> keep = file.Attachments.Select(x => x.AttachmentID).ToList();
		TableMap<AttachmentRecord>.Delete(c, "AssessmentID = @id AND Active = 1" + (keep.Count > 0 ? " AND AttachmentID NOT IN @keep" : ""), new { id = a.AssessmentID, keep }, tx);
		RememberRecent(c, tx, file);
	}

	/// <summary>Permanently deletes one assessment, its rows and its photo files.</summary>
	public void DeleteAssessment(Guid assessmentId)
	{
		List<string> files = Database.InTransaction((c, tx) =>
		{
			List<string> removed = DeleteAssessmentRows(c, tx, assessmentId);
			BumpVersion(c, tx, assessmentId, VersionKind.Assessment);
			return removed;
		});
		DeleteFiles(files);
	}

	private static List<string> DeleteAssessmentRows(IDbConnection c, IDbTransaction tx, Guid id)
	{
		object args = new { id };
		List<string> files = c.Query<string>("SELECT FileName FROM Attachment WHERE AssessmentID = @id", args, tx).Where(f => !string.IsNullOrEmpty(f)).ToList();
		TableMap<AttachmentRecord>.Delete(c, "AssessmentID = @id", args, tx);
		TableMap<CostAdjustmentResultRecord>.Delete(c, "AssessmentID = @id", args, tx);
		TableMap<AdditionalAdjustmentRecord>.Delete(c, "AssessmentID = @id", args, tx);
		TableMap<ElementPercentageRecord>.Delete(c, "AssessmentID = @id", args, tx);
		TableMap<CustomFieldResultRecord>.Delete(c, "ObjectIdentifier = @id", args, tx);
		TableMap<RecentAssessmentRecord>.Delete(c, "AssessmentID = @id", args, tx);
		DeleteLinks(c, tx, id);   // also ON DELETE CASCADE, so versions that don't know the table can still delete
		TableMap<AssessmentRecord>.Delete(c, "AssessmentID = @id", args, tx);
		return files;
	}

	private void DeleteFiles(IEnumerable<string> fileNames)
	{
		foreach (string f in fileNames)
		{
			string path = Path.Combine(AttachmentsFolder, Path.GetFileName(f));
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}

	// ───────────────────────── Listing and search ─────────────────────────

	/// <summary>
	/// One row per active assessment, plus one per active property with no assessment
	/// (the original's View/Search, decomp/BLL/BLL.Service/SearchViewService.cs). The external source and ID are the
	/// record the assessment was imported from, otherwise the record the structure was last imported from.
	/// </summary>
	public List<StructureSummary> ListStructures()
	{
		const string sql = """
			SELECT p.PropertyID, a.AssessmentID, p.StructureTypeID,
			       TRIM(COALESCE(p.OwnerFirstName, '') || ' ' || COALESCE(p.OwnerLastName, '')) AS Owner,
			       TRIM(COALESCE(p.StreetNumber, '') || ' ' || COALESCE(p.StreetName, '') || ' ' || COALESCE(s.LookupFieldName, '')) AS Address,
			       p.City, p.NFIPCID, p.NFIPCommunityName, a.DateofInspection, a.InspectorName,
			       a.PercentDamage, a.DamageMessage, COALESCE(a.LastUpdate, p.LastUpdate) AS LastUpdate,
			       COALESCE(x.Source, (SELECT l.Source FROM StructureLink l WHERE l.PropertyID = p.PropertyID ORDER BY l.ImportedOn DESC LIMIT 1)) AS ExternalSource,
			       COALESCE(x.RecordId, (SELECT l.RecordId FROM StructureLink l WHERE l.PropertyID = p.PropertyID ORDER BY l.ImportedOn DESC LIMIT 1)) AS ExternalId,
			       p.StreetName, p.ZipCode, p.ParcelNumber, p.FIRMZone, st.Status
			FROM Property p
			LEFT JOIN Assessment a ON a.PropertyID = p.PropertyID AND a.Active = 1
			LEFT JOIN Lookup s ON s.LookupID = p.StreetSuffixID
			LEFT JOIN ExternalLink x ON x.AssessmentID = a.AssessmentID
			LEFT JOIN AssessmentStatus st ON st.AssessmentID = a.AssessmentID
			WHERE p.Active = 1
			ORDER BY COALESCE(a.LastUpdate, p.LastUpdate) DESC
			""";
		return Database.Read(c => c.Query<SummaryRow>(sql).Select(r => new StructureSummary(
			r.PropertyID, r.AssessmentID, r.StructureTypeID, r.Owner, r.Address, r.City, r.NFIPCID, r.NFIPCommunityName,
			r.DateofInspection, r.InspectorName, r.PercentDamage is decimal pd ? JetScaling.WithScale(pd, 1) : null,
			r.DamageMessage, r.LastUpdate, r.ExternalSource, r.ExternalId, r.StreetName, r.ZipCode, r.ParcelNumber, r.FIRMZone, r.Status)).ToList());
	}

	private sealed class SummaryRow
	{
		public Guid PropertyID { get; set; }
		public Guid? AssessmentID { get; set; }
		public int? StructureTypeID { get; set; }
		public string Owner { get; set; }
		public string Address { get; set; }
		public string City { get; set; }
		public string NFIPCID { get; set; }
		public string NFIPCommunityName { get; set; }
		public DateTime? DateofInspection { get; set; }
		public string InspectorName { get; set; }
		public decimal? PercentDamage { get; set; }
		public string DamageMessage { get; set; }
		public DateTime? LastUpdate { get; set; }
		public string ExternalSource { get; set; }
		public string ExternalId { get; set; }
		public string StreetName { get; set; }
		public string ZipCode { get; set; }
		public string ParcelNumber { get; set; }
		public int? FIRMZone { get; set; }
		public string Status { get; set; }
	}

	public List<RecentAssessmentRecord> RecentAssessments(int count = 5) =>
		Database.Read(c => TableMap<RecentAssessmentRecord>.Select(c, orderBy: "LastUpdate DESC").Take(count).ToList());

	private static void RememberRecent(IDbConnection c, IDbTransaction tx, AssessmentFile f)
	{
		PropertyRecord p = f.Property;
		RecentAssessmentRecord r = new RecentAssessmentRecord
		{
			AssessmentID = f.Assessment.AssessmentID,
			PropertyID = p.PropertyID,
			StreetAddress = $"{p.StreetNumber} {p.StreetName}".Trim(),
			StructureType = p.StructureTypeID == 1 ? "Residential" : "Non-Residential",
			LastUpdate = Now(),
		};
		TableMap<RecentAssessmentRecord>.Upsert(c, r, tx);
	}

	// ───────────────────────── Square footage shapes ─────────────────────────

	public List<ShapeRecord> GetShapes(Guid propertyId) =>
		Database.Read(c => TableMap<ShapeRecord>.Select(c, "PropertyID = @propertyId", new { propertyId }, orderBy: "ShapeID").ToList());

	/// <summary>
	/// Replaces a property's shapes and sets its SquareFootage to their total (ShapeViewService.UpdateTotalSquareFootage).
	/// Nothing is written when the shapes and total are already the same.
	/// </summary>
	public decimal SaveShapes(Guid propertyId, IEnumerable<ShapeRecord> shapes)
	{
		List<ShapeRecord> list = shapes.ToList();
		decimal total = list.Sum(s => s.TotalSqFt ?? 0m);
		Database.InTransaction((c, tx) =>
		{
			decimal? stored = c.ExecuteScalar<decimal?>("SELECT SquareFootage FROM Property WHERE PropertyID = @propertyId", new { propertyId }, tx);
			if (SameShapes(c, tx, propertyId, list) && stored == total)
			{
				return;
			}
			ReplaceShapes(c, tx, propertyId, list);
			c.Execute("UPDATE Property SET SquareFootage = @total, LastUpdate = @now WHERE PropertyID = @propertyId", new { total, now = Now(), propertyId }, tx);
			BumpVersion(c, tx, propertyId, VersionKind.Property);
		});
		return total;
	}

	/// <summary>True when a structure's stored shapes are these (their IDs aside).</summary>
	private static bool SameShapes(IDbConnection c, IDbTransaction tx, Guid propertyId, IReadOnlyList<ShapeRecord> shapes)
	{
		List<ShapeRecord> stored = TableMap<ShapeRecord>.Select(c, "PropertyID = @propertyId", new { propertyId }, tx, orderBy: "ShapeID").ToList();
		return SameRows(stored, shapes, nameof(ShapeRecord.ShapeID), nameof(ShapeRecord.PropertyID));
	}

	private static void ReplaceShapes(IDbConnection c, IDbTransaction tx, Guid propertyId, IEnumerable<ShapeRecord> shapes)
	{
		TableMap<ShapeRecord>.Delete(c, "PropertyID = @propertyId", new { propertyId }, tx);
		foreach (ShapeRecord s in shapes)
		{
			s.ShapeID = null;
			s.PropertyID = propertyId;
			TableMap<ShapeRecord>.Insert(c, s, tx);
		}
	}

	// ───────────────────────── Photos ─────────────────────────

	/// <summary>Copies a photo into the attachments folder under a new GUID name (as ucFilePhoto did) and returns the record (not yet saved).</summary>
	public AttachmentRecord ImportPhoto(Guid assessmentId, string sourceFile, string description = null)
	{
		Directory.CreateDirectory(AttachmentsFolder);
		string name = Guid.NewGuid() + Path.GetExtension(sourceFile).ToLowerInvariant();
		File.Copy(sourceFile, Path.Combine(AttachmentsFolder, name));
		return new AttachmentRecord { AssessmentID = assessmentId, FileName = name, Description = description, Active = true, AttachmentType = "Photo" };
	}

	public string PhotoPath(AttachmentRecord a) => Path.Combine(AttachmentsFolder, Path.GetFileName(a.FileName ?? ""));

	// ───────────────────────── Settings ─────────────────────────

	public DefaultValuesRecord GetDefaults() => Database.Read(c => TableMap<DefaultValuesRecord>.Select(c, orderBy: "DefaultValueID").FirstOrDefault());

	public void SaveDefaults(DefaultValuesRecord d) =>
		Database.InTransaction((c, tx) =>
		{
			TableMap<DefaultValuesRecord>.Delete(c, "DefaultValueID <> @id", new { id = d.DefaultValueID }, tx);
			TableMap<DefaultValuesRecord>.Upsert(c, d, tx);
		});

	public UserPreferencesRecord GetPreferences() =>
		Database.Read(c => TableMap<UserPreferencesRecord>.Select(c, orderBy: "UserDefaultID").FirstOrDefault()) ?? new UserPreferencesRecord { UserDefaultID = 1 };

	public void SavePreferences(UserPreferencesRecord p) => Database.InTransaction((c, tx) => TableMap<UserPreferencesRecord>.Upsert(c, p, tx));

	public void RenameCustomField(Guid customFieldId, string name)
	{
		Database.InTransaction((c, tx) => c.Execute("UPDATE CustomField SET CustomField = @name, DateOfLastMod = @now WHERE CustomFieldID = @customFieldId",
			new { name, now = Now(), customFieldId }, tx));
		if (_reference.IsValueCreated)
		{
			CustomFieldRecord f = Reference.CustomFields.FirstOrDefault(x => x.CustomFieldID == customFieldId);
			if (f != null)
			{
				f.CustomField = name;
			}
		}
	}

	public List<CustomFieldResultRecord> GetCustomFieldResults(Guid objectId) =>
		Database.Read(c => TableMap<CustomFieldResultRecord>.Select(c, "ObjectIdentifier = @objectId", new { objectId }, orderBy: "zOrder").ToList());

	public void SaveCustomFieldResults(Guid objectId, string resultType, IEnumerable<CustomFieldResultRecord> results) =>
		Database.InTransaction((c, tx) =>
		{
			TableMap<CustomFieldResultRecord>.Delete(c, "ObjectIdentifier = @objectId", new { objectId }, tx);
			foreach (CustomFieldResultRecord r in results)
			{
				r.ObjectIdentifier = objectId;
				r.ResultType = resultType;
				TableMap<CustomFieldResultRecord>.Insert(c, r, tx);
			}
			BumpVersion(c, tx, objectId, string.Equals(resultType, "property", StringComparison.OrdinalIgnoreCase) ? VersionKind.Property : VersionKind.Assessment);
		});

	public List<NoteRecord> GetNotes() => Database.Read(c => TableMap<NoteRecord>.Select(c, "Active = 1", orderBy: "NoteName").ToList());

	public void SaveNote(NoteRecord n)
	{
		if (n.NoteID == Guid.Empty)
		{
			n.NoteID = Guid.NewGuid();
			n.Active = true;
		}
		Database.InTransaction((c, tx) => TableMap<NoteRecord>.Upsert(c, n, tx));
	}

	public void DeleteNote(Guid noteId) => Database.InTransaction((c, tx) => TableMap<NoteRecord>.Delete(c, "NoteID = @noteId", new { noteId }, tx));

	/// <summary>Saved Enterprise Import column mappings (EnterpriseImportSettings table).</summary>
	public List<EnterpriseImportSettingsRecord> GetEnterpriseMappings() =>
		Database.Read(c => TableMap<EnterpriseImportSettingsRecord>.Select(c, orderBy: "EIS_ID").ToList());

	/// <summary>Inserts (EIS_ID 0) or updates a saved mapping; EIS_ID is filled in on insert.</summary>
	public void SaveEnterpriseMapping(EnterpriseImportSettingsRecord mapping) =>
		Database.InTransaction((c, tx) => TableMap<EnterpriseImportSettingsRecord>.Upsert(c, mapping, tx));

	public void DeleteEnterpriseMapping(int id) =>
		Database.InTransaction((c, tx) => TableMap<EnterpriseImportSettingsRecord>.Delete(c, "EIS_ID = @id", new { id }, tx));

	public (int Properties, int Assessments) Counts() => Database.Read(c => (
		c.ExecuteScalar<int>("SELECT COUNT(*) FROM Property WHERE Active = 1"),
		c.ExecuteScalar<int>("SELECT COUNT(*) FROM Assessment a JOIN Property p ON p.PropertyID = a.PropertyID WHERE a.Active = 1 AND p.Active = 1")));

	/// <summary>Local time without fractional seconds (Jet DateTime precision).</summary>
	internal static DateTime Now()
	{
		DateTime n = DateTime.Now;
		return new DateTime(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, DateTimeKind.Unspecified);
	}
}
