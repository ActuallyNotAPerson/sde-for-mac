using CommunityToolkit.Mvvm.ComponentModel;

namespace Sde.App.ViewModels;

/// <summary>The groups of settings, in the order of the list on the left of the Settings page.</summary>
public enum SettingsCategory
{
	General,
	Defaults,
	ReportsAndLetters,
	OnlineServices,
	PhoneForm,
	AiAssistants,
	MapAndGis,
	DepthTables,
	Backups,
}

/// <summary>One entry in the Settings page's list: its name, icon (an icon kind from Styles/Icons.axaml) and what it covers.</summary>
public sealed record SettingsCategoryItem(SettingsCategory Category, string Title, string Icon, string Subtitle);

/// <summary>The Settings page's categories, in the style of the Mac's System Settings: a list on the left, one group shown.</summary>
public sealed partial class SettingsViewModel
{
	public IReadOnlyList<SettingsCategoryItem> Categories { get; } = new[]
	{
		new SettingsCategoryItem(SettingsCategory.General, "General", "Settings", "Your name, location checks, your own fields and note snippets."),
		new SettingsCategoryItem(SettingsCategory.Defaults, "Defaults", "Edit", "Values filled into new structures and assessments, so you type them once per event."),
		new SettingsCategoryItem(SettingsCategory.ReportsAndLetters, "Reports & letters", "Report", "Substantial improvement, owner letter templates and cumulative damage."),
		new SettingsCategoryItem(SettingsCategory.OnlineServices, "Online services", "Globe", "The public services SDE asks when you look up a structure online."),
		new SettingsCategoryItem(SettingsCategory.PhoneForm, "Phone form", "Phone", "Let inspectors work in a phone's web browser over this Mac's Wi-Fi network."),
		new SettingsCategoryItem(SettingsCategory.AiAssistants, "AI assistants", "Assistant", "Whether AI assistants may change this database, and what they changed."),
		new SettingsCategoryItem(SettingsCategory.MapAndGis, "Map & GIS", "Map", "The map's background and layers, and files for GIS."),
		new SettingsCategoryItem(SettingsCategory.DepthTables, "Depth tables", "Flood", "Your community's depth-damage tables, for suggested damage values."),
		new SettingsCategoryItem(SettingsCategory.Backups, "Backups", "Database", "Where this database is, its backups, and other databases."),
	};

	/// <summary>The category showing. General when the page opens.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsDefaults), nameof(IsReportsAndLetters), nameof(IsOnlineServices), nameof(IsPhoneForm),
		nameof(IsAiAssistants), nameof(IsMapAndGis), nameof(IsDepthTables), nameof(IsBackups))]
	public partial SettingsCategoryItem SelectedCategory { get; set; }

	/// <summary>Shows a category (for the command palette and for tests).</summary>
	public void Show(SettingsCategory category) => SelectedCategory = Categories.First(c => c.Category == category);

	private bool Showing(SettingsCategory category) => (SelectedCategory?.Category ?? SettingsCategory.General) == category;

	public bool IsGeneral => Showing(SettingsCategory.General);

	public bool IsDefaults => Showing(SettingsCategory.Defaults);

	public bool IsReportsAndLetters => Showing(SettingsCategory.ReportsAndLetters);

	public bool IsOnlineServices => Showing(SettingsCategory.OnlineServices);

	public bool IsPhoneForm => Showing(SettingsCategory.PhoneForm);

	public bool IsAiAssistants => Showing(SettingsCategory.AiAssistants);

	public bool IsMapAndGis => Showing(SettingsCategory.MapAndGis);

	public bool IsDepthTables => Showing(SettingsCategory.DepthTables);

	public bool IsBackups => Showing(SettingsCategory.Backups);

	/// <summary>A list can't be left with nothing chosen: clicking the chosen category again keeps it.</summary>
	partial void OnSelectedCategoryChanged(SettingsCategoryItem value)
	{
		if (value == null)
		{
			SelectedCategory = Categories[0];
		}
		else if (value.Category == SettingsCategory.AiAssistants)
		{
			AiAssistants.Refresh(); // an assistant may have changed something since the panel was last shown
		}
		else if (value.Category == SettingsCategory.MapAndGis)
		{
			_ = MapGis.Refresh(); // the Map page may have changed the background, or saved more pieces
		}
	}
}
