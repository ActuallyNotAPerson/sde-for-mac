using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Reports.Tests;

/// <summary>
/// Writes one sample PDF of each report to docs/sample-reports/ for people to look at.
/// Only runs when SDE_WRITE_SAMPLE_REPORTS=1 (so a normal test run does not touch the repo):
/// <c>SDE_WRITE_SAMPLE_REPORTS=1 dotnet test tests/Sde.Reports.Tests --filter SampleReports</c>
/// </summary>
public sealed class SampleReports
{
	[Fact]
	public void WriteSamples()
	{
		if (Environment.GetEnvironmentVariable("SDE_WRITE_SAMPLE_REPORTS") != "1")
		{
			return;
		}
		using TempDatabase db = new TempDatabase();
		List<AssessmentFile> files = Dataset(db);
		string dir = Path.Combine(TestPaths.Root, "docs", "sample-reports");
		Directory.CreateDirectory(dir);
		foreach ((ReportKind kind, string name) in new[]
		{
			(ReportKind.Summary, "summary.pdf"),
			(ReportKind.Detail, "detail.pdf"),
			(ReportKind.PercentDamaged, "percent-damaged.pdf"),
			(ReportKind.Community, "community.pdf"),
		})
		{
			IReadOnlyList<AssessmentFile> list = kind == ReportKind.Detail ? new[] { files[0], files[1] } : files;
			ReportGenerator.RenderToFile(new ReportRequest
			{
				Kind = kind,
				Assessments = list,
				IncludePhotos = true,
				PhotosFolder = db.Store.AttachmentsFolder,
				GeneratedOn = new DateTime(2026, 10, 7, 9, 0, 0),
			}, db.Store.Reference, Path.Combine(dir, name));
		}
		ReportGenerator.RenderToFile(new ReportRequest
		{
			Kind = ReportKind.Summary,
			Assessments = files,
			IncludePhotos = false,
			PhotosFolder = db.Store.AttachmentsFolder,
			GeneratedOn = new DateTime(2026, 10, 7, 9, 0, 0),
		}, db.Store.Reference, Path.Combine(dir, "summary-no-photos.pdf"));
	}

	/// <summary>Four structures: substantially damaged ranch, office building, a lightly damaged two-story house, an undetermined one.</summary>
	internal static List<AssessmentFile> Dataset(TempDatabase db)
	{
		AssessmentFile ranch = Fixtures.Ranch(db.Store);
		Fixtures.AddPhoto(db.Store, ranch, Fixtures.Asset("house.jpg"), isDefault: true, description: "Front elevation");
		Fixtures.AddPhoto(db.Store, ranch, Fixtures.Asset("office.png"), description: "Neighboring building");

		AssessmentFile office = Fixtures.Office(db.Store);
		Fixtures.AddPhoto(db.Store, office, Fixtures.Asset("office.png"));   // no default: the original dropped this one

		AssessmentFile cottage = SampleCases.RanchFlood(db.Store);
		PropertyRecord p = cottage.Property;
		p.StreetNumber = "18";
		p.StreetName = "Mill Creek";
		p.OwnerFirstName = "Jordan";
		p.OwnerLastName = "Rivera";
		p.NFIPCID = "399991";
		p.NFIPCommunityName = "Union County Unincorporated Areas";
		p.CountyID = Fixtures.UnionCountyOhio;
		p.StoryTypeID = 105;
		foreach (ElementPercentageRecord e in cottage.ElementPercentages)
		{
			e.UserDamage = Math.Round(e.UserDamage * 0.4m);
		}
		cottage.Assessment.DateofInspection = new DateTime(2026, 9, 23);
		db.Store.SaveAssessment(cottage);

		AssessmentFile refused = SampleCases.RanchFlood(db.Store);
		refused.Property.StreetNumber = "22";
		refused.Property.StreetName = "Mill Creek";
		refused.Property.OwnerFirstName = "Sam";
		refused.Property.OwnerLastName = "Lee";
		refused.Property.NFIPCID = "399991";
		refused.Property.NFIPCommunityName = "Union County Unincorporated Areas";
		refused.Assessment.PhysicalDamage = true;
		refused.Assessment.UndeterminedDamageTypeID = 362;
		refused.Assessment.DepreciationExplanation = "Owner declined entry; exterior inspection only.";
		db.Store.SaveAssessment(refused);

		return new List<AssessmentFile> { ranch, office, cottage, refused };
	}
}
