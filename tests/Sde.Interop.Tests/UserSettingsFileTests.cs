using System.Text;
using Newtonsoft.Json.Linq;
using Sde.Core.Records;

namespace Sde.Interop.Tests;

/// <summary>User settings JSON: FEMA's key names and order, chosen sections only, import adds/overwrites.</summary>
public class UserSettingsFileTests
{
	private static void Seed(TempDatabase t)
	{
		t.Store.SaveNote(new NoteRecord { NoteName = "Flood", Notes = "Water line 3 ft above floor." });
		t.Store.SaveNote(new NoteRecord { NoteName = "Refused", Notes = "Owner refused entry." });
		EnterpriseMapping m = new EnterpriseMapping { Name = "County", StreetNumberParsing = StreetNumberParsing.NumberNameSuffix };
		m[EnterpriseField.StreetNumber] = "SITE_ADDR";
		m[EnterpriseField.Zip] = "ZIP5";
		t.Store.SaveEnterpriseMapping(m.ToRecord());
		DefaultValuesRecord d = t.Store.GetDefaults() ?? new DefaultValuesRecord();
		d.City = "Exampleville";
		d.State = 36;
		d.NFIPCommunityID = "399990";
		d.BaseCost = 118.75m;
		t.Store.SaveDefaults(d);
	}

	[Fact]
	public void Export_UsesFemaKeysAndOrder()
	{
		using TempDatabase t = new TempDatabase();
		Seed(t);
		string path = Path.Combine(t.Folder, "settings.json");
		UserSettingsFile.Export(t.Store, path, notes: true, enterpriseSettings: true, defaultValues: true);

		byte[] bytes = File.ReadAllBytes(path);
		Assert.NotEqual(0xEF, bytes[0]);                                 // no BOM
		string json = Encoding.UTF8.GetString(bytes);
		Assert.StartsWith("{\r\n  \"Notes\": [\r\n", json);
		JObject root = JObject.Parse(json);
		Assert.Equal(new[] { "Notes", "EnterpriseSettings", "DefaultValues" }, root.Properties().Select(p => p.Name));
		Assert.Equal(new[] { "NoteID", "Notes", "NoteName", "Active" }, ((JObject)root["Notes"]![0]!).Properties().Select(p => p.Name));

		JObject setting = (JObject)root["EnterpriseSettings"]![0]!;
		List<string> keys = setting.Properties().Select(p => p.Name).ToList();
		Assert.Equal(38, keys.Count);
		Assert.Equal("AddressLine1", keys[0]);
		Assert.Equal("EIS_ID", keys[10]);
		Assert.Equal("zip", keys[^1]);
		Assert.Equal("SITE_ADDR", (string)setting["AddressLine1"]);
		Assert.Equal("433", (string)setting["AddressLine1Parsing"]);
		Assert.Equal("ZIP5", (string)setting["zip"]);

		JObject defaults = (JObject)root["DefaultValues"]!;
		Assert.Equal("BFE", defaults.Properties().First().Name);
		Assert.Equal("BaseCost", defaults.Properties().Last().Name);
		Assert.Equal(0, (int)defaults["DefaultValueID"]!);
		Assert.Equal("Exampleville", (string)defaults["City"]);
		Assert.Equal(36, (int)defaults["State"]!);
	}

	[Fact]
	public void Export_UnchosenSectionsAreNull()
	{
		using TempDatabase t = new TempDatabase();
		Seed(t);
		string path = Path.Combine(t.Folder, "notes.json");
		UserSettingsFile.Export(t.Store, path, notes: true, enterpriseSettings: false, defaultValues: false);
		JObject root = JObject.Parse(File.ReadAllText(path));
		Assert.Equal(JTokenType.Null, root["EnterpriseSettings"]!.Type);
		Assert.Equal(JTokenType.Null, root["DefaultValues"]!.Type);
		Assert.Equal(new UserSettingsContents(2, null, false), UserSettingsFile.Inspect(path));
	}

