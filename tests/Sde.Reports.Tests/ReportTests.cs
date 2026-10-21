using System.Globalization;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Reports.Tests;

/// <summary>Renders every report and reads the PDF back with poppler.</summary>
public sealed class ReportTests : IDisposable
{
	private static readonly DateTime Printed = new DateTime(2026, 10, 7, 14, 30, 0);

	private readonly TempDatabase _db = new TempDatabase();
	private readonly string _out;

	public ReportTests()
	{
		_out = Path.Combine(_db.Folder, "pdf");
		Directory.CreateDirectory(_out);
	}

	public void Dispose() => _db.Dispose();

	private string Render(ReportKind kind, bool photos, params AssessmentFile[] files) => Render(kind, photos, _db.Store.AttachmentsFolder, files);

	private string Render(ReportKind kind, bool photos, string photosFolder, params AssessmentFile[] files)
	{
		string path = Path.Combine(_out, $"{kind}-{(photos ? "photos" : "nophotos")}-{Guid.NewGuid():N}.pdf");
		ReportGenerator.RenderToFile(new ReportRequest
		{
			Kind = kind,
			Assessments = files,
			IncludePhotos = photos,
			PhotosFolder = photosFolder,
			GeneratedOn = Printed,
		}, _db.Store.Reference, path);
		return path;
	}

