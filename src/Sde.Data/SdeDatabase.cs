using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// An SDE database file (SQLite) plus its attachments folder. Replaces FEMA's SDEDatabase.mdb, which lived
/// next to the .exe; here it lives wherever the app puts it (default: ~/Library/Application Support/SDE).
/// </summary>
public sealed class SdeDatabase
{
	public const int SchemaVersion = 1;

	/// <summary>
	/// The version of the Mac-only tables (2 = v2.0, 3 = v2.5). Kept apart from <see cref="SchemaVersion"/>, which an
	/// older version of this app checks before opening a file: adding Mac-only tables never stops an older version from
	/// opening it.
	/// </summary>
	public const int MacSchemaVersion = 3;

	/// <summary>The per-database setting holding <see cref="DatabaseId"/>.</summary>
	public const string DatabaseIdKey = "DatabaseId";

	/// <summary>A hash of Schema.sql. When a database's stored hash differs, Schema.sql runs again on opening it.</summary>
	public static readonly string SchemaHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReadResource("Schema.sql")))).ToLowerInvariant();

	private long _commits;

	private long _connections;

	/// <summary>Reference tables copied from FEMA's SDEDatabase.mdb into every new database (src/Sde.Data/Seed).</summary>
	public static readonly string[] SeedTables =
	{
		"LookupType", "Lookup", "State", "County", "StreetSuffixAbbreviation", "CostAdjustment_ResidenceType",
		"DepreciationValue", "ValidationRule", "LatLongValidation", "InspectionStatus", "ResidenceType",
		"RoofCoveringType", "StructureShape", "CustomField", "UserDefault", "DatabaseInfo",
	};

	private readonly string _connectionString;

	static SdeDatabase() => SqliteTypeHandlers.Register();

	private SdeDatabase(string path)
	{
		FilePath = Path.GetFullPath(path);
		_connectionString = new SqliteConnectionStringBuilder
		{
			DataSource = FilePath,
			ForeignKeys = true,
			Pooling = false,
		}.ToString();
	}

	public string FilePath { get; }

	/// <summary>Photos and other attachments, next to the database file (FEMA used "&lt;db parent&gt;/attachments").</summary>
	public string AttachmentsFolder => Path.Combine(Path.GetDirectoryName(FilePath)!, "attachments");

	/// <summary>Documents kept with structures (elevation certificates, permits), next to the attachments folder.</summary>
	public string DocumentsFolder => Path.Combine(Path.GetDirectoryName(FilePath)!, "documents");

	/// <summary>The map's feature data: a GeoPackage beside the database ("sde.sqlite.layers.gpkg"), which QGIS and ArcGIS can open.</summary>
	public string LayersPath => FilePath + ".layers.gpkg";

	/// <summary>The file every running copy of SDE holds a lease on while it has this database open (see <see cref="DatabaseLease"/>).</summary>
	public string LockPath => FilePath + ".lock";

	/// <summary>Names this open copy of the database in <c>RowVersion.WriterId</c>, so it can tell its own saves from others'.</summary>
	public string WriterId { get; } = Guid.NewGuid().ToString("N");

	/// <summary>How many write transactions this copy has committed (the change watcher ignores its own).</summary>
	public long CommitCount => Interlocked.Read(ref _commits);

	/// <summary>How many connections this copy has opened (tests check that loading many assessments uses one).</summary>
	public long ConnectionsOpened => Interlocked.Read(ref _connections);

	/// <summary>
	/// This database's own ID, made when it is created or first opened by v2.5. Copied into side files (the layers
	/// GeoPackage, work packages) so a side file that belongs to another database is noticed.
	/// </summary>
	public Guid DatabaseId => Read(c => ReadDatabaseId(c, null));

	/// <summary>The default location: ~/Library/Application Support/SDE/sde.sqlite.</summary>
	public static string DefaultPath =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SDE", "sde.sqlite");

	/// <summary>Opens an existing database, or creates and seeds a new one.</summary>
	public static SdeDatabase OpenOrCreate(string path)
	{
		return File.Exists(path) ? Open(path) : Create(path);
	}

	public static SdeDatabase Open(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("SDE database not found", path);
		}
		SdeDatabase db = new SdeDatabase(path);
		using SqliteConnection c = db.Connect();
		int version = c.ExecuteScalar<int?>("SELECT CAST(Value AS INTEGER) FROM SchemaInfo WHERE Key = 'SchemaVersion'") ?? 0;
		if (version > SchemaVersion)
		{
			throw new InvalidOperationException($"{path} was created by a newer version of this app (schema {version}).");
		}
		EnsureSchema(c);
		return db;
	}

	/// <summary>
	/// Adds tables introduced after a database was created. Every statement in Schema.sql is CREATE … IF NOT EXISTS,
	/// so re-running it only adds what's missing; FEMA's tables are never altered. Runs only when the database's
	/// MacSchemaVersion is older than this version's, or the same but made from a different Schema.sql (a new index),
	/// so opening an up-to-date database doesn't write to it. A database made by a newer version is left alone.
	/// </summary>
	private static void EnsureSchema(SqliteConnection c)
	{
		if (!NeedsSchemaUpdate(c))
		{
			return;
		}
		using SqliteTransaction tx = c.BeginTransaction(deferred: false);
		c.Execute(ReadResource("Schema.sql"), transaction: tx);
		WriteMacSchemaVersion(c, tx);
		tx.Commit();
	}

	/// <summary>True when <see cref="EnsureSchema"/> would change the database (the AI assistant server refuses to).</summary>
	private static bool NeedsSchemaUpdate(SqliteConnection c)
	{
		bool hasInfo = c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaInfo'") > 0;
		int mac = hasInfo ? c.ExecuteScalar<int?>("SELECT CAST(Value AS INTEGER) FROM SchemaInfo WHERE Key = 'MacSchemaVersion'") ?? 1 : 1;
		string hash = hasInfo ? c.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'MacSchemaHash'") : null;
		return mac < MacSchemaVersion || (mac == MacSchemaVersion && hash != SchemaHash);
	}

	/// <summary>
	/// True when opening <paramref name="path"/> would update its tables first (it was last opened by an older version).
	/// The AI assistant server checks this and asks for the database to be opened in SDE first instead.
	/// </summary>
	public static bool NeedsUpgrade(string path)
	{
		using SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
		c.Open();
		return NeedsSchemaUpdate(c);
	}

	private static void WriteMacSchemaVersion(SqliteConnection c, IDbTransaction tx)
	{
		c.Execute("INSERT INTO SchemaInfo (Key, Value) VALUES ('MacSchemaVersion', @v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
			new { v = MacSchemaVersion.ToString() }, tx);
		c.Execute("INSERT INTO SchemaInfo (Key, Value) VALUES ('MacSchemaHash', @h) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
			new { h = SchemaHash }, tx);
		c.Execute("INSERT INTO MacSetting (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO NOTHING",
			new { key = DatabaseIdKey, value = Newtonsoft.Json.JsonConvert.SerializeObject(Guid.NewGuid().ToString("D")) }, tx);
	}

	private static Guid ReadDatabaseId(IDbConnection c, IDbTransaction tx)
	{
		string json = c.ExecuteScalar<string>("SELECT Value FROM MacSetting WHERE Key = @key", new { key = DatabaseIdKey }, tx);
		return json != null && Guid.TryParse(Newtonsoft.Json.JsonConvert.DeserializeObject<string>(json), out Guid id) ? id : Guid.Empty;
	}

	/// <summary>Creates a new, empty SDE database seeded with FEMA's reference data (like a fresh SDE install).</summary>
	public static SdeDatabase Create(string path)
	{
		if (File.Exists(path))
		{
			throw new IOException(path + " already exists");
		}
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		SdeDatabase db = new SdeDatabase(path);
		Directory.CreateDirectory(db.AttachmentsFolder);
		using SqliteConnection c = db.Connect();
		using SqliteTransaction tx = c.BeginTransaction(deferred: false);
		c.Execute(ReadResource("Schema.sql"), transaction: tx);
		foreach (string table in SeedTables)
		{
			using StreamReader csv = new StreamReader(Resource($"Seed.{table}.csv"));
			CsvTableLoader.Load(c, tx, table, csv);
		}
		c.Execute("INSERT INTO SchemaInfo (Key, Value) VALUES ('SchemaVersion', @v), ('CreatedUtc', @t), ('Source', 'FEMA SDE 3.0.0 reference data')",
			new { v = SchemaVersion.ToString(), t = DateTime.UtcNow.ToString("o") }, tx);
		WriteMacSchemaVersion(c, tx);
		tx.Commit();
		return db;
	}

	public SqliteConnection Connect()
	{
		SqliteConnection c = new SqliteConnection(_connectionString);
		c.Open();
		Interlocked.Increment(ref _connections);
		c.Execute("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;");
		return c;
	}

	/// <summary>
	/// Runs <paramref name="work"/> in one transaction (the original saved each tab separately with no transaction). The
	/// transaction takes the write lock when it starts (BEGIN IMMEDIATE), so a version check made inside it can't be
	/// overtaken by another program saving at the same moment.
	/// </summary>
	public T InTransaction<T>(Func<SqliteConnection, IDbTransaction, T> work)
	{
		using SqliteConnection c = Connect();
		using SqliteTransaction tx = c.BeginTransaction(deferred: false);
		T result = work(c, tx);
		tx.Commit();
		Interlocked.Increment(ref _commits);
		return result;
	}

	public void InTransaction(Action<SqliteConnection, IDbTransaction> work) =>
		InTransaction<bool>((c, tx) =>
		{
			work(c, tx);
			return true;
		});

	public T Read<T>(Func<SqliteConnection, T> work)
	{
		using SqliteConnection c = Connect();
		return work(c);
	}

	private static Stream Resource(string name)
	{
		Assembly asm = typeof(SdeDatabase).Assembly;
		return asm.GetManifestResourceStream("Sde.Data." + name)
			?? throw new InvalidOperationException("Missing embedded resource Sde.Data." + name);
	}

	private static string ReadResource(string name)
	{
		using StreamReader r = new StreamReader(Resource(name));
		return r.ReadToEnd();
	}
}
