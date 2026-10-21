using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Sde.App.Services;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>The main window; also provides the app's dialogs (file pickers, confirmations, notifications).</summary>
public partial class MainWindow : Window, IDialogs
{
	private WindowNotificationManager _notifications;

	public MainWindow()
	{
		InitializeComponent();
	}

	// ───────────── Edit menu: acts on the text box that has the cursor ─────────────

	private TextBox FocusedTextBox => FocusManager?.GetFocusedElement() as TextBox;

	private void OnUndo(object sender, EventArgs e)
	{
		if (FocusedTextBox is TextBox box && box.CanUndo)
		{
			box.Undo();
		}
		else
		{
			(DataContext as MainViewModel)?.UndoCommand.Execute(null);
		}
	}

	private void OnRedo(object sender, EventArgs e)
	{
		if (FocusedTextBox is TextBox box && box.CanRedo)
		{
			box.Redo();
		}
		else
		{
			(DataContext as MainViewModel)?.RedoCommand.Execute(null);
		}
	}

	private void OnCut(object sender, EventArgs e) => FocusedTextBox?.Cut();

	private void OnCopy(object sender, EventArgs e) => FocusedTextBox?.Copy();

	private void OnPaste(object sender, EventArgs e) => FocusedTextBox?.Paste();

	private void OnSelectAll(object sender, EventArgs e) => FocusedTextBox?.SelectAll();

	// ───────────── Window menu ─────────────

	private void OnMinimize(object sender, EventArgs e) => WindowState = WindowState.Minimized;

	private void OnZoom(object sender, EventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

	// ───────────── File, then Open Recent ─────────────

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (DataContext is MainViewModel main)
		{
			// Others' saves are checked for while the window is active (and at once when it becomes active).
			Activated += (_, _) => main.WindowActivated(true);
			Deactivated += (_, _) => main.WindowActivated(false);
			main.PropertyChanged += (_, args) =>
			{
				if (args.PropertyName == nameof(MainViewModel.RecentDatabases))
				{
					RebuildRecent(main);
				}
			};
			RebuildRecent(main);
		}
	}

	private void RebuildRecent(MainViewModel main)
	{
		NativeMenu menu = NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "File")?.Menu?
			.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Open Recent")?.Menu;
		if (menu == null)
		{
			return;
		}
		menu.Items.Clear();
		foreach (string path in main.RecentDatabases)
		{
			menu.Items.Add(new NativeMenuItem(Path.GetFileName(path)) { Command = main.Settings.OpenRecentCommand, CommandParameter = path, ToolTip = path });
		}
		if (menu.Items.Count == 0)
		{
			menu.Items.Add(new NativeMenuItem("No Recent Databases") { IsEnabled = false });
		}
	}

	// ───────────── Remembering where the window was ─────────────

	/// <summary>Puts the window where it was last time, if that is still on a screen.</summary>
	public void Restore(WindowPlacement placement)
	{
		if (placement == null || placement.Width < MinWidth || placement.Height < MinHeight)
		{
			return;
		}
		PixelPoint at = new PixelPoint(placement.X, placement.Y);
		bool onScreen = Screens.All.Any(screen => screen.WorkingArea.Contains(at));
		Width = placement.Width;
		Height = placement.Height;
		if (onScreen)
		{
			WindowStartupLocation = WindowStartupLocation.Manual;
			Position = at;
		}
		if (placement.Maximized)
		{
			WindowState = WindowState.Maximized;
		}
	}

	public WindowPlacement Placement() => new WindowPlacement
	{
		X = Position.X,
		Y = Position.Y,
		Width = Bounds.Width,
		Height = Bounds.Height,
		Maximized = WindowState == WindowState.Maximized,
	};

	protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
	{
		base.OnApplyTemplate(e);
		_notifications = new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
	}

	public async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, string filterName, params string[] patterns)
	{
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = title,
			AllowMultiple = multiple,
			FileTypeFilter = patterns.Length == 0 ? null : new[] { new FilePickerFileType(filterName) { Patterns = patterns } },
		});
		return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
	}

	public async Task<string> PickFolderAsync(string title)
	{
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
		return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
	}

	public async Task<string> SaveFileAsync(string title, string suggestedName, string filterName, string extension)
	{
		IStorageFile file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = title,
			SuggestedFileName = suggestedName,
			DefaultExtension = extension,
			FileTypeChoices = new[] { new FilePickerFileType(filterName) { Patterns = new[] { "*." + extension } } },
		});
		return file?.TryGetLocalPath();
	}

	public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
	{
		Window dialog = new Window
		{
			Title = title,
			Width = 440,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = (IBrush)this.FindResource("CardBg"),
		};
		Button cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
		Button ok = new Button { Content = confirmText, IsDefault = !destructive, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
		ok.Classes.Add(destructive ? "danger" : "primary");
		cancel.Click += (_, _) => dialog.Close(false);
		ok.Click += (_, _) => dialog.Close(true);
		dialog.Content = new StackPanel
		{
			Margin = new Thickness(24, 20),
			Spacing = 14,
			Children =
			{
				new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
				new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
				new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
			},
		};
		return await dialog.ShowDialog<bool>(this);
	}

	public void Notify(string title, string message, bool isError = false) =>
		_notifications?.Show(new Notification(title, message, isError ? NotificationType.Error : NotificationType.Success, TimeSpan.FromSeconds(isError ? 10 : 5)));

	public void Open(string path) => Process.Start(new ProcessStartInfo("open") { ArgumentList = { path }, UseShellExecute = false });

	public void Reveal(string path) => Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path }, UseShellExecute = false });
}

/// <summary>Small converters for the sheets.</summary>
public static class SheetConverters
{
	public static readonly Avalonia.Data.Converters.IValueConverter DetailsToggle =
		new Avalonia.Data.Converters.FuncValueConverter<bool, string>(shown => shown ? "Hide details" : "Show details");
}
