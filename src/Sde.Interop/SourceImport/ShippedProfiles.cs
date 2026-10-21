using System.Reflection;

namespace Sde.Interop;

/// <summary>
/// The example profiles that come with SDE (SourceImport/Profiles/*.json, embedded): a starting point for a new file
/// layout, never tied to one community. "Generic inspections" (a spreadsheet of inspections, matched to structures by
/// address), "County parcels" (an assessor roll that creates or updates structures, matched by parcel number) and
/// "Accela (example)" (an inspection report from one permit system, laid out as v1.5 expected).
/// </summary>
public static class ShippedProfiles
{
	public const string GenericInspections = "Generic inspections";
	public const string CountyParcels = "County parcels";
	public const string AccelaExample = "Accela (example)";

	private static readonly (string Name, string Resource)[] All =
	{
		(GenericInspections, "generic-inspections.json"),
		(CountyParcels, "county-parcels.json"),
		(AccelaExample, "accela-example.json"),
	};

	/// <summary>The examples' names, in the order the app offers them.</summary>
	public static IReadOnlyList<string> Names { get; } = All.Select(p => p.Name).ToList();

	/// <summary>A fresh copy of an example profile, or null when there is none of that name.</summary>
	public static SourceProfile Get(string name)
	{
		string resource = All.FirstOrDefault(p => p.Name == name).Resource;
		if (resource == null)
		{
			return null;
		}
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sde.Interop.Profiles." + resource)
			?? throw new InvalidOperationException($"The example profile '{name}' is missing from this build.");
		using StreamReader reader = new StreamReader(stream);
		return SourceProfile.FromJson(reader.ReadToEnd());
	}
}
