using Sde.Core;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>An assessment as get_assessment and the sde://assessment/{id} resource show it.</summary>
internal static class AssessmentReader
{
	/// <summary>
	/// Every field that applies to the assessment, by editor step, with SDE's labels and display values (the editor's
	/// and the phone form's rule: a field that doesn't apply right now isn't shown), plus the element rows and result.
	/// </summary>
	public static AssessmentDetails Details(SdeStore store, AssessmentFile file, string database)
	{
		ReferenceData r = store.Reference;
		AssessmentStatusRecord status = store.GetStatus(file.Assessment.AssessmentID);
		List<StepFields> steps = new List<StepFields>();
		foreach (IGrouping<int, FieldInfo> step in FieldCatalog.All.Where(f => f.AppliesTo(file)).GroupBy(f => f.Step).OrderBy(g => g.Key))
		{
			List<FieldValue> fields = step.Select(f =>
			{
				object value = FieldCatalog.GetValue(file, f);
				string shown = Describe.Display(f, value, r);
				string stored = FieldCatalog.Canonical(value);
				return new FieldValue(f.Key, f.Label, Describe.Shorten(shown, FieldInput.MaxTextLength), stored == shown ? null : stored);
			}).ToList();
			steps.Add(new StepFields(step.Key, FieldCatalog.Steps[step.Key - 1], fields));
		}
		return new AssessmentDetails(database, file.Assessment.AssessmentID.ToString(), file.Property.PropertyID.ToString(),
			Describe.Address(file.Property, r), Describe.StructureType(file.Property.StructureTypeID),
			AssessmentStatuses.Label(status.Value), status.IsLocked, status.ChangedBy, status.ChangedOn,
			Describe.Result(file.Assessment), file.Attachments.Count(x => x.Active), file.Assessment.LastUpdate,
			steps, Describe.Elements(file));
	}
}
