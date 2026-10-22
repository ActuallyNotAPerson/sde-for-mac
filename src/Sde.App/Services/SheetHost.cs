using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sde.App.Services;

/// <summary>
/// A sheet: a panel shown over the window (bulk edit, copy from, error details, auto-fill…). Sheets are part of the
/// main window rather than separate windows, so keyboard focus, VoiceOver and the headless tests all see them.
/// </summary>
public abstract partial class SheetViewModel : ObservableObject
{
	private readonly TaskCompletionSource<object> _closed = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

	protected SheetViewModel(string title) => Title = title;

	public string Title { get; }

	/// <summary>Completes with the sheet's result when it closes (null when cancelled).</summary>
	public Task<object> Closed => _closed.Task;

	public bool IsClosed => _closed.Task.IsCompleted;

	/// <summary>Raised when the sheet closes, so the host can take it off the screen.</summary>
	public event Action<SheetViewModel> Closing;

	/// <summary>Closes the sheet with a result.</summary>
	public void Close(object result = null)
	{
		if (_closed.TrySetResult(result))
		{
			Closing?.Invoke(this);
		}
	}

	/// <summary>Esc and the Cancel button. A sheet that is busy can refuse by overriding <see cref="CanCancel"/>.</summary>
	[RelayCommand]
	public void Cancel()
	{
		if (CanCancel)
		{
			Close(null);
		}
	}

	public virtual bool CanCancel => true;

	/// <summary>The sheet's width: small (440), medium (600) or large (800); Auto lets the content decide.</summary>
	public virtual SheetSize Size => SheetSize.Auto;
}

/// <summary>Sheet widths (the SheetWidth tokens).</summary>
public enum SheetSize
{
	/// <summary>As wide as its content, between 440 and 760.</summary>
	Auto,
	Small,
	Medium,
	Large,
}

/// <summary>Shows sheets one on top of another; the window shows <see cref="Current"/> with the rest of the window dimmed.</summary>
public sealed partial class SheetHost : ObservableObject
{
	public ObservableCollection<SheetViewModel> Open { get; } = new();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSheet), nameof(CurrentTitle), nameof(CurrentSize))]
	public partial SheetViewModel Current { get; private set; }

	public bool HasSheet => Current != null;

	/// <summary>The current sheet's title ("" when none; the window binds to this rather than through a null sheet).</summary>
	public string CurrentTitle => Current?.Title ?? "";

	/// <summary>The current sheet's width, for the window's sheet chrome.</summary>
	public SheetSize CurrentSize => Current?.Size ?? SheetSize.Auto;

	/// <summary>Esc: cancels the current sheet, if any.</summary>
	[RelayCommand]
	private void CancelCurrent() => Current?.Cancel();

	/// <summary>Shows a sheet and completes when it closes, with its result.</summary>
	public Task<object> ShowAsync(SheetViewModel sheet)
	{
		sheet.Closing += Remove;
		Open.Add(sheet);
		Current = sheet;
		return sheet.Closed;
	}

	/// <summary>Shows a sheet and returns its result as <typeparamref name="T"/> (default when cancelled).</summary>
	public async Task<T> ShowAsync<T>(SheetViewModel sheet)
	{
		object result = await ShowAsync(sheet);
		return result is T t ? t : default;
	}

	private void Remove(SheetViewModel sheet)
	{
		sheet.Closing -= Remove;
		Open.Remove(sheet);
		Current = Open.Count == 0 ? null : Open[^1];
	}
}
