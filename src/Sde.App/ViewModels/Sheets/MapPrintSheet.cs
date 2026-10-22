using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;

namespace Sde.App.ViewModels.Sheets;

/// <summary>Print, on the Map page: the page's title and whether it is printed portrait or landscape.</summary>
public sealed partial class PrintMapSheetViewModel(string title) : SheetViewModel("Print the map")
{
	/// <summary>What the sheet gives back.</summary>
	public sealed record Result(string Title, bool Landscape);

	public override SheetSize Size => SheetSize.Small;

	[ObservableProperty]
	public partial string MapTitle { get; set; } = title;

	/// <summary>0 portrait, 1 landscape.</summary>
	[ObservableProperty]
	public partial int OrientationIndex { get; set; } = 1;

	[RelayCommand]
	private void Print() => Close(new Result(string.IsNullOrWhiteSpace(MapTitle) ? "Map" : MapTitle.Trim(), OrientationIndex == 1));
}
