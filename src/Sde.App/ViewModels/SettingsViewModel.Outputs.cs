namespace Sde.App.ViewModels;

public sealed partial class SettingsViewModel
{
	private OutputSettingsViewModel _outputs;

	/// <summary>Substantial improvement, owner letters and cumulative damage (their own cards on the page).</summary>
	public OutputSettingsViewModel Outputs => _outputs ??= new OutputSettingsViewModel(_app);
}
