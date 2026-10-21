using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Data;
using Sde.Reports.Letters;

namespace Sde.App.ViewModels;

/// <summary>Where the owner letter templates live.</summary>
public static class LetterTemplateLocation
{
	public const string FolderName = "Letter Templates";

	/// <summary>
	/// Application Support/SDE/Letter Templates, or a folder next to the database when SDE_DATABASE chose it (so a test
	/// or training session keeps everything it writes in one place). Kept outside the database, so the templates carry
	/// over to a new database.
	/// </summary>
	public static string Folder(AppServices app)
	{
		string forced = Environment.GetEnvironmentVariable("SDE_DATABASE");
		string parent = !string.IsNullOrEmpty(forced) && app.Store != null
			? Path.GetDirectoryName(app.Store.Database.FilePath)
			: Path.GetDirectoryName(SdeDatabase.DefaultPath);
		return Path.Combine(parent!, FolderName);
	}

	/// <summary>The .docx files in the folder, by name (Word's "~$" lock files left out).</summary>
	public static IReadOnlyList<string> Templates(string folder) =>
		Directory.Exists(folder)
			? Directory.GetFiles(folder, "*.docx").Select(Path.GetFileName).Where(n => !n.StartsWith("~$", StringComparison.Ordinal))
				.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
			: Array.Empty<string>();
}

/// <summary>
/// The Settings page's sections for v2.0's outputs: substantial improvement rules, owner letter templates, and
/// cumulative damage. The SI and cumulative rules are kept in the database (they belong to the community whose data
/// it is); the letter templates are files in a folder. Saved as they change.
/// </summary>
public sealed partial class OutputSettingsViewModel : ObservableObject
{
	private readonly AppServices _app;
	private readonly bool _loading;

	public OutputSettingsViewModel(AppServices app)
	{
		_app = app;
		_loading = true;
		SiSettings si = app.Store.GetSiSettings();
		SiThreshold = si.ThresholdPercent;
		SiLookbackYears = si.LookbackYears;
		SiIncludePrior = si.IncludePriorProjects;
		CumulativeSettings cu = app.Store.GetCumulativeSettings();
		CumulativeEnabled = cu.Enabled;
		CumulativeMethodIndex = cu.Method == CumulativeMethod.TotalDamageOverCurrentValue ? 1 : 0;
		CumulativeLookbackYears = cu.LookbackYears;
		CumulativeThreshold = cu.ThresholdPercent;
		_loading = false;
		LoadTemplates();
	}

	// ───────────── Substantial improvement ─────────────

	/// <summary>Percent of market value at which improvements are substantial (NFIP minimum 50).</summary>
	[ObservableProperty]
	public partial decimal? SiThreshold { get; set; }

	/// <summary>Years of earlier projects counted with a new one (0 = this project only).</summary>
	[ObservableProperty]
	public partial decimal? SiLookbackYears { get; set; }

	[ObservableProperty]
	public partial bool SiIncludePrior { get; set; }

	partial void OnSiThresholdChanged(decimal? value) => SaveSi();

	partial void OnSiLookbackYearsChanged(decimal? value) => SaveSi();

	partial void OnSiIncludePriorChanged(bool value) => SaveSi();

	private void SaveSi()
	{
		if (_loading)
		{
			return;
		}
		_app.Store.SetSiSettings(new SiSettings
		{
			ThresholdPercent = SiThreshold is decimal t && t > 0m ? t : 50m,
			LookbackYears = SiLookbackYears is decimal y && y > 0m ? (int)y : 0,
			IncludePriorProjects = SiIncludePrior,
		});
	}

	// ───────────── Owner letters ─────────────

	public string TemplatesFolder => LetterTemplateLocation.Folder(_app);

	public ObservableCollection<string> Templates { get; } = new();

	public bool HasTemplates => Templates.Count > 0;

	public string TemplatesText => Templates.Count == 0
		? "There are no templates yet. Create the starter templates, then edit them in Word or Pages."
		: $"{Templates.Count} template{(Templates.Count == 1 ? "" : "s")}: {string.Join(", ", Templates)}";

	private void LoadTemplates()
	{
		Templates.Clear();
		foreach (string t in LetterTemplateLocation.Templates(TemplatesFolder))
		{
			Templates.Add(t);
		}
		OnPropertyChanged(nameof(HasTemplates));
		OnPropertyChanged(nameof(TemplatesText));
	}

	/// <summary>Writes the three starter templates into the folder, leaving any that are already there (and edited) alone.</summary>
	[RelayCommand]
	private void CreateStarterTemplates()
	{
		int before = LetterTemplateLocation.Templates(TemplatesFolder).Count;
		try
		{
			LetterTemplates.WriteStarterTemplates(TemplatesFolder);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't create the starter templates", ex);
			return;
		}
		LoadTemplates();
		int added = Templates.Count - before;
		_app.Dialogs.Notify("Starter templates", added == 0
			? "The starter templates are already in the folder, so nothing was changed."
			: $"Added {added} starter template{(added == 1 ? "" : "s")}. Review and adapt them with your community's counsel before you send any letters.");
	}

	[RelayCommand]
	private void ShowTemplatesFolder()
	{
		Directory.CreateDirectory(TemplatesFolder);
		_app.Dialogs.Reveal(TemplatesFolder);
		LoadTemplates();
	}

	// ───────────── Cumulative damage ─────────────

	[ObservableProperty]
	public partial bool CumulativeEnabled { get; set; }

	/// <summary>0: add up each event's percent damaged; 1: total repair cost over the current value.</summary>
	[ObservableProperty]
	public partial int CumulativeMethodIndex { get; set; }

	[ObservableProperty]
	public partial decimal? CumulativeLookbackYears { get; set; }

	[ObservableProperty]
	public partial decimal? CumulativeThreshold { get; set; }

	public static IReadOnlyList<string> CumulativeMethods { get; } = new[]
	{
		"Add up each event's percent damaged",
		"Total repair cost over the current building value",
	};

	partial void OnCumulativeEnabledChanged(bool value) => SaveCumulative();

	partial void OnCumulativeMethodIndexChanged(int value) => SaveCumulative();

	partial void OnCumulativeLookbackYearsChanged(decimal? value) => SaveCumulative();

	partial void OnCumulativeThresholdChanged(decimal? value) => SaveCumulative();

	private void SaveCumulative()
	{
		if (_loading)
		{
			return;
		}
		_app.Store.SetCumulativeSettings(new CumulativeSettings
		{
			Enabled = CumulativeEnabled,
			Method = CumulativeMethodIndex == 1 ? CumulativeMethod.TotalDamageOverCurrentValue : CumulativeMethod.SumOfPercents,
			LookbackYears = CumulativeLookbackYears is decimal y && y > 0m ? (int)y : 0,
			ThresholdPercent = CumulativeThreshold is decimal t && t > 0m ? t : 50m,
		});
	}
}
