using System.Runtime.InteropServices;

namespace Sde.Data;

/// <summary>
/// A lease on a database. Every running copy of SDE (the app, the AI assistant server) holds a shared lease on
/// "&lt;database&gt;.lock" while it has the database open. Restoring a backup and moving the database need the lease to
/// themselves, so they are refused while another copy of SDE has the database open. The lease is an advisory file lock
/// (flock), so it goes away by itself if a program quits unexpectedly. Versions older than v2.5 don't take it.
/// </summary>
public sealed class DatabaseLease : IDisposable
{
	private const int LockShared = 1;
	private const int LockExclusive = 2;
	private const int LockNonBlocking = 4;
	private const int LockRelease = 8;

	private readonly FileStream _file;

	private DatabaseLease(FileStream file, bool exclusive)
	{
		_file = file;
		IsExclusive = exclusive;
	}

	/// <summary>True while this lease is the only one (see <see cref="TryUpgrade"/>).</summary>
	public bool IsExclusive { get; private set; }

	[DllImport("libc", EntryPoint = "flock", SetLastError = true)]
	private static extern int Flock(int fd, int operation);

	/// <summary>A shared lease, as every copy of SDE takes while a database is open; null while someone has it to themselves (a restore is running).</summary>
	public static DatabaseLease TryShared(string lockPath) => TryTake(lockPath, exclusive: false);

	/// <summary>The lease to itself; null while any other copy of SDE has the database open.</summary>
	public static DatabaseLease TryExclusive(string lockPath) => TryTake(lockPath, exclusive: true);

	private static DatabaseLease TryTake(string lockPath, bool exclusive)
	{
		if (OperatingSystem.IsWindows())
		{
			return null;
		}
		FileStream file;
		try
		{
			file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
		}
		catch (IOException)
		{
			// .NET takes a shared lock itself when it opens a file; that fails while someone holds it exclusively.
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
		DatabaseLease lease = new DatabaseLease(file, exclusive);
		if (!lease.Lock(exclusive ? LockExclusive : LockShared))
		{
			file.Dispose();
			return null;
		}
		return lease;
	}

	/// <summary>
	/// Takes the lease to itself if no other copy of SDE holds it (for a restore or a move). Returns false, still holding
	/// a shared lease, when someone else has the database open.
	/// </summary>
	public bool TryUpgrade()
	{
		if (IsExclusive)
		{
			return true;
		}
		if (Lock(LockExclusive))
		{
			IsExclusive = true;
			return true;
		}
		// Changing a lock isn't atomic: the shared one may have been let go while trying. Take it again.
		Lock(LockShared);
		return false;
	}

	/// <summary>Goes back to a shared lease after a restore or move.</summary>
	public void Downgrade()
	{
		if (IsExclusive && Lock(LockShared))
		{
			IsExclusive = false;
		}
	}

	private bool Lock(int operation)
	{
		try
		{
			return Flock(_file.SafeFileHandle.DangerousGetHandle().ToInt32(), operation | LockNonBlocking) == 0;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			// No flock: nothing to coordinate with.
			return true;
		}
	}

	public void Dispose()
	{
		try
		{
			Flock(_file.SafeFileHandle.DangerousGetHandle().ToInt32(), LockRelease);
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException)
		{
		}
		_file.Dispose();
	}
}
