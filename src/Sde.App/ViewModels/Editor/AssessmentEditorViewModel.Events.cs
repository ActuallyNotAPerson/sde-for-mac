using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Sheets;
using Sde.Data;

namespace Sde.App.ViewModels.Editor;

/// <summary>The assessment's event: shown in the subtitle, set from the ⋯ menu, and suggested from the damage date.</summary>
public sealed partial class AssessmentEditorViewModel
{
	private bool _eventSuggestionDismissed;

	/// <summary>The event this assessment is in (for a new one, the event it will join), or null.</summary>
	[ObservableProperty]
	public partial string EventLabel { get; private set; }

	/// <summary>An event whose dates take in this assessment's damage date, offered when the assessment is in none.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasEventSuggestion), nameof(EventSuggestionText))]
	public partial EventRecord SuggestedEvent { get; private set; }

	public bool HasEventSuggestion => SuggestedEvent != null;

	public string EventSuggestionText => SuggestedEvent == null ? null : $"The damage date falls in {SuggestedEvent.Label}. Add this assessment to it?";

	/// <summary>Reads the assessment's event again, and whether another is suggested by its damage date.</summary>
	internal void RefreshEvent()
	{
		if (IsDraft)
		{
			EventLabel = _app.Scope.Event?.Label;
			SuggestedEvent = null;
		}
		else
		{
			Guid? eventId = _app.Store.GetEventOf(Assessment.AssessmentID);
			EventLabel = eventId is Guid id ? _app.Store.GetEvent(id)?.Label : null;
			SuggestedEvent = eventId == null && !_eventSuggestionDismissed && IsEditable ? _app.Store.SuggestEvent(Assessment.DateDamageOccurred) : null;
		}
		Subtitle = ComposeSubtitle();
	}

	/// <summary>A new assessment started while an event is chosen joins it when it is first saved.</summary>
	private void JoinChosenEvent()
	{
		if (_app.Scope.EventId is Guid eventId)
		{
			_app.Store.SetEvent(new[] { Assessment.AssessmentID }, eventId, SaveContext.Batch(SaveVia.App));
		}
	}

	[RelayCommand]
	private void AddToSuggestedEvent()
	{
		if (SuggestedEvent == null)
		{
			return;
		}
		_app.Store.SetEvent(new[] { Assessment.AssessmentID }, SuggestedEvent.EventID, SaveContext.Batch(SaveVia.App));
		RefreshEvent();
		_app.RaiseDataChanged();
	}

	[RelayCommand]
	private void DismissEventSuggestion()
	{
		_eventSuggestionDismissed = true;
		SuggestedEvent = null;
	}

	/// <summary>⋯, then Set event: this assessment's event (a new assessment is saved first).</summary>
	[RelayCommand]
	private async Task SetEvent()
	{
		if (IsDraft)
		{
			Save();
			if (IsDraft)
			{
				return;
			}
		}
		if (await _app.Sheets.ShowAsync(new SetEventSheetViewModel(_app, new[] { Assessment.AssessmentID }, _app.Store.GetEventOf(Assessment.AssessmentID))) is int)
		{
			RefreshEvent();
			_app.RaiseDataChanged();
		}
	}
}
