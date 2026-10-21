using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;

namespace Sde.App;

internal sealed class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		// SDE is a US tool; dates and dollars are shown in US format regardless of the Mac's region.
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
		CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
		// "SDE mcp --db <path>": the server for AI assistants, on stdin and stdout. It runs before anything touches
		// Avalonia, so no window or Dock icon appears.
		if (args.Length > 0 && args[0] == Sde.Mcp.SdeMcpServer.Command)
		{
			return Sde.Mcp.SdeMcpServer.Run(args[1..], Services.AppLog.Folder);
		}
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Services.AppLog.Write("Unhandled exception (the app is closing)", e.ExceptionObject as Exception);
		StartApp(args);
		return 0;
	}

	/// <summary>Starts the app's windows. Kept out of <see cref="Main"/> so the AI assistant server never loads Avalonia.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void StartApp(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App>()
			.UsePlatformDetect()
#if DEBUG
			.WithDeveloperTools()
#endif
			.LogToTrace();
}
