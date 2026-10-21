using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>One thing in Recently deleted.</summary>
public sealed class DeletedItemViewModel(DeletedItemRecord item)
{
	public DeletedItemRecord Item { get; } = item;

	public string Label => Item.Label;

	public string Kind => Item.Value switch
	{
		DeletedKind.Assessment => "Assessment",
		DeletedKind.Photo => "Photo",
		_ => "Structure",
	};

	public string When => $"Deleted {Item.DeletedOn:MMM d, yyyy h:mm tt}" + (string.IsNullOrEmpty(Item.DeletedBy) ? "" : " by " + Item.DeletedBy);

	public string DaysLeft
	{
		get
		{
			int days = Math.Max(0, (int)Math.Ceiling((Item.PurgeAfter - DateTime.Now).TotalDays));
			return days == 1 ? "Removed for good tomorrow" : $"Removed for good in {days} days";
		}
	}
}

/// <summary>Recently deleted: restore something, or remove it for good now.</summary>
public sealed partial class TrashViewModel(AppServices app) : ViewModelBase
{
	public ObservableCollection<DeletedItemViewModel> Items { get; } = new();

	public bool IsEmpty => Items.Count == 0;

	public string Intro => $"Deleted structures, assessments and photos stay here for {SdeStore.KeepDays} days, then they are removed for good. They aren't in lists, reports or exports while they're here.";

	public void Reload()
	{
		Items.Clear();
		foreach (DeletedItemRecord r in app.Store.GetDeletedItems())
		{
			Items.Add(new DeletedItemViewModel(r));
		}
		OnPropertyChanged(nameof(IsEmpty));
	}

	[RelayCommand]
	private void Restore(DeletedItemViewModel item)
	{
		try
		{
			app.Writes.Write(item.Item.AssessmentID is Guid id ? new[] { id } : null, () => app.Store.Restore(item.Item.ItemKey));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			app.Errors.Show("Couldn't restore " + item.Label, ex);
		}
		Reload();
	}

	[RelayCommand]
	private async Task DeleteNow(DeletedItemViewModel item)
	{
		if (!await app.Dialogs.ConfirmAsync("Delete for good?", $"{item.Label}\n\nThis can't be undone.", "Delete", destructive: true))
		{
			return;
		}
		app.Store.Purge(item.Item.ItemKey);
		Reload();
	}

	[RelayCommand]
	private async Task EmptyAll()
	{
		if (IsEmpty || !await app.Dialogs.ConfirmAsync("Empty Recently deleted?", $"The {Items.Count} item{(Items.Count == 1 ? "" : "s")} here will be removed for good. This can't be undone.", "Empty", destructive: true))
		{
			return;
		}
		app.Store.EmptyTrash();
		Reload();
	}
}
