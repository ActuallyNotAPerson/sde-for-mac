using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sde.App.ViewModels.Shell;

namespace Sde.App.Views;

/// <summary>The ⌘K palette's keys (↑ ↓ Return Esc in the search box) and clicks on a result.</summary>
public partial class CommandPaletteView : UserControl
{
	private CommandPaletteViewModel _subscribed;

	public CommandPaletteView()
	{
		InitializeComponent();
		TextBox box = this.FindControl<TextBox>("PaletteBox");
		box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
		ListBox list = this.FindControl<ListBox>("PaletteList");
		list.AddHandler(TappedEvent, OnResultTapped, RoutingStrategies.Bubble);
		list.KeyDown += OnListKeyDown;
	}

	private CommandPaletteViewModel Vm => DataContext as CommandPaletteViewModel;

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_subscribed != null)
		{
			_subscribed.Opened -= FocusBox;
			_subscribed.PropertyChanged -= OnVmChanged;
		}
		_subscribed = Vm;
		if (_subscribed != null)
		{
			_subscribed.Opened += FocusBox;
			_subscribed.PropertyChanged += OnVmChanged;
		}
	}

	/// <summary>Puts the cursor in the search box once the palette is on screen.</summary>
	private void FocusBox() => Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("PaletteBox")?.Focus(), DispatcherPriority.Background);

	/// <summary>Keeps the chosen result in view as ↑ and ↓ move through a long list.</summary>
	private void OnVmChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(CommandPaletteViewModel.Selected) && Vm?.Selected != null)
		{
			this.FindControl<ListBox>("PaletteList")?.ScrollIntoView(Vm.Selected);
		}
	}

	private void OnBoxKeyDown(object sender, KeyEventArgs e)
	{
		if (Vm == null)
		{
			return;
		}
		switch (e.Key)
		{
			case Key.Down:
				Vm.Move(1);
				e.Handled = true;
				break;
			case Key.Up:
				Vm.Move(-1);
				e.Handled = true;
				break;
			case Key.Enter:
				Vm.Run(null);
				e.Handled = true;
				break;
			case Key.Escape:
				Vm.Close();
				e.Handled = true;
				break;
		}
	}

	private void OnListKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && Vm != null)
		{
			Vm.Run(null);
			e.Handled = true;
		}
	}

	private void OnResultTapped(object sender, TappedEventArgs e)
	{
		if (e.Source is Control source && source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is PaletteItem item)
		{
			Vm?.Run(item);
		}
	}
}
