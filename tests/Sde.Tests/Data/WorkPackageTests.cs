using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Check-out and check-in between Macs (C8): a package out to the field and back.</summary>
public class WorkPackageTests
{
	private sealed class Trip : IDisposable
	{
		public Trip()
		{
			Office = new TempDatabase();
			Office.Store.UserName = "Office";
			File = SampleCases.RanchFlood(Office.Store);
			Directory.CreateDirectory(Office.Store.AttachmentsFolder);
			System.IO.File.WriteAllText(Path.Combine(Office.Store.AttachmentsFolder, "front.jpg"), "photo");
			File.Attachments.Add(new AttachmentRecord { FileName = "front.jpg", Active = true, AttachmentType = "Photo", DefaultImage = true });
			Office.Store.SaveAssessment(File);
			Office.Store.SetStatus(Id, AssessmentStatus.Reviewed);
			Office.Store.AddReviewComment(Id, "Check the basement");
			EventRecord e = Office.Store.SaveEvent(new EventRecord { Name = "September flood" });
			Office.Store.SetEvent(new[] { Id }, e.EventID);
			Field = Path.Combine(Office.Folder, "field");
			OutPath = Path.Combine(Office.Folder, "River" + WorkPackages.Extension);
			BackPath = Path.Combine(Office.Folder, "River back" + WorkPackages.Extension);
		}

		public TempDatabase Office { get; }

		public AssessmentFile File { get; }

		public Guid Id => File.Assessment.AssessmentID;

		public string Field { get; }

		public string OutPath { get; }

		public string BackPath { get; }

		/// <summary>Opens the package on the "field Mac", changes the inspector name, adds a reply, and sends it back.</summary>
		public SdeStore WorkInTheField(Action<SdeStore> also = null)
		{
			SdeStore field = new SdeStore(SdeDatabase.Open(WorkPackages.Open(OutPath, Field))) { UserName = "Field tech" };
			field.Apply(Id, f => f.Assessment.InspectorName = "Field tech");
			also?.Invoke(field);
			WorkPackages.SendBack(field, BackPath);
			return field;
		}

		public void Dispose() => Office.Dispose();
	}

	[Fact]
	public void CheckingOut_MakesAPackage_AndTheOfficeCopyReadOnly()
	{
		using Trip trip = new Trip();
		WorkPackageManifest m = WorkPackages.CheckOut(trip.Office.Store, new[] { trip.Id }, trip.OutPath, "Team 1");

		Assert.True(File.Exists(trip.OutPath));
		Assert.Equal("Team 1", trip.Office.Store.GetCheckOut(trip.Id).CheckedOutTo);
		trip.File.Assessment.InspectorName = "Office edit";
		Assert.Throws<AssessmentCheckedOutException>(() => trip.Office.Store.SaveAssessment(trip.File));
		Assert.Throws<AssessmentCheckedOutException>(() => trip.Office.Store.SetStatus(trip.Id, AssessmentStatus.Draft));
		BulkResult bulk = trip.Office.Store.ApplyChanges(new[] { trip.Id }, new[] { new FieldChange("Assessment.InspectorName", "Bulk") }, SaveContext.Batch(SaveVia.BulkEdit));
		Assert.Equal(0, bulk.Changed);
		Assert.Throws<InvalidOperationException>(() => WorkPackages.CheckOut(trip.Office.Store, new[] { trip.Id }, trip.OutPath + "2", "Team 2"));

		// The field Mac gets the assessment, its photo, its status, its comment and its event.
		SdeStore field = new SdeStore(SdeDatabase.Open(WorkPackages.Open(trip.OutPath, trip.Field)));
		Assert.Equal(m.PackageId, WorkPackages.Manifest(field).PackageId);
		AssessmentFile there = field.LoadAssessment(trip.Id);
		Assert.Equal("Inspector A", there.Assessment.InspectorName);
		Assert.True(File.Exists(field.PhotoPath(Assert.Single(there.Attachments))));
		Assert.Equal(AssessmentStatus.Reviewed, field.GetStatus(trip.Id).Value);
		Assert.Single(field.GetReviewComments(trip.Id));
		Assert.Equal("September flood", field.GetEvent(field.GetEventOf(trip.Id)!.Value).Name);
	}

