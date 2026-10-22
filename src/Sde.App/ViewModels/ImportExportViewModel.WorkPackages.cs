using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>One work package out in the field: who has it, since when, and how many assessments.</summary>
public sealed record CheckedOutPackage(Guid PackageId, string To, DateTime On, IReadOnlyList<Guid> AssessmentIds)
{
	/// <summary>"3 assessments, checked out to Team North on Oct 22, 2026".</summary>
	public string Text => (AssessmentIds.Count == 1 ? "1 assessment" : $"{AssessmentIds.Count} assessments")
		+ (string.IsNullOrWhiteSpace(To) ? "" : ", checked out to " + To)
		+ " on " + On.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

	public string TakeBackName => "Take back the work package of " + Text;
}

/// <summary>Import &amp; export, Work packages (C8): checking packages back in, and the packages still out.</summary>
public sealed partial class ImportExportViewModel
{
	public ObservableCollection<CheckedOutPackage> CheckedOut { get; } = new();

	public bool HasCheckedOut => CheckedOut.Count > 0;

	/// <summary>Reads the packages still out (after a check-out, a check-in or opening another database).</summary>
	public void RefreshWorkPackages()
	{
		CheckedOut.Clear();
		foreach (IGrouping<Guid, CheckOutRecord> g in _app.Store.GetCheckOuts().GroupBy(c => c.PackageID))
		{
			CheckOutRecord first = g.First();
			CheckedOut.Add(new CheckedOutPackage(g.Key, first.CheckedOutTo, first.CheckedOutOn, g.Select(c => c.AssessmentID).ToList()));
		}
		OnPropertyChanged(nameof(HasCheckedOut));
	}

	/// <summary>Brings a package back from the field: its assessments replace the ones here, with the field's history.</summary>
	[RelayCommand]
	private async Task CheckIn()
	{
		IReadOnlyList<string> picked = await _app.Dialogs.PickFilesAsync("Check in a work package", false, "SDE work package", "*" + WorkPackages.Extension);
		if (picked == null || picked.Count == 0)
		{
			return;
		}
		List<Guid> checkedOut = _app.Store.GetCheckOuts().Select(c => c.AssessmentID).ToList();
		CheckInResult result;
		try
		{
			result = _app.Writes.Write(checkedOut, () => WorkPackages.CheckIn(_app.Store, picked[0]));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException)
		{
			_app.Errors.Show("Couldn't check in the work package", ex);
			return;
		}
		_app.RaiseDataChanged();
		RefreshWorkPackages();
		List<string> lines = new List<string>
		{
			(result.CheckedIn == 1 ? "Checked in 1 assessment" : $"Checked in {result.CheckedIn} assessments")
				+ (result.Added == 0 ? "." : result.Added == 1 ? " and added 1 new one." : $" and added {result.Added} new ones."),
		};
		lines.AddRange(result.Refused.Select(r => "Not checked in: " + r));
		lines.AddRange(result.Warnings);
		LastResult = string.Join(" ", lines);
	}

	/// <summary>Takes every assessment of a package back early (the package can then no longer bring them back).</summary>
	[RelayCommand]
	private async Task TakeBackPackage(CheckedOutPackage package)
	{
		if (package == null)
		{
			return;
		}
		if (!await _app.Dialogs.ConfirmAsync("Take the work package back early?",
			$"{package.Text}. They can change here again, but the package can no longer bring them back. What the field entered can still be copied over, assessment by assessment.",
			"Take them back"))
		{
			return;
		}
		_app.Writes.Write(package.AssessmentIds, () =>
		{
			foreach (Guid id in package.AssessmentIds)
			{
				_app.Store.TakeBack(id);
			}
		});
		_app.RaiseDataChanged();
		RefreshWorkPackages();
		LastResult = $"Took back {package.Text}.";
	}
}
