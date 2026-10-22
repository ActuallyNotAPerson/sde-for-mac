using System.ComponentModel;
using Avalonia.Controls;
using Sde.App.ViewModels;

namespace Sde.App.Views;

public partial class SettingsView : UserControl
{
	private SettingsViewModel _subscribed;

	public SettingsView() => InitializeComponent();

	/// <summary>Each category opens at its top.</summary>
	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_subscribed != null)
		{
			_subscribed.PropertyChanged -= OnVmPropertyChanged;
		}
		_subscribed = DataContext as SettingsViewModel;
		if (_subscribed != null)
		{
			_subscribed.PropertyChanged += OnVmPropertyChanged;
		}
	}

	private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SettingsViewModel.SelectedCategory))
		{
			this.FindControl<ScrollViewer>("SettingsScroller")?.ScrollToHome();
		}
	}
}
