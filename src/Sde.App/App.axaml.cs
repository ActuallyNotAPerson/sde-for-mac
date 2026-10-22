using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.Views;

namespace Sde.App;

public partial class App : Application
{
	private MainViewModel _main;

	/// <summary>The application menu's About and Settings…: the Settings page (it shows the version).</summary>
	private void OnAboutClicked(object sender, EventArgs e) => _main?.ShowCommand.Execute(Page.Settings);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			MainWindow window = new MainWindow();
			AppServices services = new AppServices(window);
			MainViewModel main = new MainViewModel(services);
			window.DataContext = main;
			_main = main;
			window.Restore(services.Settings.Window);
			bool mapEditsSettled = false;
			window.Closing += (_, e) =>
			{
				if (main.Map.HasUnsavedEdits && !mapEditsSettled)
				{
					// Unsaved map edits: ask first (save, throw away, or keep the window open), then close again.
					e.Cancel = true;
					Dispatcher.UIThread.Post(async () =>
					{
						if (await main.Map.ConfirmLeaveEditsAsync())
						{
							mapEditsSettled = true;
							window.Close();
						}
					});
					return;
				}
				main.Editor?.Save();
				services.Settings.Window = window.Placement();
				services.Settings.Save();
				services.BackUpOnQuit();
			};
			window.Closed += (_, _) => services.PhoneForm.StopForQuit();
			desktop.MainWindow = window;
			window.Opened += (_, _) => _ = main.ShowWalkthroughIfFirstRun();

			// Anything not caught where it happened: logged and shown in plain English, and the app keeps running.
			Dispatcher.UIThread.UnhandledException += (_, e) =>
			{
				e.Handled = true;
				services.Errors.Show("Something went wrong", e.Exception);
			};
			TaskScheduler.UnobservedTaskException += (_, e) =>
			{
				AppLog.Write("Unobserved task exception", e.Exception);
				e.SetObserved();
			};
		}
		base.OnFrameworkInitializationCompleted();
	}
}
