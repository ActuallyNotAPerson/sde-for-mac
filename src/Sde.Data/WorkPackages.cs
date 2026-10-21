using System.Data;
using System.IO.Compression;
using Dapper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>What a work package holds and where it's going (manifest.json in the package, and a setting in its database).</summary>
public sealed class WorkPackageManifest
{
	/// <summary>The package layout's version.</summary>
	public int Format { get; set; } = 1;

	public Guid PackageId { get; set; }

	/// <summary><see cref="WorkPackages.Out"/> (office to field) or <see cref="WorkPackages.Back"/> (field to office).</summary>
	public string Direction { get; set; }

	/// <summary>The office database it was checked out of (see <see cref="SdeDatabase.DatabaseId"/>).</summary>
	public Guid SourceDatabaseId { get; set; }

	public string SourceDatabaseName { get; set; }

	public DateTime CreatedOn { get; set; }

	public string CreatedBy { get; set; }

	public string CheckedOutTo { get; set; }

	/// <summary>The edit history batch that built the package (it isn't sent back).</summary>
	public string CreationBatchId { get; set; }

	public List<WorkPackageItem> Items { get; set; } = new();
}

/// <summary>One assessment in a work package, with the versions it left the office at.</summary>
public sealed class WorkPackageItem
{
	public Guid AssessmentID { get; set; }

	public Guid PropertyID { get; set; }

	public string Address { get; set; }

	public long Version { get; set; }

	public long PropertyVersion { get; set; }
}

/// <summary>What a check-in did.</summary>
public sealed class CheckInResult
{
	/// <summary>Assessments brought back from the package.</summary>
	public int CheckedIn { get; set; }

	/// <summary>Assessments the field added (new structures or new visits).</summary>
	public int Added { get; set; }

	/// <summary>Why some assessments weren't brought back.</summary>
	public List<string> Refused { get; } = new();

	public List<string> Warnings { get; } = new();

	/// <summary>The edit history batch of the check-in.</summary>
	public string BatchId { get; set; }
}

/// <summary>
/// Work packages: taking assessments to another Mac (in the field, with no connection) and back. Checking out marks the
/// assessments read-only here and writes a ".sdework" file (a zip holding a database with just those assessments and
/// their structures, their photos and documents, and a manifest of their versions). The field Mac opens the package as
/// a database, works in it, and sends it back; checking in replaces the office copies (if nobody changed them here
/// meanwhile), adds the field's edit history with its original people, times and sources, and releases them. A package
/// can be checked in once. Version 1 is an exclusive check-out: there's no automatic merge of changes made on both sides.
/// </summary>
public static class WorkPackages
{
	public const string Extension = ".sdework";

	public const string Out = "out";

	public const string Back = "back";

	/// <summary>The setting that marks a database as an opened work package (it holds the manifest).</summary>
	public const string ManifestKey = "WorkPackage";

	private const string ManifestFile = "manifest.json";

	private const string DatabaseFile = "sde.sqlite";

	/// <summary>Mac-only tables that travel with an assessment, keyed by its AssessmentID.</summary>
	private static readonly string[] AssessmentTables = { "AssessmentStatus", "DamageSuggestion", "PhotoInfo", "ReviewComment", "EventAssessment", "ExternalLink" };

	// ───────────── At the office ─────────────

