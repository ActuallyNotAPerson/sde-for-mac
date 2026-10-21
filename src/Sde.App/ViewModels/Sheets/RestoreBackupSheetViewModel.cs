using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>One backup in the list.</summary>
public sealed class BackupRowViewModel(BackupInfo backup)
{
	public BackupInfo Backup { get; } = backup;

	public string When => Backup.TakenOn.ToString("dddd, MMM d, yyyy 'at' h:mm tt");

	public string Detail => $"{Backup.Structures} structure{(Backup.Structures == 1 ? "" : "s")} · {Backup.Assessments} assessment{(Backup.Assessments == 1 ? "" : "s")}"
		+ (string.IsNullOrEmpty(Backup.Reason) ? "" : " · " + Backup.Reason switch
		{
			"opened" => "made when the database was opened",
			"quit" => "made on quitting",
			"before restore" => "made before a restore",
			_ => Backup.Reason,
		});
}

/// <summary>File, then Restore from Backup: pick an automatic backup and put the database back to how it was then.</summary>
public sealed partial class RestoreBackupSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;

	public RestoreBackupSheetViewModel(AppServices app)
		: base("Restore from a backup")
	{
		_app = app;
		foreach (BackupInfo b in app.Backups?.List() ?? new List<BackupInfo>())
		{
			Backups.Add(new BackupRowViewModel(b));
		}
		Selected = Backups.FirstOrDefault();
	}

	public ObservableCollection<BackupRowViewModel> Backups { get; } = new();

	public bool HasBackups => Backups.Count > 0;

	public string Intro => _app.Backups == null
		? "Automatic backups are off for this database (it was opened with SDE_DATABASE)."
		: $"SDE backs up this database when you first open it each day and when you quit, and keeps the newest {BackupService.Keep}. Restoring replaces everything with the backup; what's there now is backed up first, so you can go back.";

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
	public partial BackupRowViewModel Selected { get; set; }

	[RelayCommand(CanExecute = nameof(CanRestore))]
	private async Task Restore()
	{
		BackupRowViewModel chosen = Selected;
		if (!await _app.Dialogs.ConfirmAsync("Restore this backup?", $"Everything in the database will be put back to {chosen.When}. What's there now is backed up first.", "Restore", destructive: true))
		{
			return;
		}
		try
		{
			_app.RestoreBackup(chosen.Backup);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't restore the backup", ex);
			return;
		}
		Close(chosen.Backup);
		_app.Dialogs.Notify("Backup restored", $"The database is as it was on {chosen.When}.");
	}

	private bool CanRestore() => Selected != null;

	[RelayCommand]
	private void ShowFolder()
	{
		if (_app.Backups != null)
		{
			Directory.CreateDirectory(_app.Backups.BackupsFolder);
			_app.Dialogs.Reveal(_app.Backups.BackupsFolder);
		}
	}
}
