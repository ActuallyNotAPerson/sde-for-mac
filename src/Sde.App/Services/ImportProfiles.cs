using Sde.Interop;

namespace Sde.App.Services;

/// <summary>
/// Import profiles as JSON files in one folder (~/Library/Application Support/SDE/Import Profiles), one per source or
/// file layout. Files rather than database rows: a profile is set up once and used for every disaster's database, and
/// a colleague can be given the file. The first time the folder is needed, profiles saved by v1.5 (its "Accela
/// Profiles" folder) are copied in; the old folder is left as it was, for v1.5.
/// </summary>
public sealed class ImportProfiles
{
	public const string FolderName = "Import Profiles";

	/// <summary>Where v1.5 kept its (Accela) import profiles, next to the new folder.</summary>
	public const string OldFolderName = "Accela Profiles";

	/// <summary>The name a profile gets when it has none.</summary>
	public const string DefaultName = "Import";

	public ImportProfiles(string folder)
	{
		Folder = folder;
		OldFolder = Path.Combine(Path.GetDirectoryName(folder) ?? "", OldFolderName);
		CopyOldProfiles();
	}

	public string Folder { get; }

	/// <summary>v1.5's folder (read once, never changed).</summary>
	public string OldFolder { get; }

	/// <summary>Saved profile names, alphabetically.</summary>
	public IReadOnlyList<string> Names() =>
		Directory.Exists(Folder)
			? Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.OrdinalIgnoreCase).ToList()
			: Array.Empty<string>();

	public bool Exists(string name) => !string.IsNullOrWhiteSpace(name) && File.Exists(PathFor(name));

	/// <summary>Reads a saved profile; settings this version doesn't know are listed in <paramref name="warnings"/>.</summary>
	public SourceProfile Load(string name, out List<string> warnings)
	{
		SourceProfile p = SourceProfile.FromJson(File.ReadAllText(PathFor(name)), out warnings);
		p.Name = name;
		return p;
	}

	/// <summary>Saves the profile under its name (replacing a profile of that name). Returns the name used.</summary>
	public string Save(SourceProfile profile)
	{
		string name = SafeName(profile.Name);
		profile.Name = name;
		Directory.CreateDirectory(Folder);
		File.WriteAllText(PathFor(name), profile.ToJson());
		return name;
	}

	public void Delete(string name)
	{
		if (Exists(name))
		{
			File.Delete(PathFor(name));
		}
	}

	/// <summary>Copies a profile file someone shared into the folder (checking that it reads). Returns its name.</summary>
	public string Import(string path, out List<string> warnings)
	{
		SourceProfile p = SourceProfile.FromJson(File.ReadAllText(path), out warnings);
		if (string.IsNullOrWhiteSpace(p.Name) || p.Name is DefaultName or SourceProfile.V15SourceName)
		{
			p.Name = Path.GetFileNameWithoutExtension(path);
		}
		return Save(p);
	}

	public string PathFor(string name) => Path.Combine(Folder, SafeName(name) + ".json");

	/// <summary>A name that works as a file name ("Import" when blank).</summary>
	public static string SafeName(string name)
	{
		string cleaned = string.Concat((name ?? "").Split(Path.GetInvalidFileNameChars())).Replace(':', '-').Trim().TrimStart('.');
		return cleaned.Length == 0 ? DefaultName : cleaned;
	}

	/// <summary>
	/// Once: when the new folder doesn't exist yet and v1.5's does, copies v1.5's profiles across. They read as
	/// Inspections profiles whose source is "Accela", so their links keep matching.
	/// </summary>
	private void CopyOldProfiles()
	{
		if (Directory.Exists(Folder) || !Directory.Exists(OldFolder))
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(Folder);
			foreach (string file in Directory.GetFiles(OldFolder, "*.json"))
			{
				File.Copy(file, Path.Combine(Folder, Path.GetFileName(file)), overwrite: false);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Not worth stopping the app for: the profiles are still in the old folder and can be added with "Use a profile someone shared".
			AppLog.Write("Couldn't copy the v1.5 import profiles", ex);
		}
	}
}
