using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.Legacy;

namespace Sde.Interop.Tests;

/// <summary>
/// SDE 2.x XML import. There are no real SDE 2.x exports to test with, so the fixtures are written by serializing
/// the ported XmlSerializer classes (whose element names are FEMA's), then read back and merged with ExchangeService.
/// </summary>
public class LegacySde2ImportTests
{
	private static readonly Guid P1 = LegacyFixtures.P1;
	private static readonly Guid P2 = LegacyFixtures.P2;
	private static readonly Guid A1 = LegacyFixtures.A1;
	private static readonly Guid A2 = LegacyFixtures.A2;

	private static string WriteFixture(string root) => LegacyFixtures.WriteSde2Export(root);

	[Fact]
	public void Read_MapsSde2Records()
	{
		using TempDatabase t = new TempDatabase();
		ReferenceData r = t.Store.Reference;
		string folder = WriteFixture(t.Folder);
		string propertyXml = Path.Combine(folder, "Prop1", "PropertyData.xml");
		byte[] before = File.ReadAllBytes(propertyXml);
		Assert.Contains("\r\n", Encoding.UTF8.GetString(before));

		using LegacyReadResult result = LegacySde2Import.Read(folder, r);

		Assert.Equal(before, File.ReadAllBytes(propertyXml));            // not rewritten in place
		Assert.Equal(2, result.Summary.Properties.Length);
		Assert.Contains(result.Warnings, w => w.Contains("'photos' has no PropertyData.xml"));
		Assert.Contains(result.Warnings, w => w.Contains("damage element 99"));
		Assert.Contains(result.Warnings, w => w.Contains("photo 'missing.jpg' was not found"));

		PropertyRecord p = result.Data.Properties.Single(x => x.PropertyID == P1);
		Assert.True(p.Active);
		Assert.Equal(1, p.StructureTypeID);
		Assert.Equal("123", p.StreetNumber);
		Assert.Equal("River", p.StreetName);
		Assert.Equal(172, p.StreetSuffixID);                           // OldID 20 → "Street"
		Assert.Equal(36, p.StateID);
		Assert.Equal(2122, p.CountyID);
		Assert.Null(p.MailingCountyID);                                // VerifyCountyID: unknown county
		Assert.Null(p.CellPhone);
		Assert.Equal(92, p.ResidenceTypeID);
		Assert.Equal(54, p.StructureQualityID);
		Assert.Equal(45, p.FoundationTypeID);
		Assert.Equal(95, p.RoofCoveringTypeID);
		Assert.Equal(130, p.SuperStructureTypeID);
		Assert.Equal(49, p.HVACTypeID);
		Assert.Equal(25, p.ExteriorFinishTypeID);
		Assert.Equal(104, p.StoryTypeID);                              // OldID 2 for a residence: 104, not non-residential 102
		Assert.Equal(30, p.FIRMZone);
		Assert.Equal(41, p.RegulatoryFloodway);
		Assert.Null(p.BFE);
		Assert.Equal(new DateTime(2010, 9, 30), p.FIRMPanelDate);
		Assert.Equal(1962, p.YearOfConstruction);
		Assert.Equal(39.1031m, p.Latitude);
		Assert.Equal(500.5m, p.FirstFloorElevation);
		Assert.Equal(1450m, p.SquareFootage);
		Assert.Equal(new DateTime(2012, 5, 1, 10, 0, 0), p.LastUpdate);
		Assert.Equal("$118.75", p.BaseCostPerSqFt);
		Assert.Equal("Exampleville", p.CommunityInformation);

		AssessmentRecord a = result.Data.Assessments.Single(x => x.AssessmentID == A1);
		Assert.Equal(P1, a.PropertyID);
		Assert.Equal(20, a.CauseofDamageID);
		Assert.Equal(359, a.DurationOfFloodUnitsID);
		Assert.Equal(2m, a.DurationOfFlood);
		Assert.Null(a.DepthOfFlood);
		Assert.Equal(386, a.DepreciationRatingID);
		Assert.Equal(r.DepreciationPercent(0, 0, 1, 386), a.DepreciationValue);   // -1 → the rating's table value
		Assert.Equal(118.75m, a.BaseCost);
		Assert.Equal(52.3m, a.PercentDamage);
		Assert.Equal(new DateTime(2012, 9, 14), a.DateDamageOccurred);
		Assert.Equal(new DateTime(2012, 9, 21, 15, 15, 0), a.LastUpdate);
		Assert.Equal(LookupIds.ValueComputedAcv, a.ValueOfBuildingID);
		Assert.Equal(LookupIds.RepairComputedDamages, a.CostOfRepairsID);
		Assert.False(a.PhysicalDamage);

		Assert.Equal(new[] { 4, 15, 8 }, result.Data.ElementPercentages.Where(e => e.AssessmentID == A1).Select(e => e.CalcID));   // 15 → 19 → HVAC
		CostAdjustmentResultRecord garage = Assert.Single(result.Data.CostAdjustments);
		Assert.Equal(376, garage.CostAdjustmentLookupID);              // type 23 "Garage", not type 10's 85
		Assert.Equal("Sq Ft", garage.Units);
		Assert.Equal(7800m, garage.TotalCost);
		Assert.Equal(new[] { "Shed", null, null, "Fence", null }, result.Data.AdditionalAdjustments.Where(x => x.AssessmentID == A1).OrderBy(x => x.zOrder).Select(x => x.AdjustmentName));
		ShapeRecord shape = Assert.Single(result.Data.Shapes);
		Assert.Equal(P1, shape.PropertyID);
		Assert.Equal("c", shape.ShapeType);

		AssessmentRecord b = result.Data.Assessments.Single(x => x.AssessmentID == A2);
		Assert.True(b.PhysicalDamage);
		Assert.Equal(362, b.UndeterminedDamageTypeID);                 // "1.00003" → Resident Refused Inspection
		Assert.Null(b.DepreciationValue);
		Assert.Null(b.PercentDamage);
		Assert.Null(b.DateDamageOccurred);
		Assert.Equal(2, result.Data.Properties.Single(x => x.PropertyID == P2).StructureTypeID);
		Assert.Equal(106, result.Data.Properties.Single(x => x.PropertyID == P2).StructureUseID);
		Assert.Equal(101, result.Data.Properties.Single(x => x.PropertyID == P2).StoryTypeID);

		// Two photos called IMG_0001.JPG: each assessment gets its own folder's file, under distinct names.
		AttachmentRecord photo1 = result.Data.Attachments.Single(x => x.AssessmentID == A1 && x.Description == "Front");
		AttachmentRecord photo2 = result.Data.Attachments.Single(x => x.AssessmentID == A2);
		Assert.True(photo1.DefaultImage);
		Assert.True(photo2.DefaultImage);
		Assert.NotEqual(photo1.FileName, photo2.FileName);
		Assert.EndsWith(".jpg", photo1.FileName);
		Assert.Equal("photo of 123 River", File.ReadAllText(Path.Combine(result.PhotosFolder, photo1.FileName)));
		Assert.Equal("photo of 9 Elm", File.ReadAllText(Path.Combine(result.PhotosFolder, photo2.FileName)));
	}

