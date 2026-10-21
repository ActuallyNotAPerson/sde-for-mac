using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sde.App.ViewModels;

namespace Sde.App.Views;

public partial class ImportExportView : UserControl
{
	public ImportExportView()
	{
		InitializeComponent();
		DragDrop.SetAllowDrop(this, true);
		AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: 1 } ? DragDropEffects.Copy : DragDropEffects.None);
		AddHandler(DragDrop.DropEvent, OnDrop);
	}

	private async void OnDrop(object sender, DragEventArgs e)
	{
		if (e.DataTransfer.TryGetFiles() is { Length: 1 } files && files[0].TryGetLocalPath() is string path && DataContext is ImportExportViewModel vm)
		{
			await vm.LoadPendingAsync(path);
		}
	}
}
