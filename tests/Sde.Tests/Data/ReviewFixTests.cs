using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Regression tests for the v2.5 code review's findings in the data layer.</summary>
public class ReviewFixTests
{
	[Fact]
	public void Import_APhotoThatCantBeCopied_IsAWarning_NotAFailure()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}
		using TempDatabase source = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(source.Store);
		Directory.CreateDirectory(source.Store.AttachmentsFolder);
		File.WriteAllText(Path.Combine(source.Store.AttachmentsFolder, "good.jpg"), "photo");
		File.WriteAllText(Path.Combine(source.Store.AttachmentsFolder, "locked.jpg"), "photo");
		f.Attachments.Add(new AttachmentRecord { FileName = "good.jpg", Active = true, AttachmentType = "Photo" });
		f.Attachments.Add(new AttachmentRecord { FileName = "locked.jpg", Active = true, AttachmentType = "Photo" });
		source.Store.SaveAssessment(f);
		ExchangeService from = new ExchangeService(source.Store);
		(string folder, _) = from.Export(from.ReadDataset(), Path.Combine(source.Folder, "out"));
		string unreadable = Path.Combine(SdeExchangeFolder.PhotosFolder(folder), "locked.jpg");
		File.SetUnixFileMode(unreadable, UnixFileMode.None);
		try
		{
			using TempDatabase target = new TempDatabase();
			ImportResult result = new ExchangeService(target.Store).ImportFolder(folder, ExistingRecordAction.Overwrite);

			Assert.Equal(1, result.AssessmentsAdded);
			Assert.Equal(1, result.PhotosCopied);
			Assert.Contains(result.Warnings, w => w.Contains("locked.jpg"));
			Assert.Equal((1, 1), target.Store.Counts());
		}
		finally
		{
			File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	[Fact]
	public void SearchAndReplace_InAStructureField_IgnoresFinal_AndFindsStructuresWithoutAssessments()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		PropertyRecord bare = t.Store.SaveProperty(new PropertyRecord { StreetNumber = "9", StreetName = "River", StructureTypeID = 1 });
		SearchField street = t.Store.SearchFields().Single(x => x.Field.Member == nameof(PropertyRecord.StreetName));

		List<SearchMatch> preview = t.Store.PreviewReplace(street, "River", "Riverside");
		Assert.Equal(2, preview.Count);
		Assert.All(preview, m => Assert.False(m.Locked));
		Assert.Contains(preview, m => m.PropertyID == bare.PropertyID);

		BulkResult result = t.Store.Replace(street, "River", "Riverside");
		Assert.Equal(2, result.Changed);
		Assert.Empty(result.Locked);
		Assert.Equal("Riverside", t.Store.GetProperty(f.Property.PropertyID).StreetName);
		Assert.Equal("Riverside", t.Store.GetProperty(bare.PropertyID).StreetName);
		// The Final assessment itself didn't change, so it stays locked as it was.
		Assert.False(t.Store.ChangedSinceLocked(f.Assessment.AssessmentID));

		// And a revert puts the structures back.
		BulkResult reverted = t.Store.RevertBatch(result.BatchId);
		Assert.Equal(2, reverted.Changed);
		Assert.Equal("River", t.Store.GetProperty(bare.PropertyID).StreetName);
	}

	[Fact]
	public void RestoringAPhoto_OntoAFinalAssessment_IsRefused()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		f.Attachments.Add(new AttachmentRecord { FileName = "p.jpg", Active = true, AttachmentType = "Photo" });
		t.Store.SaveAssessment(f);
		f = t.Store.LoadAssessment(f.Assessment.AssessmentID);
		f.Attachments.Single().Active = false;
		t.Store.SaveAssessment(f);
		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);

		Assert.Throws<AssessmentLockedException>(() => t.Store.Restore("P:p.jpg"));
		Assert.Empty(t.Store.LoadAssessment(f.Assessment.AssessmentID).Attachments);
		Assert.Contains(t.Store.GetDeletedItems(), d => d.ItemKey == "P:p.jpg");
	}

	[Fact]
	public void RestoringAPhoto_BringsBackOnlyTheOneThatWasRemoved()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile first = SampleCases.RanchFlood(t.Store);
		AssessmentFile second = t.Store.CreateAssessment(first.Property.PropertyID, applyDefaults: false);
		// An import can leave two assessments sharing a photo file name.
		foreach (AssessmentFile f in new[] { first, second })
		{
			AssessmentFile loaded = t.Store.LoadAssessment(f.Assessment.AssessmentID);
			loaded.Attachments.Add(new AttachmentRecord { FileName = "shared.jpg", Active = true, AttachmentType = "Photo" });
			t.Store.SaveAssessment(loaded);
		}
		AssessmentFile a = t.Store.LoadAssessment(first.Assessment.AssessmentID);
		a.Attachments.Single().Active = false;
		t.Store.SaveAssessment(a);
		using (SqliteConnection c = t.Database.Connect())
		{
			// Something else (an older version) made the second one's row inactive too.
			c.Execute("UPDATE Attachment SET Active = 0 WHERE AssessmentID = @AssessmentID", new { second.Assessment.AssessmentID });
		}

		t.Store.Restore("P:shared.jpg");

		Assert.Single(t.Store.LoadAssessment(first.Assessment.AssessmentID).Attachments);
		Assert.Empty(t.Store.LoadAssessment(second.Assessment.AssessmentID).Attachments);
	}
}