	/// <summary>
	/// Checks assessments out into a package file. Refused for Final or Letter sent assessments and ones already checked
	/// out. Returns the manifest.
	/// </summary>
	public static WorkPackageManifest CheckOut(SdeStore office, IReadOnlyCollection<Guid> assessmentIds, string packagePath, string checkedOutTo, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.App;
		List<AssessmentFile> files = office.LoadAssessments(assessmentIds.Distinct()).Where(f => f.Assessment.Active && f.Property.Active).ToList();
		if (files.Count == 0)
		{
			throw new ArgumentException("Choose at least one assessment to check out.");
		}
		Dictionary<Guid, AssessmentStatusRecord> statuses = office.GetStatuses();
		HashSet<Guid> alreadyOut = office.GetCheckOuts().Select(r => r.AssessmentID).ToHashSet();
		foreach (AssessmentFile f in files)
		{
			if (statuses.TryGetValue(f.Assessment.AssessmentID, out AssessmentStatusRecord s) && s.IsLocked)
			{
				throw new InvalidOperationException($"{Address(f)} is {AssessmentStatuses.Label(s.Value)}, so it can't go out for field work.");
			}
			if (alreadyOut.Contains(f.Assessment.AssessmentID))
			{
				throw new InvalidOperationException($"{Address(f)} is already checked out.");
			}
		}
		WorkPackageManifest manifest = new WorkPackageManifest
		{
			PackageId = Guid.NewGuid(),
			Direction = Out,
			SourceDatabaseId = office.Database.DatabaseId,
			SourceDatabaseName = Path.GetFileName(office.Database.FilePath),
			CreatedOn = office.Clock(),
			CreatedBy = ctx.User ?? office.UserName,
			CheckedOutTo = string.IsNullOrWhiteSpace(checkedOutTo) ? null : checkedOutTo.Trim(),
		};
		foreach (AssessmentFile f in files)
		{
			VersionStamp stamp = office.GetStamp(f.Assessment.AssessmentID);
			manifest.Items.Add(new WorkPackageItem
			{
				AssessmentID = f.Assessment.AssessmentID,
				PropertyID = f.Property.PropertyID,
				Address = Address(f),
				Version = stamp.Assessment,
				PropertyVersion = stamp.Property,
			});
		}

		string staging = Staging();
		try
		{
			SdeStore package = new SdeStore(SdeDatabase.Create(Path.Combine(staging, DatabaseFile))) { UserName = office.UserName };
			ExchangeService source = new ExchangeService(office);
			SdeDataset data = source.ReadDataset(manifest.Items.Select(i => i.AssessmentID));
			SaveContext build = SaveContext.Batch(SaveVia.WorkPackage, "Checked out" + (manifest.CheckedOutTo == null ? "" : " to " + manifest.CheckedOutTo));
			ImportResult imported = new ExchangeService(package).Import(data, office.AttachmentsFolder, ExistingRecordAction.Overwrite, context: build);
			manifest.CreationBatchId = build.BatchId;
			CopyMacRows(office.Database, package.Database, manifest.Items.Select(i => i.AssessmentID).ToList());
			CopyFiles(package, office.Database.DocumentsFolder, package.Database.DocumentsFolder, "Document");
			package.SetSetting(ManifestKey, manifest);
			string clean = Path.Combine(staging, "out");
			Directory.CreateDirectory(clean);
			package.Database.Read(c => c.Execute("VACUUM INTO @path", new { path = Path.Combine(clean, DatabaseFile) }));
			CopyFolder(package.Database.AttachmentsFolder, Path.Combine(clean, "attachments"));
			CopyFolder(package.Database.DocumentsFolder, Path.Combine(clean, "documents"));
			File.WriteAllText(Path.Combine(clean, ManifestFile), JsonConvert.SerializeObject(manifest, Formatting.Indented));
			if (imported.Warnings.Count > 0)
			{
				File.WriteAllLines(Path.Combine(clean, "warnings.txt"), imported.Warnings);
			}
			Zip(clean, packagePath);
		}
		finally
		{
			TryDelete(staging);
		}

		// Recorded only now the package exists, and only if nothing changed while it was being made.
		office.Database.InTransaction((c, tx) =>
		{
			foreach (WorkPackageItem item in manifest.Items)
			{
				VersionStamp now = SdeStore.GetStamp(c, tx, item.AssessmentID, item.PropertyID);
				if (now.Assessment != item.Version || now.Property != item.PropertyVersion)
				{
					throw new InvalidOperationException($"{item.Address} changed while the package was being made. Make the package again.");
				}
				TableMap<CheckOutRecord>.Insert(c, new CheckOutRecord
				{
					AssessmentID = item.AssessmentID,
					PackageID = manifest.PackageId,
					CheckedOutOn = manifest.CreatedOn,
					CheckedOutBy = manifest.CreatedBy,
					CheckedOutTo = manifest.CheckedOutTo,
					Version = item.Version,
					PropertyVersion = item.PropertyVersion,
				}, tx);
				office.WriteEvent(c, tx, item.PropertyID, item.AssessmentID, "Checked out in a work package" + (manifest.CheckedOutTo == null ? "" : " to " + manifest.CheckedOutTo), ctx);
			}
		});
		return manifest;
	}

