using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Exchange;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Events (v2.5, Mac-only): the disasters assessments belong to.</summary>
public class EventTests
{
	[Fact]
	public void AnEvent_IsSavedAndListed_AndNeedsAName()
	{
		using TempDatabase t = new TempDatabase();
		EventRecord e = t.Store.SaveEvent(new EventRecord { Name = " September flood ", Kind = "Flood", DRNumber = "DR-4999", StartDate = new DateTime(2026, 9, 14) });
		Assert.NotEqual(Guid.Empty, e.EventID);
		Assert.Equal("September flood (DR-4999)", Assert.Single(t.Store.GetEvents()).Label);
		Assert.Throws<ArgumentException>(() => t.Store.SaveEvent(new EventRecord { Name = " " }));
		Assert.Throws<ArgumentException>(() => t.Store.SaveEvent(new EventRecord { Name = "Backwards", StartDate = new DateTime(2026, 9, 14), EndDate = new DateTime(2026, 9, 1) }));
	}

	[Fact]
	public void Assessments_JoinAndLeaveAnEvent_WithHistory_AndWithoutChangingTheAssessment()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		Guid id = f.Assessment.AssessmentID;
		EventRecord e = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		VersionStamp before = t.Store.GetStamp(id);

		Assert.Equal(1, t.Store.SetEvent(new[] { id }, e.EventID));
		Assert.Equal(0, t.Store.SetEvent(new[] { id }, e.EventID));
		Assert.Equal(e.EventID, t.Store.GetEventOf(id));
		Assert.Equal(1, t.Store.EventCounts()[e.EventID]);
		Assert.Contains(t.Store.GetHistory(id), h => h.Label == "Added to event September flood");
		Assert.Equal(before, t.Store.GetStamp(id));

		t.Store.SetEvent(new[] { id }, null);
		Assert.Null(t.Store.GetEventOf(id));
		Assert.Contains(t.Store.GetHistory(id), h => h.Label == "Taken out of event September flood");
	}

	[Fact]
	public void DeletingAnEvent_KeepsItsAssessments()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		EventRecord e = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		t.Store.SetEvent(new[] { f.Assessment.AssessmentID }, e.EventID);
		t.Store.DeleteEvent(e.EventID);
		Assert.Empty(t.Store.GetEvents());
		Assert.Null(t.Store.GetEventOf(f.Assessment.AssessmentID));
		Assert.Equal((1, 1), t.Store.Counts());
	}

	[Fact]
	public void TheDamageDate_SuggestsTheEventItFallsIn()
	{
		using TempDatabase t = new TempDatabase();
		t.Store.SaveEvent(new EventRecord { Name = "Spring storm", StartDate = new DateTime(2026, 4, 2), EndDate = new DateTime(2026, 4, 4) });
		EventRecord flood = t.Store.SaveEvent(new EventRecord { Name = "September flood", StartDate = new DateTime(2026, 9, 14) });
		Assert.Equal(flood.EventID, t.Store.SuggestEvent(new DateTime(2026, 9, 20, 15, 0, 0))?.EventID);
		Assert.Equal("Spring storm", t.Store.SuggestEvent(new DateTime(2026, 4, 4))?.Name);
		Assert.Null(t.Store.SuggestEvent(new DateTime(2026, 4, 5)));
		Assert.Null(t.Store.SuggestEvent(null));
	}

	[Fact]
	public void EventsNeverReachTheFemaExport()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		ExchangeService x = new ExchangeService(t.Store);
		(string before, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "a"));
		EventRecord e = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		t.Store.SetEvent(new[] { f.Assessment.AssessmentID }, e.EventID);
		(string after, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "b"));
		Assert.Equal(File.ReadAllBytes(Path.Combine(before, "Export.json")), File.ReadAllBytes(Path.Combine(after, "Export.json")));
	}

	[Fact]
	public void TheEventSummary_CarriesTheEventsDetails()
	{
		EventDetails details = new EventDetails("September flood", "Flood", "DR-4999", new DateTime(2026, 9, 14), new DateTime(2026, 9, 20));
		EventSummary s = EventSummary.Build(Array.Empty<AssessmentFile>(), details: details);
		Assert.Equal("September flood", s.EventName);
		Assert.Equal("Sep 14, 2026 to Sep 20, 2026", s.Event.DateRange);
		Assert.Equal("From Sep 14, 2026", (details with { End = null }).DateRange);
	}
}
