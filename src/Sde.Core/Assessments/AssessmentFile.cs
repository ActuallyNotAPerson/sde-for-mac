using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>
/// Everything that makes up one assessment: the property it belongs to, the assessment itself and its
/// child rows. This is the unit the app loads, edits, calculates and saves (in one transaction).
/// </summary>
public sealed class AssessmentFile
{
	public PropertyRecord Property { get; set; }
	public AssessmentRecord Assessment { get; set; }
	public List<CostAdjustmentResultRecord> CostAdjustments { get; set; } = new();
	public List<AdditionalAdjustmentRecord> AdditionalAdjustments { get; set; } = new();
	public List<ElementPercentageRecord> ElementPercentages { get; set; } = new();
	public List<AttachmentRecord> Attachments { get; set; } = new();
	/// <summary>Custom-field values for the assessment (ResultType "assessment").</summary>
	public List<CustomFieldResultRecord> CustomFields { get; set; } = new();

	public bool IsResidential => Property?.StructureTypeID == 1;

	/// <summary>
	/// A new, unsaved assessment for a property, as AssessmentService.CreateNewInspection + InspectionService.AddInspection
	/// set it up: Active, inspection date and cost-data date = today, value basis = Computed ACV (390), repair basis =
	/// Computed Damages (393). With <paramref name="defaults"/>, the "Enter Default Data" values are filled in
	/// (PropertyService.CreateNewInspection).
	/// </summary>
	public static AssessmentFile CreateNew(PropertyRecord property, DefaultValuesRecord defaults = null)
	{
		AssessmentFile file = new AssessmentFile
		{
			Property = property,
			Assessment = new AssessmentRecord
			{
				AssessmentID = Guid.NewGuid(),
				PropertyID = property.PropertyID,
				Active = true,
				DateofInspection = DateTime.Today,
				CostDataDate = DateTime.Today,
				ValueOfBuildingID = LookupIds.ValueComputedAcv,
				CostOfRepairsID = LookupIds.RepairComputedDamages,
			},
		};
		if (defaults != null)
		{
			DefaultValues.ApplyToAssessment(defaults, file);
		}
		return file;
	}
}