	public static TheoryData<ReportKind, bool, bool> AllCombinations()
	{
		TheoryData<ReportKind, bool, bool> data = new();
		foreach (ReportKind kind in Enum.GetValues<ReportKind>())
		{
			foreach (bool photos in new[] { true, false })
			{
				foreach (bool residential in new[] { true, false })
				{
					data.Add(kind, photos, residential);
				}
			}
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(AllCombinations))]
	public void EveryReport_ShowsTheKeyContent(ReportKind kind, bool photos, bool residential)
	{
		AssessmentFile f = residential ? Fixtures.Ranch(_db.Store) : Fixtures.Office(_db.Store);
		Fixtures.AddPhoto(_db.Store, f, Fixtures.Asset(residential ? "house.jpg" : "office.png"), isDefault: true);
		AssessmentRecord a = f.Assessment;

		string pdf = Render(kind, photos, f);
		string text = Poppler.Text(pdf);
		int pages = Poppler.Pages(pdf);

		string percent = a.PercentDamage.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
		if (residential)
		{
			Assert.Equal("75.0%", percent);
			Assert.Equal("Substantially Damaged", a.DamageMessage);
		}
		Assert.Contains(residential ? "123 River Street" : "400 Front Street", text);
		Assert.Contains(percent, text);
		Assert.Contains(a.DamageMessage, text);
		Assert.Contains("Substantial Damage Estimator", text);
		Assert.Contains("Wednesday, October 7, 2026", text);
		Assert.Contains("Page 1 of " + pages, text);

		switch (kind)
		{
			case ReportKind.Summary:
				Assert.Equal(1, pages);
				Assert.Contains(residential ? "Residential Summary Report" : "Non-Residential Summary Report", text);
				Assert.Contains(Money(a.ReplacementCost), text);
				Assert.Contains(Money(a.ComputedActualCashValue), text);
				Assert.Contains("Basis for Value of Structure", text);
				Assert.Contains("Basis for Cost of Repairs", text);
				Assert.Contains("Per FEMA Publication 213, Actual Cash Value may be used as Market Value.", text);
				Assert.Contains("Authorized Local Official", text);
				Assert.Contains("Printed Name", text);
				Assert.Contains(residential ? "Residence Type" : "Use", text);
				Assert.Contains("NFIP Information", text);
				Assert.Contains("Optional User Entered Data", text);
				if (residential)
				{
					Assert.Contains("$170,434.38", text);
					Assert.Contains("$129,189.26", text);   // value of building = computed ACV
					Assert.Contains("$96,900.46", text);    // cost of repair = computed damages
					Assert.Contains("Example, Pat", text);
					Assert.Contains("Union", text);
					Assert.Contains("43706-1234", Poppler.Text(Render(ReportKind.Detail, false, f)));   // mailing ZIP+4 formatted
				}
				break;

			case ReportKind.Detail:
				Assert.InRange(pages, 2, 5);
				Assert.Contains(residential ? "Residential Detail Report" : "Non-Residential Detail Report", text);
				Assert.Contains(Money(a.TotalReplacementCost), text);
				Assert.Contains(Money(a.TotalEstimatedDamages), text);
				foreach (ElementPercentageRecord e in f.ElementPercentages)
				{
					Assert.Contains(e.ElementName, text);
				}
				Assert.Contains("Cost Adjustments", text);
				Assert.Contains("Additional Adjustments", text);
				Assert.Contains("Total Adjustment calculation includes the Geographical Adjustment.", text);
				Assert.Contains("Per FEMA Publication 213, Actual Cash Value may be used as Market Value.", text);
				Assert.Contains("Custom Field Name", text);
				Assert.Contains("Authorized Local Official", text);
				Assert.Contains("Print Name", text);
				Assert.Contains("(Date)", text);
				Assert.Contains(residential ? "Residential" : "Non-Residential", text);
				if (residential)
				{
					Assert.Contains("$170,434.38", text);
					Assert.Contains("Garage", text);
					Assert.Contains("Shed", text);
					Assert.Contains("$7,800.00", text);       // garage 240 × $32.50
					Assert.Contains("Block 12 sweep", text);   // custom field
					Assert.Contains("Mailing Address", text);
					Assert.Contains("c/o J. Example", text);
				}
				else
				{
					Assert.Contains("Number Of Stories", text);
					Assert.Contains("Office Building", text);
					Assert.Contains("Sprinkler", text);
				}
				break;

			case ReportKind.PercentDamaged:
				Assert.Contains("SDE Structure and Percent Damage Report", text);
				Assert.Contains("Community NFIP ID and Name", text);
				Assert.Contains("399990", text);
				Assert.Contains("Exampleville, City of", text);
				Assert.Contains("Percent Damaged", text);
				Assert.Contains("Union", text);
				Assert.Contains(residential ? "Example, Pat" : "Riverside Holdings LLC", text);
				Assert.Contains("09/2", text);   // assessment date MM/dd/yyyy
				break;

			case ReportKind.Community:
				Assert.Contains("SDE Community Report", text);
				Assert.Contains("Community NFIP ID and Name", text);
				Assert.Contains(residential ? "Single Family" : "Office Building", text);   // "Residence" may wrap in the narrow column
				Assert.Contains("Computed Actual Cash Value", text);
				Assert.Contains("Computed Damages", text);
				Assert.Contains(Money(a.ComputedActualCashValue), text);
				if (residential)
				{
					Assert.Contains("$129,189.26", text);
					Assert.Contains("$96,900.46", text);
				}
				break;
		}

		List<(int Page, int Width, int Height)> images = Poppler.Images(pdf);
		if (photos)
		{
			Assert.Single(images);
			Assert.Equal(800, images[0].Width);
			if (kind == ReportKind.Detail)
			{
				Assert.Contains("(Default Image)", text);
				Assert.Contains("No Photo Available", text);   // the empty "Additional Image" slot
			}
		}
		else
		{
			Assert.Empty(images);
			Assert.DoesNotContain("No Photo Available", text);
		}
	}

	[Fact]
	public void Summary_IsOnePagePerAssessment_WithTheRightVariant()
	{
		AssessmentFile ranch = Fixtures.Ranch(_db.Store);
		AssessmentFile office = Fixtures.Office(_db.Store);
		Fixtures.AddPhoto(_db.Store, ranch, Fixtures.Asset("house.jpg"), isDefault: true);

		string pdf = Render(ReportKind.Summary, true, ranch, office, ranch);

		Assert.Equal(3, Poppler.Pages(pdf));
		Assert.Contains("Residential Summary Report", Poppler.Text(pdf, 1));
		Assert.Contains("123 River Street", Poppler.Text(pdf, 1));
		Assert.Contains("Page 1 of 3", Poppler.Text(pdf, 1));
		string page2 = Poppler.Text(pdf, 2);
		Assert.Contains("Non-Residential Summary Report", page2);
		Assert.Contains("400 Front Street", page2);
		Assert.Contains("No Photo Available", page2);
		Assert.Contains("Page 2 of 3", page2);
		Assert.Contains("123 River Street", Poppler.Text(pdf, 3));
		Assert.Equal(new[] { 1, 3 }, Poppler.Images(pdf).Select(i => i.Page));
	}

