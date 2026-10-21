using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.ViewModels;

public sealed partial class StructuresViewModel
{
	/// <summary>Export ▾, then Owner letters: a letter to the owner of each checked structure (or each one shown).</summary>
	[RelayCommand]
	private async Task OwnerLetters()
	{
		IReadOnlyList<Guid> ids = TargetIds();
		if (ids.Count == 0)
		{
			_app.Dialogs.Notify("No letters to write", "There are no assessments in the list. Add a structure, or clear the search.");
			return;
		}
		_main.Editor?.Save();   // letters use what's on screen, not the last autosave
		await _app.Sheets.ShowAsync(new OwnerLettersSheetViewModel(_app, ids));
	}
}