	[Fact]
	public void Read_ThenExchangeImport_ProducesCalculatedAssessments()
	{
		using TempDatabase t = new TempDatabase();
		string folder = WriteFixture(t.Folder);
		LegacyReadResult legacy = LegacySde2Import.Read(folder, t.Store.Reference);
		ImportResult imported;
		using (legacy)
		{
			imported = new ExchangeService(t.Store).Import(legacy.Data, legacy.PhotosFolder, ExistingRecordAction.Skip);
		}
		Assert.False(Directory.Exists(Path.GetDirectoryName(legacy.PhotosFolder)));   // staging removed on dispose
		Assert.Equal(2, imported.PropertiesAdded);
		Assert.Equal(2, imported.AssessmentsAdded);
		Assert.Equal(2, imported.PhotosCopied);

		AssessmentFile f = t.Store.LoadAssessment(A1);
		Assert.Equal(P1, f.Property.PropertyID);
		Assert.Equal(1450m, f.Property.SquareFootage);
		Assert.Equal(12, f.ElementPercentages.Count);                 // normalized to the residential rows
		Assert.Equal(10m, f.ElementPercentages.Single(e => e.CalcID == 4).UserDamage);
		Assert.Equal(100m, f.ElementPercentages.Single(e => e.CalcID == 15).UserDamage);
		Assert.Equal(90m, f.ElementPercentages.Single(e => e.CalcID == 8).UserDamage);
		CostAdjustmentResultRecord garage = f.CostAdjustments.Single(c => c.CostAdjustmentLookupID == 376);
		Assert.Equal(240m, garage.Quantity);
		Assert.Equal(7800m, garage.TotalCost);
		Assert.Equal(7, f.AdditionalAdjustments.Count);
		Assert.Equal(2500m, f.AdditionalAdjustments.Single(x => x.zOrder == 1).TotalCost);
		Assert.Equal(800m, f.AdditionalAdjustments.Single(x => x.zOrder == 4).TotalCost);
		Assert.NotNull(f.Assessment.PercentDamage);
		Assert.True(f.Assessment.PercentDamage > 0m);
		Assert.True(f.Assessment.TotalReplacementCost > 0m);
		Assert.False(string.IsNullOrEmpty(f.Assessment.DamageMessage));

		ShapeRecord shape = Assert.Single(t.Store.GetShapes(P1));
		Assert.Equal("c", shape.ShapeType);
		Assert.Equal(1450m, shape.TotalSqFt);

		AttachmentRecord photo = f.Attachments.Single(x => x.Description == "Front");
		Assert.Equal("photo of 123 River", File.ReadAllText(t.Store.PhotoPath(photo)));

		AssessmentFile g = t.Store.LoadAssessment(A2);
		Assert.True(g.Assessment.PhysicalDamage);
		Assert.Equal(0m, g.Assessment.PercentDamage);
		Assert.Contains("Resident Refused Inspection", g.Assessment.DamageMessage);
		Assert.Equal("photo of 9 Elm", File.ReadAllText(t.Store.PhotoPath(g.Attachments.Single())));

		// Importing the same export again with Overwrite replaces, rather than duplicates, the photos.
		using LegacyReadResult again = LegacySde2Import.Read(folder, t.Store.Reference);
		ImportResult second = new ExchangeService(t.Store).Import(again.Data, again.PhotosFolder, ExistingRecordAction.Overwrite);
		Assert.Equal(2, second.AssessmentsUpdated);
		Assert.Equal(2, Directory.GetFiles(t.Store.AttachmentsFolder).Length);
	}

	[Fact]
	public void Read_FolderWithoutSummary_Throws()
	{
		using TempDatabase t = new TempDatabase();
		Assert.Throws<FileNotFoundException>(() => LegacySde2Import.Read(t.Folder, t.Store.Reference));
	}
}
