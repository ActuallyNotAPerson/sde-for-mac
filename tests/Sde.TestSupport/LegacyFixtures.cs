using System.Xml;
using System.Xml.Serialization;
using Sde.Interop.Legacy;

namespace Sde.TestSupport;

/// <summary>
/// SDE 2.x export fixtures, written by serializing the ported XmlSerializer classes (whose element names are FEMA's);
/// there are no real SDE 2.x exports to test with. Shared by the interop and UI tests.
/// </summary>
public static class LegacyFixtures
{
	public static readonly Guid P1 = Guid.Parse("11111111-2222-3333-4444-555555555555");
	public static readonly Guid P2 = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
	public static readonly Guid A1 = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
	public static readonly Guid A2 = Guid.Parse("a2a2a2a2-0000-0000-0000-000000000002");

	private static void Save<T>(string path, T value)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		using XmlWriter w = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, NewLineChars = "\r\n" });
		new XmlSerializer(typeof(T)).Serialize(w, value);
	}

	/// <summary>An SDE 2.x export: a residential and a non-residential structure, one assessment each, two photos named alike.</summary>
	public static string WriteSde2Export(string root)
	{
		string folder = Path.Combine(root, "sde2-export");
		Save(Path.Combine(folder, "SummaryData.xml"), new XmlSummaryData.NewDataSet
		{
			Properties = new[]
			{
				new XmlSummaryData.NewDataSetProperties { PropertyID = P1.ToString(), StreetAddress = "123 River", DateOfLastModification = "5/1/2012", FileReference = "Prop1" },
				new XmlSummaryData.NewDataSetProperties { PropertyID = P2.ToString(), StreetAddress = "9 Elm", DateOfLastModification = "5/1/2012", FileReference = "Prop2" },
			},
			Assessments = new[]
			{
				new XmlSummaryData.NewDataSetAssessments { APropertyID = P1.ToString(), AssessmentID = A1.ToString(), Inspector = "Inspector A", PercentDamaged = "52.3%", DateOfAssessment = "9/20/2012", DateOfDamage = "9/14/2012" },
				new XmlSummaryData.NewDataSetAssessments { APropertyID = P2.ToString(), AssessmentID = A2.ToString(), PercentDamaged = "0%", DateOfAssessment = "9/21/2012" },
			},
		});

		Save(Path.Combine(folder, "Prop1", "PropertyData.xml"), new XmlPropertyData.NewDataSet
		{
			tblProperty = new XmlPropertyData.NewDataSetTblProperty
			{
				PropertyID = P1.ToString(), ResidenceTypeID = 1, QualityID = 4, ParcelNumber = "P-1", LotNumber = "7", Subdivision = "Riverside",
				YearOfConstruction = "1962", Latitude = "39.1031", Longitude = "-84.512", Datum = "NAVD88", StructureType = "Residential",
				FloorElevation = "500.5", ResidenceInformation = "Ranch", DateOfLastModification = "2012-05-01T10:00:00",
			},
			tblPropertyAddress = new XmlPropertyData.NewDataSetTblPropertyAddress
			{
				PropertyID = P1.ToString(), FirstName = "Pat", LastName = "Example", AddressLine1 = "123", AddressLine2 = "River", StreetSuffixID = 20,
				CountyID = 2122, StateID = 36, Zip = "43702", City = "Exampleville", AddressPhone = "5135551212", CellPhone = null,
			},
			tblOwnerAddress = new XmlPropertyData.NewDataSetTblOwnerAddress
			{
				PropertyID = P1.ToString(), FirstName = "Pat", LastName = "Example", AddressLine1 = "PO Box 9", StateID = 36, CountyID = 99999, Zip = "43701", City = "Exampleville",
			},
			tblNFIPInfo = new XmlPropertyData.NewDataSetTblNFIPInfo
			{
				PropertyID = P1.ToString(), FloodWayID = 2, FirmZoneID = 3, FirmPanelNumber = "39061C0100", FirmPanelDate = "9/30/2010", BaseFloodElevation = "",
				Suffix = "E", CommunityName = "Exampleville, City of", CommunityID = "399990",
			},
			tblAdjustments = new XmlPropertyData.NewDataSetTblAdjustments
			{
				PropertyID = P1.ToString(), FoundationID = 4, RoofingID = 2, SuperStructureID = 2, HVACID = 2, ExteriorFinishID = 2, StoryID = 2,
			},
			tblSquareFoot_Property = new XmlPropertyData.NewDataSetTblSquareFoot_Property { PropertyID = P1.ToString(), TotalSquareFoot = 1450m },
		});
		Save(Path.Combine(folder, "Prop1", "AssessmentData_1.xml"), new XmlAssessmentData.NewDataSet
		{
			tblAssessment = new XmlAssessmentData.NewDataSetTblAssessment
			{
				AssessmentID = A1.ToString(), PropertyID = P1.ToString(), DateOfAssessment = "9/20/2012", LocalMultiplier = 0.93m, CommunityUse = "Exampleville",
				BaseCostPerSqFt = "$118.75", DateOfLastModification = "9/21/2012 3:15:00 PM", DamageTypeID = 2, DepthOfFloodAboveFloor = "3",
				DepthOfFlood = "", DurationOfFlood = "2", UnitMeasureOfTime = "days", DateOfDamage = "9/14/2012", RepairRecon = "",
			},
			tblAssessmentPreview = new XmlAssessmentData.NewDataSetTblAssessmentPreview { AssessmentID = A1.ToString(), InspectorName = "Inspector A", PercentDamaged = "52.3%" },
			tblAssessment_Damage = new[]
			{
				new XmlAssessmentData.NewDataSetTblAssessment_Damage { AssessmentID = A1.ToString(), DamageID = 1, PercentDamage = 10 },
				new XmlAssessmentData.NewDataSetTblAssessment_Damage { AssessmentID = A1.ToString(), DamageID = 15, PercentDamage = 100 },
				new XmlAssessmentData.NewDataSetTblAssessment_Damage { AssessmentID = A1.ToString(), DamageID = 6, PercentDamage = 90 },
				new XmlAssessmentData.NewDataSetTblAssessment_Damage { AssessmentID = A1.ToString(), DamageID = 99, PercentDamage = 50 },
			},
			tblAssessment_Refinement = new[]
			{
				new XmlAssessmentData.NewDataSetTblAssessment_Refinement { AssessmentID = A1.ToString(), RefinementID = 15, Quantity = 240, UnitCost = 32.5m },
			},
			tblAdditionalAdjustments = new XmlAssessmentData.NewDataSetTblAdditionalAdjustments
			{
				AssessmentID = A1.ToString(), AddRefine1Name = "Shed", AddRefine1Qty = 1, AddRefine1UnitCost = 2500m, LumpSumRefine1Name = "Fence", LumpSumRefine1Value = 800m,
			},
			tblDepreciation = new XmlAssessmentData.NewDataSetTblDepreciation
			{
				AssessmentID = A1.ToString(), DepreciationValueID = 6, DepreciationValue = -1m, ReferenceDocument = "RSMeans 2012", PublicationDate = "1/1/2012",
			},
			tblOutputSummary = new XmlAssessmentData.NewDataSetTblOutputSummary { AssessmentID = A1.ToString(), ValueOfBuildingID = 1, CostOfRepairsID = 1 },
			tblAttachment = new[]
			{
				new XmlAssessmentData.NewDataSetTblAttachment { AssessmentID = A1.ToString(), thePath = @"C:\SDE2\Photos\IMG_0001.JPG", Description = "Front", DisplayDefault = "True" },
				new XmlAssessmentData.NewDataSetTblAttachment { AssessmentID = A1.ToString(), thePath = @"C:\SDE2\Photos\missing.jpg", Description = "Lost", DisplayDefault = "False" },
			},
		});
		File.WriteAllText(Path.Combine(folder, "Prop1", "img_0001.jpg"), "photo of 123 River");

		Save(Path.Combine(folder, "Prop2", "PropertyData.xml"), new XmlPropertyData.NewDataSet
		{
			tblProperty = new XmlPropertyData.NewDataSetTblProperty { PropertyID = P2.ToString(), StructureType = "Non-Residential", StructureUseID = 1, DateOfLastModification = "5/1/2012" },
			tblPropertyAddress = new XmlPropertyData.NewDataSetTblPropertyAddress { PropertyID = P2.ToString(), AddressLine1 = "9", AddressLine2 = "Elm", StateID = 36 },
			tblNFIPInfo = new XmlPropertyData.NewDataSetTblNFIPInfo { PropertyID = P2.ToString(), FirmPanelDate = " " },
			tblAdjustments = new XmlPropertyData.NewDataSetTblAdjustments { PropertyID = P2.ToString(), StoryID = 1 },
		});
		Save(Path.Combine(folder, "Prop2", "assessmentdata.xml"), new XmlAssessmentData.NewDataSet
		{
			tblAssessment = new XmlAssessmentData.NewDataSetTblAssessment { AssessmentID = A2.ToString(), PropertyID = P2.ToString(), DateOfAssessment = "9/21/2012", DamageTypeID = 5 },
			tblAssessmentPreview = new XmlAssessmentData.NewDataSetTblAssessmentPreview { AssessmentID = A2.ToString(), PercentDamaged = "N/A" },
			tblDepreciation = new XmlAssessmentData.NewDataSetTblDepreciation { AssessmentID = A2.ToString(), DepreciationValue = 1.00003m },
			tblOutputSummary = new XmlAssessmentData.NewDataSetTblOutputSummary { AssessmentID = A2.ToString() },
			tblAttachment = new[]
			{
				new XmlAssessmentData.NewDataSetTblAttachment { AssessmentID = A2.ToString(), thePath = @"D:\Old\IMG_0001.JPG", Description = "Door notice", DisplayDefault = "1" },
			},
		});
		File.WriteAllText(Path.Combine(folder, "Prop2", "IMG_0001.JPG"), "photo of 9 Elm");

		Directory.CreateDirectory(Path.Combine(folder, "photos"));
		return folder;
	}
}
