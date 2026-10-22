using System.Globalization;
using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Sde.Data;

/// <summary>
/// One automatic or manual backup: a folder with a copy of the database, its photos and documents, and its map layers.
/// The counts are only filled in by <see cref="BackupService.List(bool)"/> when asked for (they mean opening the copy).
/// </summary>
public sealed record BackupInfo(string Folder, DateTime TakenOn, string Reason, long DatabaseBytes, int? Structures = null, int? Assessments = null);

/// <summary>
/// Automatic backups, kept in a Backups folder beside the database: one when a database is first opened each day and
/// one on quitting when something changed, keeping the newest <see cref="Keep"/>. Each backup is a consistent copy of
/// the database (SQLite VACUUM INTO) plus the photos and documents, which are hard links to the originals (they never
/// change once added, so a link costs no space and survives the original being deleted). The map layers GeoPackage is
/// copied the same way when it changed since the previous backup, and otherwise linked to that backup's copy (backup
/// copies never change either). Restoring a backup backs up the current state first.
/// </summary>
public sealed class BackupService
{
	public const int Keep = 14;

	public const string FolderName = "Backups";

	private const string Stamp = "yyyy-MM-dd HHmmss";

	private readonly SdeDatabase _database;

	public BackupService(SdeDatabase database, Func<DateTime> clock = null)
	{
		_database = database;
		Clock = clock ?? (() => DateTime.Now);
	}

	public Func<DateTime> Clock { get; }

	public string BackupsFolder => Path.Combine(Path.GetDirectoryName(_database.FilePath)!, FolderName);

	/// <summary>
	/// Backups of this database, newest first. Cheap: only the folder names and sizes are read, unless
	/// <paramref name="withCounts"/> asks for the number of structures and assessments in each (the Restore sheet).
	/// </summary>
	public List<BackupInfo> List(bool withCounts = false)
	{
		if (!Directory.Exists(BackupsFolder))
		{
			return new List<BackupInfo>();
		}
		List<BackupInfo> found = new List<BackupInfo>();
		string prefix = Prefix;
		foreach (string dir in Directory.GetDirectories(BackupsFolder, prefix + "*"))
		{
			string rest = Path.GetFileName(dir)[prefix.Length..];
			string stamp = rest.Length >= Stamp.Length ? rest[..Stamp.Length] : rest;
			if (!DateTime.TryParseExact(stamp, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime taken))
			{
				continue;
			}
			string db = Path.Combine(dir, "sde.sqlite");
			if (!File.Exists(db))
			{
				continue;
			}
			string reason = rest.Length > Stamp.Length ? rest[Stamp.Length..].Trim(' ', '(', ')') : "";
			if (withCounts)
			{
				(int structures, int assessments) = Counts(db);
				found.Add(new BackupInfo(dir, taken, reason, new FileInfo(db).Length, structures, assessments));
			}
			else
			{
				found.Add(new BackupInfo(dir, taken, reason, new FileInfo(db).Length));
			}
		}
		return found.OrderByDescending(b => b.TakenOn).ToList();
	}

	/// <summary>"sde " for sde.sqlite, so several databases can share a folder.</summary>
	private string Prefix => Path.GetFileNameWithoutExtension(_database.FilePath) + " ";

	/// <summary>Backs up now and removes the oldest beyond <see cref="Keep"/>.</summary>
	public BackupInfo BackUp(string reason)
	{
		Directory.CreateDirectory(BackupsFolder);
		DateTime now = Clock();
		string folder = Path.Combine(BackupsFolder, Prefix + now.ToString(Stamp, CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));
		for (int n = 2; Directory.Exists(folder); n++)
		{
			folder = Path.Combine(BackupsFolder, Prefix + now.AddSeconds(n - 1).ToString(Stamp, CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));
		}
		string partial = folder + ".partial";
		Directory.CreateDirectory(partial);
		try
		{
			BackupInfo previous = List().FirstOrDefault();
			string dbCopy = Path.Combine(partial, "sde.sqlite");
			_database.Read(c => c.Execute("VACUUM INTO @dbCopy", new { dbCopy }));
			LinkFolder(_database.AttachmentsFolder, Path.Combine(partial, "attachments"));
			LinkFolder(_database.DocumentsFolder, Path.Combine(partial, "documents"));
			BackUpLayers(previous, Path.Combine(partial, LayersFileName));
			Directory.Move(partial, folder);
		}
		catch
		{
			TryDelete(partial);
			throw;
		}
		Prune();
		BackupInfo made = List().First(b => b.Folder == folder);
		(int structures, int assessments) = Counts(Path.Combine(folder, "sde.sqlite"));
		return made with { Structures = structures, Assessments = assessments };
	}

