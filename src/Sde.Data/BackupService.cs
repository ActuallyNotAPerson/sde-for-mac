using System.Globalization;
using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Sde.Data;

/// <summary>One automatic or manual backup: a folder with a copy of the database and its photos.</summary>
public sealed record BackupInfo(string Folder, DateTime TakenOn, string Reason, long DatabaseBytes, int Structures, int Assessments);

/// <summary>
/// Automatic backups, kept in a Backups folder beside the database: one when a database is first opened each day and
/// one on quitting when something changed, keeping the newest <see cref="Keep"/>. Each backup is a consistent copy of
/// the database (SQLite VACUUM INTO) plus the photos, which are hard links to the originals (photos never change once
/// added, so a link costs no space and survives the original being deleted). Restoring a backup backs up the current
/// state first.
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

	/// <summary>Backups of this database, newest first.</summary>
	public List<BackupInfo> List()
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
			(int structures, int assessments) = Counts(db);
			found.Add(new BackupInfo(dir, taken, reason, new FileInfo(db).Length, structures, assessments));
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
			string dbCopy = Path.Combine(partial, "sde.sqlite");
			_database.Read(c => c.Execute("VACUUM INTO @dbCopy", new { dbCopy }));
			string photos = Path.Combine(partial, "attachments");
			Directory.CreateDirectory(photos);
			if (Directory.Exists(_database.AttachmentsFolder))
			{
				foreach (string f in Directory.GetFiles(_database.AttachmentsFolder))
				{
					LinkOrCopy(f, Path.Combine(photos, Path.GetFileName(f)));
				}
			}
			Directory.Move(partial, folder);
		}
		catch
		{
			TryDelete(partial);
			throw;
		}
		Prune();
		return List().First(b => b.Folder == folder);
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

	/// <summary>When the database file (or its write-ahead log) was last written.</summary>
	private DateTime LastWrite()
	{
		DateTime t = File.GetLastWriteTime(_database.FilePath);
		string wal = _database.FilePath + "-wal";
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
	/// Replaces the database and photos with a backup. The current state is backed up first ("before restore"), so a
	/// restore can itself be undone. The caller must reopen the database afterwards; every connection is closed here.
	/// Photos added since the backup are kept in the attachments folder (unused).
	/// </summary>
	public BackupInfo Restore(BackupInfo backup)
	{
		BackupInfo before = BackUp("before restore");
		SqliteConnection.ClearAllPools();
		string db = _database.FilePath;
		File.Copy(Path.Combine(backup.Folder, "sde.sqlite"), db, overwrite: true);
		foreach (string side in new[] { db + "-wal", db + "-shm" })
		{
			if (File.Exists(side))
			{
				File.Delete(side);
			}
		}
		string photos = Path.Combine(backup.Folder, "attachments");
		if (Directory.Exists(photos))
		{
			Directory.CreateDirectory(_database.AttachmentsFolder);
			foreach (string f in Directory.GetFiles(photos))
			{
				string target = Path.Combine(_database.AttachmentsFolder, Path.GetFileName(f));
				if (!File.Exists(target))
				{
					LinkOrCopy(f, target);
				}
			}
		}
		return before;
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
