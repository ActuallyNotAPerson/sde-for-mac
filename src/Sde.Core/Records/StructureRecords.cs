// Records for structures and assessments.
//
// Property names, types AND declaration order match FEMA's DAL models (decomp/DAL/DAL.Models/*.cs) exactly:
// SDE's Export.json is these objects serialized by Newtonsoft in declaration order, so keeping the order
// keeps our exports identical to the ones FEMA's SDE writes and reads.

namespace Sde.Core.Records;

/// <summary>A building/parcel (DAL PropertyModel). One property can have many assessments.</summary>
[Table("Property")]
public sealed class PropertyRecord
{
	[Key] public Guid PropertyID { get; set; }
	public string BaseCostPerSqFt { get; set; }
	[JetScale(2)] public decimal? BFE { get; set; }
	public string City { get; set; }
	public int? Conveyance { get; set; }
	public int? CountyID { get; set; }
	public int? ExteriorFinishTypeID { get; set; }
	public DateTime? FIRMPanelDate { get; set; }
	public string FIRMPanelNumber { get; set; }
	public int? FIRMZone { get; set; }
	[JetScale(2)] public decimal? FirstFloorElevation { get; set; }
	public int? FoundationTypeID { get; set; }
	public int? HVACTypeID { get; set; }
	public DateTime ImportDate { get; set; }
	[JetScale(6)] public decimal? Latitude { get; set; }
	[JetScale(6)] public decimal? Longitude { get; set; }
	public string LotNumber { get; set; }
	public string NFIPCID { get; set; }
	public string NFIPCommunityName { get; set; }
	public string OwnerFirstName { get; set; }
	public string OwnerLastName { get; set; }
	public string PanelSuffix { get; set; }
	public string ParcelNumber { get; set; }
	public int? RegulatoryFloodway { get; set; }
	public int? ResidenceTypeID { get; set; }
	public int? RoofCoveringTypeID { get; set; }
	public int? SprinklerSystem { get; set; }
	[JetScale(2)] public decimal? SquareFootage { get; set; }
	public int? StateID { get; set; }
	public int? StoryTypeID { get; set; }
	public string StreetName { get; set; }
	public string StreetNumber { get; set; }
	public int? StreetSuffixID { get; set; }
	public string StructureInformation { get; set; }
	public int? StructureQualityID { get; set; }
	public int? StructureShapeID { get; set; }
	public int? StructureTypeID { get; set; }
	public int? StructureUseID { get; set; }
	public string Subdivision { get; set; }
	public int? SuperStructureTypeID { get; set; }
	public string TeamName { get; set; }
	public int? YearOfConstruction { get; set; }
	public string ZipCode { get; set; }
	public bool Active { get; set; }
	public string MailingOwnerFirstName { get; set; }
	public string MailingOwnerLastName { get; set; }
	public string MailingStreetName { get; set; }
	public string MailingStreetNumber { get; set; }
	public int? MailingStreetSuffixID { get; set; }
	public string MailingZipCode { get; set; }
	public string MailingCity { get; set; }
	public int? MailingStateID { get; set; }
	public int? MailingCountyID { get; set; }
	public string MailingPhoneNumber { get; set; }
	public string MailingCareOf { get; set; }
	public string PhoneNumber { get; set; }
	public string CellPhone { get; set; }
	public string Datum { get; set; }
	public string CommunityInformation { get; set; }
	public DateTime? LastUpdate { get; set; }
}

