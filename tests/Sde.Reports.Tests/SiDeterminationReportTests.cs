using Sde.Core.Improvements;
using Sde.Core.Records;

namespace Sde.Reports.Tests;

/// <summary>The substantial improvement determination PDF, read back with pdftotext.</summary>
public class SiDeterminationReportTests : IDisposable
{
	private static readonly DateTime At = new DateTime(2026, 10, 20, 9, 15, 0);
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	private string Pdf(string name) => Path.Combine(_db.Folder, name);

	/// <summary>A synthetic structure (no real address).</summary>
	private static PropertyRecord Structure() => new PropertyRecord
	{
		PropertyID = Guid.NewGuid(),
		OwnerFirstName = "Pat", OwnerLastName = "Sample",
		StreetNumber = "12", StreetName = "Example", StreetSuffixID = 172, City = "Sample City", StateID = 36, ZipCode = "00000",
		ParcelNumber = "000-111-222", NFIPCID = "999999", NFIPCommunityName = "Sample Community", FIRMZone = 30, BFE = 101.5m,
		YearOfConstruction = 1962,
	};

	private static ImprovementProjectRecord Project(Guid propertyId, string permit, string description, DateTime date, decimal cost, bool included = true, string why = null) => new()
	{
		ProjectID = Guid.NewGuid(), PropertyID = propertyId, PermitNumber = permit, Description = description, ProjectDate = date, Cost = cost,
		CostBasis = "Contractor's estimate", Included = included, ExclusionReason = why,
	};

	[Fact]
	public void SubstantialImprovement_WithLookBack_PrintsProjectsCostsAndDetermination()
	{
		PropertyRecord p = Structure();
		ImprovementProjectRecord current = Project(p.PropertyID, "BP-0002", "Second-story addition", new DateTime(2026, 6, 1), 42000m);
		ImprovementProjectRecord earlier = Project(p.PropertyID, "BP-0001", "Kitchen remodel", new DateTime(2023, 3, 15), 18000m);
		ImprovementProjectRecord excluded = Project(p.PropertyID, "BP-0000", "Exterior paint", new DateTime(2024, 1, 10), 6000m, included: false, why: "routine maintenance");
		SiResult r = ImprovementCalculator.Evaluate(current, new[] { earlier, excluded }, 110000m, new SiSettings { LookbackYears = 5 });

		string path = Pdf("si.pdf");
		SiDeterminationReport.RenderToFile(new SiDeterminationRequest
		{
			Property = p, Project = current, Result = r, MarketValueBasis = "Adjusted tax assessed value",
			DeterminedBy = "A. Official, Floodplain Administrator", DeterminedOn = new DateTime(2026, 10, 19), Notes = "Synthetic test data.", GeneratedOn = At,
		}, _db.Store.Reference, path);

		Assert.InRange(Poppler.Pages(path), 1, 2);
		string text = Poppler.Text(path);
		Assert.Contains("Substantial Improvement Determination", text);
		Assert.Contains("12 Example Street", text);
		Assert.Contains("Sample, Pat", text);
		Assert.Contains("000-111-222", text);
		Assert.Contains("Second-story addition", text);
		Assert.Contains("Kitchen remodel", text);
		Assert.Contains("Excluded: routine maintenance", text);
		Assert.Contains("$60,000.00", text);
		Assert.Contains("$110,000.00", text);
		Assert.Contains("Adjusted tax assessed value", text);
		Assert.Contains("54.5%", text);
		Assert.Contains("Substantial Improvement", text);
		Assert.Contains("5 years, from 06/01/2021", text);
		Assert.Contains("10/19/2026", text);
		Assert.Contains("Authorized Local Official", text);
		Assert.Contains("Determined by: A. Official, Floodplain Administrator", text);
		Assert.Contains("Synthetic test data.", text);
		Assert.DoesNotContain("Not Substantial Improvement", text);
		Assert.DoesNotContain("\u2014", text);   // no em dashes
	}

	[Fact]
	public void NotSubstantial_AndMissingMarketValue_AreWorded()
	{
		PropertyRecord p = Structure();
		ImprovementProjectRecord current = Project(p.PropertyID, "BP-0003", "New windows", new DateTime(2026, 6, 1), 9000m);

		string below = Pdf("below.pdf");
		SiDeterminationReport.RenderToFile(new SiDeterminationRequest
		{
			Property = p, Project = current, Result = ImprovementCalculator.Evaluate(current, null, 100000m), MarketValueBasis = "Professional appraisal", GeneratedOn = At,
		}, _db.Store.Reference, below);
		string text = Poppler.Text(below);
		Assert.Contains("Not Substantial Improvement", text);
		Assert.Contains("9.0%", text);
		Assert.Contains("None (this project only)", text);

		string unknown = Pdf("unknown.pdf");
		SiDeterminationReport.RenderToFile(new SiDeterminationRequest
		{
			Property = p, Result = ImprovementCalculator.Evaluate(current, null, null), GeneratedOn = At,
		}, _db.Store.Reference, unknown);
		string t2 = Poppler.Text(unknown);
		Assert.Contains("Cannot be determined", t2);
		Assert.Contains("No market value has been entered", t2);
		Assert.Equal(1, Poppler.Pages(unknown));
	}

	[Fact]
	public void MissingInputs_AreRefused()
	{
		Assert.Throws<ArgumentException>(() => SiDeterminationReport.RenderToFile(new SiDeterminationRequest(), _db.Store.Reference, Pdf("x.pdf")));
		Assert.False(File.Exists(Pdf("x.pdf")));
	}
}
