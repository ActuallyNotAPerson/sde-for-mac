using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>
/// Work packages (C8) on the field Mac: opening a ".sdework" file as a database, a banner on every page saying it is a
/// work package, and sending the work back to the office.
/// </summary>
public sealed partial class MainViewModel
{
	/// <summary>The open database's work package manifest, or null for an ordinary database.</summary>
	public WorkPackageManifest WorkPackage => WorkPackages.Manifest(_app.Store);

	public bool IsWorkPackage => WorkPackage != null;

	/// <summary>"A work package from sde.sqlite, checked out to Team North on Oct 22, 2026. …"</summary>
	public string WorkPackageText
	{
		get
		{
			WorkPackageManifest m = WorkPackage;
			if (m == null)
			{
				return null;
			}
			string to = string.IsNullOrWhiteSpace(m.CheckedOutTo) ? "" : ", checked out to " + m.CheckedOutTo;
			return $"A work package from {m.SourceDatabaseName}{to} on {m.CreatedOn.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}. "
				+ "When the field work is done, send it back to the office.";
		}
	}

	/// <summary>Database button, then Open a work package: unpacks it beside SDE's other databases and opens it.</summary>
	[RelayCommand]
	private async Task OpenWorkPackage()
	{
		IReadOnlyList<string> picked = await _app.Dialogs.PickFilesAsync("Open a work package", false, "SDE work package", "*" + WorkPackages.Extension);
		if (picked == null || picked.Count == 0)
		{
			return;
		}
		Editor?.Save();
		try
		{
			string database = WorkPackages.Open(picked[0], _app.WorkPackagesFolder);
			CloseEditor();
			_app.Open(database);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException)
		{
			_app.Errors.Show("Couldn't open the work package", ex);
		}
	}

	/// <summary>The banner's Send back: writes the field's work into a package file to check in at the office.</summary>
	[RelayCommand]
	private async Task SendBack()
	{
		WorkPackageManifest m = WorkPackage;
		if (m == null)
		{
			return;
		}
		Editor?.Save();
		string name = (string.IsNullOrWhiteSpace(m.CheckedOutTo) ? "Field" : m.CheckedOutTo.Trim()) + " work done" + WorkPackages.Extension;
		foreach (char c in Path.GetInvalidFileNameChars())
		{
			name = name.Replace(c, '-');
		}
		string path = await _app.Dialogs.SaveFileAsync("Send the work back", name, "SDE work package", WorkPackages.Extension.TrimStart('.'));
		if (path == null)
		{
			return;
		}
		try
		{
			WorkPackages.SendBack(_app.Store, path);
			_app.Dialogs.Notify("Ready to go back", $"Take {Path.GetFileName(path)} to the office. There, choose Check in a work package on Import & export.");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			_app.Errors.Show("Couldn't write the work package", ex);
		}
	}
}
