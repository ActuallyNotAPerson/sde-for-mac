using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Tests.Core;

public class ValidatorTests
{
	[Fact]
	public void NewEmptyResidential_FlagsFemaRequiredFields()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1 });
		AssessmentFile f = t.Store.CreateAssessment(p.PropertyID, false);
		List<ValidationIssue> issues = new AssessmentValidator(t.Store.Reference).Validate(f, checkLatLong: false);
		List<string> required = issues.Where(i => i.Severity == IssueSeverity.Required).Select(i => i.FieldName).ToList();
		Assert.Contains("NFIP Community ID", required);
		Assert.Contains("Residence Type", required);
		Assert.Contains("Depreciation Percentage", required);
		Assert.DoesNotContain("Structure Use", required);   // only for non-residential
		Assert.DoesNotContain("Assessment Date", required); // new assessments are dated today
	}

	[Fact]
	public void DatesAndFloodRules()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		AssessmentValidator v = new AssessmentValidator(t.Store.Reference);
		Assert.DoesNotContain(v.Validate(f, false), i => i.ControlName is "DurationOfFlood" or "DateDamageOccurred");

		f.Assessment.DateDamageOccurred = f.Assessment.DateofInspection!.Value.AddDays(3);
		f.Assessment.DurationOfFloodUnitsID = null;
		List<ValidationIssue> issues = v.Validate(f, false);
		Assert.Contains(issues, i => i.RuleId == 189); // damage after inspection
		Assert.Contains(issues, i => i.RuleId == 213); // inspection before damage
		Assert.Contains(issues, i => i.RuleId == 209); // flood duration units missing

		f.Assessment.DateofInspection = DateTime.Today.AddDays(1);
		Assert.Contains(v.Validate(f, false, DateTime.Today), i => i.RuleId == 208); // assessment date in the future
	}

	[Fact]
	public void LatLongInState_OnlyWhenPreferenceOn()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		AssessmentValidator v = new AssessmentValidator(t.Store.Reference);
		f.Property.Latitude = 39.10m;   // Exampleville, Ohio (StateID 36)
		f.Property.Longitude = -84.51m;
		Assert.DoesNotContain(v.Validate(f, true), i => i.RuleId is 202 or 203);
		f.Property.Latitude = 29.95m;   // New Orleans
		f.Property.Longitude = -90.07m;
		Assert.Contains(v.Validate(f, true), i => i.RuleId == 202);
		Assert.Contains(v.Validate(f, true), i => i.RuleId == 203);
		Assert.DoesNotContain(v.Validate(f, false), i => i.RuleId is 202 or 203);
	}

	[Fact]
	public void UndeterminedDamage_SkipsDepreciationRule_AndReportsMessage()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		f.Assessment.DepreciationValue = null;
		AssessmentValidator v = new AssessmentValidator(t.Store.Reference);
		Assert.Contains(v.Validate(f, false), i => i.RuleId == 197);
		f.Assessment.PhysicalDamage = true;
		f.Assessment.UndeterminedDamageTypeID = 360;
		Assert.DoesNotContain(v.Validate(f, false), i => i.RuleId == 197);
		t.Store.SaveAssessment(f);
		Assert.Equal("No Physical Damage Sustained", f.Assessment.DamageMessage);
		Assert.Equal(0m, f.Assessment.PercentDamage);
	}
}
