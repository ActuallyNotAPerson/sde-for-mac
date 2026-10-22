using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Sde.Data;

namespace Sde.App.Services;

/// <summary>
/// Asks the open database's <see cref="ChangeWatcher"/> about every 1.5 seconds, while the window is active, whether
/// another program saved something (the AI assistant server, SDE on another Mac, an older version), and tells the
/// window so lists and the open assessment can catch up. Activating the window checks at once.
/// </summary>
public sealed class ExternalChangeMonitor
{
	public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);

	private readonly AppServices _app;
	private readonly DispatcherTimer _timer;
	private bool _failedOnce;

	public ExternalChangeMonitor(AppServices app)
	{
		_app = app;
		_timer = new DispatcherTimer { Interval = Interval };
		_timer.Tick += (_, _) => PollNow();
	}

	/// <summary>Raised on the UI thread with what others saved.</summary>
	public event Action<ExternalChanges> Changed;

	/// <summary>Polls while true (the window is active); becoming active checks straight away.</summary>
	public bool IsActive
	{
		get => _timer.IsEnabled;
		set
		{
			if (value == _timer.IsEnabled)
			{
				return;
			}
			if (value)
			{
				PollNow();
				_timer.Start();
			}
			else
			{
				_timer.Stop();
			}
		}
	}

	/// <summary>Checks now, raising <see cref="Changed"/> if anything was saved by someone else. Returns what was found.</summary>
	public ExternalChanges PollNow()
	{
		ExternalChanges changes;
		try
		{
			changes = _app.Watcher?.Poll();
		}
		catch (SqliteException ex)
		{
			// A busy or vanished database file: try again next time, and say so in the log once.
			if (!_failedOnce)
			{
				_failedOnce = true;
				AppLog.Write("Couldn't check the database for changes made elsewhere", ex);
			}
			return null;
		}
		if (changes != null)
		{
			Changed?.Invoke(changes);
		}
		return changes;
	}
}
