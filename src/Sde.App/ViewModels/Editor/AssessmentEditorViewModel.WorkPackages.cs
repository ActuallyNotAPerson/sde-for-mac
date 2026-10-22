using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Sheets;
using Sde.Data;

namespace Sde.App.ViewModels.Editor;

/// <summary>
/// Work packages (C8) in the editor: an assessment checked out to a field Mac is read-only here, with a banner saying
/// where it is and a way to take it back early; after that, values the field entered can be copied over from the
/// package field by field.
/// </summary>
public sealed partial class AssessmentEditorViewModel
{
	/// <summary>The check-out of this assessment, or null when it is here to edit.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsCheckedOut), nameof(IsEditable), nameof(CheckOutText))]
	public partial CheckOutRecord CheckOut { get; private set; }

	public bool IsCheckedOut => CheckOut != null;

	/// <summary>"Checked out to Team North on Oct 22, 2026 (Reviewer A), in a work package. …"</summary>
	public string CheckOutText => CheckOut == null ? null
		: "Checked out" + (string.IsNullOrWhiteSpace(CheckOut.CheckedOutTo) ? "" : " to " + CheckOut.CheckedOutTo)
			+ " on " + CheckOut.CheckedOutOn.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
			+ (string.IsNullOrWhiteSpace(CheckOut.CheckedOutBy) ? "" : $" ({CheckOut.CheckedOutBy})")
			+ ", in a work package. It can't change here until the package is checked back in.";

	private void LoadCheckOut() => CheckOut = IsDraft ? null : _app.Store.GetCheckOut(Assessment.AssessmentID);

	/// <summary>
	/// Takes the assessment back from its work package early, so it can change here again. The package can no longer
	/// bring it back; what the field did can still be copied over with Copy from a work package.
	/// </summary>
	[RelayCommand]
	private async Task TakeBack()
	{
		if (CheckOut == null)
		{
			return;
		}
		if (!await _app.Dialogs.ConfirmAsync("Take it back early?",
			"It can change here again, but its work package can no longer bring this assessment back. You can still copy what the field entered from the package, value by value, with Copy from a work package in the ⋯ menu.",
			"Take it back"))
		{
			return;
		}
		Guid id = Assessment.AssessmentID;
		try
		{
			_app.Writes.Write(new[] { id }, () => _app.Store.TakeBack(id));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't take it back", ex);
			return;
		}
		LoadCheckOut();
		_app.RaiseDataChanged();
	}

	/// <summary>⋯, then Copy from a work package: the field's values for this assessment, chosen one by one.</summary>
	[RelayCommand]
	private async Task CopyFromPackage()
	{
		if (IsDraft || !IsEditable)
		{
			return;
		}
		IReadOnlyList<string> picked = await _app.Dialogs.PickFilesAsync("Choose the work package", false, "SDE work package", "*" + WorkPackages.Extension);
		if (picked == null || picked.Count == 0)
		{
			return;
		}
		Save();
		PackageCompareSheetViewModel sheet;
		try
		{
			sheet = new PackageCompareSheetViewModel(_app, picked[0], Assessment.AssessmentID);
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException)
		{
			_app.Dialogs.Notify("Can't use that package", ex.Message, isError: true);
			return;
		}
		if (await _app.Sheets.ShowAsync(sheet) is int copied && copied > 0)
		{
			_app.Dialogs.Notify("Values copied", copied == 1 ? "1 value came over from the work package." : $"{copied} values came over from the work package.");
		}
	}
}
