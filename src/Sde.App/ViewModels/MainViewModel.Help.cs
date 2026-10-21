using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.ViewModels;

/// <summary>The Help menu: SDE Help, the manual, the sample database and the walkthrough.</summary>
public sealed partial class MainViewModel
{
	/// <summary>The open database is the practice one; every page shows a banner saying so.</summary>
	public bool IsSampleDatabase => _app.IsSample;

	/// <summary>Help, then SDE Help: every field's help in a searchable list.</summary>
	[RelayCommand]
	private void ShowHelp() => _ = Sheets.ShowAsync(new HelpIndexSheetViewModel(_app));

	/// <summary>Help, then FEMA SDE User Manual: the whole manual, or where to read it when the PDF is not here.</summary>
	[RelayCommand]
	private void OpenManual()
	{
		if (!_app.Manual.OpenFull())
		{
			_ = Sheets.ShowAsync(new ManualMissingSheetViewModel(_app));
		}
	}

	/// <summary>
	/// Help, then Open Sample Database: makes a fresh practice database (after asking, because it replaces the last one)
	/// and opens it.
	/// </summary>
	[RelayCommand]
	public async Task OpenSampleDatabase()
	{
		Editor?.Save();
		if (!await _app.Dialogs.ConfirmAsync("Open the sample database?",
			"This opens a practice database of made-up structures. It is rebuilt each time, so anything you changed in it before is discarded. Your own database is not touched, and comes back when you leave the sample.",
			"Open Sample"))
		{
			return;
		}
		try
		{
			_app.Open(SampleDatabase.CreateFresh(_app.SampleFolder));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			_app.Errors.Show("Couldn't open the sample database", ex);
		}
	}

	/// <summary>The banner's "Back to my database": reopens the database SDE had before the sample.</summary>
	[RelayCommand]
	private void LeaveSampleDatabase()
	{
		Editor?.Save();
		string path = _app.SampleReturnPath is string back && File.Exists(back) ? back
			: _app.Settings.DatabasePath is string p && File.Exists(p) ? p : Sde.Data.SdeDatabase.DefaultPath;
		try
		{
			_app.Open(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			_app.Errors.Show("Couldn't open your database", ex);
		}
	}

	/// <summary>Help, then Show the Walkthrough. Choosing "Open the sample database" there opens it.</summary>
	[RelayCommand]
	public async Task ShowWalkthrough()
	{
		object result = await Sheets.ShowAsync(new WalkthroughSheetViewModel());
		if (result as string == WalkthroughSheetViewModel.OpenSampleResult)
		{
			await OpenSampleDatabase();
		}
	}

	/// <summary>On launch: shows the walkthrough the first time only (see <see cref="AppServices.ShouldShowWalkthrough"/>).</summary>
	public Task ShowWalkthroughIfFirstRun()
	{
		if (!_app.ShouldShowWalkthrough)
		{
			return Task.CompletedTask;
		}
		_app.Settings.SeenWalkthrough = true;
		try
		{
			_app.Settings.Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Write("Couldn't remember that the walkthrough was shown", ex);
		}
		return ShowWalkthrough();
	}
}
