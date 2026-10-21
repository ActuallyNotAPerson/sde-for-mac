using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Reports.Internal;

namespace Sde.Reports.Tests;

/// <summary>The values BLL ReportsService derived for the report datasets (decomp/BLL/BLL.Service/ReportsService.cs).</summary>
public sealed class DatasetTests : IDisposable
{
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	private AssessmentView View(Action<AssessmentRecord> change)
	{
		AssessmentFile f = new AssessmentFile
		{
			Property = new PropertyRecord { StructureTypeID = 1 },
			Assessment = new AssessmentRecord
			{
				PercentDamage = 60m,
				ComputedActualCashValue = 100000m,
				ComputedDamages = 60000m,
				MarketAppraisal = 150000m,
				TaxAssessedValue = 80000m,
				FactorAdjustment = 1.25m,
				ContractorEstimate = 45000m,
				CommunityEstimate = 50000m,
				ValueOfBuildingID = LookupIds.ValueComputedAcv,
				CostOfRepairsID = LookupIds.RepairComputedDamages,
			},
		};
		change(f.Assessment);
		return new AssessmentView(f, _db.Store.Reference);
	}

	[Fact]
	public void ValueOfBuilding_FollowsTheBasis()
	{
		Assert.Equal("$100,000.00", View(_ => { }).ValueOfBuilding);
		Assert.Equal("$150,000.00", View(a => a.ValueOfBuildingID = LookupIds.ValueMarketAppraisal).ValueOfBuilding);
		Assert.Equal("$100,000.00", View(a => a.ValueOfBuildingID = LookupIds.ValueAdjustedTax).ValueOfBuilding);   // 80,000 × 1.25
		Assert.Equal("$0.00", View(a => { a.ValueOfBuildingID = LookupIds.ValueAdjustedTax; a.FactorAdjustment = null; }).ValueOfBuilding);
		Assert.Equal("Not Provided", View(a => a.ValueOfBuildingID = null).ValueOfBuilding);
		Assert.Equal("Not Provided", View(a => a.PercentDamage = 0m).ValueOfBuilding);
		Assert.Equal("Not Provided", View(a => a.PercentDamage = null).ValueOfBuilding);
		Assert.Equal("Not Provided", View(a => a.ComputedActualCashValue = null).ValueOfBuilding);   // the original threw here
		Assert.Equal("Computed Actual Cash Value", View(_ => { }).BasisValue);
		Assert.Equal("Adj. Tax Assessed Value", View(a => a.ValueOfBuildingID = LookupIds.ValueAdjustedTax).BasisValue);
	}

	[Fact]
	public void CostOfRepair_FollowsTheBasis()
	{
		Assert.Equal("$60,000.00", View(_ => { }).CostOfRepair);
		Assert.Equal("$45,000.00", View(a => a.CostOfRepairsID = LookupIds.RepairContractorEstimate).CostOfRepair);
		Assert.Equal("$50,000.00", View(a => a.CostOfRepairsID = LookupIds.RepairCommunityEstimate).CostOfRepair);
		Assert.Equal("$0.00", View(a => a.PercentDamage = 0m).CostOfRepair);
		Assert.Equal("Not Provided", View(a => a.PercentDamage = null).CostOfRepair);
		Assert.Equal("Not Provided", View(a => a.CostOfRepairsID = null).CostOfRepair);
		Assert.Equal("Contractor Estimate", View(a => a.CostOfRepairsID = LookupIds.RepairContractorEstimate).BasisCost);
	}

	[Fact]
	public void Determination_Tones()
	{
		Assert.Equal(Determination.Substantial, View(a => a.DamageMessage = "Substantially Damaged").Determination);
		Assert.Equal(Determination.NotSubstantial, View(a => a.DamageMessage = "Not Substantially Damaged").Determination);
		Assert.Equal(Determination.Other, View(a => a.DamageMessage = "Data Entry Incomplete").Determination);
		Assert.Equal(Determination.Other, View(a => a.DamageMessage = "Vacant Lot / Property").Determination);
	}

