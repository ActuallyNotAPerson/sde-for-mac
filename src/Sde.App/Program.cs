using System.Globalization;
using Avalonia;

namespace Sde.App;

internal sealed class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		// SDE is a US tool; dates and dollars are shown in US format regardless of the Mac's region.
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
		CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Services.AppLog.Write("Unhandled exception (the app is closing)", e.ExceptionObject as Exception);
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App>()
			.UsePlatformDetect()
#if DEBUG
			.WithDeveloperTools()
#endif
			.LogToTrace();
}
