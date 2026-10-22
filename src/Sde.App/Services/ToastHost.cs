using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Controls;

namespace Sde.App.Services;

/// <summary>One toast: a title, a line of detail and an optional action (Undo), until it times out or is dismissed.</summary>
public sealed class ToastViewModel
{
	public ToastViewModel(string title, string message, Tone tone, string actionText, ICommand actionCommand, ICommand closeCommand)
	{
		Title = title;
		Message = message;
		Tone = tone;
		ActionText = actionText;
		ActionCommand = actionCommand;
		CloseCommand = closeCommand;
	}

	public string Title { get; }

	public string Message { get; }

	public Tone Tone { get; }

	public string ActionText { get; }

	public ICommand ActionCommand { get; }

	public ICommand CloseCommand { get; }
}

/// <summary>
/// The window's toasts, newest at the bottom, at most <see cref="MaxItems"/> at a time. A toast closes by itself after
/// a few seconds (errors stay longer), when its action is used, or when it is dismissed.
/// </summary>
public sealed class ToastHost
{
	public const int MaxItems = 3;

	public ObservableCollection<ToastViewModel> Items { get; } = new();

	/// <summary>
	/// Shows a toast. <paramref name="action"/> runs when its button (<paramref name="actionText"/>, such as "Undo") is
	/// pressed; <paramref name="duration"/> defaults to 5 seconds, or 10 for an error and 8 when there is an action.
	/// </summary>
	public ToastViewModel Show(string title, string message, Tone tone = Tone.Success, string actionText = null, Action action = null, TimeSpan? duration = null)
	{
		ToastViewModel toast = null;
		ICommand close = new RelayCommand(() => Dismiss(toast));
		ICommand run = action == null ? null : new RelayCommand(() =>
		{
			Dismiss(toast);
			action();
		});
		toast = new ToastViewModel(title, message, tone, action == null ? null : actionText, run, close);
		Items.Add(toast);
		while (Items.Count > MaxItems)
		{
			Items.RemoveAt(0);
		}
		TimeSpan after = duration ?? TimeSpan.FromSeconds(tone == Tone.Danger ? 10 : action != null ? 8 : 5);
		if (after > TimeSpan.Zero)
		{
			DispatcherTimer.RunOnce(() => Dismiss(toast), after);
		}
		return toast;
	}

	public void Dismiss(ToastViewModel toast)
	{
		if (toast != null)
		{
			Items.Remove(toast);
		}
	}
}