	[Fact]
	public void Detail_RestartsPageNumbersForEachAssessment()
	{
		AssessmentFile ranch = Fixtures.Ranch(_db.Store);
		AssessmentFile office = Fixtures.Office(_db.Store);

		string pdf = Render(ReportKind.Detail, false, ranch, office);
		int pages = Poppler.Pages(pdf);
		string first = Poppler.Text(pdf, 1);
		string last = Poppler.Text(pdf, pages);

		Assert.Contains("Page 1 of", first);
		Assert.Contains("123 River Street", first);
		Assert.Contains("400 Front Street", last);   // the footer carries the structure's address
		int officeStart = Enumerable.Range(1, pages).First(p => Poppler.Text(pdf, p).Contains("Non-Residential Detail Report"));
		Assert.Contains("Page 1 of", Poppler.Text(pdf, officeStart));
	}

	[Fact]
	public void ListReports_GroupByCommunityThenStructure()
	{
		AssessmentFile ranch = Fixtures.Ranch(_db.Store);
		AssessmentFile office = Fixtures.Office(_db.Store, "399991", "Union County");
		// A second assessment of the same structure.
		AssessmentFile again = _db.Store.CreateAssessment(ranch.Property.PropertyID, applyDefaults: false);
		again.Assessment.DateofInspection = new DateTime(2026, 9, 30);
		_db.Store.SaveAssessment(again);

		foreach (ReportKind kind in new[] { ReportKind.PercentDamaged, ReportKind.Community })
		{
			string text = Poppler.Text(Render(kind, false, ranch, office, again));
			int c1 = text.IndexOf("399990", StringComparison.Ordinal);
			int c2 = text.IndexOf("399991", StringComparison.Ordinal);
			Assert.True(c1 >= 0 && c2 > c1, kind + ": communities in order of first appearance");
			Assert.Single(AllIndexes(text, "123 River Street, Exampleville, Ohio 43702"));   // one structure heading…
			Assert.Contains("09/21/2026", text);                                            // …with both assessments
			Assert.Contains("09/30/2026", text);
			Assert.Contains("Union County", text);
		}
	}

	[Fact]
	public void PhotosWithoutADefault_StillAppear_FirstPhotoUsed()
	{
		// Original bug: "(DefaultImage OR DefaultImage IS NULL)" dropped assessments whose photos had no default.
		AssessmentFile f = Fixtures.Ranch(_db.Store);
		Fixtures.AddPhoto(_db.Store, f, Fixtures.SolidPng(_db.Folder, 64, 48, 200, 60, 60));
		Fixtures.AddPhoto(_db.Store, f, Fixtures.SolidPng(_db.Folder, 40, 40, 60, 60, 200));

		foreach (ReportKind kind in new[] { ReportKind.Summary, ReportKind.PercentDamaged, ReportKind.Community })
		{
			string pdf = Render(kind, true, f);
			Assert.Contains("123 River Street", Poppler.Text(pdf));
			(int _, int w, int h) = Assert.Single(Poppler.Images(pdf));
			Assert.Equal((64, 48), (w, h));
		}
		List<(int Page, int Width, int Height)> detail = Poppler.Images(Render(ReportKind.Detail, true, f));
		Assert.Equal(new[] { (64, 48), (40, 40) }, detail.Select(i => (i.Width, i.Height)));
	}

	[Fact]
	public void DefaultPhoto_IsUsedFirst()
	{
		// SQLite stores True as 1 (Jet: -1), so "ORDER BY DefaultImage ASC" would put the default last.
		AssessmentFile f = Fixtures.Ranch(_db.Store);
		Fixtures.AddPhoto(_db.Store, f, Fixtures.SolidPng(_db.Folder, 64, 48, 200, 60, 60));
		Fixtures.AddPhoto(_db.Store, f, Fixtures.SolidPng(_db.Folder, 40, 40, 60, 60, 200), isDefault: true);
		f = _db.Store.LoadAssessment(f.Assessment.AssessmentID);

		(int _, int w, int h) = Assert.Single(Poppler.Images(Render(ReportKind.Summary, true, f)));
		Assert.Equal((40, 40), (w, h));
		List<(int Page, int Width, int Height)> detail = Poppler.Images(Render(ReportKind.Detail, true, f));
		Assert.Equal(new[] { (40, 40), (64, 48) }, detail.Select(i => (i.Width, i.Height)));
	}

