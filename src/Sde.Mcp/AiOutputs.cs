namespace Sde.Mcp;

/// <summary>
/// Where the output tools write: an "AI outputs" folder beside the database, made when first needed. Writing only
/// there (never where an assistant asks) keeps every file in one place the person can find, and avoids macOS privacy
/// prompts for other folders that would be attributed to the assistant's app.
/// </summary>
internal static class AiOutputs
{
	/// <summary>The folder's name.</summary>
	public const string FolderName = "AI outputs";

	/// <summary>The longest file name (before the extension and any " 2") an output takes.</summary>
	private const int MaxNameLength = 100;

	/// <summary>The AI outputs folder for <paramref name="databasePath"/>.</summary>
	public static string Folder(string databasePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, FolderName);

	/// <summary>Makes choosing a free name and taking it one step, so two outputs made at once never share a name.</summary>
	private static readonly object Gate = new object();

	/// <summary>
	/// A path in the AI outputs folder (made if needed) for a new file: <paramref name="name"/> made safe (no folders, no
	/// characters macOS or Windows can't use, not hidden), or <paramref name="fallback"/>, with " 2", " 3"… added when
	/// that name is taken. <paramref name="extension"/> includes the dot; an empty one makes a folder name. The name is
	/// taken at once (an empty file, or the folder), so nothing else can choose it before the output is written.
	/// </summary>
	public static string FreePath(string databasePath, string name, string fallback, string extension)
	{
		string folder = Folder(databasePath);
		Directory.CreateDirectory(folder);
		string safe = SafeName(name);
		if (safe.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && extension.Length > 0)
		{
			safe = safe[..^extension.Length].TrimEnd(' ', '.');
		}
		if (safe.Length == 0)
		{
			safe = SafeName(fallback);
		}
		lock (Gate)
		{
			string path = Path.Combine(folder, safe + extension);
			for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
			{
				path = Path.Combine(folder, $"{safe} {i}{extension}");
			}
			if (extension.Length == 0)
			{
				Directory.CreateDirectory(path);
			}
			else
			{
				new FileStream(path, FileMode.CreateNew, FileAccess.Write).Dispose();
			}
			return path;
		}
	}

	/// <summary>Gives back a name <see cref="FreePath"/> took when nothing was written to it after all.</summary>
	public static void Release(string path)
	{
		try
		{
			if (File.Exists(path) && new FileInfo(path).Length == 0)
			{
				File.Delete(path);
			}
			else if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
			{
				Directory.Delete(path);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Left for the person to tidy; it is empty.
		}
	}

	/// <summary>A name with folder separators, control characters and other characters file systems refuse replaced, trimmed and shortened.</summary>
	public static string SafeName(string name)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		string cleaned = new string((name ?? "").Select(c => invalid.Contains(c) || c is ':' or '/' or '\\' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c).ToArray());
		cleaned = cleaned.Trim().TrimStart('.', '-', ' ').Trim();
		if (cleaned.Length > MaxNameLength)
		{
			cleaned = cleaned[..MaxNameLength].Trim();
		}
		return cleaned;
	}
}
