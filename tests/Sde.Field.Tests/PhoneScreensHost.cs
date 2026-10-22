using System.Net;
using System.Text.Json;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Field.Tests;

/// <summary>
/// Not a test: the phone form running with made-up data, for tools/phone-screens/shoot.mjs to photograph. It does
/// nothing unless SDE_PHONE_SCREENS names a folder; then it writes server.json there (the address and pairing token)
/// and serves until a file called "done" appears (or three minutes pass).
/// </summary>
public class PhoneScreensHost
{
	[Fact]
	public async Task ServeForScreenshots()
	{
		string folder = Environment.GetEnvironmentVariable("SDE_PHONE_SCREENS");
		if (string.IsNullOrEmpty(folder))
		{
			return;
		}
		Directory.CreateDirectory(folder);
		using TempDatabase db = new TempDatabase();
		SdeStore store = db.Store;
		store.UserName = "Reviewer A";
		AssessmentFile river = SampleCases.RanchFlood(store);
		List<Guid> properties = new List<Guid> { river.Property.PropertyID };
		foreach ((string number, string street) in new[] { ("48", "Mill Creek"), ("212", "Orchard"), ("9", "Bridge") })
		{
			PropertyRecord p = store.SaveProperty(new PropertyRecord { StructureTypeID = 1, StreetNumber = number, StreetName = street, StreetSuffixID = river.Property.StreetSuffixID, City = river.Property.City, StateID = river.Property.StateID });
			store.CreateAssessment(p.PropertyID, applyDefaults: false);
			properties.Add(p.PropertyID);
		}
		TeamRecord team = store.SaveTeam(new TeamRecord { Name = "Team North", Members = "Inspector A, Inspector B" });
		store.AssignToTeam(properties, team.TeamID, null, DateTime.Today.AddDays(2));
		store.SetAssignmentStatus(store.GetAssignments(team.TeamID)[0].AssignmentID, AssignmentStatuses.Visited);

		FieldServer server = new FieldServer(() => store, new DirectFieldWriter(() => store));
		server.Start(new FieldServerOptions { BindAddress = IPAddress.Loopback, TempFolder = Path.Combine(db.Folder, "phone-temp") });
		try
		{
			File.WriteAllText(Path.Combine(folder, "server.json"), JsonSerializer.Serialize(new { baseAddress = server.BaseAddress.ToString(), token = server.Pairing.Current.Token }));
			string done = Path.Combine(folder, "done");
			for (int i = 0; i < 1800 && !File.Exists(done); i++)
			{
				await Task.Delay(100);
			}
		}
		finally
		{
			await server.StopAsync();
			File.Delete(Path.Combine(folder, "server.json"));
		}
	}
}
