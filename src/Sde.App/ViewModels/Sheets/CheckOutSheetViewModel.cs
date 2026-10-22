using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Structures, Export, Work package for the field (C8): checks the assessments out into a ".sdework" file for a Mac in
/// the field. They become read-only here until the package comes back and is checked in. Final and Letter sent
/// assessments, and ones already checked out, are left out. Closes with a line saying what was written.
/// </summary>
public sealed partial class CheckOutSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly List<Guid> _ids;

	public CheckOutSheetViewModel(AppServices app, IReadOnlyList<Guid> assessmentIds) : base("Work package for the field")
	{
		_app = app;
		HashSet<Guid> checkedOut = app.Store.GetCheckOuts().Select(c => c.AssessmentID).ToHashSet();
		int locked = 0;
		int already = 0;
		_ids = new List<Guid>();
		foreach (Guid id in assessmentIds.Distinct())
		{
			if (checkedOut.Contains(id))
			{
				already++;
			}
			else if (app.Store.GetStatus(id).IsLocked)
			{
				locked++;
			}
			else
			{
				_ids.Add(id);
			}
		}
		Count = _ids.Count;
		string left = string.Join(" ", new[]
		{
			locked == 0 ? null : $"{locked} Final or Letter sent {(locked == 1 ? "is" : "are")} left out.",
			already == 0 ? null : $"{already} already checked out {(already == 1 ? "is" : "are")} left out.",
		}.Where(x => x != null));
		Summary = (Count == 1 ? "1 assessment" : $"{Count} assessments") + " with their structures, photos and documents." + (left.Length == 0 ? "" : " " + left);
		CheckedOutTo = "";
	}

	public override SheetSize Size => SheetSize.Small;

	/// <summary>How many assessments go into the package.</summary>
	public int Count { get; }

	public string Summary { get; }

	public bool CanCheckOut => Count > 0;

	/// <summary>Who takes it: a team or a person (kept with the check-out and shown in the editor).</summary>
	[ObservableProperty]
	public partial string CheckedOutTo { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; }

	public bool HasError => !string.IsNullOrEmpty(Error);

	[RelayCommand]
	private async Task Create()
	{
		if (Count == 0)
		{
			return;
		}
		string to = string.IsNullOrWhiteSpace(CheckedOutTo) ? null : CheckedOutTo.Trim();
		string suggested = (to ?? "Field") + " work package" + WorkPackages.Extension;
		foreach (char c in Path.GetInvalidFileNameChars())
		{
			suggested = suggested.Replace(c, '-');
		}
		string path = await _app.Dialogs.SaveFileAsync("Save the work package", suggested, "SDE work package", WorkPackages.Extension.TrimStart('.'));
		if (path == null)
		{
			return;
		}
		try
		{
			WorkPackageManifest manifest = _app.Writes.Write(_ids, () => WorkPackages.CheckOut(_app.Store, _ids, path, to));
			_app.RaiseDataChanged();
			Close($"Checked out {(manifest.Items.Count == 1 ? "1 assessment" : $"{manifest.Items.Count} assessments")} to {Path.GetFileName(path)}. They are read-only here until it comes back.");
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or AssessmentLockedException or AssessmentCheckedOutException)
		{
			Error = ex.Message;
		}
	}
}
