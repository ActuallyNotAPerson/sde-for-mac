using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>What closing <see cref="EventSheetViewModel"/> with Delete returns.</summary>
public sealed record EventDeleted(Guid EventID);

/// <summary>
/// Adds or edits a disaster event: its name, kind, FEMA disaster number, dates, notes and the datum offset used to
/// convert high-water marks. Closes with the saved <see cref="EventRecord"/>, or <see cref="EventDeleted"/>.
/// </summary>
public sealed partial class EventSheetViewModel : SheetViewModel
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
	private readonly AppServices _app;
	private readonly EventRecord _existing;

	public EventSheetViewModel(AppServices app, EventRecord existing = null) : base(existing == null ? "New event" : "Edit event")
	{
		_app = app;
		_existing = existing;
		Name = existing?.Name;
		Kind = existing?.Kind ?? EventKinds.All[0];
		DRNumber = existing?.DRNumber;
		StartDate = existing?.StartDate;
		EndDate = existing?.EndDate;
		DatumOffset = existing?.DatumOffsetFt?.ToString("0.###", Us);
		Notes = existing?.Notes;
	}

	public override SheetSize Size => SheetSize.Medium;

	public IReadOnlyList<string> Kinds => EventKinds.All;

	public bool IsExisting => _existing != null;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	public partial string Name { get; set; }

	[ObservableProperty]
	public partial string Kind { get; set; }

	/// <summary>FEMA's disaster number, e.g. "DR-4321".</summary>
	[ObservableProperty]
	public partial string DRNumber { get; set; }

	[ObservableProperty]
	public partial DateTime? StartDate { get; set; }

	[ObservableProperty]
	public partial DateTime? EndDate { get; set; }

	/// <summary>Feet added to high-water marks in another vertical datum, as typed ("0.6", "-1.2"); blank for none.</summary>
	[ObservableProperty]
	public partial string DatumOffset { get; set; }

	[ObservableProperty]
	public partial string Notes { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; }

	public bool HasError => !string.IsNullOrEmpty(Error);

	[RelayCommand(CanExecute = nameof(HasName))]
	private void Save()
	{
		decimal? offset = null;
		if (!string.IsNullOrWhiteSpace(DatumOffset))
		{
			if (!decimal.TryParse(DatumOffset.Trim(), NumberStyles.Number, Us, out decimal d) || Math.Abs(d) > 20m)
			{
				Error = "The datum offset is a number of feet between -20 and 20, for example 0.6.";
				return;
			}
			offset = d;
		}
		EventRecord e = new EventRecord
		{
			EventID = _existing?.EventID ?? Guid.Empty,
			CreatedOn = _existing?.CreatedOn,
			CreatedBy = _existing?.CreatedBy,
			Name = Name,
			Kind = Kind,
			DRNumber = DRNumber,
			StartDate = StartDate?.Date,
			EndDate = EndDate?.Date,
			DatumOffsetFt = offset,
			Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
		};
		try
		{
			Close(_app.Store.SaveEvent(e));
		}
		catch (ArgumentException ex)
		{
			Error = ex.Message;
		}
	}

	private bool HasName() => !string.IsNullOrWhiteSpace(Name);

	/// <summary>Deletes the event after asking. Its assessments stay; they just no longer belong to it.</summary>
	[RelayCommand]
	private async Task Delete()
	{
		if (_existing == null)
		{
			return;
		}
		int count = _app.Store.AssessmentsInEvent(_existing.EventID).Count;
		string what = count == 0 ? "No assessments are in it." : $"Its {count:N0} assessment{(count == 1 ? " stays" : "s stay")} in the database, in no event.";
		if (await _app.Dialogs.ConfirmAsync($"Delete \"{_existing.Label}\"?", what, "Delete event", destructive: true))
		{
			_app.Store.DeleteEvent(_existing.EventID);
			Close(new EventDeleted(_existing.EventID));
		}
	}
}

/// <summary>One event in the Manage events list.</summary>
public sealed class EventRowViewModel(EventRecord e, int count, Func<EventRecord, Task> edit)
{
	public EventRecord Event { get; } = e;

	public string Name { get; } = e.Label;

