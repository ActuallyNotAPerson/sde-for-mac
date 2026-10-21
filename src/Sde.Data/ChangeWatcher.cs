using Dapper;
using Microsoft.Data.Sqlite;

namespace Sde.Data;

/// <summary>What another program (or another copy of SDE) saved since the last look.</summary>
public sealed class ExternalChanges
{
	public HashSet<Guid> Assessments { get; } = new HashSet<Guid>();

	public HashSet<Guid> Properties { get; } = new HashSet<Guid>();

	/// <summary>
	/// The database changed but nothing in it says what: a version older than v2.5 saved (it doesn't record versions).
	/// Lists should reload, and an open assessment compared by its LastUpdate.
	/// </summary>
	public bool Unversioned { get; set; }

	public bool Any => Assessments.Count > 0 || Properties.Count > 0 || Unversioned;
}

/// <summary>
/// Notices saves made by other programs using the same database: the AI assistant server, SDE on another Mac through a
/// shared folder, an older version of SDE. One long-lived connection asks SQLite whether anything was committed by
/// another connection (PRAGMA data_version, which costs almost nothing); only then are the versions read. The app calls
/// <see cref="Poll"/> about every 1.5 seconds while its window is active.
/// </summary>
public sealed class ChangeWatcher : IDisposable
{
	private readonly SdeDatabase _database;
	private readonly SqliteConnection _connection;
	private long _dataVersion;
	private long _lastSeq;
	private long _commits;

	public ChangeWatcher(SdeDatabase database)
	{
		_database = database;
		_connection = database.Connect();
		_dataVersion = DataVersion();
		_lastSeq = _connection.ExecuteScalar<long?>("SELECT MAX(Seq) FROM RowVersion") ?? 0;
		_commits = database.CommitCount;
	}

	/// <summary>What others saved since the last call, or null when nothing did (this copy's own saves don't count).</summary>
	public ExternalChanges Poll()
	{
		long version = DataVersion();
		if (version == _dataVersion)
		{
			return null;
		}
		_dataVersion = version;
		long commits = _database.CommitCount;
		bool ownCommit = commits != _commits;
		_commits = commits;

		ExternalChanges changes = new ExternalChanges();
		List<RowVersionRecord> rows = _connection.Query<RowVersionRecord>(
			"SELECT ObjectID, Kind, Version, Seq, WriterId, ChangedOn FROM RowVersion WHERE Seq > @last ORDER BY Seq", new { last = _lastSeq }).ToList();
		foreach (RowVersionRecord row in rows)
		{
			_lastSeq = Math.Max(_lastSeq, row.Seq);
			if (row.WriterId == _database.WriterId)
			{
				continue;
			}
			(row.Value == VersionKind.Property ? changes.Properties : changes.Assessments).Add(row.ObjectID);
		}
		changes.Unversioned = rows.Count == 0 && !ownCommit;
		return changes.Any ? changes : null;
	}

	private long DataVersion() => _connection.ExecuteScalar<long>("PRAGMA data_version");

	public void Dispose() => _connection.Dispose();
}
