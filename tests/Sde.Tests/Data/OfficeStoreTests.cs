using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>The reads behind the Review and Compliance pages and their sidebar counts (v2.5, M8).</summary>
public class OfficeStoreTests
{
	[Fact]
	public void ActiveAssessmentStatuses_CountAssessmentsWithNoStatusAsDraft_AndLeaveOutDeletedOnes()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		AssessmentFile second = t.Store.CreateAssessment(ranch.Property.PropertyID, applyDefaults: false);
		t.Store.SetStatus(ranch.Assessment.AssessmentID, AssessmentStatus.Reviewed);

		Dictionary<Guid, AssessmentStatus> statuses = t.Store.ActiveAssessmentStatuses();
		Assert.Equal(2, statuses.Count);
		Assert.Equal(AssessmentStatus.Reviewed, statuses[ranch.Assessment.AssessmentID]);
		Assert.Equal(AssessmentStatus.Draft, statuses[second.Assessment.AssessmentID]);

		t.Store.TrashAssessment(second.Assessment.AssessmentID);
		Assert.Equal(ranch.Assessment.AssessmentID, Assert.Single(t.Store.ActiveAssessmentStatuses()).Key);
		t.Store.TrashProperty(ranch.Property.PropertyID);
		Assert.Empty(t.Store.ActiveAssessmentStatuses());
	}

	[Fact]
	public void PropertiesInEvent_AreTheStructuresOfItsActiveAssessments()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		AssessmentFile other = SampleCases.RanchFlood(t.Store);
		EventRecord e = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		t.Store.SetEvent(new[] { ranch.Assessment.AssessmentID }, e.EventID);

		HashSet<Guid> inEvent = t.Store.PropertiesInEvent(e.EventID);
		Assert.Equal(ranch.Property.PropertyID, Assert.Single(inEvent));
		Assert.DoesNotContain(other.Property.PropertyID, inEvent);
		t.Store.TrashAssessment(ranch.Assessment.AssessmentID);
		Assert.Empty(t.Store.PropertiesInEvent(e.EventID));
		Assert.Empty(t.Store.PropertiesInEvent(Guid.NewGuid()));
	}

	[Fact]
	public void ActiveComplianceCases_LeaveOutDeletedStructures_UntilTheyAreRestored()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		ComplianceCaseRecord sd = t.Store.OpenComplianceCase(ranch.Assessment.AssessmentID);
		SIDeterminationRecord si = t.Store.SaveSiDetermination(new SIDeterminationRecord
		{
			PropertyID = ranch.Property.PropertyID,
			ThresholdPercent = 50m,
			CumulativeCost = 90000m,
			MarketValue = 150000m,
			Percent = 60m,
			Result = SiResult.SubstantialImprovement,
		});
		Assert.Null(t.Store.GetComplianceCaseForImprovement(si.DeterminationID));
		ComplianceCaseRecord siCase = t.Store.OpenComplianceCaseForImprovement(si.DeterminationID);
		Assert.Equal(siCase.CaseID, t.Store.GetComplianceCaseForImprovement(si.DeterminationID).CaseID);
		Assert.Equal(new[] { sd.CaseID, siCase.CaseID }.OrderBy(x => x), t.Store.GetActiveComplianceCases().Select(c => c.CaseID).OrderBy(x => x));

		t.Store.TrashProperty(ranch.Property.PropertyID);
		Assert.Empty(t.Store.GetActiveComplianceCases());
		Assert.Equal(2, t.Store.GetComplianceCases().Count);
		t.Store.Restore(Assert.Single(t.Store.GetDeletedItems()).ItemKey);
		Assert.Equal(2, t.Store.GetActiveComplianceCases().Count);
	}
}