	[Fact]
	public void RoundTrip_AddsNotesAndMappings_OverwritesDefaults()
	{
		using TempDatabase source = new TempDatabase();
		Seed(source);
		string path = Path.Combine(source.Folder, "settings.json");
		UserSettingsFile.Export(source.Store, path, true, true, true);

		using TempDatabase target = new TempDatabase();
		target.Store.SaveNote(new NoteRecord { NoteName = "Existing", Notes = "Keep me." });
		DefaultValuesRecord old = target.Store.GetDefaults() ?? new DefaultValuesRecord();
		old.City = "Dayton";
		old.Zip = "45402";
		target.Store.SaveDefaults(old);

		UserSettingsImportResult result = UserSettingsFile.Import(target.Store, path);
		Assert.Equal(2, result.NotesAdded);
		Assert.Equal(1, result.MappingsAdded);
		Assert.True(result.DefaultsReplaced);

		List<NoteRecord> notes = target.Store.GetNotes();
		Assert.Equal(new[] { "Existing", "Flood", "Refused" }, notes.Select(n => n.NoteName));
		Assert.DoesNotContain(notes, n => source.Store.GetNotes().Any(s => s.NoteID == n.NoteID));   // new IDs

		EnterpriseMapping mapping = EnterpriseMapping.FromRecord(Assert.Single(target.Store.GetEnterpriseMappings()));
		Assert.Equal("County", mapping.Name);
		Assert.Equal("SITE_ADDR", mapping[EnterpriseField.StreetNumber]);
		Assert.Equal("ZIP5", mapping[EnterpriseField.Zip]);
		Assert.Equal(StreetNumberParsing.NumberNameSuffix, mapping.StreetNumberParsing);

		DefaultValuesRecord d = target.Store.GetDefaults();
		Assert.Equal("Exampleville", d.City);
		Assert.Null(d.Zip);                                             // overwritten, not merged
		Assert.Equal(36, d.State);
		Assert.Equal(118.75m, d.BaseCost);

		// Importing again adds the notes and mapping again (as the original did).
		UserSettingsFile.Import(target.Store, path, notes: true, enterpriseSettings: false, defaultValues: false);
		Assert.Equal(5, target.Store.GetNotes().Count);
		Assert.Single(target.Store.GetEnterpriseMappings());
	}

	[Fact]
	public void Import_ReadsAFileWrittenByFemaSde()
	{
		// As the original's Newtonsoft output would look (Windows line breaks, a section left out as null).
		const string json = "{\r\n  \"Notes\": [\r\n    {\r\n      \"NoteID\": \"0b4c3a8e-58c1-4b8c-9a55-0e2b9b6c1f11\",\r\n      \"Notes\": \"High water mark on siding.\",\r\n"
			+ "      \"NoteName\": \"HWM\",\r\n      \"Active\": true\r\n    }\r\n  ],\r\n  \"EnterpriseSettings\": null,\r\n  \"DefaultValues\": {\r\n"
			+ "    \"BFE\": \"512\",\r\n    \"CauseOfDamage\": \"20\",\r\n    \"City\": \"Exampleville\",\r\n    \"County\": 2122,\r\n    \"DefaultValueID\": 0,\r\n"
			+ "    \"State\": 36,\r\n    \"BaseCost\": 118.7500\r\n  }\r\n}";
		using TempDatabase t = new TempDatabase();
		string path = Path.Combine(t.Folder, "fema.json");
		File.WriteAllText(path, json);
		UserSettingsImportResult result = UserSettingsFile.Import(t.Store, path);
		Assert.Equal(1, result.NotesAdded);
		Assert.Equal(0, result.MappingsAdded);
		Assert.Equal("HWM", Assert.Single(t.Store.GetNotes()).NoteName);
		DefaultValuesRecord d = t.Store.GetDefaults();
		Assert.Equal("512", d.BFE);
		Assert.Equal("20", d.CauseOfDamage);
		Assert.Equal(2122, d.County);
		Assert.Equal(118.75m, d.BaseCost);
	}
}
