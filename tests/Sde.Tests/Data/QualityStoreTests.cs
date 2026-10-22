using Dapper;
using Sde.Core.Assessments;
using Sde.Core.Quality;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Loading the quality checks' inputs from the database, and setting findings aside.</summary>
public class QualityStoreTests : IDisposable
{
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	private Guid AddEvent(string name, params Guid[] assessmentIds)
	{
		Guid eventId = Guid.NewGuid();
		_db.Database.InTransaction((c, tx) =>
		{
			c.Execute("INSERT INTO Event (EventID, Name) VALUES (@eventId, @name)", new { eventId, name }, tx);
			foreach (Guid id in assessmentIds)
			{
				c.Execute("INSERT INTO EventAssessment (AssessmentID, EventID) VALUES (@id, @eventId)", new { id, eventId }, tx);
			}
		});
		return eventId;
	}

	[Fact]
	public void LoadQaInput_ReadsAssessments_Suggestions_Events_AndDismissals()
	{
		AssessmentFile ranch = SampleCases.RanchFlood(_db.Store);
		AssessmentFile second = SampleCases.RanchFlood(_db.Store);
		Guid aid = ranch.Assessment.AssessmentID;
		Assert.Equal(1, _db.Store.AddDamageSuggestions(aid, new[] { new DamageSuggestionRecord { ElementName = "Foundation", SuggestedPercent = 10m, DepthFt = 1m, TableName = "Synthetic" } }));
		Guid eventId = AddEvent("September flood", aid);
		_db.Store.DismissQaFinding(QaRuleIds.MissingPhotos, aid, "", "Photos are on the county drive");

		QaInput all = _db.Store.LoadQaInput();
		Assert.Equal(new[] { aid, second.Assessment.AssessmentID }.OrderBy(x => x), all.Assessments.Select(f => f.Assessment.AssessmentID).OrderBy(x => x));
		Assert.Equal(1, all.SuggestionCounts[aid]);
		Assert.False(all.SuggestionCounts.ContainsKey(second.Assessment.AssessmentID));
		Assert.Equal(eventId, all.EventOf[aid]);
		Assert.Equal("September flood", all.Events[eventId].Name);
		Assert.Single(all.Dismissals);
		Assert.NotNull(all.Reference);

		QaInput one = _db.Store.LoadQaInput(new[] { second.Assessment.AssessmentID, Guid.NewGuid() });
		Assert.Equal(second.Assessment.AssessmentID, Assert.Single(one.Assessments).Assessment.AssessmentID);

		List<QaFinding> found = QaRules.Check(all);
		Assert.Single(found, f => f.RuleId == QaRuleIds.UnacceptedSuggestions && f.ObjectId == aid);
		QaFinding photos = Assert.Single(found, f => f.RuleId == QaRuleIds.MissingPhotos && f.ObjectId == aid);
		Assert.Equal("Photos are on the county drive", photos.Dismissal.Reason);
		Assert.False(found.Single(f => f.RuleId == QaRuleIds.MissingPhotos && f.ObjectId == second.Assessment.AssessmentID).IsDismissed);
		// The two sample structures share an address.
		Assert.Equal(2, found.Count(f => f.RuleId == QaRuleIds.DuplicateAddress));
	}

	[Fact]
	public void Dismiss_RecordsWhoWhenAndWhy_ReplacesTheReason_AndCanBeUndone()
	{
		DateTime now = new DateTime(2026, 10, 21, 9, 30, 0);
		_db.Store.Clock = () => now;
		_db.Store.UserName = "Reviewer A";
		AssessmentFile ranch = SampleCases.RanchFlood(_db.Store);
		QaFinding finding = QaRules.Check(_db.Store.LoadQaInput()).First(f => f.RuleId == QaRuleIds.MissingCoordinates);

		QaDismissalRecord d = _db.Store.DismissQaFinding(finding, "  Located by parcel instead  ");
		Assert.Equal("Located by parcel instead", d.Reason);
		QaDismissalRecord stored = Assert.Single(_db.Store.GetQaDismissals());
		Assert.Equal((QaRuleIds.MissingCoordinates, ranch.Property.PropertyID, "", "Reviewer A", now), (stored.RuleId, stored.ObjectID, stored.Detail, stored.DismissedBy, stored.DismissedOn));

		_db.Store.DismissQaFinding(finding, "Second thoughts", new SaveContext { User = "Lead B" });
		QaDismissalRecord replaced = Assert.Single(_db.Store.GetQaDismissals());
		Assert.Equal(("Second thoughts", "Lead B"), (replaced.Reason, replaced.DismissedBy));
		Assert.True(QaRules.Check(_db.Store.LoadQaInput()).First(f => f.RuleId == QaRuleIds.MissingCoordinates).IsDismissed);

		Assert.True(_db.Store.RemoveQaDismissal(finding.RuleId, finding.ObjectId, finding.Detail));
		Assert.False(_db.Store.RemoveQaDismissal(finding.RuleId, finding.ObjectId, finding.Detail));
		Assert.Empty(_db.Store.GetQaDismissals());
		Assert.False(QaRules.Check(_db.Store.LoadQaInput()).First(f => f.RuleId == QaRuleIds.MissingCoordinates).IsDismissed);
	}

	[Fact]
	public void Dismiss_NeedsAReason_AKnownCheck_AndOneThatCanBeSetAside()
	{
		Guid id = Guid.NewGuid();
		Assert.Throws<ArgumentException>(() => _db.Store.DismissQaFinding(QaRuleIds.NearThreshold, id, "50", " "));
		Assert.Throws<ArgumentException>(() => _db.Store.DismissQaFinding("NoSuchCheck", id, "", "reason"));
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _db.Store.DismissQaFinding(QaRuleIds.UnacceptedSuggestions, id, "", "reason"));
		Assert.Contains("can't be set aside", ex.Message);
		Assert.Empty(_db.Store.GetQaDismissals());
	}
}
