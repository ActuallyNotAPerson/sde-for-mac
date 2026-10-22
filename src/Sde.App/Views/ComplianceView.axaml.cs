using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>The Compliance page: the cases with their filters, headline numbers and exports.</summary>
public partial class ComplianceView : UserControl
{
	public ComplianceView() => InitializeComponent();

	private ComplianceViewModel Vm => (ComplianceViewModel)DataContext;

	/// <summary>Double-clicking a row (not a column header) opens its case.</summary>
	private void OnCasesDoubleTapped(object sender, TappedEventArgs e)
	{
		if (Vm.Selected != null && e.Source is Control c && c.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) == null)
		{
			Vm.OpenSelectedCommand.Execute(null);
		}
	}

	/// <summary>Return opens the selected case.</summary>
	private void OnCasesKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && Vm.Selected != null)
		{
			Vm.OpenSelectedCommand.Execute(null);
			e.Handled = true;
		}
	}
}
