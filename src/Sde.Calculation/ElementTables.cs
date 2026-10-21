using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using Newtonsoft.Json;

namespace Sde.Calculation;

/// <summary>One row of FEMA's Lookup.json (the DB LookupID → "OldID" key map the element tables use).</summary>
public sealed class LookupRow
{
	public int LookupID { get; set; }

	public int LookupTypeID { get; set; }

	public string LookupFieldName { get; set; }

	public string LookupValue1 { get; set; }

	public string LookupValue2 { get; set; }

	public int? zOrder { get; set; }

	public int? OldID { get; set; }
}

/// <summary>An element's share of total replacement cost for one building configuration.</summary>
public readonly record struct ElementShare(string Element, decimal Percentage);

/// <summary>
/// The three data tables the original engine loaded from JSON\*.json at startup, indexed by key.
/// Original: Calculations.LoadJSON (decomp/CalculationEngine/CalculationEngine/Calculations.cs:25-45),
/// which kept rows with Element != "" and then scanned them linearly with LINQ Where (file order).
/// Here each key maps to its rows in the same file order, so lookups return identical lists.
/// </summary>
public sealed class ElementTables
{
	private readonly Dictionary<(int Type, int Id), LookupRow> _lookups;

	private readonly Dictionary<(int Residence, int Foundation, int Superstructure, int Roof, int Exterior, int Hvac, int Story), List<ElementShare>> _residential;

	private readonly Dictionary<(int Story, int Use, int Sprinkler, int Conveyance), List<ElementShare>> _commercial;

	private static readonly Lazy<ElementTables> _embedded = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

	public IReadOnlyList<LookupRow> Lookups { get; }

	/// <summary>Tables shipped inside this assembly (built from FEMA's JSON by tools/build_calc_tables.py).</summary>
	public static ElementTables Default => _embedded.Value;

	private ElementTables(List<LookupRow> lookups,
		IEnumerable<(int, int, int, int, int, int, int, ElementShare)> residential,
		IEnumerable<(int, int, int, int, ElementShare)> commercial)
	{
		Lookups = lookups;
		// Original used _Lookups.FirstOrDefault(type && id): keep the first occurrence.
		_lookups = new Dictionary<(int, int), LookupRow>();
		foreach (LookupRow row in lookups)
		{
			_lookups.TryAdd((row.LookupTypeID, row.LookupID), row);
		}
		_residential = new();
		foreach (var (r, f, s, roof, e, h, st, share) in residential)
		{
			var key = (r, f, s, roof, e, h, st);
			if (!_residential.TryGetValue(key, out List<ElementShare> list))
			{
				_residential[key] = list = new List<ElementShare>(12);
			}
			list.Add(share);
		}
		_commercial = new();
		foreach (var (st, use, spr, conv, share) in commercial)
		{
			var key = (st, use, spr, conv);
			if (!_commercial.TryGetValue(key, out List<ElementShare> list))
			{
				_commercial[key] = list = new List<ElementShare>(7);
			}
			list.Add(share);
		}
	}

	public IReadOnlyList<ElementShare> Residential(int residence, int foundation, int superstructure, int roof, int exterior, int hvac, int story)
	{
		return _residential.TryGetValue((residence, foundation, superstructure, roof, exterior, hvac, story), out List<ElementShare> list) ? list : Array.Empty<ElementShare>();
	}

	public IReadOnlyList<ElementShare> Commercial(int story, int structureUse, int sprinkler, int conveyance)
	{
		return _commercial.TryGetValue((story, structureUse, sprinkler, conveyance), out List<ElementShare> list) ? list : Array.Empty<ElementShare>();
	}

	/// <summary>Returns the lookup row, or null when (type, id) is not in Lookup.json.</summary>
	public LookupRow FindLookup(int lookupTypeId, int lookupId)
	{
		return _lookups.TryGetValue((lookupTypeId, lookupId), out LookupRow row) ? row : null;
	}

	public int ResidentialKeyCount => _residential.Count;

	public int CommercialKeyCount => _commercial.Count;

	public IEnumerable<(int Residence, int Foundation, int Superstructure, int Roof, int Exterior, int Hvac, int Story)> ResidentialKeys => _residential.Keys;

