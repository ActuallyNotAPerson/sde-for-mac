using System.Data;
using System.Reflection;
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
	/// The version of the Mac-only tables (2 = v2.0). Kept apart from <see cref="SchemaVersion"/>, which an older version
	/// of this app checks before opening a file: adding Mac-only tables never stops an older version from opening it.
	/// </summary>
	public const int MacSchemaVersion = 2;

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
	/// MacSchemaVersion is older than this version's, so opening an up-to-date database doesn't write to it.
	/// </summary>
	private static void EnsureSchema(SqliteConnection c)
	{
		bool hasInfo = c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaInfo'") > 0;
		int mac = hasInfo ? c.ExecuteScalar<int?>("SELECT CAST(Value AS INTEGER) FROM SchemaInfo WHERE Key = 'MacSchemaVersion'") ?? 1 : 1;
		if (mac >= MacSchemaVersion)
		{
			return;
		}
		using SqliteTransaction tx = c.BeginTransaction();
		c.Execute(ReadResource("Schema.sql"), transaction: tx);
		WriteMacSchemaVersion(c, tx);
		tx.Commit();
	}

	private static void WriteMacSchemaVersion(SqliteConnection c, IDbTransaction tx) =>
		c.Execute("INSERT INTO SchemaInfo (Key, Value) VALUES ('MacSchemaVersion', @v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
			new { v = MacSchemaVersion.ToString() }, tx);

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
		using SqliteTransaction tx = c.BeginTransaction();
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
		c.Execute("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;");
		return c;
	}

	/// <summary>Runs <paramref name="work"/> in one transaction (the original saved each tab separately with no transaction).</summary>
	public T InTransaction<T>(Func<SqliteConnection, IDbTransaction, T> work)
	{
		using SqliteConnection c = Connect();
		using SqliteTransaction tx = c.BeginTransaction();
		T result = work(c, tx);
		tx.Commit();
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
