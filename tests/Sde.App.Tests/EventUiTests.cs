using Avalonia.Headless.XUnit;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.App.ViewModels.Shell;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Records;
using Sde.Data;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// Events (v2.5, M3): the sidebar switcher scopes Structures and the Dashboard to one event and is remembered per
/// database; events are added, set and suggested from the damage date. Also writes the events screenshots.
/// </summary>
public class EventUiTests
{
	private static UiSession WithSamples()
	{
		UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	private static Guid Row(UiSession ui, string addressStart) =>
		ui.Main.Structures.Rows.First(r => r.Address.StartsWith(addressStart, StringComparison.Ordinal)).AssessmentID!.Value;

	private static T Field<T>(AssessmentEditorViewModel e, string member)
		where T : FieldViewModel =>
		e.Sections.SelectMany(s => s.AllFields).OfType<T>().First(f => f.Member == member);

	/// <summary>An event in September 2026 (the samples' damage date is in it) holding two of the three samples.</summary>
	private static EventRecord September(UiSession ui, params Guid[] members)
	{
		EventRecord e = ui.Store.SaveEvent(new EventRecord
		{
			Name = "September flood",
			Kind = "Flood",
			DRNumber = "DR-0000",
			StartDate = new DateTime(2026, 9, 1),
			EndDate = new DateTime(2026, 9, 30),
		});
		ui.Store.SetEvent(members, e.EventID);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return e;
	}

	[AvaloniaFact]
	public void ChoosingAnEvent_ScopesStructuresAndTheDashboard_AndIsRemembered()
	{
		using UiSession ui = WithSamples();
		Guid river = Row(ui, "123");
		Guid mill = Row(ui, "48");
		EventRecord e = September(ui, river, mill);

		EventChoice choice = Assert.Single(ui.Main.Events.Events, c => c.Id == e.EventID);
		Assert.Equal("September flood (DR-0000)", choice.Name);
		Assert.Contains("2 assessments", choice.Detail);
		ui.Main.Events.ChooseCommand.Execute(choice);
		ui.Pump();

		Assert.Equal(new[] { mill, river }.OrderBy(x => x), ui.Main.Structures.Rows.Select(r => r.AssessmentID!.Value).OrderBy(x => x));
		Assert.Equal(2, ui.Main.SidebarItems.Single(i => i.Page == Page.Structures).Count);
		Assert.Equal(e.EventID.ToString("D"), ui.Store.GetSetting<string>(Services.EventScope.SettingKey));

		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		DashboardViewModel d = ui.Main.Dashboard;
		Assert.False(d.IsAllEvents);
		Assert.Equal(2, d.Summary.AssessmentCount);
		Assert.Contains("September flood", d.Subtitle);
		Assert.Equal("Review status", d.ProgressTitle);
		Assert.Equal("DR-0000", d.Summary.Event.DisasterNumber);
		ui.Snapshot("88-dashboard-event");

		// A new structure started while the event is chosen joins it when first saved.
		ui.Main.Structures.NewStructureCommand.Execute("1");
		ui.Pump();
		Assert.Equal(e.Label, ui.Main.Editor.EventLabel);
		Field<TextFieldViewModel>(ui.Main.Editor, nameof(PropertyRecord.StreetName)).Text = "Bridge";
		ui.Main.ShowStructures();
		ui.Pump();
		Assert.Equal(3, ui.Main.Structures.Rows.Count);
		Guid added = ui.Main.Structures.Rows.Single(r => r.AssessmentID != river && r.AssessmentID != mill).AssessmentID!.Value;
		Assert.Equal(e.EventID, ui.Store.GetEventOf(added));

		// All events again shows everything.
		ui.Main.Events.ChooseCommand.Execute(EventChoice.All);
		ui.Pump();
		Assert.Equal(4, ui.Main.Structures.Rows.Count);
		Assert.Null(ui.Store.GetSetting<string>(Services.EventScope.SettingKey));
	}

	[AvaloniaFact]
	public async Task SetEvent_FromTheList_AndTheEditorSuggestsAnEventFromTheDamageDate()
	{
		using UiSession ui = WithSamples();
		Guid river = Row(ui, "123");
		Guid riverside = Row(ui, "700");
		EventRecord e = September(ui);

		// The editor offers the event whose dates take in the damage date.
		ui.Main.OpenAssessment(river);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.True(editor.HasEventSuggestion);
		Assert.Contains("September flood", editor.EventSuggestionText);
		ui.Snapshot("89-editor-event-suggestion");
		editor.AddToSuggestedEventCommand.Execute(null);
		Assert.Equal(e.EventID, ui.Store.GetEventOf(river));
		Assert.False(editor.HasEventSuggestion);
		Assert.Contains("September flood", editor.Subtitle);
		ui.Main.ShowStructures();
		ui.Pump();

		// Change, Set event on a checked row; then take it out again with No event.
		ui.Main.Structures.Rows.Single(r => r.AssessmentID == riverside).IsChecked = true;
		Task set = ui.Main.Structures.SetEventCommand.ExecuteAsync(null);
		ui.Pump();
		SetEventSheetViewModel sheet = Assert.IsType<SetEventSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(e.EventID, sheet.Selected.Id);
		ui.Snapshot("87-set-event");
		sheet.ApplyCommand.Execute(null);
		await set;
		Assert.Equal(e.EventID, ui.Store.GetEventOf(riverside));
		Assert.Contains(ui.Store.GetHistory(riverside), h => h.Field == "Event" && h.NewValue == e.Label);

		set = ui.Main.Structures.SetEventCommand.ExecuteAsync(null);
		ui.Pump();
		sheet = Assert.IsType<SetEventSheetViewModel>(ui.Main.Sheets.Current);
		sheet.Selected = EventOption.None;
		sheet.ApplyCommand.Execute(null);
		await set;
		Assert.Null(ui.Store.GetEventOf(riverside));
	}

	[AvaloniaFact]
	public async Task NewEvent_IsChosen_ThenDeletingItGoesBackToAllEvents()
	{
		using UiSession ui = WithSamples();
		Task adding = ui.Main.NewEventCommand.ExecuteAsync(null);
		ui.Pump();
		EventSheetViewModel sheet = Assert.IsType<EventSheetViewModel>(ui.Main.Sheets.Current);
		Assert.False(sheet.SaveCommand.CanExecute(null));
		sheet.Name = "Spring storm";
		sheet.Kind = "Tornado";
		sheet.StartDate = new DateTime(2027, 4, 2);
		sheet.EndDate = new DateTime(2027, 4, 1);
		ui.Snapshot("86b-event-sheet");
		sheet.SaveCommand.Execute(null);
		Assert.Contains("can't end before it starts", sheet.Error);
		sheet.EndDate = new DateTime(2027, 4, 4);
		sheet.DatumOffset = "lots";
		sheet.SaveCommand.Execute(null);
		Assert.Contains("datum offset", sheet.Error);
		sheet.DatumOffset = "0.6";
		sheet.SaveCommand.Execute(null);
		await adding;
		ui.Pump();

		EventRecord saved = Assert.Single(ui.Store.GetEvents());
		Assert.Equal(0.6m, saved.DatumOffsetFt);
		Assert.Equal(saved.EventID, ui.Services.Scope.EventId);
		Assert.Equal("Spring storm", ui.Main.Events.Label);
		Assert.True(ui.Main.Structures.IsEmpty);
		Assert.Equal("Nothing in Spring storm yet", ui.Main.Structures.EmptyTitle);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		Task managing = ui.Main.ManageEventsCommand.ExecuteAsync(null);
		ui.Pump();
		ManageEventsSheetViewModel manage = Assert.IsType<ManageEventsSheetViewModel>(ui.Main.Sheets.Current);
		EventRowViewModel row = Assert.Single(manage.Rows);
		Assert.Contains("0 assessments", row.Detail);
		Task editing = row.EditCommand.ExecuteAsync(null);
		ui.Pump();
		EventSheetViewModel edit = Assert.IsType<EventSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(edit.IsExisting);
		await edit.DeleteCommand.ExecuteAsync(null);   // the test dialogs say yes
		await editing;
		Assert.True(manage.IsEmpty);
		manage.DoneCommand.Execute(null);
		await managing;
		ui.Pump();

		Assert.True(ui.Services.Scope.IsAll);
		Assert.Equal(EventChoice.All.Name, ui.Main.Events.Label);
		Assert.Equal(3, ui.Main.Structures.Rows.Count);
	}
}