	[Fact]
	public void CheckingIn_BringsTheFieldsWorkBack_WithItsHistory_Once()
	{
		using Trip trip = new Trip();
		WorkPackages.CheckOut(trip.Office.Store, new[] { trip.Id }, trip.OutPath, "Team 1");
		Guid added = Guid.Empty;
		trip.WorkInTheField(field =>
		{
			field.AddReviewComment(trip.Id, "Basement checked: 3 ft of water");
			AssessmentFile visit = field.CreateAssessment(trip.File.Property.PropertyID, applyDefaults: false);
			added = visit.Assessment.AssessmentID;
		});

		CheckInResult result = WorkPackages.CheckIn(trip.Office.Store, trip.BackPath);

		Assert.Empty(result.Refused);
		Assert.Equal(1, result.CheckedIn);
		Assert.Equal(1, result.Added);
		Assert.Equal("Field tech", trip.Office.Store.LoadAssessment(trip.Id).Assessment.InspectorName);
		Assert.NotNull(trip.Office.Store.LoadAssessment(added));
		Assert.Null(trip.Office.Store.GetCheckOut(trip.Id));
		Assert.Equal(2, trip.Office.Store.GetReviewComments(trip.Id).Count);
		EditHistoryRecord change = Assert.Single(trip.Office.Store.GetBatch(result.BatchId), h => h.Field == "Assessment.InspectorName");
		Assert.Equal("Field tech", change.ChangedBy);
		Assert.Equal("Inspector A", change.OldValue);

		// Editable here again, and the same package can't be checked in twice.
		trip.File.Assessment.CostData = "After";
		trip.Office.Store.SaveAssessment(trip.Office.Store.LoadAssessment(trip.Id));
		CheckInResult again = WorkPackages.CheckIn(trip.Office.Store, trip.BackPath);
		Assert.Equal(0, again.CheckedIn);
		Assert.Contains(again.Refused, r => r.Contains("already checked in"));
	}

	[Fact]
	public void CheckingIn_RefusesAnAssessmentChangedAtTheOffice_AndAPackageOfAnotherDatabase()
	{
		using Trip trip = new Trip();
		WorkPackages.CheckOut(trip.Office.Store, new[] { trip.Id }, trip.OutPath, "Team 1");
		// The structure changes here through another assessment of it that wasn't checked out.
		AssessmentFile sibling = trip.Office.Store.CreateAssessment(trip.File.Property.PropertyID, applyDefaults: false);
		trip.Office.Store.Apply(sibling.Assessment.AssessmentID, f => f.Property.OwnerLastName = "Changed here");
		trip.WorkInTheField();

		using TempDatabase other = new TempDatabase();
		Assert.Throws<InvalidOperationException>(() => WorkPackages.CheckIn(other.Store, trip.BackPath));
		Assert.Throws<InvalidOperationException>(() => WorkPackages.CheckIn(trip.Office.Store, trip.OutPath));

		CheckInResult result = WorkPackages.CheckIn(trip.Office.Store, trip.BackPath);
		Assert.Equal(0, result.CheckedIn);
		Assert.Contains(result.Refused, r => r.Contains("changed here"));
		Assert.Equal("Inspector A", trip.Office.Store.LoadAssessment(trip.Id).Assessment.InspectorName);
		Assert.NotNull(trip.Office.Store.GetCheckOut(trip.Id));
	}

	[Fact]
	public void TakingBackEarly_ThenCopyingChosenValuesFromThePackage()
	{
		using Trip trip = new Trip();
		WorkPackages.CheckOut(trip.Office.Store, new[] { trip.Id }, trip.OutPath, "Team 1");
		trip.WorkInTheField(field => field.Apply(trip.Id, f => f.Assessment.DepthOfFlood = 3m));
		trip.Office.Store.TakeBack(trip.Id);
		trip.Office.Store.Apply(trip.Id, f => f.Assessment.CostData = "Office");

		List<FieldDiff> differences = WorkPackages.Compare(trip.Office.Store, trip.BackPath, trip.Id);
		Assert.Contains(differences, d => d.Key == "Assessment.InspectorName" && d.NewValue == "Field tech");
		Assert.Contains(differences, d => d.Key == "Assessment.DepthOfFlood");

		WorkPackages.CopyFromPackage(trip.Office.Store, trip.BackPath, trip.Id, new[] { "Assessment.DepthOfFlood" });
		AssessmentFile now = trip.Office.Store.LoadAssessment(trip.Id);
		Assert.Equal(3m, now.Assessment.DepthOfFlood);
		Assert.Equal("Inspector A", now.Assessment.InspectorName);
		Assert.Equal("Office", now.Assessment.CostData);
		Assert.Contains(WorkPackages.CheckIn(trip.Office.Store, trip.BackPath).Refused, r => r.Contains("taken back"));
	}
}