	[Fact]
	public void MissingOrUnreadablePhotos_ShowAPlaceholder()
	{
		AssessmentFile missing = Fixtures.Ranch(_db.Store);
		AttachmentRecord gone = Fixtures.AddPhoto(_db.Store, missing, Fixtures.Asset("house.jpg"), isDefault: true);
		File.Delete(_db.Store.PhotoPath(gone));

		AssessmentFile broken = Fixtures.Office(_db.Store);
		string gif = Path.Combine(_db.Folder, "scan.gif");
		File.WriteAllBytes(gif, new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 2, 3 });
		Fixtures.AddPhoto(_db.Store, broken, gif, isDefault: true);

		foreach (ReportKind kind in Enum.GetValues<ReportKind>())
		{
			string pdf = Render(kind, true, missing, broken);
			string text = Poppler.Text(pdf);
			Assert.Contains("123 River Street", text);
			Assert.Contains("400 Front Street", text);
			Assert.Contains("Photo file not found", text);
			Assert.Contains("Photo format not supported", text);
			Assert.Empty(Poppler.Images(pdf));
		}
	}

	[Fact]
	public void NoPhotosFolder_DoesNotCrash()
	{
		AssessmentFile f = Fixtures.Ranch(_db.Store);
		Fixtures.AddPhoto(_db.Store, f, Fixtures.Asset("house.jpg"), isDefault: true);
		string text = Poppler.Text(Render(ReportKind.Summary, true, photosFolder: null, f));
		Assert.Contains("Photo file not found", text);
	}

	[Fact]
	public void UndeterminedAndIncomplete_PrintTheirMessages()
	{
		AssessmentFile f = Fixtures.Ranch(_db.Store);
		f.Assessment.PhysicalDamage = true;
		f.Assessment.UndeterminedDamageTypeID = 362;   // Resident Refused Inspection
		_db.Store.SaveAssessment(f);
		Assert.Equal("Resident Refused Inspection", f.Assessment.DamageMessage);
		Assert.Equal(0m, f.Assessment.PercentDamage);

		string summary = Poppler.Text(Render(ReportKind.Summary, false, f));
		Assert.Contains("Resident Refused Inspection", summary);
		Assert.Contains("0.0%", summary);
		Assert.Contains("Not Provided", summary);   // value of building is "Not Provided" at 0 %
		Assert.Contains("$0.00", summary);          // cost of repair is $0.00 at 0 %
	}

	[Fact]
	public void EmptyRequest_RendersAMessagePage()
	{
		foreach (ReportKind kind in Enum.GetValues<ReportKind>())
		{
			string pdf = Render(kind, true);
			Assert.Equal(1, Poppler.Pages(pdf));
			Assert.Contains("No assessments to report.", Poppler.Text(pdf));
		}
	}

	[Fact]
	public void Render_WritesToAStreamAndLeavesItOpen()
	{
		AssessmentFile f = Fixtures.Ranch(_db.Store);
		using MemoryStream ms = new MemoryStream();
		ReportGenerator.Render(new ReportRequest { Kind = ReportKind.Summary, Assessments = new[] { f }, IncludePhotos = false }, _db.Store.Reference, ms);
		Assert.True(ms.CanWrite);
		Assert.True(ms.Length > 10_000);
		Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(ms.ToArray(), 0, 4));
	}

	[Fact]
	public void ConcurrentRenders_ShareTheFontResolver()
	{
		AssessmentFile f = Fixtures.Ranch(_db.Store);
		ReportKind[] kinds = Enum.GetValues<ReportKind>();
		long[] sizes = new long[8];
		Parallel.For(0, sizes.Length, i =>
		{
			using MemoryStream ms = new MemoryStream();
			ReportGenerator.Render(new ReportRequest { Kind = kinds[i % kinds.Length], Assessments = new[] { f }, IncludePhotos = false, GeneratedOn = Printed }, _db.Store.Reference, ms);
			sizes[i] = ms.Length;
		});
		Assert.All(sizes, s => Assert.True(s > 5_000));
	}

	private static string Money(decimal? v) => v.Value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

	private static List<int> AllIndexes(string text, string value)
	{
		List<int> list = new();
		for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
		{
			list.Add(i);
		}
		return list;
	}
}
