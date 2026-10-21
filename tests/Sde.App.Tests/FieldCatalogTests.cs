using Avalonia.Headless.XUnit;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.App.Tests;

/// <summary>
/// FieldCatalog lists every field a person can edit; bulk edit, copy-from, history, help and the phone form rely on it.
/// The editor's fields must all be in it, on the same step (labels may differ: the editor groups fields under headings).
/// </summary>
public class FieldCatalogTests
{
	[AvaloniaFact]
	public void EveryEditorField_IsInTheCatalog_OnTheSameStep()
	{
		using UiSession ui = new UiSession();
		AssessmentFile ranch = SampleCases.RanchFlood(ui.Store);
		PropertyRecord shop = ui.Store.SaveProperty(new PropertyRecord { StreetNumber = "5", StreetName = "Main", StructureTypeID = 2 });
		AssessmentFile nonResidential = ui.Store.CreateAssessment(shop.PropertyID, applyDefaults: false);

		List<string> missing = new List<string>();
		HashSet<string> seen = new HashSet<string>();
		foreach (Guid id in new[] { ranch.Assessment.AssessmentID, nonResidential.Assessment.AssessmentID })
		{
			ui.Main.OpenAssessment(id);
			ui.Pump();
			AssessmentEditorViewModel e = ui.Main.Editor;
			for (int step = 0; step < e.Sections.Count; step++)
			{
				foreach (FieldViewModel f in e.Sections[step].AllFields)
				{
					FieldModel model = f.RecordType == typeof(PropertyRecord) ? FieldModel.Property : FieldModel.Assessment;
					FieldInfo info = FieldCatalog.Find(model, f.Member);
					seen.Add(model + "." + f.Member);
					if (info == null)
					{
						missing.Add($"{model}.{f.Member} ({f.Label}) is not in FieldCatalog");
					}
					else if (info.Step != step + 1)
					{
						missing.Add($"{info.Key} is on step {step + 1} in the editor but step {info.Step} in FieldCatalog");
					}
				}
			}
		}
		Assert.Empty(missing);
		// And the other way round: everything in the catalog is somewhere in the editor. The value and repair-cost bases
		// are chosen with option cards on the Result step rather than field boxes.
		seen.Add("Assessment." + nameof(AssessmentRecord.ValueOfBuildingID));
		seen.Add("Assessment." + nameof(AssessmentRecord.CostOfRepairsID));
		List<string> notInEditor = FieldCatalog.All.Select(f => f.Key).Where(k => !seen.Contains(k)).ToList();
		Assert.True(notInEditor.Count == 0, "In FieldCatalog but not in the editor: " + string.Join(", ", notInEditor));
	}
}
