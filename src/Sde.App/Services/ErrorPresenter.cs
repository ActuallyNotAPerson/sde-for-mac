using System.Text;
using Microsoft.Data.Sqlite;
using Sde.Data;

namespace Sde.App.Services;

/// <summary>
/// The app's log: one file per day in ~/Library/Logs/SDE (next to the database when SDE_DATABASE is set, so tests and
/// training sessions keep their logs to themselves). Kept for 30 days.
/// </summary>
public static class AppLog
{
	private static readonly object Gate = new object();

	public static string Folder
	{
		get
		{
			string forced = Environment.GetEnvironmentVariable("SDE_DATABASE");
			if (!string.IsNullOrEmpty(forced))
			{
				return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(forced))!, "Logs");
			}
			if (OperatingSystem.IsMacOS())
			{
				return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "SDE");
			}
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SDE", "Logs");
		}
	}

	public static string TodayFile => Path.Combine(Folder, "sde-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

	/// <summary>Appends a line (and the exception, if any). Never throws: a log that can't be written is skipped.</summary>
	public static void Write(string message, Exception ex = null)
	{
		try
		{
			lock (Gate)
			{
				Directory.CreateDirectory(Folder);
				StringBuilder text = new StringBuilder();
				text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ").AppendLine(message);
				if (ex != null)
				{
					text.AppendLine(ex.ToString());
				}
				File.AppendAllText(TodayFile, text.ToString());
				foreach (string old in Directory.GetFiles(Folder, "sde-*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)))
				{
					File.Delete(old);
				}
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

/// <summary>What to tell a person about an error: a title, a plain-English message, and the technical details.</summary>
public sealed record ErrorDescription(string Title, string Message, string Details);

/// <summary>
/// Turns exceptions into plain English, logs them, and shows them in an error sheet with "Show details". Every place
/// the app catches an unexpected error goes through here, as do the global handlers.
/// </summary>
public sealed class ErrorPresenter
{
	private readonly SheetHost _sheets;

	public ErrorPresenter(SheetHost sheets) => _sheets = sheets;

	/// <summary>The errors shown so far (tests read this).</summary>
	public List<ErrorDescription> Shown { get; } = new();

	/// <summary>Logs the error and shows it. <paramref name="action"/> says what was being done, e.g. "Couldn't export".</summary>
	public void Show(string action, Exception ex)
	{
		ErrorDescription d = Describe(action, ex);
		AppLog.Write(d.Title + ": " + d.Message, ex);
		Shown.Add(d);
		_ = _sheets.ShowAsync(new ErrorSheetViewModel(d));
	}

	/// <summary>A plain-English description of an exception.</summary>
	public static ErrorDescription Describe(string action, Exception ex)
	{
		Exception e = ex is AggregateException agg && agg.InnerExceptions.Count == 1 ? agg.InnerException! : ex;
		string title = string.IsNullOrWhiteSpace(action) ? "Something went wrong" : action;
		string message = e switch
		{
			AssessmentLockedException locked => locked.Message,
			FileNotFoundException nf => $"The file \"{Path.GetFileName(nf.FileName ?? "")}\" isn't there any more. It may have been moved or renamed.",
			DirectoryNotFoundException => "A folder SDE needed isn't there any more. It may have been moved, renamed or disconnected.",
			UnauthorizedAccessException => "SDE isn't allowed to use that file or folder. Check its permissions, or choose a different place (for example your Documents folder).",
			PathTooLongException => "That file name or folder path is too long. Choose a shorter one.",
			SqliteException sq when sq.SqliteErrorCode is 5 or 6 => "The database is busy (another copy of SDE, or a backup, may be using it). Wait a moment and try again.",
			SqliteException sq when sq.SqliteErrorCode is 11 or 26 => "The database file is damaged or isn't an SDE database. Restore a backup (File, then Restore from Backup).",
			SqliteException sq when sq.SqliteErrorCode == 13 => "The disk is full. Free up some space and try again.",
			SqliteException => "The database couldn't be read or written.",
			IOException io when io.HResult == unchecked((int)0x80070070) || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase) => "The disk is full. Free up some space and try again.",
			IOException => "A file couldn't be read or written. If it's open in another app, close it and try again.",
			TimeoutException => "That took too long and was stopped. Try again.",
			OperationCanceledException => "It was stopped before it finished.",
			OutOfMemoryException => "Your Mac ran out of memory. Close other apps and try again.",
			// Our own InvalidOperation and Argument exceptions are written for people, and so are the GIS core's.
			InvalidOperationException or ArgumentException or FormatException or NotSupportedException or Sde.Gis.GisException => e.Message,
			_ when e.GetType().Name == "OnlineServiceException" => e.Message,
			_ => "Something unexpected happened. Nothing was lost; anything already saved is still there.",
		};
		string details = $"{e.GetType().FullName}: {e.Message}\n\nLog: {AppLog.TodayFile}\n\n{e}";
		return new ErrorDescription(title, message, details);
	}
}

/// <summary>The error sheet: a message, with the technical details folded away.</summary>
public sealed partial class ErrorSheetViewModel(ErrorDescription error) : SheetViewModel(error.Title)
{
	public ErrorDescription Error { get; } = error;

	public string Message => Error.Message;

	public string Details => Error.Details;

	[CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
	public partial bool ShowDetails { get; set; }

	[CommunityToolkit.Mvvm.Input.RelayCommand]
	private void ToggleDetails() => ShowDetails = !ShowDetails;
}
