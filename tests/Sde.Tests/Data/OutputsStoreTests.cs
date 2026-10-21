using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// The v2.0 output tables: improvement projects and SI determinations, contractor bids and appeals, and the letter log.
/// None of them may change an assessment's FEMA fields (PercentDamage, DamageMessage).
/// </summary>
public class OutputsStoreTests
{
	private static (TempDatabase Db, AssessmentFile File) Setup()
	{
		TempDatabase t = new TempDatabase();
		t.Store.Clock = () => new DateTime(2026, 10, 20, 9, 0, 0);
		t.Store.UserName = "Official A";
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		return (t, t.Store.LoadAssessment(f.Assessment.AssessmentID));
	}

	private static void AssertFemaUnchanged(SdeStore store, AssessmentFile before)
	{
		AssessmentFile now = store.LoadAssessment(before.Assessment.AssessmentID);
		Assert.Equal(before.Assessment.PercentDamage, now.Assessment.PercentDamage);
		Assert.Equal(before.Assessment.DamageMessage, now.Assessment.DamageMessage);
		Assert.Equal(before.Assessment.LastUpdate, now.Assessment.LastUpdate);
	}

	[Fact]
	public void ImprovementProjects_AddChangeDelete_WithHistory()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid pid = f.Property.PropertyID;
		ImprovementProjectRecord older = t.Store.SaveImprovementProject(new ImprovementProjectRecord
		{
			PropertyID = pid, PermitNumber = "BP-0001", Description = "Kitchen remodel", ProjectDate = new DateTime(2023, 3, 15), Cost = 18000m,
		});
		ImprovementProjectRecord current = t.Store.SaveImprovementProject(new ImprovementProjectRecord
		{
			PropertyID = pid, PermitNumber = "BP-0002", Description = "Second-story addition", ProjectDate = new DateTime(2026, 6, 1), Cost = 42000m,
			CostBasis = "Contractor's estimate",
		});
		ImprovementProjectRecord undated = t.Store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = pid, Description = "Deck" });
		Assert.NotEqual(Guid.Empty, current.ProjectID);
		Assert.NotNull(current.CreatedOn);

		List<ImprovementProjectRecord> list = t.Store.GetImprovementProjects(pid);
		Assert.Equal(new[] { current.ProjectID, older.ProjectID, undated.ProjectID }, list.Select(p => p.ProjectID));
		Assert.Equal(42000m, list[0].Cost);
		Assert.True(list[0].Included);

		older.Included = false;
		older.ExclusionReason = "routine maintenance";
		older.Cost = 19000m;
		t.Store.SaveImprovementProject(older);
		ImprovementProjectRecord reloaded = t.Store.GetImprovementProjects(pid).Single(p => p.ProjectID == older.ProjectID);
		Assert.False(reloaded.Included);
		Assert.Equal("routine maintenance", reloaded.ExclusionReason);
		Assert.Equal(19000m, reloaded.Cost);

		t.Store.DeleteImprovementProject(undated.ProjectID);
		Assert.Equal(2, t.Store.GetImprovementProjects(pid).Count);

		List<string> events = t.Store.GetPropertyHistory(pid).Select(h => h.Label).ToList();
		Assert.Contains("Improvement project added: BP-0002 Second-story addition", events);
		Assert.Contains(events, e => e.StartsWith("Improvement project changed: BP-0001 Kitchen remodel (") && e.Contains("cost $18,000.00 to $19,000.00") && e.Contains("excluded: routine maintenance"));
		Assert.Contains("Improvement project deleted: Deck", events);
		AssertFemaUnchanged(t.Store, f);

		// Saving without a change writes nothing more.
		int count = t.Store.GetPropertyHistory(pid).Count;
		t.Store.SaveImprovementProject(t.Store.GetImprovementProjects(pid)[0]);
		Assert.Equal(count, t.Store.GetPropertyHistory(pid).Count);
	}

	[Fact]
	public void SiDeterminations_AreKept_NewestFirst_AndSettingsDefaultToTheNfipMinimum()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid pid = f.Property.PropertyID;
		SiSettings defaults = t.Store.GetSiSettings();
		Assert.Equal(50m, defaults.ThresholdPercent);
		Assert.Equal(0, defaults.LookbackYears);
		t.Store.SetSiSettings(new SiSettings { ThresholdPercent = 40m, LookbackYears = 5, IncludePriorProjects = true });
		Assert.Equal(40m, t.Store.GetSiSettings().ThresholdPercent);
		Assert.Equal(5, t.Store.GetSiSettings().LookbackYears);

		ImprovementProjectRecord p = t.Store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = pid, Description = "Addition", ProjectDate = new DateTime(2026, 6, 1), Cost = 60000m });
		SiResult r = ImprovementCalculator.Evaluate(p, t.Store.GetImprovementProjects(pid), 110000m, t.Store.GetSiSettings());
		SIDeterminationRecord first = t.Store.SaveSiDetermination(r.ToRecord(pid, new DateTime(2026, 10, 1), "Professional appraisal", null, null));
		SIDeterminationRecord second = t.Store.SaveSiDetermination(r.ToRecord(pid, new DateTime(2026, 10, 19), "Professional appraisal", "B. Reviewer", "Rechecked"));
		List<SIDeterminationRecord> list = t.Store.GetSiDeterminations(pid);
		Assert.Equal(new[] { second.DeterminationID, first.DeterminationID }, list.Select(d => d.DeterminationID));
		Assert.Equal(SiResult.SubstantialImprovement, list[0].Result);
		Assert.Equal(54.5m, list[0].Percent);
		Assert.Equal("Official A", list[1].DeterminedBy);
		Assert.Contains(t.Store.GetPropertyHistory(pid), h => h.Label == "SI determination: Substantial Improvement (54.5%)");
		AssertFemaUnchanged(t.Store, f);

		// Deleting the structure for good takes its projects and determinations with it.
		t.Store.DeleteProperty(pid);
		Assert.Empty(t.Store.GetImprovementProjects(pid));
		Assert.Empty(t.Store.GetSiDeterminations(pid));
	}

	[Fact]
	public void BidsAndAppeal_AreRecorded_EvenWhenTheAssessmentIsLocked()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		t.Store.SetStatus(id, AssessmentStatus.Final);
		f = t.Store.LoadAssessment(id);

		ContractorBidRecord a = t.Store.SaveBid(new ContractorBidRecord { AssessmentID = id, Contractor = "Sample Builders", Amount = 41000m, BidDate = new DateTime(2026, 10, 5) });
		ContractorBidRecord b = t.Store.SaveBid(new ContractorBidRecord { AssessmentID = id, Contractor = "Example Contracting", Amount = 39500m, BidDate = new DateTime(2026, 10, 2) });
		Assert.Equal(new[] { b.BidID, a.BidID }, t.Store.GetBids(id).Select(x => x.BidID));
		a.Amount = 40000m;
		t.Store.SaveBid(a);
		Assert.Equal(40000m, t.Store.GetBids(id).Single(x => x.BidID == a.BidID).Amount);
		t.Store.DeleteBid(b.BidID);
		Assert.Single(t.Store.GetBids(id));

		Assert.Null(t.Store.GetAppeal(id));
		AppealRecord appeal = t.Store.SaveAppeal(new AppealRecord { AssessmentID = id, ReceivedOn = new DateTime(2026, 10, 3), Basis = "Repair estimate too high" });
		Assert.Equal(AppealOutcomes.Pending, t.Store.GetAppeal(id).Outcome);
		appeal.Outcome = AppealOutcomes.Upheld;
		t.Store.SaveAppeal(appeal);
		AppealRecord decided = t.Store.GetAppeal(id);
		Assert.Equal(AppealOutcomes.Upheld, decided.Outcome);
		Assert.Equal(new DateTime(2026, 10, 20, 9, 0, 0), decided.DecidedOn);
		Assert.Equal("Official A", decided.DecidedBy);

		List<string> events = t.Store.GetHistory(id).Select(h => h.Label).ToList();
		Assert.Contains("Contractor bid added: Sample Builders, $41,000.00", events);
		Assert.Contains("Contractor bid changed: Sample Builders, $40,000.00", events);
		Assert.Contains("Contractor bid removed: Example Contracting, $39,500.00", events);
		Assert.Contains("Appeal received 10/03/2026: Repair estimate too high", events);
		Assert.Contains("Appeal outcome: Upheld", events);
		Assert.Equal(AssessmentStatus.Final, t.Store.GetStatus(id).Value);
		AssertFemaUnchanged(t.Store, f);
	}

	[Fact]
	public void CumulativeSettings_AreOffByDefault_AndKeptPerDatabase()
	{
		using TempDatabase t = new TempDatabase();
		Assert.False(t.Store.GetCumulativeSettings().Enabled);
		t.Store.SetCumulativeSettings(new CumulativeSettings { Enabled = true, Method = CumulativeMethod.TotalDamageOverCurrentValue, LookbackYears = 5, ThresholdPercent = 45m });
		CumulativeSettings s = t.Store.GetCumulativeSettings();
		Assert.True(s.Enabled);
		Assert.Equal(CumulativeMethod.TotalDamageOverCurrentValue, s.Method);
		Assert.Equal(5, s.LookbackYears);
		Assert.Equal(45m, s.ThresholdPercent);
		using TempDatabase other = new TempDatabase();
		Assert.False(other.Store.GetCumulativeSettings().Enabled);
	}

	[Fact]
	public void Letters_AreLogged_AndMarkingSentLocksTheAssessment()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		Guid id = f.Assessment.AssessmentID;
		t.Store.AddLetters(new[]
		{
			new LetterLogRecord { AssessmentID = id, Kind = "Substantial damage", Template = "Substantial damage notice.docx", FileName = "/tmp/Letters/123 River Street (Substantial damage).docx" },
		});
		LetterLogRecord letter = Assert.Single(t.Store.GetLetters(id));
		Assert.Equal(new DateTime(2026, 10, 20, 9, 0, 0), letter.GeneratedOn);
		Assert.Null(letter.SentOn);
		Assert.Contains(t.Store.GetHistory(id), h => h.Label == "Letter written: Substantial damage (123 River Street (Substantial damage).docx)");

		Assert.Equal(1, t.Store.MarkLetterSent(id, new DateTime(2026, 10, 21)));
		Assert.Equal(new DateTime(2026, 10, 21), Assert.Single(t.Store.GetLetters(id)).SentOn);
		AssessmentStatusRecord status = t.Store.GetStatus(id);
		Assert.Equal(AssessmentStatus.LetterSent, status.Value);
		Assert.True(status.IsLocked);
		Assert.Contains(t.Store.GetHistory(id), h => h.Label == "Letter sent: Substantial damage (10/21/2026)");

		// Again: nothing left to mark, and the status stays.
		Assert.Equal(0, t.Store.MarkLetterSent(id));
		Assert.Equal(AssessmentStatus.LetterSent, t.Store.GetStatus(id).Value);
		AssessmentFile now = t.Store.LoadAssessment(id);
		Assert.Equal(f.Assessment.PercentDamage, now.Assessment.PercentDamage);
		Assert.Equal(f.Assessment.DamageMessage, now.Assessment.DamageMessage);
	}

	[Fact]
	public void CountNotFinal_CountsDraftsReviewedAndAppeals()
	{
		(TempDatabase t, AssessmentFile f) = Setup();
		using TempDatabase _db = t;
		AssessmentFile second = SampleCases.RanchFlood(t.Store);
		AssessmentFile third = SampleCases.RanchFlood(t.Store);
		Assert.Equal(3, t.Store.CountNotFinal());
		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		t.Store.SetStatus(second.Assessment.AssessmentID, AssessmentStatus.LetterSent);
		t.Store.SetStatus(third.Assessment.AssessmentID, AssessmentStatus.Appeal);
		Assert.Equal(1, t.Store.CountNotFinal());
		Assert.Equal(0, t.Store.CountNotFinal(new[] { f.Assessment.AssessmentID, second.Assessment.AssessmentID }));
		Assert.Equal(1, t.Store.CountNotFinal(new[] { third.Assessment.AssessmentID, third.Assessment.AssessmentID }));
	}
}