	[Theory]
	[InlineData("43702", "43702")]
	[InlineData("437021234", "43702-1234")]
	[InlineData("43702-1234", "43702-1234")]
	[InlineData("", "")]
	[InlineData(null, "")]
	[InlineData("ABC", "ABC")]
	public void Zip_IsFormattedLikeTheRdlc(string input, string expected) => Assert.Equal(expected, Fmt.Zip(input));

	[Fact]
	public void Formats()
	{
		Assert.Equal("75.0%", Fmt.Percent1(75m));
		Assert.Equal("24.20%", Fmt.Percent2(24.2m));
		Assert.Equal("-$1.50", Fmt.Money(-1.5m));
		Assert.Equal("$1,234.50", Fmt.Money("1234.5"));
		Assert.Equal("", Fmt.Money((decimal?)null));
		Assert.Equal("09/07/2026", Fmt.Date(new DateTime(2026, 9, 7)));
		Assert.Equal("Wednesday, October 7, 2026", Fmt.LongDate(new DateTime(2026, 10, 7)));
		Assert.Equal("Example, Pat", Fmt.LastFirst("Example", "Pat"));
		Assert.Equal("Riverside LLC", Fmt.LastFirst("Riverside LLC", ""));
		Assert.Equal("Pat", Fmt.LastFirst(null, "Pat"));
		Assert.Equal("1,450", Fmt.Plain(1450.00m));
		Assert.Equal("-84.507634", Fmt.Raw(-84.507634m));
	}

	[Fact]
	public void Structure_Lookups()
	{
		AssessmentFile f = Fixtures.Ranch(_db.Store);
		AssessmentView v = new AssessmentView(f, _db.Store.Reference);
		Assert.Equal("123 River Street", v.StreetAddress);
		Assert.Equal("Exampleville, Ohio 43702", v.CityStateZip);
		Assert.Equal("123 River Street, Exampleville, Ohio 43702", v.FullAddress);
		Assert.Equal("Union", v.County);
		Assert.Equal("Single Family Residence", v.TypeOfStructure);
		Assert.Equal("2 Days", v.DurationOfFlood);
		Assert.Equal("486.5 ft.", v.FirstFloorElevation);
		Assert.Equal("77 Hill Street", v.MailingStreetAddress);

		AssessmentView office = new AssessmentView(Fixtures.Office(_db.Store), _db.Store.Reference);
		Assert.False(office.IsResidential);
		Assert.Equal("Office Building", office.TypeOfStructure);
		Assert.Equal("Yes", office.Sprinkler);
		Assert.Equal("No", office.Conveyance);   // IIf(Conveyance Is Nothing, "No", …)
		Assert.Equal("Riverside Holdings LLC", office.OwnerName);
	}

	[Fact]
	public void CustomFields_ListEveryDefinedField()
	{
		AssessmentFile f = Fixtures.Ranch(_db.Store);
		IReadOnlyList<(string Name, string Value)> fields = new AssessmentView(f, _db.Store.Reference).CustomFields;
		Assert.Equal(new[] { "Custom Field 1", "Custom Field 2", "Custom Field 3" }, fields.Select(x => x.Name));
		Assert.Equal("Block 12 sweep", fields[0].Value);
		Assert.Equal("", fields[1].Value);
	}

	[Fact]
	public void PhotoOrder_DefaultFirstThenAttachmentOrder()
	{
		AssessmentFile f = new AssessmentFile
		{
			Attachments =
			{
				new AttachmentRecord { AttachmentID = 1, FileName = "a.jpg", Active = true },
				new AttachmentRecord { AttachmentID = 2, FileName = "b.jpg", Active = true, DefaultImage = true },
				new AttachmentRecord { AttachmentID = 3, FileName = "c.jpg", Active = false },
				new AttachmentRecord { AttachmentID = 4, FileName = "d.jpg", Active = true },
				new AttachmentRecord { AttachmentID = 5, FileName = "", Active = true },
			},
		};
		Assert.Equal(new[] { "b.jpg", "a.jpg", "d.jpg" }, PhotoLibrary.Ordered(f).Select(a => a.FileName));
	}
}
