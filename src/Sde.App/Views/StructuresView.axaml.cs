using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Sde.App.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sde.App.ViewModels;

namespace Sde.App.Views;

public partial class StructuresView : UserControl
{
	private StructuresViewModel _subscribed;

	/// <summary>Each column's header text, as written in the view (the sorted one also shows a caret).</summary>
	private readonly Dictionary<DataGridColumn, string> _headers = new();

	public StructuresView()
	{
		InitializeComponent();
		// A right-click selects the row first, so the row menu acts on the row that was clicked.
		Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
	}

	private StructuresViewModel Vm => (StructuresViewModel)DataContext;

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_subscribed != null)
		{
			_subscribed.FocusSearchRequested -= FocusSearch;
			_subscribed.ColumnsChanged -= ApplyColumns;
			_subscribed.PropertyChanged -= OnVmPropertyChanged;
		}
		_subscribed = DataContext as StructuresViewModel;
		if (_subscribed != null)
		{
			_subscribed.FocusSearchRequested += FocusSearch;
			_subscribed.ColumnsChanged += ApplyColumns;
			_subscribed.PropertyChanged += OnVmPropertyChanged;
			ApplyColumns();
		}
	}

	private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(StructuresViewModel.SortKey) or nameof(StructuresViewModel.SortDescending))
		{
			ApplyColumns();
		}
	}

	/// <summary>Shows the chosen columns and marks the sorted one with a caret (up for ascending, down for descending).</summary>
	private void ApplyColumns()
	{
		if (Vm == null)
		{
			return;
		}
		foreach (DataGridColumn c in Grid.Columns)
		{
			string key = c.SortMemberPath;
			if (key == null)
			{
				continue;
			}
			c.IsVisible = Vm.IsColumnVisible(key);
			if (!_headers.TryGetValue(c, out string text))
			{
				text = c.Header as string;
				_headers[c] = text;
			}
			c.Header = Vm.SortKey == key ? SortedHeader(text, Vm.SortDescending) : text;
		}
	}

	/// <summary>A sorted column's header: its text and a caret in the accent colour.</summary>
	private static Control SortedHeader(string text, bool descending) => new StackPanel
	{
		Orientation = Orientation.Horizontal,
		Spacing = 4,
		Children =
		{
			new TextBlock { Text = text, Classes = { "sortedHeader" }, VerticalAlignment = VerticalAlignment.Center },
			new Icon { Kind = descending ? "CaretDown" : "CaretUp", Size = 12, Classes = { "accent" }, VerticalAlignment = VerticalAlignment.Center },
		},
	};

	/// <summary>Header clicks sort in the view model (so the list's order is also the order reports print in).</summary>
	private void OnSorting(object sender, DataGridColumnEventArgs e)
	{
		e.Handled = true;
		if (e.Column.SortMemberPath is string key)
		{
			Vm.SortBy(key);
		}
	}

	private void FocusSearch()
	{
		TextBox box = this.FindControl<TextBox>("SearchBox");
		box?.Focus();
		box?.SelectAll();
	}

	private void OnGridPointerPressed(object sender, PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(Grid).Properties.IsRightButtonPressed || e.Source is not Control source)
		{
			return;
		}
		if (source.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is StructureRowViewModel row)
		{
			Vm.Selected = row;
		}
	}

	private void OnRowDoubleTapped(object sender, TappedEventArgs e)
	{
		if (Vm.Selected != null && e.Source is Control c && c.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) == null)
		{
			Vm.OpenCommand.Execute(Vm.Selected);
		}
	}

	private void OnListKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && Vm.Selected != null)
		{
			Vm.OpenCommand.Execute(Vm.Selected);
			e.Handled = true;
		}
	}
}
