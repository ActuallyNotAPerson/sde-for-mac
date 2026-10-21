using System.Diagnostics;

namespace Sde.Data.Infrastructure;

/// <summary>
/// Reads FEMA's Access databases (.mdb) on macOS through mdbtools' command-line programs (mdb-tables, mdb-export).
/// There is no Jet/ACE driver for macOS. mdbtools is looked up in: $SDE_MDBTOOLS, the app bundle
/// (…/Contents/MacOS/mdbtools/), Homebrew (/opt/homebrew/bin, /usr/local/bin) and PATH.
/// </summary>
public static class MdbTools
{
	public static string FindExecutable(string name)
	{
		List<string> dirs = new();
		string env = Environment.GetEnvironmentVariable("SDE_MDBTOOLS");
		if (!string.IsNullOrEmpty(env))
		{
			dirs.Add(env);
		}
		dirs.Add(Path.Combine(AppContext.BaseDirectory, "mdbtools"));
		dirs.Add("/opt/homebrew/bin");
		dirs.Add("/usr/local/bin");
		dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
		foreach (string dir in dirs)
		{
			string path = Path.Combine(dir, name);
			if (File.Exists(path))
			{
				return path;
			}
		}
		return null;
	}

	public static bool IsAvailable => FindExecutable("mdb-export") != null;

	public static IReadOnlyList<string> ListTables(string mdbPath) =>
		Run("mdb-tables", "-1", mdbPath).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>CSV of one table with ISO dates (the format <see cref="CsvTableLoader"/> reads).</summary>
	public static string ExportCsv(string mdbPath, string table) =>
		Run("mdb-export", "-D", "%Y-%m-%d %H:%M:%S", "-T", "%Y-%m-%d %H:%M:%S", mdbPath, table);

	private static string Run(string tool, params string[] args)
	{
		string exe = FindExecutable(tool)
			?? throw new InvalidOperationException($"{tool} was not found. Reading Access (.mdb) files needs mdbtools: install it with 'brew install mdbtools'.");
		ProcessStartInfo psi = new ProcessStartInfo(exe)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardOutputEncoding = System.Text.Encoding.UTF8,
		};
		foreach (string a in args)
		{
			psi.ArgumentList.Add(a);
		}
		using Process p = Process.Start(psi)!;
		Task<string> stderr = p.StandardError.ReadToEndAsync();
		string output = p.StandardOutput.ReadToEnd();
		p.WaitForExit();
		if (p.ExitCode != 0)
		{
			throw new InvalidOperationException($"{tool} failed ({p.ExitCode}): {stderr.Result.Trim()}");
		}
		return output;
	}
}
