using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>The reads behind the Review page and its sidebar count (v2.5, M8).</summary>
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
}
