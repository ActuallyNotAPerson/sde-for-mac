using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.Services;

/// <summary>
/// A small practice database with a handful of obviously made-up structures (fictional people and a fictional town), for
/// learning the app and for taking screenshots. It is marked as a sample, so the app says so on every page and asks
/// before anything built from it is exported.
/// </summary>
public static class SampleDatabase
{
	/// <summary>The MacSetting that marks a database as the sample.</summary>
	public const string SettingKey = "IsSample";

	/// <summary>The folder inside the app's Application Support folder that holds the sample.</summary>
	public const string FolderName = "Sample";

	public const string FileName = "SDE Sample.sqlite";

	/// <summary>The inspector named on every sample assessment.</summary>
	public const string Inspector = "Inspector A";

	/// <summary>The fictional town the sample structures are in.</summary>
	public const string Town = "Exampleville";

	/// <summary>
	/// Makes the sample database at <paramref name="path"/> (which must not exist yet) and returns the path: the
	/// structures from <see cref="AddSamples"/> plus a lightly damaged cottage and a structure whose owner refused entry.
	/// </summary>
	public static string Create(string path)
	{
		if (File.Exists(path))
		{
			throw new IOException("A database already exists at " + path);
		}
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		SdeStore store = new SdeStore(SdeDatabase.OpenOrCreate(path)) { UserName = "Sample data" };
		AddSamples(store);

		AssessmentFile cottage = AddRanch(store);
		cottage.Property.StreetNumber = "9";
		cottage.Property.StreetName = "Orchard";
		cottage.Property.OwnerFirstName = "Robin";
		cottage.Property.OwnerLastName = "Sample";
		cottage.Property.StoryTypeID = 105;
		foreach (ElementPercentageRecord e in cottage.ElementPercentages)
		{
			e.UserDamage = Math.Round(e.UserDamage * 0.3m);
		}
		cottage.Assessment.DateofInspection = new DateTime(2026, 9, 23);
		store.SaveAssessment(cottage);

		AssessmentFile refused = AddRanch(store);
		refused.Property.StreetNumber = "22";
		refused.Property.StreetName = "Orchard";
		refused.Property.OwnerFirstName = "Sam";
		refused.Property.OwnerLastName = "Placeholder";
		refused.Assessment.PhysicalDamage = true;
		refused.Assessment.UndeterminedDamageTypeID = 362;   // Resident Refused Inspection
		refused.Assessment.DepreciationExplanation = "Owner declined entry; exterior inspection only.";
		store.SaveAssessment(refused);

		store.SetSetting(SettingKey, true);
		return path;
	}

	/// <summary>
	/// Makes a new sample database in <paramref name="folder"/>, replacing any earlier one there. The folder must be
	/// named "Sample", so a wrong path can never delete anything else.
	/// </summary>
	public static string CreateFresh(string folder)
	{
		if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), FolderName, StringComparison.Ordinal))
		{
			throw new ArgumentException($"The sample folder must be named \"{FolderName}\".", nameof(folder));
		}
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
		return Create(Path.Combine(folder, FileName));
	}

	/// <summary>
	/// Adds three structures: a flooded ranch house that is substantially damaged, a two-story house, and a
	/// non-residential building with no data yet.
	/// </summary>
	public static void AddSamples(SdeStore store)
	{
		AddRanch(store);

		PropertyRecord p = store.SaveProperty(new PropertyRecord
		{
			OwnerFirstName = "Alex", OwnerLastName = "Sample", StreetNumber = "48", StreetName = "Mill Creek", StreetSuffixID = 172,
			City = Town, StateID = 36, NFIPCID = "990001", NFIPCommunityName = "Exampleville, Town of",
			StructureTypeID = 1, ResidenceTypeID = 92, FoundationTypeID = 47, SuperStructureTypeID = 130, RoofCoveringTypeID = 95,
			ExteriorFinishTypeID = 26, HVACTypeID = 49, StoryTypeID = 105, YearOfConstruction = 1928, SquareFootage = 2200,
		});
		AssessmentFile f = store.CreateAssessment(p.PropertyID, false);
		f.Assessment.BaseCost = 132.40m;
		f.Assessment.GeographicAdjustment = 0.93m;
		f.Assessment.InspectorName = Inspector;
		store.Calculator.NormalizeRows(f);
		store.Calculator.ApplyDepreciationRating(f, 385);
		foreach (ElementPercentageRecord e in f.ElementPercentages)
		{
			e.UserDamage = e.ElementName switch { "Floor Finish" => 80, "HVAC" => 100, "Interior Finish" => 60, "Electrical" => 50, _ => 30 };
		}
		store.SaveAssessment(f);

		// A non-residential building with no data yet.
		PropertyRecord office = store.SaveProperty(new PropertyRecord { StreetNumber = "700", StreetName = "Riverside", City = Town, StructureTypeID = 2, StoryTypeID = 101, StructureUseID = 106 });
		store.CreateAssessment(office.PropertyID, false);
	}

	/// <summary>A 1962 single-family ranch on a slab, flooded: 75.0 percent damaged, substantially damaged.</summary>
	private static AssessmentFile AddRanch(SdeStore store)
	{
		PropertyRecord p = store.SaveProperty(new PropertyRecord
		{
			OwnerFirstName = "Pat", OwnerLastName = "Example",
			StreetNumber = "123", StreetName = "River", StreetSuffixID = 172, City = Town, StateID = 36, ZipCode = "00000",
			NFIPCID = "990001", NFIPCommunityName = "Exampleville, Town of",
			StructureTypeID = 1, ResidenceTypeID = 92, FoundationTypeID = 45, SuperStructureTypeID = 130,
			RoofCoveringTypeID = 95, ExteriorFinishTypeID = 25, HVACTypeID = 49, StoryTypeID = 104,
			YearOfConstruction = 1962, StructureQualityID = 54, SquareFootage = 1450m,
		});
		AssessmentFile f = store.CreateAssessment(p.PropertyID, applyDefaults: false);
		AssessmentRecord a = f.Assessment;
		a.InspectorName = Inspector;
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