	public IEnumerable<(int Story, int Use, int Sprinkler, int Conveyance)> CommercialKeys => _commercial.Keys;

	private static ElementTables LoadEmbedded()
	{
		Assembly asm = typeof(ElementTables).Assembly;
		List<LookupRow> lookups;
		using (Stream s = asm.GetManifestResourceStream("Sde.Calculation.lookup.json"))
		using (StreamReader reader = new StreamReader(s))
		{
			lookups = JsonConvert.DeserializeObject<List<LookupRow>>(reader.ReadToEnd());
		}
		var residential = ReadTsv(asm, "Sde.Calculation.residential.tsv.gz", 9)
			.Select(c => (I(c[0]), I(c[1]), I(c[2]), I(c[3]), I(c[4]), I(c[5]), I(c[6]), new ElementShare(c[7], D(c[8]))))
			.ToList();
		var commercial = ReadTsv(asm, "Sde.Calculation.commercial.tsv.gz", 6)
			.Select(c => (I(c[0]), I(c[1]), I(c[2]), I(c[3]), new ElementShare(c[4], D(c[5]))))
			.ToList();
		return new ElementTables(lookups, residential, commercial);
	}

	/// <summary>
	/// Loads FEMA's original JSON files (Lookup.json, Commercial.json, Residential.json) from a folder,
	/// exactly as the original engine did (Newtonsoft, case-insensitive names, Element != "").
	/// Use this if FEMA publishes updated tables.
	/// </summary>
	public static ElementTables LoadFemaJson(string jsonDirectory)
	{
		List<LookupRow> lookups = JsonConvert.DeserializeObject<List<LookupRow>>(File.ReadAllText(Path.Combine(jsonDirectory, "Lookup.json")));
		List<FemaCommercialRow> com = JsonConvert.DeserializeObject<List<FemaCommercialRow>>(File.ReadAllText(Path.Combine(jsonDirectory, "Commercial.json")));
		List<FemaResidentialRow> res;
		using (StreamReader sr = new StreamReader(Path.Combine(jsonDirectory, "Residential.json")))
		using (JsonTextReader jr = new JsonTextReader(sr))
		{
			res = new JsonSerializer().Deserialize<List<FemaResidentialRow>>(jr);
		}
		return new ElementTables(lookups,
			res.Where(x => x.Element != "").Select(x => (x.ResidenceID, x.FoundationID, x.SuperstructureID, x.RoofCoveringID, x.ExteriorFinishID, x.HvacID, x.StoryID, new ElementShare(x.Element, x.Percentage))),
			com.Where(x => x.Element != "").Select(x => (x.StoryID, x.StructureUseID, x.SprinklerID, x.ConveyanceID, new ElementShare(x.Element, x.Percentage))));
	}

	private static IEnumerable<string[]> ReadTsv(Assembly asm, string resource, int columns)
	{
		using Stream s = asm.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing embedded resource " + resource);
		using GZipStream gz = new GZipStream(s, CompressionMode.Decompress);
		using StreamReader reader = new StreamReader(gz);
		reader.ReadLine(); // header
		string line;
		while ((line = reader.ReadLine()) != null)
		{
			string[] cells = line.Split('\t');
			if (cells.Length != columns)
			{
				throw new InvalidDataException($"{resource}: expected {columns} columns, got {cells.Length}");
			}
			yield return cells;
		}
	}

	private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

	// Newtonsoft parsed FEMA's Percentage strings with invariant culture; decimal.Parse keeps the same scale ("13.0" → 13.0m).
	private static decimal D(string s) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);

	private sealed class FemaResidentialRow
	{
		public string Element { get; set; }
		public decimal Percentage { get; set; }
		public int ResidenceID { get; set; }
		public int FoundationID { get; set; }
		public int SuperstructureID { get; set; }
		public int RoofCoveringID { get; set; }
		public int ExteriorFinishID { get; set; }
		public int HvacID { get; set; }
		public int StoryID { get; set; }
	}

	private sealed class FemaCommercialRow
	{
		public string Element { get; set; }
		public decimal Percentage { get; set; }
		public int StructureUseID { get; set; }
		public int SprinklerID { get; set; }
		public int ConveyanceID { get; set; }
		public int StoryID { get; set; }
	}
}