	/// <summary>
	/// Checks a package that came back from the field in: each assessment whose office copy nobody changed meanwhile
	/// replaces it (one batch of edit history, with the field's own rows kept as they were made), assessments the field
	/// added are added, and the check-outs are released. Assessments that were taken back, changed here, or already
	/// checked in are refused and listed.
	/// </summary>
	public static CheckInResult CheckIn(SdeStore office, string packagePath, SaveContext context = null)
	{
		string staging = Staging();
		try
		{
			ZipFile.ExtractToDirectory(packagePath, staging);
			WorkPackageManifest manifest = ReadManifestFile(staging);
			if (manifest.Direction != Back)
			{
				throw new InvalidOperationException("This package hasn't been sent back from the field yet. Open it on the field Mac, then use Send Back.");
			}
			if (manifest.SourceDatabaseId != office.Database.DatabaseId)
			{
				throw new InvalidOperationException($"This package belongs to another database ({manifest.SourceDatabaseName}). Open that database to check it in.");
			}
			SdeStore package = new SdeStore(SdeDatabase.Open(Path.Combine(staging, DatabaseFile)));
			CheckInResult result = new CheckInResult();
			Dictionary<Guid, CheckOutRecord> checkedOut = office.GetCheckOuts().Where(r => r.PackageID == manifest.PackageId).ToDictionary(r => r.AssessmentID);
			List<Guid> accepted = new List<Guid>();
			foreach (WorkPackageItem item in manifest.Items)
			{
				if (!checkedOut.TryGetValue(item.AssessmentID, out CheckOutRecord r))
				{
					result.Refused.Add($"{item.Address}: already checked in, or taken back early.");
					continue;
				}
				VersionStamp now = office.GetStamp(item.AssessmentID);
				if (now == null)
				{
					result.Refused.Add($"{item.Address}: deleted here while it was out.");
					continue;
				}
				if (now.Assessment != r.Version || now.Property != r.PropertyVersion)
				{
					result.Refused.Add($"{item.Address}: changed here while it was out. Take it back and copy the field's values over one by one.");
					continue;
				}
				accepted.Add(item.AssessmentID);
			}
			HashSet<Guid> inOffice = office.Database.Read(c => c.Query<Guid>("SELECT AssessmentID FROM Assessment").ToHashSet());
			HashSet<Guid> manifestIds = manifest.Items.Select(i => i.AssessmentID).ToHashSet();
			List<Guid> added = package.Database.Read(c => c.Query<Guid>("SELECT AssessmentID FROM Assessment WHERE Active = 1").ToList())
				.Where(id => !manifestIds.Contains(id) && !inOffice.Contains(id)).ToList();
			List<Guid> bringing = accepted.Concat(added).ToList();
			if (bringing.Count == 0)
			{
				return result;
			}

			SaveContext ctx = (context ?? SaveContext.Batch(SaveVia.WorkPackage, "Checked in from a work package")).AsBatch();
			ctx = new SaveContext { User = ctx.User, Via = SaveVia.WorkPackage, BatchId = ctx.BatchId, Event = ctx.Event ?? "Checked in from a work package" };
			result.BatchId = ctx.BatchId;
			SdeDataset data = new ExchangeService(package).ReadDataset(bringing);
			ImportResult imported = new ExchangeService(office).Import(data, package.AttachmentsFolder, ExistingRecordAction.Overwrite, context: ctx);
			result.Warnings.AddRange(imported.Warnings);
			CopyMacRows(package.Database, office.Database, bringing);
			CopyFiles(office, package.Database.DocumentsFolder, office.Database.DocumentsFolder, "Document");
			office.Database.InTransaction((c, tx) =>
			{
				// The field's own history replaces the rows the import wrote, so each change keeps who made it and when.
				// (IDs are passed as text: Dapper's list expansion doesn't use the GUID type handler.)
				c.Execute("DELETE FROM EditHistory WHERE BatchId = @BatchId AND Field IS NOT NULL", new { ctx.BatchId }, tx);
				foreach (EditHistoryRecord h in package.Database.Read(p => TableMap<EditHistoryRecord>.Select(p,
					"(BatchId IS NULL OR BatchId <> @creation) AND (AssessmentID IN @ids OR (AssessmentID IS NULL AND PropertyID IN (SELECT PropertyID FROM Assessment WHERE AssessmentID IN @ids)))",
					new { creation = manifest.CreationBatchId ?? "", ids = bringing.Select(g => g.ToString("D")).ToList() }, orderBy: "HistoryID").ToList()))
				{
					h.HistoryID = 0;
					h.BatchId = ctx.BatchId;
					TableMap<EditHistoryRecord>.Insert(c, h, tx);
				}
				foreach (Guid id in accepted)
				{
					TableMap<CheckOutRecord>.Delete(c, "AssessmentID = @id", new { id }, tx);
					office.BumpVersion(c, tx, id, VersionKind.Assessment);
				}
			});
			result.CheckedIn = accepted.Count;
			result.Added = added.Count;
			return result;
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			TryDelete(staging);
		}
	}

