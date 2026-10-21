using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;

namespace Sde.App.Views;

public partial class AssessmentEditorView : UserControl
{
	private AssessmentEditorViewModel _subscribed;

	public AssessmentEditorView()
	{
		InitializeComponent();
		// Photos can be dropped anywhere on the editor (the Photos step shows the drop zone).
		DragDrop.SetAllowDrop(this, true);
		AddHandler(DragDrop.DragOverEvent, OnDragOver);
		AddHandler(DragDrop.DropEvent, OnDrop);
		// Seen before the slider handles arrow keys itself.
		AddHandler(KeyDownEvent, OnSliderKey, RoutingStrategies.Tunnel);
		// Photos are reordered by dragging them by their handle.
		AddHandler(PointerPressedEvent, OnPhotoHandlePressed, RoutingStrategies.Tunnel);
	}

	/// <summary>What a photo being dragged to a new place carries (only within this window).</summary>
	private static readonly DataFormat<PhotoViewModel> PhotoFormat = DataFormat.CreateInProcessFormat<PhotoViewModel>("sde-photo");

	/// <summary>Starts dragging a photo when its handle is pressed.</summary>
	private async void OnPhotoHandlePressed(object sender, PointerPressedEventArgs e)
	{
		if (e.Source is not Control source || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			return;
		}
		Control handle = source.Classes.Contains("dragHandle") ? source : source.FindAncestorOfType<Border>(includeSelf: false) is Border b && b.Classes.Contains("dragHandle") ? b : null;
		if (handle?.DataContext is not PhotoViewModel photo)
		{
			return;
		}
		DataTransfer data = new DataTransfer();
		data.Add(DataTransferItem.Create(PhotoFormat, photo));
		await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
	}

	/// <summary>The photo card under the pointer during a drag, if any.</summary>
	private static PhotoViewModel PhotoUnder(DragEventArgs e) =>
		(e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("photoCard"))?.DataContext as PhotoViewModel;

	private AssessmentEditorViewModel Vm => DataContext as AssessmentEditorViewModel;

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_subscribed != null)
		{
			_subscribed.PropertyChanged -= OnVmPropertyChanged;
			_subscribed.FocusFieldRequested -= FocusField;
		}
		_subscribed = Vm;
		if (_subscribed != null)
		{
			_subscribed.PropertyChanged += OnVmPropertyChanged;
			_subscribed.FocusFieldRequested += FocusField;
		}
	}

	private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
	{
		if (args.PropertyName == nameof(AssessmentEditorViewModel.SelectedSection))
		{
			this.FindControl<ScrollViewer>("StepScroller")?.ScrollToHome();
		}
	}

	/// <summary>
	/// Puts the cursor in a field's input and scrolls it into view. Runs after the step has been laid out, since
	/// choosing an issue usually switches steps first.
	/// </summary>
	private void FocusField(FieldViewModel field)
	{
		Dispatcher.UIThread.Post(() =>
		{
			Control holder = this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => ReferenceEquals(c.DataContext, field) && c.IsEffectivelyVisible);
			if (holder == null)
			{
				return;
			}
			Control input = holder.GetVisualDescendants().OfType<Control>()
				.FirstOrDefault(c => c.Focusable && c.IsEffectivelyEnabled && c is TextBox or ComboBox or CheckBox or CalendarDatePicker or NumericUpDown);
			holder.BringIntoView();
			(input ?? holder).Focus(NavigationMethod.Tab);
		}, DispatcherPriority.Background);
	}

	/// <summary>On a damage slider, 0 to 4 jump to 0, 25, 50, 75 and 100 percent.</summary>
	private void OnSliderKey(object sender, KeyEventArgs e)
	{
		if (e.KeyModifiers != KeyModifiers.None || e.Source is not Control source)
		{
			return;
		}
		Slider slider = source as Slider ?? source.FindAncestorOfType<Slider>();
		if (slider == null)
		{
			return;
		}
		int? step = e.Key switch
		{
			Key.D0 or Key.NumPad0 => 0,
			Key.D1 or Key.NumPad1 => 1,
			Key.D2 or Key.NumPad2 => 2,
			Key.D3 or Key.NumPad3 => 3,
			Key.D4 or Key.NumPad4 => 4,
			_ => null,
		};
		if (step is int n)
		{
			slider.Value = slider.Minimum + (slider.Maximum - slider.Minimum) * n / 4;
			e.Handled = true;
		}
	}

	private void OnDragOver(object sender, DragEventArgs e)
	{
		if (e.DataTransfer.Contains(PhotoFormat))
		{
			e.DragEffects = PhotoUnder(e) != null ? DragDropEffects.Move : DragDropEffects.None;
			return;
		}
		e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
	}

	private async void OnDrop(object sender, DragEventArgs e)
	{
		if (e.DataTransfer.TryGetValue(PhotoFormat) is PhotoViewModel dragged)
		{
			// Dropped on another photo: it takes that photo's place.
			if (PhotoUnder(e) is PhotoViewModel target && !ReferenceEquals(target, dragged) && Vm != null)
			{
				Vm.MovePhotoTo(dragged, Vm.Photos.IndexOf(target));
			}
			return;
		}
		IStorageItem[] files = e.DataTransfer.TryGetFiles();
		if (files == null || Vm == null)
		{
			return;
		}
		string[] images = files.Select(f => f.TryGetLocalPath())
			.Where(p => p != null && AssessmentEditorViewModel.PhotoPatterns.Any(pat => p.EndsWith(pat[1..], StringComparison.OrdinalIgnoreCase)))
			.ToArray();
		AssessmentEditorViewModel vm = Vm;
		if (images.Length > 0 && vm.Sections.OfType<PhotosSectionViewModel>().FirstOrDefault() is PhotosSectionViewModel photos)
		{
			vm.SelectedSection = photos;
		}
		await vm.AddPhotosAsync(images);
	}
}
