using Newtonsoft.Json;
using Sde.Data.Infrastructure;

namespace Sde.Data;

public sealed partial class SdeStore
{
	/// <summary>
	/// A per-database setting (stored as JSON in MacSetting), or <paramref name="fallback"/> when it isn't set or can't
	/// be read. Settings that belong to the person rather than the data (window size, recent files) live in the app's
	/// own settings file instead.
	/// </summary>
	public T GetSetting<T>(string key, T fallback = default)
	{
		string json = Database.Read(c => TableMap<MacSettingRecord>.Get(c, new { Key = key })?.Value);
		if (json == null)
		{
			return fallback;
		}
		try
		{
			return JsonConvert.DeserializeObject<T>(json);
		}
		catch (JsonException)
		{
			return fallback;
		}
	}

	/// <summary>Saves a per-database setting; null removes it.</summary>
	public void SetSetting<T>(string key, T value) => Database.InTransaction((c, tx) =>
	{
		if (value == null)
		{
			TableMap<MacSettingRecord>.Delete(c, "Key = @key", new { key }, tx);
			return;
		}
		TableMap<MacSettingRecord>.Upsert(c, new MacSettingRecord { Key = key, Value = JsonConvert.SerializeObject(value) }, tx);
	});

	/// <summary>The Structures list's saved views, by name.</summary>
	public List<SavedViewRecord> GetSavedViews() => Database.Read(c => TableMap<SavedViewRecord>.Select(c, orderBy: "Name").ToList());

	/// <summary>Adds or replaces a saved view (names are matched ignoring case).</summary>
	public void SaveView(string name, string state) => Database.InTransaction((c, tx) =>
	{
		TableMap<SavedViewRecord>.Delete(c, "Name = @name", new { name }, tx);
		TableMap<SavedViewRecord>.Insert(c, new SavedViewRecord { Name = name.Trim(), State = state, UpdatedOn = Clock() }, tx);
	});

	public void DeleteView(string name) => Database.InTransaction((c, tx) => TableMap<SavedViewRecord>.Delete(c, "Name = @name", new { name }, tx));
}
