using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Cli;

/// <summary>
/// Case files (tests/cases, docs/windows-crosscheck/cases): a structure and one assessment as JSON, with element damage
/// and adjustments by name. Used by `sde add` and the tests.
/// </summary>
public static class CaseInput
{
	/// <summary>Creates the structure and assessment described by a case file, calculated and saved.</summary>
	public static AssessmentFile Add(SdeStore s, string path)
	{
		JObject json = JObject.Parse(File.ReadAllText(path));
		PropertyRecord p = json["Property"]?.ToObject<PropertyRecord>() ?? new PropertyRecord();
		p.PropertyID = Guid.Empty;
		s.SaveProperty(p);
		AssessmentFile file = s.CreateAssessment(p.PropertyID, applyDefaults: false);
		if (json["Assessment"] is JObject assessmentJson)
		{
			using JsonReader reader = assessmentJson.CreateReader();
			JsonSerializer.CreateDefault().Populate(reader, file.Assessment);
			file.Assessment.AssessmentID = file.Assessment.AssessmentID == Guid.Empty ? Guid.NewGuid() : file.Assessment.AssessmentID;
		}
		Apply(json, file, s.Reference, s.Calculator);
		s.SaveAssessment(file);
		return file;
	}

	/// <summary>Applies the friendly parts of a case file (element damage by name, adjustments by name) to an assessment.</summary>
	public static void Apply(JObject json, AssessmentFile file, ReferenceData r, AssessmentCalculator calc)
	{
		calc.NormalizeRows(file);
		if (json["DepreciationRating"] is JToken rating)
		{
			calc.ApplyDepreciationRating(file, rating.Value<int>());
		}
		if (json["DepreciationPercent"] is JToken pct)
		{
			file.Assessment.DepreciationValue = pct.Value<decimal>();
		}
		if (json["ElementDamage"] is JObject damage)
		{
			foreach (JProperty d in damage.Properties())
			{
				ElementPercentageRecord row = file.ElementPercentages.FirstOrDefault(e =>
					string.Equals(e.ElementName, d.Name, StringComparison.OrdinalIgnoreCase) || e.CalcID.ToString() == d.Name)
					?? throw new ArgumentException($"Element '{d.Name}' is not shown for this structure. Elements: {string.Join(", ", file.ElementPercentages.Select(e => e.ElementName))}");
				row.UserDamage = d.Value.Value<decimal>();
			}
		}
		if (json["CostAdjustments"] is JObject costs)
		{
			foreach (JProperty c in costs.Properties())
			{
				CostAdjustmentResultRecord row = file.CostAdjustments.FirstOrDefault(x =>
					string.Equals(r.LookupName(x.CostAdjustmentLookupID), c.Name, StringComparison.OrdinalIgnoreCase))
					?? throw new ArgumentException($"Cost adjustment '{c.Name}' does not apply. Options: {string.Join(", ", file.CostAdjustments.Select(x => r.LookupName(x.CostAdjustmentLookupID)))}");
				JArray v = (JArray)c.Value;
				row.Quantity = v[0].Value<decimal?>();
				row.UnitCost = v[1].Value<decimal?>();
			}
		}
		if (json["AdditionalAdjustments"] is JArray extras)
		{
			int z = 1;
			foreach (JArray e in extras.Cast<JArray>())
			{
				AdditionalAdjustmentRecord row = file.AdditionalAdjustments.First(x => x.zOrder == z++);
				row.AdjustmentName = e[0].Value<string>();
				row.Quantity = e[1].Value<decimal?>();
				row.UnitCost = e[2].Value<decimal?>();
			}
		}
	}
}
