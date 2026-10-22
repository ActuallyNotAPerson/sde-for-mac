using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.Views;

[assembly: AvaloniaTestApplication(typeof(Sde.App.Tests.TestAppBuilder))]
[assembly: Avalonia.Headless.XUnit.AvaloniaTestFramework]

namespace Sde.App.Tests;

public static class TestAppBuilder
{
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
		.UseSkia()
		.WithInterFont()
		.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
		.AfterSetup(_ => Logger.Sink = BindingErrors.Instance);
}

/// <summary>Collects Avalonia binding / XAML errors so tests can assert there are none.</summary>
public sealed class BindingErrors : ILogSink
{
	public static readonly BindingErrors Instance = new();

	public ConcurrentQueue<string> Messages { get; } = new();

	public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area is LogArea.Binding or LogArea.Property or "XamlIl";

	public void Log(LogEventLevel level, string area, object source, string messageTemplate) => Messages.Enqueue($"[{area}] {messageTemplate} ({source})");

	public void Log(LogEventLevel level, string area, object source, string messageTemplate, params object[] propertyValues) =>
		Messages.Enqueue($"[{area}] {messageTemplate} | {string.Join(", ", propertyValues)} ({source})");
}

/// <summary>IDialogs for tests: records what the app asked for and returns scripted answers.</summary>
public sealed class TestDialogs : IDialogs
{
	public List<string> Notifications { get; } = new();
	public List<string> Opened { get; } = new();
	public Queue<object> Answers { get; } = new();

	public Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, string filterName, params string[] patterns) =>
		Task.FromResult(Answers.TryDequeue(out object a) && a is IReadOnlyList<string> files ? files : (IReadOnlyList<string>)Array.Empty<string>());

	public Task<string> PickFolderAsync(string title) => Task.FromResult(Answers.TryDequeue(out object a) ? a as string : null);

	public Task<string> SaveFileAsync(string title, string suggestedName, string filterName, string extension) => Task.FromResult(Answers.TryDequeue(out object a) ? a as string : null);

	public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) => Task.FromResult(!Answers.TryDequeue(out object a) || a is true);

	public void Notify(string title, string message, bool isError = false) => Notifications.Add((isError ? "ERROR " : "") + title + ": " + message);

	public void Open(string path) => Opened.Add(path);

	public void Reveal(string path) => Opened.Add("reveal:" + path);
}

/// <summary>The real main window and view models over a temporary database, rendered off-screen.</summary>
public sealed class UiSession : IDisposable
{
	private readonly TempDatabase _db;

	public UiSession(ThemeVariant theme = null, int width = 1440, int height = 900)
	{
		_db = new TempDatabase();
		Environment.SetEnvironmentVariable("SDE_DATABASE", _db.Database.FilePath);
		Application.Current!.RequestedThemeVariant = theme ?? ThemeVariant.Light;
		Dialogs = new TestDialogs();
		Services = new AppServices(Dialogs);
		// A made-up name: without one, edit history, "deleted by" and locks carry the Mac account's full name, which must
		// never reach a screenshot in the docs.
		Services.SetYourName("Reviewer A");
		Main = new MainViewModel(Services);
		Window = new MainWindow { DataContext = Main, Width = width, Height = height };
		Window.Show();
		Pump();
	}

	public TestDialogs Dialogs { get; }
	public AppServices Services { get; }
	public MainViewModel Main { get; }
	public MainWindow Window { get; }
	public Sde.Data.SdeStore Store => Services.Store;
	public string Folder => _db.Folder;

	/// <summary>Lets layout, bindings and timers run.</summary>
	public void Pump()
	{
		for (int i = 0; i < 3; i++)
		{
			Dispatcher.UIThread.RunJobs();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		}
	}

	/// <summary>
	/// Renders the window to a PNG (for looking at the UI and for docs). The docs copies are macOS renders, so on other
	/// systems the PNG goes to a temporary folder unless SDE_WRITE_SCREENSHOTS=1.
	/// </summary>
	public string Snapshot(string name)
	{
		Pump();
		bool forDocs = OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SDE_WRITE_SCREENSHOTS") == "1";
		string dir = forDocs ? Path.Combine(TestPaths.Root, "docs", "screenshots") : Path.Combine(Path.GetTempPath(), "sde-screenshots");
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, name + ".png");
		using WriteableBitmap frame = Window.CaptureRenderedFrame();
		frame!.Save(path, PngBitmapEncoderOptions.Default);
		return path;
	}

	public void Dispose()
	{
		Main.Editor?.Save();
		Window.Close();
		Services.Close();
		Environment.SetEnvironmentVariable("SDE_DATABASE", null);
		_db.Dispose();
	}
}
