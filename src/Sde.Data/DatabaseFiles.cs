using Dapper;
using Microsoft.Data.Sqlite;

namespace Sde.Data;

/// <summary>
/// Copies or moves a database to another place with everything that belongs to it: the photos and documents it uses
/// (in the "attachments" and "documents" folders beside it) and its map layers GeoPackage. Used by "Move or copy
/// database…" and the manual backup.
/// </summary>
public static class DatabaseFiles
{
	/// <summary>
	/// Writes a consistent copy of the database to <paramref name="newPath"/> (SQLite VACUUM INTO), with the photos and
	/// documents it refers to and its layers. Files already in the target folders are kept. The copy keeps the
	/// database's ID: it holds the same structures. <paramref name="progress"/> gets (done, total) as files are copied.
	/// </summary>
	public static void Copy(SdeDatabase source, string newPath, IProgress<(int Done, int Total)> progress = null)
	{
		newPath = Path.GetFullPath(newPath);
		if (string.Equals(newPath, source.FilePath, StringComparison.Ordinal))
		{
			throw new IOException("Choose a different place: that's where this database is now.");
		}
		if (File.Exists(newPath))
		{
			throw new IOException("There's already a database at " + newPath + ".");
		}
		string folder = Path.GetDirectoryName(newPath)!;
		Directory.CreateDirectory(folder);
		source.Read(c => c.Execute("VACUUM INTO @newPath", new { newPath }));
		if (File.Exists(source.LayersPath))
		{
			string layers = newPath + ".layers.gpkg";
			using SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source.LayersPath, Pooling = false }.ToString());
			c.Open();
			c.Execute("VACUUM INTO @layers", new { layers });
		}
		(List<string> photos, List<string> documents) = FilesUsed(source);
		int total = photos.Count + documents.Count;
		int done = 0;
		CopyFiles(photos, source.AttachmentsFolder, Path.Combine(folder, "attachments"), ref done, total, progress);
		CopyFiles(documents, source.DocumentsFolder, Path.Combine(folder, "documents"), ref done, total, progress);
	}

	/// <summary>
	/// Moves the database: copies it (see <see cref="Copy"/>), then removes the original database files and the photos
	/// and documents no other database in the old folder uses. Needs the database to itself: refused while another copy
	/// of SDE has it open. <paramref name="lease"/> is the caller's own lease; without one, a lease is taken here. The
	/// caller opens the new path afterwards.
	/// </summary>
	public static void Move(SdeDatabase source, string newPath, DatabaseLease lease = null, IProgress<(int Done, int Total)> progress = null)
	{
		DatabaseLease own = lease == null ? DatabaseLease.TryExclusive(source.LockPath) : null;
		bool exclusive = own != null || lease?.TryUpgrade() == true || OperatingSystem.IsWindows();
		try
		{
			if (!exclusive)
			{
				throw new InvalidOperationException("This database is also open in another copy of SDE (another window, or an AI assistant connected to it). Close that first, then move it.");
			}
			Copy(source, newPath, progress);
			(List<string> photos, List<string> documents) = FilesUsed(source);
			HashSet<string> usedElsewhere = FilesUsedByOthers(source);
			SqliteConnection.ClearAllPools();
			foreach (string path in new[] { source.FilePath, source.LayersPath })
			{
				foreach (string f in new[] { path, path + "-wal", path + "-shm" })
				{
					TryDelete(f);
				}
			}
			if (usedElsewhere != null)
			{
				foreach (string f in photos.Where(f => !usedElsewhere.Contains(f)))
				{
					TryDelete(Path.Combine(source.AttachmentsFolder, f));
				}
				foreach (string f in documents.Where(f => !usedElsewhere.Contains(f)))
				{
					TryDelete(Path.Combine(source.DocumentsFolder, f));
				}
			}
			TryDelete(source.LockPath);
		}
		finally
		{
			own?.Dispose();
			lease?.Downgrade();
		}
	}

	/// <summary>The photo and document files a database refers to (in Recently deleted too).</summary>
	private static (List<string> Photos, List<string> Documents) FilesUsed(SdeDatabase db) => db.Read(c =>
		(c.Query<string>("SELECT DISTINCT FileName FROM Attachment WHERE FileName IS NOT NULL AND FileName <> ''").Select(Path.GetFileName).ToList(),
		 c.Query<string>("SELECT DISTINCT FileName FROM Document WHERE FileName IS NOT NULL AND FileName <> ''").Select(Path.GetFileName).ToList()));

	/// <summary>
	/// The photo and document files the other SDE databases in the same folder refer to (they share the attachments
	/// folder, as FEMA's layout does). Null when one of them can't be read, so nothing is deleted.
	/// </summary>
	private static HashSet<string> FilesUsedByOthers(SdeDatabase db)
	{
		HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
		string folder = Path.GetDirectoryName(db.FilePath)!;
		foreach (string other in Directory.GetFiles(folder, "*.sqlite").Where(f => !string.Equals(Path.GetFullPath(f), db.FilePath, StringComparison.Ordinal)))
		{
			try
			{
				using SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = other, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
				c.Open();
				bool hasDocuments = c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Document'") > 0;
				used.UnionWith(c.Query<string>("SELECT DISTINCT FileName FROM Attachment WHERE FileName IS NOT NULL").Select(Path.GetFileName));
				if (hasDocuments)
				{
					used.UnionWith(c.Query<string>("SELECT DISTINCT FileName FROM Document WHERE FileName IS NOT NULL").Select(Path.GetFileName));
				}
			}
			catch (SqliteException)
			{
				return null;
			}
		}
		return used;
	}

	private static void CopyFiles(IEnumerable<string> names, string from, string to, ref int done, int total, IProgress<(int Done, int Total)> progress)
	{
		Directory.CreateDirectory(to);
		foreach (string name in names)
		{
			string source = Path.Combine(from, name);
			string target = Path.Combine(to, name);
			if (File.Exists(source) && !File.Exists(target))
			{
				File.Copy(source, target);
			}
			done++;
			progress?.Report((done, total));
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}
}