	/// <summary>
	/// The differences between the office copy of an assessment and the package's, field by field (OldValue: here,
	/// NewValue: the field's), for copying the field's values over after taking an assessment back early.
	/// </summary>
	public static List<FieldDiff> Compare(SdeStore office, string packagePath, Guid assessmentId)
	{
		AssessmentFile field = ReadAssessment(packagePath, assessmentId) ?? throw new ArgumentException("That assessment isn't in the package.");
		AssessmentFile here = office.LoadAssessment(assessmentId) ?? throw new ArgumentException("That assessment no longer exists here.");
		return AssessmentDiff.Compare(here, field, office.Reference);
	}

	/// <summary>Copies the chosen fields' values (AssessmentDiff keys) from the package's copy onto the office copy.</summary>
	public static AssessmentFile CopyFromPackage(SdeStore office, string packagePath, Guid assessmentId, IReadOnlyList<string> keys, SaveContext context = null)
	{
		AssessmentFile field = ReadAssessment(packagePath, assessmentId) ?? throw new ArgumentException("That assessment isn't in the package.");
		return office.Apply(assessmentId, f => AssessmentMerge.ApplyValues(f, field, keys, office.Reference, office.Calculator),
			context ?? SaveContext.Batch(SaveVia.WorkPackage, "Copied values from a work package"));
	}

	/// <summary>One assessment as the package holds it, or null.</summary>
	public static AssessmentFile ReadAssessment(string packagePath, Guid assessmentId)
	{
		string staging = Staging();
		try
		{
			ZipFile.ExtractToDirectory(packagePath, staging);
			return new SdeStore(SdeDatabase.Open(Path.Combine(staging, DatabaseFile))).LoadAssessment(assessmentId);
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			TryDelete(staging);
		}
	}

	// ───────────── In the field ─────────────

	/// <summary>
	/// Unpacks a package into <paramref name="workFolder"/> (one folder per package) and returns its database, which
	/// the app opens like any other. Opening the same package again reopens the work already done.
	/// </summary>
	public static string Open(string packagePath, string workFolder)
	{
		string staging = Staging();
		try
		{
			ZipFile.ExtractToDirectory(packagePath, staging);
			WorkPackageManifest manifest = ReadManifestFile(staging);
			if (manifest.Direction != Out)
			{
				throw new InvalidOperationException("This package is on its way back to the office. Check it in there.");
			}
			string folder = Path.Combine(workFolder, manifest.PackageId.ToString("N"));
			string database = Path.Combine(folder, DatabaseFile);
			if (!File.Exists(database))
			{
				Directory.CreateDirectory(workFolder);
				Directory.Move(staging, folder);
			}
			return database;
		}
		finally
		{
			TryDelete(staging);
		}
	}

	/// <summary>The manifest of an opened work package's database, or null for an ordinary database.</summary>
	public static WorkPackageManifest Manifest(SdeStore store) => store.GetSetting<WorkPackageManifest>(ManifestKey);

	/// <summary>Writes the field's work (an opened package's database) into a package to take back to the office.</summary>
	public static void SendBack(SdeStore package, string packagePath)
	{
		WorkPackageManifest manifest = Manifest(package) ?? throw new InvalidOperationException("This database isn't a work package.");
		manifest.Direction = Back;
		string staging = Staging();
		try
		{
			package.Database.Read(c => c.Execute("VACUUM INTO @path", new { path = Path.Combine(staging, DatabaseFile) }));
			CopyFolder(package.Database.AttachmentsFolder, Path.Combine(staging, "attachments"));
			CopyFolder(package.Database.DocumentsFolder, Path.Combine(staging, "documents"));
			File.WriteAllText(Path.Combine(staging, ManifestFile), JsonConvert.SerializeObject(manifest, Formatting.Indented));
			Zip(staging, packagePath);
		}
		finally
		{
			TryDelete(staging);
		}
	}

	// ───────────── Helpers ─────────────

	private static string Address(AssessmentFile f)
	{
		string street = string.Join(" ", new[] { f.Property.StreetNumber, f.Property.StreetName }.Where(s => !string.IsNullOrWhiteSpace(s)));
		return string.IsNullOrWhiteSpace(street) ? "A structure with no address" : street;
	}

	private static WorkPackageManifest ReadManifestFile(string folder)
	{
		string path = Path.Combine(folder, ManifestFile);
		if (!File.Exists(path) || !File.Exists(Path.Combine(folder, DatabaseFile)))
		{
			throw new InvalidOperationException("That file isn't an SDE work package.");
		}
		WorkPackageManifest manifest = JsonConvert.DeserializeObject<WorkPackageManifest>(File.ReadAllText(path));
		if (manifest == null || manifest.Format > 1)
		{
			throw new InvalidOperationException("This work package was made by a newer version of SDE. Update SDE to open it.");
		}
		return manifest;
	}

