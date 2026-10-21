using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sde.App.ViewModels;

namespace Sde.App.Views;

public partial class SourceImportView : UserControl
{
	public SourceImportView()
	{
		InitializeComponent();
		DragDrop.SetAllowDrop(this, true);
		AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: 1 } ? DragDropEffects.Copy : DragDropEffects.None);
		AddHandler(DragDrop.DropEvent, (_, e) =>
		{
			if (e.DataTransfer.TryGetFiles() is { Length: 1 } files && files[0].TryGetLocalPath() is string path && DataContext is SourceImportViewModel vm)
			{
				vm.OpenFile(path);
			}
		});
	}
}