/// <summary>One damage inspection of a property after an event (DAL AssessmentModel).</summary>
[Table("Assessment")]
public sealed class AssessmentRecord
{
	[Key] public Guid AssessmentID { get; set; }
	public int? CauseofDamageID { get; set; }
	public DateTime? DateofInspection { get; set; }
	public DateTime? DateDamageOccurred { get; set; }
	public int? DepreciationRatingID { get; set; }
	public decimal? DepreciationValue { get; set; }
	public decimal? DepthOfFlood { get; set; }
	[JetScale(4)] public decimal? DepthOfFloodAboveFFE { get; set; }
	[JetScale(0)] public decimal? DurationOfFlood { get; set; }
	public int? DurationOfFloodUnitsID { get; set; }
	public decimal? GeographicAdjustment { get; set; }
	public string InspectorName { get; set; }
	public string InspectorPhoneNumber { get; set; }
	/// <summary>Despite the name, this is SDE's "Damage Undetermined" checkbox.</summary>
	public bool PhysicalDamage { get; set; }
	public Guid PropertyID { get; set; }
	public bool Active { get; set; }
	/// <summary>Percent damaged as a percentage (e.g. 52.3), Numeric(4,1).</summary>
	[JetScale(1)] public decimal? PercentDamage { get; set; }
	public int? UndeterminedDamageTypeID { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? BaseCost { get; set; }
	public string CostData { get; set; }
	public DateTime? CostDataDate { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? MarketAppraisal { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? TaxAssessedValue { get; set; }
	[JetScale(2)] public decimal? FactorAdjustment { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? ContractorEstimate { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? CommunityEstimate { get; set; }
	[JetScale(2)] public decimal? ExistingImprovementPercent { get; set; }
	/// <summary>390 Computed ACV, 391 Professional Market Appraisal, 392 Adjusted Tax Assessed Value.</summary>
	public int? ValueOfBuildingID { get; set; }
	/// <summary>393 Computed Damages, 394 Contractor's Estimate, 395 Community's Estimate.</summary>
	public int? CostOfRepairsID { get; set; }
	public string CauseOfDamageDescription { get; set; }
	public string DepreciationExplanation { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? ReplacementCost { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? ComputedDamages { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? ComputedActualCashValue { get; set; }
	public decimal? RepairReconstructPercent { get; set; }
	public string DamageMessage { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? TotalReplacementCost { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? TotalEstimatedDamages { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? ReplacementCostSqFt { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? TotalAdjustments { get; set; }
	public DateTime? LastUpdate { get; set; }
}

/// <summary>A photo or file attached to an assessment (DAL AttachmentModel). Files live in the attachments folder.</summary>
[Table("Attachment")]
public sealed class AttachmentRecord
{
	[Key, AutoIncrement] public int AttachmentID { get; set; }
	public string AttachmentType { get; set; }
	public bool DefaultImage { get; set; }
	public string Description { get; set; }
	public string FileName { get; set; }
	public Guid AssessmentID { get; set; }
	public bool Active { get; set; }
}

/// <summary>
/// User-entered % damaged for one building element plus the computed cost (DAL ElementPercentageModel).
/// ItemCost and DamageValues are strings in FEMA's model (and therefore in Export.json) although the
/// columns are Currency; they are kept as strings for compatibility.
/// </summary>
[Table("ElementPercentage")]
public sealed class ElementPercentageRecord
{
	public Guid AssessmentID { get; set; }
	/// <summary>Element lookup ID (Lookup type 2): 4 Foundation … 15 HVAC, 16 Skirting, 17 Interiors.</summary>
	public int CalcID { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public string DamageValues { get; set; }
	[Key] public Guid ElementID { get; set; }
	public string ElementName { get; set; }
	public decimal? ElementPercentage { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public string ItemCost { get; set; }
	public decimal UserDamage { get; set; }
}

/// <summary>A standard cost adjustment line (Lookup type 23) on an assessment (DAL CostAdjustmentResultModel).</summary>
[Table("CostAdjustmentResult")]
public sealed class CostAdjustmentResultRecord
{
	[Key] public Guid ResultID { get; set; }
	public int CostAdjustmentLookupID { get; set; }
	public Guid AssessmentID { get; set; }
	public decimal? Quantity { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? UnitCost { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? TotalCost { get; set; }
	public string Units { get; set; }
}

/// <summary>A free-form additional adjustment line (up to 7) on an assessment (DAL AdditionalAdjustmentModel).</summary>
[Table("AdditionalAdjustment")]
public sealed class AdditionalAdjustmentRecord
{
	[Key] public Guid AdditionalAdjustmentID { get; set; }
	public Guid AssessmentID { get; set; }
	public string AdjustmentName { get; set; }
	public decimal? Quantity { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? UnitCost { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? TotalCost { get; set; }
	public int? zOrder { get; set; }
}

/// <summary>A custom-field value for a property or an assessment (DAL CustomFieldResultModel).</summary>
[Table("CustomField_Results")]
public sealed class CustomFieldResultRecord
{
	[Key] public Guid CustomFieldID { get; set; }
	[Key] public Guid ObjectIdentifier { get; set; }
	public string Result { get; set; }
	/// <summary>"property" or "assessment".</summary>
	public string ResultType { get; set; }
	public int zOrder { get; set; }
}

/// <summary>
/// A square-footage calculator shape for a property (DAL ShapesExportModel order, used by Export.json).
/// ShapeType: l, t, r (rectangle), u, or c (total entered directly).
/// </summary>
[Table("Shapes")]
public sealed class ShapeRecord
{
	[Key, AutoIncrement] public int? ShapeID { get; set; }
	public string ShapeType { get; set; }
	public Guid PropertyID { get; set; }
	public string DescriptionA { get; set; }
	public string DescriptionB { get; set; }
	public string DescriptionC { get; set; }
	[JetScale(0)] public decimal? LengthA { get; set; }
	[JetScale(0)] public decimal? LengthB { get; set; }
	[JetScale(0)] public decimal? LengthC { get; set; }
	[JetScale(0)] public decimal? LengthD { get; set; }
	public int? StoriesA { get; set; }
	public int? StoriesB { get; set; }
	public int? StoriesC { get; set; }
	[JetScale(0)] public decimal? TotalSqFt { get; set; }
	[JetScale(0)] public decimal? WidthA { get; set; }
	[JetScale(0)] public decimal? WidthB { get; set; }
	[JetScale(0)] public decimal? WidthC { get; set; }
	[JetScale(0)] public decimal? WidthD { get; set; }
}

/// <summary>Recently opened assessments (DAL LastFiveAssesmentsModel; table name is FEMA's spelling).</summary>
[Table("LastFiveAssesments")]
public sealed class RecentAssessmentRecord
{
	[Key] public Guid AssessmentID { get; set; }
	public Guid PropertyID { get; set; }
	public string StreetAddress { get; set; }
	public string StructureType { get; set; }
	public DateTime LastUpdate { get; set; }
}