	/// <summary>
	/// Replaces the Mac-only rows of these assessments (status, suggestions, photo details and markup, comments, event,
	/// links, documents) in <paramref name="to"/> with those in <paramref name="from"/>. Columns are matched by name, so
	/// packages made by another version of the app still work.
	/// </summary>
	private static void CopyMacRows(SdeDatabase from, SdeDatabase to, IReadOnlyList<Guid> assessmentIds)
	{
		to.InTransactionWithAttached(from.FilePath, "src", (c, tx) =>
		{
			c.Execute("CREATE TEMP TABLE IF NOT EXISTS moving (id TEXT PRIMARY KEY); DELETE FROM moving;", transaction: tx);
			foreach (Guid id in assessmentIds)
			{
				c.Execute("INSERT OR IGNORE INTO moving (id) VALUES (@id)", new { id }, tx);
			}
			// Events first: memberships refer to them.
			string eventColumns = SharedColumns(c, tx, "Event");
			c.Execute($"INSERT OR IGNORE INTO main.Event ({eventColumns}) SELECT {eventColumns} FROM src.Event WHERE EventID IN (SELECT EventID FROM src.EventAssessment WHERE AssessmentID IN (SELECT id FROM moving))", transaction: tx);
			foreach (string table in AssessmentTables)
			{
				string columns = SharedColumns(c, tx, table);
				c.Execute($"DELETE FROM main.{table} WHERE AssessmentID IN (SELECT id FROM moving)", transaction: tx);
				c.Execute($"INSERT INTO main.{table} ({columns}) SELECT {columns} FROM src.{table} WHERE AssessmentID IN (SELECT id FROM moving) ORDER BY rowid", transaction: tx);
			}
			// Documents belong to the structure (an elevation certificate), so they travel with its assessments.
			string documentColumns = SharedColumns(c, tx, "Document");
			const string properties = "SELECT PropertyID FROM main.Assessment WHERE AssessmentID IN (SELECT id FROM moving)";
			c.Execute($"DELETE FROM main.Document WHERE PropertyID IN ({properties})", transaction: tx);
			c.Execute($"INSERT INTO main.Document ({documentColumns}) SELECT {documentColumns} FROM src.Document WHERE PropertyID IN ({properties})", transaction: tx);
			// And the markup of the photos that moved.
			string markupColumns = SharedColumns(c, tx, "PhotoMarkup");
			c.Execute($"INSERT OR REPLACE INTO main.PhotoMarkup ({markupColumns}) SELECT {markupColumns} FROM src.PhotoMarkup WHERE FileName IN (SELECT FileName FROM main.Attachment WHERE AssessmentID IN (SELECT id FROM moving))", transaction: tx);
		});
	}

	/// <summary>The columns a table has in both the main and the attached database, quoted and comma-separated.</summary>
	private static string SharedColumns(IDbConnection c, IDbTransaction tx, string table)
	{
		HashSet<string> main = c.Query<string>($"SELECT name FROM pragma_table_info('{table}', 'main')", transaction: tx).ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<string> both = c.Query<string>($"SELECT name FROM pragma_table_info('{table}', 'src')", transaction: tx).Where(main.Contains).ToList();
		return string.Join(", ", both.Select(n => "\"" + n + "\""));
	}

	/// <summary>Copies the stored files a table names (FileName column) from one folder to another, when missing.</summary>
	private static void CopyFiles(SdeStore target, string fromFolder, string toFolder, string table)
	{
		List<string> names = target.Database.Read(c => c.Query<string>($"SELECT FileName FROM {table} WHERE FileName IS NOT NULL").ToList());
		Directory.CreateDirectory(toFolder);
		foreach (string name in names.Select(Path.GetFileName))
		{
			string from = Path.Combine(fromFolder, name);
			string to = Path.Combine(toFolder, name);
			if (File.Exists(from) && !File.Exists(to))
			{
				File.Copy(from, to);
			}
		}
	}

	private static void CopyFolder(string from, string to)
	{
		Directory.CreateDirectory(to);
		if (Directory.Exists(from))
		{
			foreach (string f in Directory.GetFiles(from))
			{
				File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
			}
		}
	}

	private static void Zip(string folder, string packagePath)
	{
		string temp = packagePath + ".partial";
		if (File.Exists(temp))
		{
			File.Delete(temp);
		}
		ZipFile.CreateFromDirectory(folder, temp, CompressionLevel.Optimal, includeBaseDirectory: false);
		File.Move(temp, packagePath, overwrite: true);
	}

	private static string Staging()
	{
		string path = Path.Combine(Path.GetTempPath(), "sde-work-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static void TryDelete(string folder)
	{
		try
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
