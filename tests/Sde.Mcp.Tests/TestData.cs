using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Mcp.Tests;

/// <summary>Made-up structures, events and teams for the tool tests (synthetic data only).</summary>
internal static class TestData
{
	/// <summary>A residential structure on Sample Road with a blank assessment, optionally placed on the map.</summary>
	public static AssessmentFile Structure(SdeStore store, string number, string street = "Sample", decimal? latitude = null, decimal? longitude = null, string owner = "Owner")
	{
		PropertyRecord p = store.SaveProperty(new PropertyRecord
		{
			StructureTypeID = 1, StreetNumber = number, StreetName = street, City = "Testville", OwnerLastName = owner,
			Latitude = latitude, Longitude = longitude,
		});
		return store.CreateAssessment(p.PropertyID, applyDefaults: false);
	}

	/// <summary>The sample ranch (75%, substantially damaged) placed on the map.</summary>
	public static AssessmentFile PlacedRanch(SdeStore store)
	{
		AssessmentFile ranch = SampleCases.RanchFlood(store);
		return store.Apply(ranch.Assessment.AssessmentID, f =>
		{
			f.Property.Latitude = 39.70m;
			f.Property.Longitude = -84.20m;
		});
	}

	/// <summary>A flood event starting 2026-09-14.</summary>
	public static EventRecord Event(SdeStore store, string name = "September flood", string disasterNumber = "DR-4999") =>
		store.SaveEvent(new EventRecord { Name = name, Kind = "Flood", DRNumber = disasterNumber, StartDate = new DateTime(2026, 9, 14) });

	/// <summary>A field team.</summary>
	public static TeamRecord Team(SdeStore store, string name) => store.SaveTeam(new TeamRecord { Name = name });
}
