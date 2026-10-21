using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Changing many assessments at once: bulk edit, revert, and FEMA's Search and Replace.</summary>
public class BulkTests
{
	private static List<AssessmentFile> Three(TempDatabase t)
	{
		List<AssessmentFile> files = new List<AssessmentFile> { SampleCases.RanchFlood(t.Store) };
		foreach (string street in new[] { "Mill Creek", "Elm" })
		{
			PropertyRecord p = t.Store.SaveProperty(new PropertyRecord { StreetNumber = "9", StreetName = street, StructureTypeID = 1, YearOfConstruction = 1962 });
			AssessmentFile f = t.Store.CreateAssessment(p.PropertyID, false);
			f.Assessment.CauseofDamageID = LookupIds.CauseFlood;
			f.Assessment.DurationOfFlood = 3;
			t.Store.SaveAssessment(f);
			files.Add(f);
		}
		return files;
	}

	[Fact]
	public void ApplyChanges_SetsAField_SkipsFinal_AndIsOneBatch()
	{
		using TempDatabase t = new TempDatabase();
		List<AssessmentFile> files = Three(t);
		t.Store.SetStatus(files[0].Assessment.AssessmentID, AssessmentStatus.Final);
		List<Guid> ids = files.Select(f => f.Assessment.AssessmentID).ToList();

		BulkResult r = t.Store.ApplyChanges(ids, new[] { new FieldChange("Assessment.InspectorName", "Team B") }, SaveContext.Batch(SaveVia.BulkEdit));
		Assert.Equal(2, r.Changed);
		Assert.Equal(new[] { ids[0] }, r.Locked);
		Assert.Equal("Inspector A", t.Store.LoadAssessment(ids[0]).Assessment.InspectorName);
		Assert.All(ids.Skip(1), id => Assert.Equal("Team B", t.Store.LoadAssessment(id).Assessment.InspectorName));
		Assert.Equal(2, t.Store.GetBatch(r.BatchId).Count(h => h.Field == "Assessment.InspectorName"));
	}

	[Fact]
	public void ApplyChanges_FollowsTheEditorsRules()
	{
		using TempDatabase t = new TempDatabase();
		List<AssessmentFile> files = Three(t);
		Guid id = files[1].Assessment.AssessmentID;
		t.Store.ApplyChanges(new[] { id }, new[] { new FieldChange("Assessment.CauseofDamageID", 22) }, SaveContext.Batch(SaveVia.BulkEdit));
		// A cause other than flood clears the flood details, as in the editor.
		Assert.Null(t.Store.LoadAssessment(id).Assessment.DurationOfFlood);
	}

	[Fact]
	public void RevertBatch_PutsValuesBack_ButNotOnesChangedSince()
	{
		using TempDatabase t = new TempDatabase();
		List<AssessmentFile> files = Three(t);
		List<Guid> ids = files.Select(f => f.Assessment.AssessmentID).ToList();
		BulkResult r = t.Store.ApplyChanges(ids, new[] { new FieldChange("Assessment.InspectorName", "Team B") }, SaveContext.Batch(SaveVia.BulkEdit));
		AssessmentFile edited = t.Store.LoadAssessment(ids[2]);
		edited.Assessment.InspectorName = "Kim";
		t.Store.SaveAssessment(edited);

		BulkResult back = t.Store.RevertBatch(r.BatchId);
		Assert.Equal(2, back.Changed);
		Assert.Equal(1, back.ChangedSince);
		Assert.Equal("Inspector A", t.Store.LoadAssessment(ids[0]).Assessment.InspectorName);
		Assert.Null(t.Store.LoadAssessment(ids[1]).Assessment.InspectorName);
		Assert.Equal("Kim", t.Store.LoadAssessment(ids[2]).Assessment.InspectorName);
	}

	[Fact]
	public void SearchFields_AreFemasList()
	{
		using TempDatabase t = new TempDatabase();
		List<SearchField> fields = t.Store.SearchFields();
		Assert.Equal(t.Store.Reference.OfType(LookupTypes.SearchAndReplaceField).Count(), fields.Count);
		Assert.Contains(fields, f => f.Label == "Street Name" && f.IsText);
		Assert.Contains(fields, f => f.Label == "Assessment Date" && !f.IsText);
	}

	[Fact]
	public void Replace_ChangesOnlyTheMatch_KeepingCapitals()
	{
		using TempDatabase t = new TempDatabase();
		List<AssessmentFile> files = Three(t);
		t.Store.CreateAssessment(files[1].Property.PropertyID, false);   // a second assessment of Mill Creek: listed once
		SearchField street = t.Store.SearchFields().Single(f => f.Field.Member == nameof(PropertyRecord.StreetName));

		List<SearchMatch> preview = t.Store.PreviewReplace(street, "creek", "Brook");
		SearchMatch m = Assert.Single(preview);
		Assert.Equal("Mill Creek", m.Before);
		Assert.Equal("Mill Brook", m.After);      // FEMA's version gave "mill Brook"

		BulkResult r = t.Store.Replace(street, "creek", "Brook");
		Assert.Equal(1, r.Changed);
		Assert.Equal("Mill Brook", t.Store.GetProperty(files[1].Property.PropertyID).StreetName);
	}

	[Fact]
	public void Replace_NumbersAndDates_MatchTheWholeValue()
	{
		using TempDatabase t = new TempDatabase();
		Three(t);
		SearchField year = t.Store.SearchFields().Single(f => f.Field.Member == nameof(PropertyRecord.YearOfConstruction));
		Assert.Equal(3, t.Store.PreviewReplace(year, "1962", "1963").Count);
		Assert.Empty(t.Store.PreviewReplace(year, "196", "1963"));
		t.Store.Replace(year, "1962", "1963");
		Assert.All(t.Store.GetProperties(), p => Assert.Equal(1963, p.YearOfConstruction));
	}
}
