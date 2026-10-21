using System.Text.Json;
using System.Text.Json.Serialization;
using Sde.Core.Assessments;

namespace Sde.App.Services;

/// <summary>
/// The community's depth-damage tables, as JSON files in one folder (~/Library/Application Support/SDE/Depth Tables,
/// next to the database when SDE_DATABASE is set). Files rather than database rows, like import profiles: a table is
/// set up once and used for every event's database, and can be given to a colleague. SDE ships none.
/// </summary>
public sealed class DepthTables
{
	public const string FolderName = "Depth Tables";

	/// <summary>The name a table gets when it has none.</summary>
	public const string DefaultName = "Depth table";

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() },
	};

	public DepthTables(string folder)
	{
		Folder = folder;
	}

	public string Folder { get; }

	/// <summary>Saved table names, alphabetically.</summary>
	public IReadOnlyList<string> Names() =>
		Directory.Exists(Folder)
			? Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.OrdinalIgnoreCase).ToList()
			: Array.Empty<string>();

	/// <summary>Every table that can be read (a damaged file is logged and left out), alphabetically.</summary>
	public IReadOnlyList<DepthDamageTable> All()
	{
		List<DepthDamageTable> list = new List<DepthDamageTable>();
		foreach (string name in Names())
		{
			try
			{
				list.Add(Load(name));
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
			{
				AppLog.Write("Couldn't read the depth table " + name, ex);
			}
		}
		return list;
	}

	public bool Exists(string name) => !string.IsNullOrWhiteSpace(name) && File.Exists(PathFor(name));

	public DepthDamageTable Load(string name)
	{
		DepthDamageTable t = JsonSerializer.Deserialize<DepthDamageTable>(File.ReadAllText(PathFor(name)), Json) ?? new DepthDamageTable();
		t.Name = name;
		t.Rows ??= new List<DepthDamageRow>();
		return t;
	}

	/// <summary>Saves the table under its name (replacing a table of that name). Returns the name used.</summary>
	public string Save(DepthDamageTable table)
	{
		string name = ImportProfiles.SafeName(table.Name);
		if (name == ImportProfiles.DefaultName)
		{
			name = DefaultName;
		}
		table.Name = name;
		Directory.CreateDirectory(Folder);
		File.WriteAllText(PathFor(name), JsonSerializer.Serialize(table, Json));
		return name;
	}

	public void Delete(string name)
	{
		if (Exists(name))
		{
			File.Delete(PathFor(name));
		}
	}

	/// <summary>A name not used yet: <paramref name="wanted"/>, or "wanted (2)", "wanted (3)"…</summary>
	public string UnusedName(string wanted)
	{
		string name = string.IsNullOrWhiteSpace(wanted) ? DefaultName : wanted.Trim();
		string candidate = name;
		for (int i = 2; Exists(candidate); i++)
		{
			candidate = $"{name} ({i})";
		}
		return candidate;
	}

	public string PathFor(string name) => Path.Combine(Folder, ImportProfiles.SafeName(name) + ".json");
}