	/// <summary>The map layers GeoPackage's name inside a backup folder.</summary>
	public const string LayersFileName = "layers.gpkg";

	/// <summary>Hard-links (or copies) every file of a folder into a backup.</summary>
	private static void LinkFolder(string source, string target)
	{
		Directory.CreateDirectory(target);
		if (Directory.Exists(source))
		{
			foreach (string f in Directory.GetFiles(source))
			{
				LinkOrCopy(f, Path.Combine(target, Path.GetFileName(f)));
			}
		}
	}

	/// <summary>
	/// Copies the layers GeoPackage into a backup: a consistent copy (VACUUM INTO) when it changed since the previous
	/// backup, otherwise a link to the previous backup's copy.
	/// </summary>
	private void BackUpLayers(BackupInfo previous, string target)
	{
		string live = _database.LayersPath;
		if (!File.Exists(live))
		{
			return;
		}
		string earlier = previous == null ? null : Path.Combine(previous.Folder, LayersFileName);
		if (earlier != null && File.Exists(earlier) && LastWrite(live) < File.GetLastWriteTime(earlier))
		{
			LinkOrCopy(earlier, target);
			return;
		}
		using SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = live, Pooling = false }.ToString());
		c.Open();
		c.Execute("VACUUM INTO @target", new { target });
	}

	/// <summary>The automatic backup when a database is opened: once a day.</summary>
	public BackupInfo BackUpIfNoneToday()
	{
		BackupInfo newest = List().FirstOrDefault();
		return newest != null && newest.TakenOn.Date == Clock().Date ? null : BackUp("opened");
	}

	/// <summary>The automatic backup on quitting: only when the database changed since the newest backup.</summary>
	public BackupInfo BackUpIfChanged()
	{
		BackupInfo newest = List().FirstOrDefault();
		DateTime changed = LastWrite();
		return newest != null && changed <= newest.TakenOn ? null : BackUp("quit");
	}

	/// <summary>When the database (or its layers GeoPackage) was last written.</summary>
	private DateTime LastWrite()
	{
		DateTime t = LastWrite(_database.FilePath);
		if (File.Exists(_database.LayersPath))
		{
			DateTime layers = LastWrite(_database.LayersPath);
			if (layers > t)
			{
				t = layers;
			}
		}
		return t;
	}

	/// <summary>When a SQLite file (or its write-ahead log) was last written.</summary>
	private static DateTime LastWrite(string path)
	{
		DateTime t = File.GetLastWriteTime(path);
		string wal = path + "-wal";
		if (File.Exists(wal) && new FileInfo(wal).Length > 0 && File.GetLastWriteTime(wal) > t)
		{
			t = File.GetLastWriteTime(wal);
		}
		return t;
	}

	/// <summary>Keeps the newest <see cref="Keep"/> backups.</summary>
	public void Prune()
	{
		foreach (BackupInfo old in List().Skip(Keep))
		{
			TryDelete(old.Folder);
		}
		if (Directory.Exists(BackupsFolder))
		{
			foreach (string partial in Directory.GetDirectories(BackupsFolder, Prefix + "*.partial"))
			{
				TryDelete(partial);
			}
		}
	}

	/// <summary>
	/// Replaces the database, its layers and its photos and documents with a backup. Refused while another copy of SDE
	/// (the AI assistant server, another window) has the database open: <paramref name="lease"/> is the caller's own
	/// lease, which must be the only one; without one, a lease is taken here. The current state is backed up first
	/// ("before restore"), so a restore can itself be undone. The write-ahead log is checkpointed, then each file is
	/// copied beside its target and renamed over it (the layers first, then the database), so a failure part-way never
	/// leaves a half-written database. The caller must reopen the database afterwards; every connection is closed here.
	/// Photos and documents added since the backup are kept (unused).
	/// </summary>
	public BackupInfo Restore(BackupInfo backup, DatabaseLease lease = null)
	{
		DatabaseLease own = lease == null ? DatabaseLease.TryExclusive(_database.LockPath) : null;
		bool exclusive = own != null || lease?.TryUpgrade() == true || OperatingSystem.IsWindows();
		try
		{
			if (!exclusive)
			{
				throw new InvalidOperationException("This database is also open in another copy of SDE (another window, or an AI assistant connected to it). Close that first, then restore.");
			}
			BackupInfo before = BackUp("before restore");
			_database.Read(c => c.Execute("PRAGMA wal_checkpoint(TRUNCATE)"));
			SqliteConnection.ClearAllPools();

			string layers = Path.Combine(backup.Folder, LayersFileName);
			if (File.Exists(layers))
			{
				ReplaceSqliteFile(layers, _database.LayersPath);
			}
			else
			{
				DeleteSqliteFile(_database.LayersPath);
			}
			ReplaceSqliteFile(Path.Combine(backup.Folder, "sde.sqlite"), _database.FilePath);
			RestoreFolder(Path.Combine(backup.Folder, "attachments"), _database.AttachmentsFolder);
			RestoreFolder(Path.Combine(backup.Folder, "documents"), _database.DocumentsFolder);
			return before;
		}
		finally
		{
			own?.Dispose();
			lease?.Downgrade();
		}
	}

	/// <summary>Copies a SQLite file beside its target, removes the target's write-ahead log, and renames the copy over it.</summary>
	private static void ReplaceSqliteFile(string source, string target)
	{
		string temp = target + ".restoring";
		File.Copy(source, temp, overwrite: true);
		try
		{
			foreach (string side in new[] { target + "-wal", target + "-shm" })
			{
				if (File.Exists(side))
				{
					File.Delete(side);
				}
			}
			File.Move(temp, target, overwrite: true);
		}
		catch
		{
			File.Delete(temp);
			throw;
		}
	}

	private static void DeleteSqliteFile(string path)
	{
		foreach (string f in new[] { path, path + "-wal", path + "-shm" })
		{
			if (File.Exists(f))
			{
				File.Delete(f);
			}
		}
	}

	/// <summary>Puts back files from a backup folder that are missing from the live one (they never change, so existing ones are the same).</summary>
	private static void RestoreFolder(string source, string target)
	{
		if (!Directory.Exists(source))
		{
			return;
		}
		Directory.CreateDirectory(target);
		foreach (string f in Directory.GetFiles(source))
		{
			string to = Path.Combine(target, Path.GetFileName(f));
			if (!File.Exists(to))
			{
				LinkOrCopy(f, to);
			}
		}
	}

	private static (int Structures, int Assessments) Counts(string db)
	{
		try
		{
			using SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
			c.Open();
			return (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Property WHERE Active = 1"),
				c.ExecuteScalar<int>("SELECT COUNT(*) FROM Assessment a JOIN Property p ON p.PropertyID = a.PropertyID WHERE a.Active = 1 AND p.Active = 1"));
		}
		catch (SqliteException)
		{
			return (0, 0);
		}
	}

	[DllImport("libc", EntryPoint = "link", SetLastError = true)]
	private static extern int HardLink(string existing, string newPath);

	/// <summary>A hard link where the file system allows one (same disk, macOS or Linux); otherwise a copy.</summary>
	private static void LinkOrCopy(string source, string target)
	{
		if (!OperatingSystem.IsWindows())
		{
			try
			{
				if (HardLink(source, target) == 0)
				{
					return;
				}
			}
			catch (DllNotFoundException)
			{
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
		File.Copy(source, target, overwrite: true);
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
