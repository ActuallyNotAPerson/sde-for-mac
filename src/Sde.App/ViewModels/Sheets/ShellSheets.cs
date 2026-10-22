using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// A question with two answers, shown as a small sheet over the window (the app's <see cref="IDialogs.ConfirmAsync"/>).
/// Closes with true when confirmed; Cancel and Esc give null, which the caller reads as no.
/// </summary>
public sealed partial class ConfirmSheetViewModel(string title, string message, string confirmText, bool destructive) : SheetViewModel(title)
{
	public string Message { get; } = message;

	public string ConfirmText { get; } = confirmText;

	/// <summary>The confirm button is red (deleting, restoring over the current data) rather than the accent.</summary>
	public bool IsDestructive { get; } = destructive;

	public bool IsNotDestructive => !IsDestructive;

	public override SheetSize Size => SheetSize.Small;

	[RelayCommand]
	private void Confirm() => Close(true);
}

/// <summary>What Move or copy database… should do.</summary>
public enum MoveOrCopyChoice
{
	None,
	/// <summary>Move it (with photos, documents and map layers) and open it from the new place.</summary>
	Move,
	/// <summary>Copy it to a new place and open the copy; the original stays as it is.</summary>
	CopyAndOpen,
	/// <summary>Save a backup copy in a folder and keep working in this one.</summary>
	Backup,
}

/// <summary>The database switcher's Move or copy database… sheet: three choices, then Continue.</summary>
public sealed partial class MoveOrCopySheetViewModel(string databaseName) : SheetViewModel("Move or copy the database")
{
	public string Intro { get; } = $"\"{databaseName}\" goes with its photos, documents and map layers, whichever you choose.";

	public override SheetSize Size => SheetSize.Medium;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsCopy), nameof(IsBackup))]
	public partial bool IsMove { get; set; } = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMove), nameof(IsBackup))]
	public partial bool IsCopy { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMove), nameof(IsCopy))]
	public partial bool IsBackup { get; set; }

	public MoveOrCopyChoice Choice => IsMove ? MoveOrCopyChoice.Move : IsCopy ? MoveOrCopyChoice.CopyAndOpen : IsBackup ? MoveOrCopyChoice.Backup : MoveOrCopyChoice.None;

	[RelayCommand]
	private void Continue() => Close(Choice);
}
