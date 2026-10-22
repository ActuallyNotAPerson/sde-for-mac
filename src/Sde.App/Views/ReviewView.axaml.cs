using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>The Review page: the queue with the selected assessment beside it, and the quality checks.</summary>
public partial class ReviewView : UserControl
{
	public ReviewView() => InitializeComponent();

	private ReviewViewModel Vm => (ReviewViewModel)DataContext;

	/// <summary>Double-clicking a row (not a column header) opens its assessment.</summary>
	private void OnQueueDoubleTapped(object sender, TappedEventArgs e)
	{
		if (Vm.Selected != null && e.Source is Control c && c.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) == null)
		{
			Vm.OpenSelectedCommand.Execute(null);
		}
	}

	/// <summary>Return opens the selected row's assessment.</summary>
	private void OnQueueKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && Vm.Selected != null)
		{
			Vm.OpenSelectedCommand.Execute(null);
			e.Handled = true;
		}
	}
}
