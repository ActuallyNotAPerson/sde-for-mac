// SDE 2.x export files, as FEMA's SDE 3 read them with XmlSerializer.
//
// Ported from decomp/BLL/BLL.ViewModel/XMLSummaryDataViewModel.cs, XMLPropertyData.cs and XMLAssessmentData.cs.
// Class nesting, class names, property names/types/order and the Xml* attributes are kept exactly, because they
// define the element names XmlSerializer reads (the decompiled backing fields are folded into auto-properties).
//
//   <export folder>/
//     SummaryData.xml                 XmlSummaryData.NewDataSet (Properties + Assessments index)
//     <one folder per structure>/
//       PropertyData.xml              XmlPropertyData.NewDataSet
//       *assessmentdata*              XmlAssessmentData.NewDataSet (one file per assessment)

using System.Xml.Serialization;

namespace Sde.Interop.Legacy;

/// <summary>SummaryData.xml (BLL.ViewModel.XMLSummaryDataViewModel).</summary>
public static class XmlSummaryData
{
	[XmlType(AnonymousType = true)]
	[XmlRoot(Namespace = "", IsNullable = false)]
	public class NewDataSet
	{
		[XmlElement("Properties")]
		public NewDataSetProperties[] Properties { get; set; }

		[XmlElement("Assessments")]
		public NewDataSetAssessments[] Assessments { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetProperties
	{
		public string PropertyID { get; set; }
		public string StreetAddress { get; set; }
		public bool IsOverwriteable { get; set; }
		public string DateOfLastModification { get; set; }
		public string FileReference { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetAssessments
	{
		public string APropertyID { get; set; }
		public string AssessmentID { get; set; }
		public string Inspector { get; set; }
		public string DateOfDamage { get; set; }
		public string CauseOfDamage { get; set; }
		public string DateOfAssessment { get; set; }
		public string PercentDamaged { get; set; }
		public object IsComplete { get; set; }
		public string DateOfLastModification { get; set; }
		public string FileReferenceA { get; set; }
	}
}

/// <summary>PropertyData.xml (BLL.ViewModel.XMLPropertyData).</summary>
public static class XmlPropertyData
{
	[XmlType(AnonymousType = true)]
	[XmlRoot(Namespace = "", IsNullable = false)]
	public class NewDataSet
	{
		public NewDataSetTblProperty tblProperty { get; set; }
		public NewDataSetTblPropertyAddress tblPropertyAddress { get; set; }
		public NewDataSetTblOwnerAddress tblOwnerAddress { get; set; }
		public NewDataSetTblNFIPInfo tblNFIPInfo { get; set; }
		public NewDataSetTblAdjustments tblAdjustments { get; set; }
		public NewDataSetTblSquareFoot_Property tblSquareFoot_Property { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblProperty
	{
		public string PropertyID { get; set; }
		public string OriginalDatabaseID { get; set; }
		public int? ResidenceTypeID { get; set; }
		public int? StoriesID { get; set; }
		public int? StructureUseID { get; set; }
		public int? QualityID { get; set; }
		public string ParcelNumber { get; set; }
		public string LotNumber { get; set; }
		public string Subdivision { get; set; }
		public string YearOfConstruction { get; set; }
		public string Longitude { get; set; }
		public string Latitude { get; set; }
		public string Datum { get; set; }
		public string StructureType { get; set; }
		public string FloorElevation { get; set; }
		public string ResidenceInformation { get; set; }
		public bool IsOverWritable { get; set; }
		public string DateOfLastModification { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblPropertyAddress
	{
		public string PropertyAddressID { get; set; }
		public string PropertyID { get; set; }
		public string FirstName { get; set; }
		public string LastName { get; set; }
		public string AddressLine1 { get; set; }
		public string AddressLine2 { get; set; }
		public int? StreetSuffixID { get; set; }
		public int? CountyID { get; set; }
		public int? StateID { get; set; }
		public string Zip { get; set; }
		public string City { get; set; }
		public string AddressPhone { get; set; }
		public string CellPhone { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblOwnerAddress
	{
		public string OwnerAddressID { get; set; }
		public string PropertyID { get; set; }
		public string FirstName { get; set; }
		public string LastName { get; set; }
		public string AddressLine1 { get; set; }
		public string AddressLine2 { get; set; }
		public int? StreetSuffixID { get; set; }
		public int? CountyID { get; set; }
		public int? StateID { get; set; }
		public string Zip { get; set; }
		public string City { get; set; }
		public string AddressPhone { get; set; }
		public string CareOf { get; set; }
		public bool SameChecked { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblNFIPInfo
	{
		public string NFIPInfoID { get; set; }
		public string PropertyID { get; set; }
		public int? FloodWayID { get; set; }
		public int? FirmZoneID { get; set; }
		public string FirmPanelNumber { get; set; }
		public string FirmPanelDate { get; set; }
		public string BaseFloodElevation { get; set; }
		public string Suffix { get; set; }
		public string CommunityName { get; set; }
		public string CommunityID { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblAdjustments
	{
		public string AdjustmentsID { get; set; }
		public string PropertyID { get; set; }
		public int? FoundationID { get; set; }
		public int? RoofingID { get; set; }
		public int? SuperStructureID { get; set; }
		public int? HVACID { get; set; }
		public int? SprinklerID { get; set; }
		public int? ConveyanceID { get; set; }
		public int? ExteriorFinishID { get; set; }
		public int? StoryID { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblSquareFoot_Property
	{
		public string SquareFootID { get; set; }
		public string PropertyID { get; set; }
		public decimal TotalSquareFoot { get; set; }
	}
}

/// <summary>An assessment file, any name containing "assessmentdata" (BLL.ViewModel.XMLAssessmentData).</summary>
public static class XmlAssessmentData
{
	[XmlType(AnonymousType = true)]
	[XmlRoot(Namespace = "", IsNullable = false)]
	public class NewDataSet
	{
		public NewDataSetTblAssessment tblAssessment { get; set; }
		public NewDataSetTblAssessmentPreview tblAssessmentPreview { get; set; }

		[XmlElement("tblAssessment_Damage")]
		public NewDataSetTblAssessment_Damage[] tblAssessment_Damage { get; set; }

		[XmlElement("tblAssessment_Refinement")]
		public NewDataSetTblAssessment_Refinement[] tblAssessment_Refinement { get; set; }

		public NewDataSetTblAdditionalAdjustments tblAdditionalAdjustments { get; set; }
		public NewDataSetTblDepreciation tblDepreciation { get; set; }
		public NewDataSetTblInspector tblInspector { get; set; }
		public NewDataSetTblOutputSummary tblOutputSummary { get; set; }

		[XmlElement("tblAttachment")]
		public NewDataSetTblAttachment[] tblAttachment { get; set; }

		public NewDataSetTblSquareFoot_Assessment tblSquareFoot_Assessment { get; set; }
		public NewDataSetTblSquareShape_Assessment tblSquareShape_Assessment { get; set; }
		public NewDataSetTblSDEVersions tblSDEVersions { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblAssessment
	{
		public string AssessmentID { get; set; }
		public string PropertyID { get; set; }
		public string DateOfAssessment { get; set; }
		public decimal? LocalMultiplier { get; set; }
		public string CommunityUse { get; set; }
		public string BaseCostPerSqFt { get; set; }
		public string DateOfLastModification { get; set; }
		public int AssessmentDepreciation { get; set; }
		public int DamageTypeID { get; set; }
		public string DepthOfFloodAboveFloor { get; set; }
		public string DepthOfFlood { get; set; }
		public string DurationOfFlood { get; set; }
		public string UnitMeasureOfTime { get; set; }
		public string OtherDamage { get; set; }
		public string DateOfDamage { get; set; }
		public bool IsComplete { get; set; }
		public string RepairRecon { get; set; }
		public string PreExistingImprovements { get; set; }
		public bool IsPreview { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblAssessmentPreview
	{
		public string AssessmentPreviewID { get; set; }
		public string AssessmentID { get; set; }
		public string InspectorName { get; set; }
		public string InspectorPhone { get; set; }
		public string InspectionDate { get; set; }
		public string CauseOfDamage { get; set; }
		public string DateOfDamage { get; set; }
		public string FloodDepth { get; set; }
		public string Duration { get; set; }
		public string TaxValue { get; set; }
		public string CommunityValue { get; set; }
		public string ContractorValue { get; set; }
		public string AppraisalValue { get; set; }
		public string ComputedValue { get; set; }
		public string ComputedDamage { get; set; }
		public string PercentDamaged { get; set; }
		public string ValueDetermination { get; set; }
		public string CostDetermination { get; set; }
		public string ModDate { get; set; }
		public string RepairRecontstruction { get; set; }
		public string ReplacementCost { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblAssessment_Damage
	{
		public string AssessmentID { get; set; }
		public int DamageID { get; set; }
		public int PercentDamage { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblAssessment_Refinement
	{
		public string AssessmentID { get; set; }
		public int RefinementID { get; set; }
		public int Quantity { get; set; }
		public decimal UnitCost { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblAdditionalAdjustments
	{
		public string AdditionalAdjustmentID { get; set; }
		public string AssessmentID { get; set; }
		public string AddRefine1Name { get; set; }
		public int AddRefine1Qty { get; set; }
		public decimal AddRefine1UnitCost { get; set; }
		public string AddRefine2Name { get; set; }
		public int AddRefine2Qty { get; set; }
		public decimal AddRefine2UnitCost { get; set; }
		public string AddRefine3Name { get; set; }
		public int AddRefine3Qty { get; set; }
		public decimal AddRefine3UnitCost { get; set; }
		public string LumpSumRefine1Name { get; set; }
		public decimal LumpSumRefine1Value { get; set; }
		public string LumpSumRefine2Name { get; set; }
		public decimal LumpSumRefine2Value { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblDepreciation
	{
		public string DepreciationID { get; set; }
		public string AssessmentID { get; set; }
		public int DepreciationValueID { get; set; }
		public string ReferenceDocument { get; set; }
		public string PublicationDate { get; set; }
		public string Explanation { get; set; }
		public decimal DepreciationValue { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblInspector
	{
		public string InspectorID { get; set; }
		public string AssessmentID { get; set; }
		public string InspectorName { get; set; }
		public string InspectorPhone { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblOutputSummary
	{
		public string OutputSummaryID { get; set; }
		public string AssessmentID { get; set; }
		public decimal ProfessionalAppraisal { get; set; }
		public decimal ContractorEstimate { get; set; }
		public decimal CommunityEstimate { get; set; }
		public decimal TaxAssessment { get; set; }
		public int FactorAdjustment { get; set; }
		public int PreExistingImprovements { get; set; }
		public int PercentDamaged { get; set; }
		public int ValueOfBuildingID { get; set; }
		public int CostOfRepairsID { get; set; }
		public int ComputedDamages { get; set; }
		public int ComputedActualCashValue { get; set; }
		public int ReplacementCost { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblAttachment
	{
		public string AttachmentID { get; set; }
		public string AssessmentID { get; set; }
		public string Description { get; set; }
		public string thePath { get; set; }
		public string theType { get; set; }
		public string DisplayDefault { get; set; }
		public string OriginalFileName { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblSquareFoot_Assessment
	{
		public string SquareFootID { get; set; }
		public string AssessmentID { get; set; }
		public decimal? TotalSquareFoot { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblSquareShape_Assessment
	{
		public string SquareShapeID { get; set; }
		public string SquareFootID { get; set; }
		public string Length { get; set; }
		public string Width { get; set; }
		public string Stories { get; set; }
		public string Description { get; set; }
	}

	[XmlType(AnonymousType = true)]
	public class NewDataSetTblSDEVersions
	{
		public string VersionID { get; set; }
		public string PropertyID { get; set; }
		public string AssessmentID { get; set; }
		public decimal SDEVersion { get; set; }
	}
}