	/// <summary>"Flood · Sep 14 to Sep 20, 2026 · 12 assessments".</summary>
	public string Detail { get; } = string.Join(" · ", new[] { e.Kind, e.ToDetails().DateRange, $"{count:N0} assessment{(count == 1 ? "" : "s")}" }
		.Where(x => !string.IsNullOrWhiteSpace(x)));

	public string EditName => "Edit " + e.Label;

	public IAsyncRelayCommand EditCommand { get; } = new AsyncRelayCommand(() => edit(e));
}

/// <summary>Lists the events with how many assessments each has; New and Edit open <see cref="EventSheetViewModel"/>.</summary>
public sealed partial class ManageEventsSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;

	public ManageEventsSheetViewModel(AppServices app) : base("Events")
	{
		_app = app;
		Reload();
	}

	public override SheetSize Size => SheetSize.Medium;

	public ObservableCollection<EventRowViewModel> Rows { get; } = new();

	public bool IsEmpty => Rows.Count == 0;

	/// <summary>True after any event was added, changed or deleted, so the pages refresh when the sheet closes.</summary>
	public bool Changed { get; private set; }

	private void Reload()
	{
		Dictionary<Guid, int> counts = _app.Store.EventCounts();
		Rows.Clear();
		foreach (EventRecord e in _app.Store.GetEvents())
		{
			Rows.Add(new EventRowViewModel(e, counts.GetValueOrDefault(e.EventID), Edit));
		}
		OnPropertyChanged(nameof(IsEmpty));
	}

	[RelayCommand]
	private async Task New() => await Edit(null);

	private async Task Edit(EventRecord e)
	{
		object result = await _app.Sheets.ShowAsync(new EventSheetViewModel(_app, e));
		if (result != null)
		{
			Changed = true;
			Reload();
		}
	}

	[RelayCommand]
	private void Done() => Close(Changed);
}

/// <summary>A choice in Set event: an event, or no event.</summary>
public sealed record EventOption(Guid? Id, string Label)
{
	public static readonly EventOption None = new EventOption(null, "No event");

	public override string ToString() => Label;
}

/// <summary>
/// Puts the chosen assessments in an event, moves them from another, or takes them out of any (one batch in edit
/// history). Closes with how many changed.
/// </summary>
public sealed partial class SetEventSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly IReadOnlyList<Guid> _assessmentIds;

	public SetEventSheetViewModel(AppServices app, IReadOnlyList<Guid> assessmentIds, Guid? suggested = null) : base("Set event")
	{
		_app = app;
		_assessmentIds = assessmentIds;
		Fill(suggested ?? app.Scope.EventId);
		Dictionary<Guid, Guid> memberships = app.Store.GetEventMemberships();
		int inOne = assessmentIds.Count(memberships.ContainsKey);
		Intro = $"{assessmentIds.Count:N0} assessment{(assessmentIds.Count == 1 ? "" : "s")} chosen"
			+ (inOne == 0 ? ", none in an event yet." : $", {inOne:N0} already in an event.");
	}

	public override SheetSize Size => SheetSize.Small;

	public string Intro { get; }

	public ObservableCollection<EventOption> Options { get; } = new();

	[ObservableProperty]
	public partial EventOption Selected { get; set; }

	private void Fill(Guid? select)
	{
		Options.Clear();
		Options.Add(EventOption.None);
		foreach (EventRecord e in _app.Store.GetEvents())
		{
			Options.Add(new EventOption(e.EventID, e.Label));
		}
		Selected = Options.FirstOrDefault(o => o.Id == select && select != null) ?? Options.Skip(1).FirstOrDefault() ?? EventOption.None;
	}

	/// <summary>Adds an event and chooses it.</summary>
	[RelayCommand]
	private async Task NewEvent()
	{
		if (await _app.Sheets.ShowAsync(new EventSheetViewModel(_app)) is EventRecord added)
		{
			Fill(added.EventID);
		}
	}

	[RelayCommand]
	private void Apply()
	{
		int changed = _app.Store.SetEvent(_assessmentIds, Selected?.Id, SaveContext.Batch(SaveVia.App));
		Close(changed);
	}
}
