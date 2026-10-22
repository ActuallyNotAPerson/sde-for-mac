using System.Text;
using Microsoft.Extensions.Logging;

namespace Sde.Mcp;

/// <summary>
/// Writes the server's log to "sde-mcp-yyyy-MM-dd.log" in SDE's log folder (the app passes the folder in), next to
/// the app's own daily logs, which it keeps for 30 days. Information and up only: the protocol traffic, which can
/// include owners' names, stays in the assistant's own log (stderr). Never throws.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
	private readonly object _gate = new object();
	private readonly string _folder;
	private readonly LogLevel _minimum;

	public FileLoggerProvider(string folder, LogLevel minimum = LogLevel.Information)
	{
		_folder = folder;
		_minimum = minimum;
		Prune();
	}

	/// <summary>Today's log file.</summary>
	public string TodayFile => Path.Combine(_folder, "sde-mcp-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

	public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

	public void Dispose()
	{
	}

	private void Write(string category, LogLevel level, string message, Exception ex)
	{
		try
		{
			StringBuilder text = new StringBuilder();
			text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  [mcp ").Append(Environment.ProcessId).Append("] ")
				.Append(level).Append(' ').Append(category).Append(": ").AppendLine(message);
			if (ex != null)
			{
				text.AppendLine(ex.ToString());
			}
			lock (_gate)
			{
				Directory.CreateDirectory(_folder);
				File.AppendAllText(TodayFile, text.ToString());
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private void Prune()
	{
		try
		{
			if (!Directory.Exists(_folder))
			{
				return;
			}
			foreach (string old in Directory.GetFiles(_folder, "sde-mcp-*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)))
			{
				File.Delete(old);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
	{
		public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
		{
			if (IsEnabled(logLevel))
			{
				provider.Write(category, logLevel, formatter(state, exception), exception);
			}
		}
	}
}
