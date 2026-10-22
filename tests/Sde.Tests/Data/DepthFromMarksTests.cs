using Sde.Core.Assessments;
using Sde.Core.Depth;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Flood depth from high-water marks for many structures (C4): proposals, the event's datum offset, one batch.</summary>
public class DepthFromMarksTests
{
	private const decimal Lat = 39.7m;
	private const decimal Lon = -84.2m;

	private static HighWaterMark Mark(double northMetres, decimal elevation, string datum = "NAVD88") => new HighWaterMark
	{
		Latitude = (double)Lat + northMetres / 111195.0,
		Longitude = (double)Lon,
		ElevationFt = elevation,
		DatumName = datum,
		Quality = 2,
	};

	private static AssessmentFile Placed(TempDatabase t, string datum, decimal firstFloor)
	{
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		return t.Store.Apply(f.Assessment.AssessmentID, x =>
		{
			x.Property.Latitude = Lat;
			x.Property.Longitude = Lon;
			x.Property.Datum = datum;
			x.Property.FirstFloorElevation = firstFloor;
		});
	}

	[Fact]
	public void Proposals_FillFloodElevationAndDepthAboveTheFloor_InOneDepthToolBatch()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile here = Placed(t, "NAVD88", 740m);
		AssessmentFile noPosition = SampleCases.RanchFlood(t.Store);
		HighWaterMark[] marks = { Mark(50, 742.5m), Mark(-50, 742.5m) };

		List<DepthRow> rows = DepthFromMarks.Propose(t.Store, new[] { here.Assessment.AssessmentID, noPosition.Assessment.AssessmentID }, marks);
		DepthRow row = Assert.Single(rows, r => r.AssessmentID == here.Assessment.AssessmentID);
		Assert.True(row.CanApply);
		Assert.Equal(742.5m, row.Proposal.FloodElevationFt);
		Assert.Equal(2.5m, row.Proposal.DepthAboveLowestFloorFt);
		Assert.Contains("latitude", Assert.Single(rows, r => r.AssessmentID == noPosition.Assessment.AssessmentID).Skipped);

		DepthApplyResult result = DepthFromMarks.Apply(t.Store, rows);
		Assert.Equal(1, result.Changed);
		AssessmentFile now = t.Store.LoadAssessment(here.Assessment.AssessmentID);
		Assert.Equal(742.5m, now.Assessment.DepthOfFlood);
		Assert.Equal(2.5m, now.Assessment.DepthOfFloodAboveFFE);
		Assert.All(t.Store.GetBatch(result.BatchId).Where(h => h.Field != null), h => Assert.Equal(SaveVia.DepthTool, h.Via));
	}

	[Fact]
	public void MarksInAnotherDatum_UseTheEventsOffset_OrProposeNothing()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = Placed(t, "NGVD29", 740m);
		Guid id = f.Assessment.AssessmentID;
		HighWaterMark[] marks = { Mark(0, 742.5m, "NAVD88") };

		DepthRow without = Assert.Single(DepthFromMarks.Propose(t.Store, new[] { id }, marks));
		Assert.False(without.CanApply);
		Assert.Contains("offset", without.Skipped);

		EventRecord e = t.Store.SaveEvent(new EventRecord { Name = "Spring flood", DatumOffsetFt = 0.6m });
		t.Store.SetEvent(new[] { id }, e.EventID);
		DepthRow with = Assert.Single(DepthFromMarks.Propose(t.Store, new[] { id }, marks));
		Assert.True(with.CanApply);
		Assert.Equal(743.1m, with.Proposal.FloodElevationFt);
		Assert.Equal(0.6m, with.Proposal.OffsetAppliedFt);
	}

	[Fact]
	public void FinalAndCheckedOutAssessments_AreLeftAlone()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile final = Placed(t, "NAVD88", 740m);
		AssessmentFile open = Placed(t, "NAVD88", 740m);
		HighWaterMark[] marks = { Mark(0, 742m) };
		List<DepthRow> rows = DepthFromMarks.Propose(t.Store, new[] { final.Assessment.AssessmentID, open.Assessment.AssessmentID }, marks);
		t.Store.SetStatus(final.Assessment.AssessmentID, AssessmentStatus.Final);

		DepthApplyResult result = DepthFromMarks.Apply(t.Store, rows);
		Assert.Equal(new[] { final.Assessment.AssessmentID }, result.Refused);
		Assert.Equal(1, result.Changed);
		Assert.Equal(742m, t.Store.LoadAssessment(open.Assessment.AssessmentID).Assessment.DepthOfFlood);
	}
}
