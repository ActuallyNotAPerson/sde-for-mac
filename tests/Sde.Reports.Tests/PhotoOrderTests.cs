using System.Text;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Reports.Internal;

namespace Sde.Reports.Tests;

/// <summary>The photo order set in the editor (saved as the attachment order) is the order reports print the photos in.</summary>
public class PhotoOrderTests : IDisposable
{
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	[Fact]
	public void SavedOrder_IsTheReportOrder_StarredPhotoFirst()
	{
		AssessmentFile f = SampleCases.RanchFlood(_db.Store);
		foreach (string name in new[] { "front", "kitchen", "basement", "garage" })
		{
			string path = Path.Combine(_db.Folder, name + ".jpg");
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes(name));
			AttachmentRecord a = _db.Store.ImportPhoto(f.Assessment.AssessmentID, path, name);
			a.DefaultImage = name == "front";
			f.Attachments.Add(a);
		}
		_db.Store.SaveAssessment(f);

		// The editor's list: front (starred) first, then garage, basement, kitchen.
		AssessmentFile edit = _db.Store.LoadAssessment(f.Assessment.AssessmentID);
		edit.Attachments = new[] { "front", "garage", "basement", "kitchen" }.Select(n => edit.Attachments.Single(a => a.Description == n)).ToList();
		_db.Store.SaveAssessment(edit);

		AssessmentFile saved = _db.Store.LoadAssessment(f.Assessment.AssessmentID);
		Assert.Equal(new[] { "front", "garage", "basement", "kitchen" }, PhotoLibrary.Ordered(saved).Select(a => a.Description));

		// The detail report's second photo follows the order too.
		PhotoLibrary library = new PhotoLibrary(_db.Store.AttachmentsFolder);
		Assert.Equal("garage", library.Additional(saved).Description);

		// Moving the star: the starred photo prints first, the rest keep their order.
		saved.Attachments.ForEach(a => a.DefaultImage = a.Description == "basement");
		_db.Store.SaveAssessment(saved);
		Assert.Equal(new[] { "basement", "front", "garage", "kitchen" }, PhotoLibrary.Ordered(_db.Store.LoadAssessment(f.Assessment.AssessmentID)).Select(a => a.Description));
	}
}
