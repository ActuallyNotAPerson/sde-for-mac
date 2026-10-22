using Newtonsoft.Json;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Core;

/// <summary>Putting the editor's changes onto someone else's newer copy (<see cref="AssessmentMerge"/>).</summary>
public class MergeTests
{
	private static AssessmentFile Copy(AssessmentFile f) => JsonConvert.DeserializeObject<AssessmentFile>(JsonConvert.SerializeObject(f));

	private static (TempDatabase T, AssessmentFile Baseline) Setup()
	{
		TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		return (t, t.Store.LoadAssessment(f.Assessment.AssessmentID));
	}

	[Fact]
	public void DifferentFields_KeepBothSidesChanges()
	{
		(TempDatabase t, AssessmentFile baseline) = Setup();
		using TempDatabase _db = t;
		AssessmentFile mine = Copy(baseline);
		AssessmentFile theirs = Copy(baseline);
		mine.Assessment.InspectorName = "Mine";
		mine.ElementPercentages.Single(e => e.ElementName == "Plumbing").UserDamage = 80;
		theirs.Property.OwnerLastName = "Theirs";
		theirs.Property.TeamName = "Team 2";

		MergeResult m = AssessmentMerge.Merge(baseline, mine, theirs, t.Store.Reference, t.Store.Calculator);

		Assert.Empty(m.Conflicts);
		Assert.Equal("Mine", m.Merged.Assessment.InspectorName);
		Assert.Equal(80, m.Merged.ElementPercentages.Single(e => e.ElementName == "Plumbing").UserDamage);
		Assert.Equal("Theirs", m.Merged.Property.OwnerLastName);
		// Columns the editor never shows (FEMA's team name) keep the other side's value.
		Assert.Equal("Team 2", m.Merged.Property.TeamName);
		// Recalculated with both changes.
		AssessmentFile expected = Copy(m.Merged);
		t.Store.Calculator.Recalculate(expected);
		Assert.Equal(expected.Assessment.PercentDamage, m.Merged.Assessment.PercentDamage);
	}

	[Fact]
	public void TheSameFieldChangedDifferently_IsAConflict_TheirsUnlessKeepMine()
	{
		(TempDatabase t, AssessmentFile baseline) = Setup();
		using TempDatabase _db = t;
		AssessmentFile mine = Copy(baseline);
		AssessmentFile theirs = Copy(baseline);
		mine.Assessment.DepthOfFlood = 3;
		theirs.Assessment.DepthOfFlood = 4;
		mine.Assessment.InspectorName = "Same";
		theirs.Assessment.InspectorName = "Same";

		MergeResult m = AssessmentMerge.Merge(baseline, mine, theirs, t.Store.Reference, t.Store.Calculator);
		FieldDiff conflict = Assert.Single(m.Conflicts);
		Assert.Equal("Assessment.DepthOfFlood", conflict.Key);
		Assert.Equal(4, m.Merged.Assessment.DepthOfFlood);

		MergeResult keep = AssessmentMerge.Merge(baseline, mine, theirs, t.Store.Reference, t.Store.Calculator, keepMine: true);
		Assert.Equal(3, keep.Merged.Assessment.DepthOfFlood);
	}

	[Fact]
	public void MyStructureTypeChange_ReplaysItsRules_ButKeepsTheStoriesIChoseAfter()
	{
		(TempDatabase t, AssessmentFile baseline) = Setup();
		using TempDatabase _db = t;
		AssessmentFile mine = Copy(baseline);
		mine.Property.StructureTypeID = 2;
		FieldRules.AfterChange(mine, nameof(PropertyRecord.StructureTypeID), t.Store.Calculator);
		mine.Property.StoryTypeID = 101;
		mine.Property.StructureUseID = 106;
		AssessmentFile theirs = Copy(baseline);
		theirs.Property.City = "Elsewhere";

		MergeResult m = AssessmentMerge.Merge(baseline, mine, theirs, t.Store.Reference, t.Store.Calculator);

		Assert.Equal(2, m.Merged.Property.StructureTypeID);
		Assert.Equal(101, m.Merged.Property.StoryTypeID);
		Assert.Equal(106, m.Merged.Property.StructureUseID);
		Assert.Equal("Elsewhere", m.Merged.Property.City);
	}

	[Fact]
	public void Photos_AddedOnBothSides_AreAllKept_AndARemovalIsKept()
	{
		(TempDatabase t, AssessmentFile baseline) = Setup();
		using TempDatabase _db = t;
		baseline.Attachments.Add(new AttachmentRecord { AttachmentID = 7, FileName = "old.jpg", Active = true, DefaultImage = true });
		AssessmentFile mine = Copy(baseline);
		AssessmentFile theirs = Copy(baseline);
		mine.Attachments.Add(new AttachmentRecord { FileName = "mine.jpg", Active = true });
		mine.Attachments.Single(a => a.FileName == "old.jpg").Active = false;
		theirs.Attachments.Add(new AttachmentRecord { FileName = "theirs.jpg", Active = true, Description = "Their photo" });

		MergeResult m = AssessmentMerge.Merge(baseline, mine, theirs, t.Store.Reference, t.Store.Calculator);

		Assert.Empty(m.Conflicts);
		Assert.Equal(new[] { "theirs.jpg", "mine.jpg" }, m.Merged.Attachments.Where(a => a.Active).Select(a => a.FileName));
		Assert.False(m.Merged.Attachments.Single(a => a.FileName == "old.jpg").Active);
	}
}
