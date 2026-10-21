using Sde.Core.Records;

namespace Sde.Core.Exchange;

/// <summary>
/// The contents of an SDE data exchange (FEMA's SDEExportModel, decomp/DAL/DAL.Models/SDEExportModel.cs).
/// Serialized as Export.json; property names and order are part of the format.
/// </summary>
public sealed class SdeDataset
{
	public List<PropertyRecord> Properties { get; set; } = new();
	public List<AssessmentRecord> Assessments { get; set; } = new();
	public List<AttachmentRecord> Attachments { get; set; } = new();
	public List<ElementPercentageRecord> ElementPercentages { get; set; } = new();
	public List<CostAdjustmentResultRecord> CostAdjustments { get; set; } = new();
	public List<AdditionalAdjustmentRecord> AdditionalAdjustments { get; set; } = new();
	public List<CustomFieldResultRecord> CustomFieldsResults { get; set; } = new();
	public List<ShapeRecord> Shapes { get; set; } = new();

	public int Count => Properties.Count + Assessments.Count;

	/// <summary>
	/// Keeps only the given properties and assessments and the rows that belong to them.
	/// Unlike the original filtered export (ucExportSDEdata.xaml.cs:339-340), property-level custom fields are kept.
	/// </summary>
	public SdeDataset Filter(ISet<Guid> propertyIds, ISet<Guid> assessmentIds)
	{
		HashSet<Guid> objects = new HashSet<Guid>(propertyIds);
		objects.UnionWith(assessmentIds);
		return new SdeDataset
		{
			Properties = Properties.Where(p => propertyIds.Contains(p.PropertyID)).ToList(),
			Assessments = Assessments.Where(a => assessmentIds.Contains(a.AssessmentID)).ToList(),
			Attachments = Attachments.Where(x => assessmentIds.Contains(x.AssessmentID)).ToList(),
			ElementPercentages = ElementPercentages.Where(x => assessmentIds.Contains(x.AssessmentID)).ToList(),
			CostAdjustments = CostAdjustments.Where(x => assessmentIds.Contains(x.AssessmentID)).ToList(),
			AdditionalAdjustments = AdditionalAdjustments.Where(x => assessmentIds.Contains(x.AssessmentID)).ToList(),
			CustomFieldsResults = CustomFieldsResults.Where(x => objects.Contains(x.ObjectIdentifier)).ToList(),
			Shapes = Shapes.Where(x => propertyIds.Contains(x.PropertyID)).ToList(),
		};
	}
}
