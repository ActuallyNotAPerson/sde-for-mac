using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;
using Sde.Mcp;

namespace Sde.App.ViewModels;

/// <summary>One call by an AI assistant that changed the database, with Revert.</summary>
public sealed partial class AiChangeRowViewModel : ObservableObject
{
	private readonly Func<AiChangeRowViewModel, Task> _revert;

	public AiChangeRowViewModel(AiChangeBatch batch, bool reverted, Func<AiChangeRowViewModel, Task> revert)
	{
		Batch = batch;
		_revert = revert;
		IsReverted = reverted;
	}

	public AiChangeBatch Batch { get; }

	/// <summary>"123 River Street: Inspector, Flood elevation".</summary>
	public string Summary => Batch.Summary;

	/// <summary>"claude-code (AI) · Oct 22, 2026 2:14 PM · 3 changes".</summary>
	public string Detail => string.Join(" · ", new[]
	{
		Batch.ChangedBy,
		Batch.ChangedOn.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture),
		Batch.Changes == 1 ? "1 change" : $"{Batch.Changes} changes",
	}.Where(x => !string.IsNullOrWhiteSpace(x)));

	public string RevertName => "Revert " + Summary;

	/// <summary>What Revert does with this kind of change ("Revert moves the new structure to Recently deleted, …").</summary>
	public string RevertNote => Batch.RevertNote ?? AiChangeKinds.RevertNote(Batch.Kind);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanRevert))]
	public partial bool IsReverted { get; set; }

	public bool CanRevert => !IsReverted;

	[RelayCommand]
	private Task Revert() => _revert(this);
}

/// <summary>
/// Settings › AI assistants: the switch that lets AI assistants such as Claude change this database (off by default;
/// reading, checking and reports never need it), the setup to paste into Claude Code or Claude Desktop, and the
/// assistants' recent changes with Revert. The server itself is <c>SDE mcp</c> (src/Sde.Mcp).
/// </summary>
public sealed partial class AiAssistantsViewModel : ObservableObject
{
	/// <summary>The per-database setting (MacSetting) listing the AI changes reverted from this panel, so they show as reverted.</summary>
	public const string RevertedKey = "Ui.RevertedAiChanges";

	private readonly AppServices _app;
	private bool _loading;

	public AiAssistantsViewModel(AppServices app)
	{
		_app = app;
		Refresh();
	}

	/// <summary>On: AI assistants may change assessments (recorded in History as theirs). Off: they can only read.</summary>
	[ObservableProperty]
	public partial bool AllowEdits { get; set; }

	partial void OnAllowEditsChanged(bool value)
	{
		if (!_loading)
		{
			AiAssistantSettings.SetAllowEdits(_app.Store, value);
		}
	}

	/// <summary>SDE's program inside the app: what an assistant runs to reach this database.</summary>
	public string ProcessPath => _app.ProgramPath ?? "SDE";

	public string DatabasePath => _app.Store.Database.FilePath;

	public string ClaudeCodeCommand => AiAssistantSettings.ClaudeCodeCommand(ProcessPath, DatabasePath);

	public string ClaudeDesktopConfig => AiAssistantSettings.ClaudeDesktopConfig(ProcessPath, DatabasePath);

	/// <summary>Set when SDE runs from a temporary copy macOS made (App Translocation): the setup would stop working.</summary>
	public string TranslocationWarning => SdeMcpContext.TranslocationWarning(ProcessPath);

	public bool HasTranslocationWarning => TranslocationWarning != null;

	public ObservableCollection<AiChangeRowViewModel> Changes { get; } = new();

	public bool HasChanges => Changes.Count > 0;

	public bool HasNoChanges => Changes.Count == 0;

	/// <summary>Reads the switch and the recent changes again (an assistant may have changed something meanwhile).</summary>
	public void Refresh()
	{
		_loading = true;
		AllowEdits = AiAssistantSettings.AllowEdits(_app.Store);
		_loading = false;
		HashSet<string> reverted = Reverted();
		Changes.Clear();
		foreach (AiChangeBatch batch in AiAssistantSettings.RecentChanges(_app.Store))
		{
			Changes.Add(new AiChangeRowViewModel(batch, reverted.Contains(batch.BatchId), RevertAsync));
		}
		OnPropertyChanged(nameof(HasChanges));
		OnPropertyChanged(nameof(HasNoChanges));
		OnPropertyChanged(nameof(DatabasePath));
		OnPropertyChanged(nameof(ClaudeCodeCommand));
		OnPropertyChanged(nameof(ClaudeDesktopConfig));
		OnPropertyChanged(nameof(TranslocationWarning));
		OnPropertyChanged(nameof(HasTranslocationWarning));
	}

	[RelayCommand]
	private async Task CopyClaudeCode()
	{
		await _app.Dialogs.CopyTextAsync(ClaudeCodeCommand);
		_app.Dialogs.Notify("Command copied", "Paste it into Terminal and press Return. Claude Code can then use this database.");
	}

	[RelayCommand]
	private async Task CopyClaudeDesktop()
	{
		await _app.Dialogs.CopyTextAsync(ClaudeDesktopConfig);
		_app.Dialogs.Notify("Setup copied", "In Claude Desktop choose Settings, Developer, Edit Config, add it to the file, then restart Claude Desktop.");
	}

	/// <summary>
	/// Puts back what one AI call changed (a new entry in History). Values someone changed again since, and locked
	/// assessments, are left alone. The open editor is saved first and catches up.
	/// </summary>
	private async Task RevertAsync(AiChangeRowViewModel row)
	{
		if (!await _app.Dialogs.ConfirmAsync("Revert this change?",
			$"{row.Summary}. SDE puts back the values from before. Anything changed again since is left as it is.", "Revert"))
		{
			return;
		}
		BulkResult result;
		try
		{
			result = _app.Writes.Write(row.Batch.AssessmentIds, () => AiAssistantSettings.Revert(_app.Store, row.Batch.BatchId, _app.UserName));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't revert the change", ex);
			return;
		}
		HashSet<string> reverted = Reverted();
		reverted.Add(row.Batch.BatchId);
		_app.Store.SetSetting(RevertedKey, reverted.ToList());
		row.IsReverted = true;
		string message = result.Changed == 1 ? "Put back 1 value." : $"Put back {result.Changed} values.";
		if (result.ChangedSince > 0)
		{
			message += result.ChangedSince == 1 ? " 1 was changed again since and was left as it is." : $" {result.ChangedSince} were changed again since and were left as they are.";
		}
		if (result.Locked.Count > 0)
		{
			message += " Locked assessments were skipped.";
		}
		_app.Dialogs.Notify("Change reverted", message);
	}

	private HashSet<string> Reverted() => (_app.Store.GetSetting<List<string>>(RevertedKey) ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
}
