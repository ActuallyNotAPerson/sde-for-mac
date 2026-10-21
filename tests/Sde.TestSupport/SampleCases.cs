using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.TestSupport;

/// <summary>Reusable structures for tests (same as tests/cases/ranch-flood.json).</summary>
public static class SampleCases
{
	/// <summary>A 1962 single-family ranch on a slab, flooded. Hand-checked result: 75.0%, Substantially Damaged.</summary>
	public static AssessmentFile RanchFlood(SdeStore store)
	{
		PropertyRecord p = store.SaveProperty(new PropertyRecord
		{
			OwnerFirstName = "Pat", OwnerLastName = "Example",
			StreetNumber = "123", StreetName = "River", StreetSuffixID = 172, City = "Exampleville", StateID = 36, ZipCode = "43702",
			NFIPCID = "399990", NFIPCommunityName = "Exampleville, City of",
			StructureTypeID = 1, ResidenceTypeID = 92, FoundationTypeID = 45, SuperStructureTypeID = 130,
			RoofCoveringTypeID = 95, ExteriorFinishTypeID = 25, HVACTypeID = 49, StoryTypeID = 104,
			YearOfConstruction = 1962, StructureQualityID = 54, SquareFootage = 1450m,
		});
		AssessmentFile f = store.CreateAssessment(p.PropertyID, applyDefaults: false);
		AssessmentRecord a = f.Assessment;
		a.InspectorName = "Inspector A";
		a.DateDamageOccurred = new DateTime(2026, 9, 14);
		a.CauseofDamageID = LookupIds.CauseFlood;
		a.DurationOfFlood = 2;
		a.DurationOfFloodUnitsID = 359;
		a.BaseCost = 118.75m;
		a.GeographicAdjustment = 0.93m;
		AssessmentCalculator calc = store.Calculator;
		calc.NormalizeRows(f);
		calc.ApplyDepreciationRating(f, 386);
		Dictionary<string, decimal> damage = new()
		{
			["Foundation"] = 10, ["Superstructure"] = 20, ["Exterior Finish"] = 35, ["Interior Finish"] = 90,
			["Doors and Windows"] = 60, ["Cabinets and Countertops"] = 100, ["Floor Finish"] = 100,
			["Plumbing"] = 50, ["Electrical"] = 70, ["Appliances"] = 100, ["HVAC"] = 100,
		};
		foreach (ElementPercentageRecord e in f.ElementPercentages)
		{
			e.UserDamage = damage.GetValueOrDefault(e.ElementName);
		}
		CostAdjustmentResultRecord garage = f.CostAdjustments.Single(c => store.Reference.LookupName(c.CostAdjustmentLookupID) == "Garage");
		garage.Quantity = 240;
		garage.UnitCost = 32.50m;
		AdditionalAdjustmentRecord shed = f.AdditionalAdjustments.Single(x => x.zOrder == 1);
		shed.AdjustmentName = "Shed";
		shed.Quantity = 1;
		shed.UnitCost = 2500m;
		store.SaveAssessment(f);
		return f;
	}
}
