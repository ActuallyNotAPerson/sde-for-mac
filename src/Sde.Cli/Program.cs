using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sde.Calculation;
using Sde.Calculation.Models;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Cli;

/// <summary>
/// Command-line front end for the SDE Mac port. Proves the recompiled FEMA logic runs natively,
/// and is handy for batch work and tests.
/// </summary>
public static class Program
{
	private const string Usage = """
		sde: FEMA Substantial Damage Estimator (Mac port) command line

		Usage: sde [--db PATH] <command> [args]

		Commands:
		  init                        Create a new SDE database (seeded with FEMA reference data)
		  info                        Database location and record counts
		  list                        List structures and their latest results
		  add CASE.json               Add a structure + assessment from a case file and calculate it
		  show ASSESSMENT-ID          Show an assessment's calculation
		  validate ASSESSMENT-ID      Run SDE's validation rules on an assessment
		  calc INPUT.json             Run FEMA's calculation engine on a raw DataInputOutputModel
		  export FOLDER [IDS...]      Write an SDE exchange folder (SDE_n/Export.json + photos)
		  import PATH [--existing skip|overwrite|copy]
		                              Import an SDE exchange folder or a FEMA SDE .mdb database
		  lookups [TYPE-ID]           List lookup values (building types, causes, …)

		Default database: ~/Library/Application Support/SDE/sde.sqlite
		Only init makes a new database. The other commands open one that already exists.
		""";

	public static int Main(string[] args)
	{
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
		List<string> a = args.ToList();
		string dbPath = TakeOption(a, "--db") ?? SdeDatabase.DefaultPath;
		if (a.Count == 0 || a[0] is "-h" or "--help" or "help")
		{
			Console.WriteLine(Usage);
			return a.Count == 0 ? 1 : 0;
		}
		try
		{
			string command = a[0];
			List<string> rest = a.Skip(1).ToList();
			return command switch
			{
				"init" => Init(dbPath),
				"info" => Info(dbPath),
				"list" => List(Store(dbPath)),
				"add" => Add(Store(dbPath), Arg(rest, 0, "CASE.json")),
				"show" => Show(Store(dbPath), Guid.Parse(Arg(rest, 0, "ASSESSMENT-ID"))),
				"validate" => Validate(Store(dbPath), Guid.Parse(Arg(rest, 0, "ASSESSMENT-ID"))),
				"calc" => Calc(Arg(rest, 0, "INPUT.json")),
				"export" => Export(Store(dbPath), Arg(rest, 0, "FOLDER"), rest.Skip(1)),
				"import" => Import(Store(dbPath), rest),
				"lookups" => Lookups(Store(dbPath), rest.Count > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : null),
				_ => throw new ArgumentException($"Unknown command '{command}'. Run 'sde help'."),
			};
		}
		catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or InvalidOperationException or FormatException or IOException)
		{
			Console.Error.WriteLine("error: " + ex.Message);
			return 2;
		}
	}

	/// <summary>
	/// Opens the database for a command. Only "init" makes a new one, so a mistyped --db path is an error rather than a
	/// new, empty database.
	/// </summary>
	private static SdeStore Store(string dbPath)
	{
		if (!File.Exists(dbPath))
		{
			throw new FileNotFoundException($"There is no SDE database at {dbPath}. Check the path, or run 'sde --db \"{dbPath}\" init' to make a new one.", dbPath);
		}
		return new SdeStore(SdeDatabase.Open(dbPath));
	}

	private static int Init(string dbPath)
	{
		SdeDatabase db = SdeDatabase.Create(dbPath);
		Console.WriteLine($"Created {db.FilePath}");
		Console.WriteLine($"Photos folder: {db.AttachmentsFolder}");
		return 0;
	}

	private static int Info(string dbPath)
	{
		SdeStore s = Store(dbPath);
		(int props, int assessments) = s.Counts();
		Console.WriteLine($"Database:    {s.Database.FilePath}");
		Console.WriteLine($"Photos:      {s.AttachmentsFolder}");
		Console.WriteLine($"Structures:  {props}");
		Console.WriteLine($"Assessments: {assessments}");
		Console.WriteLine($"Lookups:     {s.Reference.Lookups.Count}, counties: {s.Reference.Counties.Count}");
		Console.WriteLine($"Element tables: {ElementTables.Default.ResidentialKeyCount:N0} residential, {ElementTables.Default.CommercialKeyCount:N0} non-residential configurations");
		return 0;
	}

	private static int List(SdeStore s)
	{
		List<StructureSummary> rows = s.ListStructures();
		Console.WriteLine($"{"Assessment",-36}  {"Type",-7} {"Address",-32} {"Inspected",-10} {"% Dmg",6}  Result");
		foreach (StructureSummary r in rows)
		{
			Console.WriteLine($"{r.AssessmentID?.ToString() ?? "(no assessment)",-36}  {(r.StructureTypeID == 1 ? "Res" : "Non-Res"),-7} {Trunc(r.Address, 32),-32} {r.DateofInspection:yyyy-MM-dd} {r.PercentDamage,6:0.0}  {r.DamageMessage}");
		}
		Console.WriteLine($"{rows.Count} rows");
		return 0;
	}

	/// <summary>
	/// Case file: { "Property": { PropertyRecord fields }, "Assessment": { AssessmentRecord fields },
	///   "ElementDamage": { "Foundation": 40, "HVAC": 100, … }  (element name or CalcID → % damaged),
	///   "CostAdjustments": { "Garage": [quantity, unitCost], … }, "AdditionalAdjustments": [ ["Shed", 1, 2500], … ],
	///   "DepreciationRating": 386 }
	/// </summary>
	private static int Add(SdeStore s, string path)
	{
		AssessmentFile file = CaseInput.Add(s, path);
		Console.WriteLine($"Added assessment {file.Assessment.AssessmentID}");
		PrintResult(file, s.Reference);
		return 0;
	}

	private static int Show(SdeStore s, Guid id)
	{
		AssessmentFile file = s.LoadAssessment(id) ?? throw new ArgumentException("No assessment " + id);
		PrintResult(file, s.Reference);
		return 0;
	}

	private static void PrintResult(AssessmentFile f, ReferenceData r)
	{
		PropertyRecord p = f.Property;
		AssessmentRecord a = f.Assessment;
		Console.WriteLine($"  {p.StreetNumber} {p.StreetName}, {p.City}  ({(p.StructureTypeID == 1 ? r.LookupName(p.ResidenceTypeID) ?? "Residential" : r.LookupName(p.StructureUseID) ?? "Non-Residential")})");
		Console.WriteLine($"  Base cost {a.BaseCost:C}/sf × {p.SquareFootage:N0} sf × {a.GeographicAdjustment} geo adj + adjustments {a.TotalAdjustments:C}");
		Console.WriteLine($"  Replacement cost      {a.TotalReplacementCost,14:C}");
		Console.WriteLine($"  Depreciation          {a.DepreciationValue,13:0.0}%  ({r.LookupName(a.DepreciationRatingID)})");
		Console.WriteLine($"  Actual cash value     {a.ComputedActualCashValue,14:C}");
		Console.WriteLine($"  Computed damages      {a.ComputedDamages,14:C}");
		foreach (ElementPercentageRecord e in f.ElementPercentages)
		{
			Console.WriteLine($"      {e.ElementName,-26} {e.ElementPercentage,5:0.0}% of cost  {e.UserDamage,5:0.##}% damaged  = {Money(e.DamageValues),12:C}");
		}
		Console.WriteLine($"  Value basis: {r.LookupName(a.ValueOfBuildingID)};  repair basis: {r.LookupName(a.CostOfRepairsID)}");
		Console.WriteLine($"  PERCENT DAMAGED       {a.PercentDamage,13:0.0}%  →  {a.DamageMessage}");
	}

	private static int Validate(SdeStore s, Guid id)
	{
		AssessmentFile file = s.LoadAssessment(id) ?? throw new ArgumentException("No assessment " + id);
		List<ValidationIssue> issues = new AssessmentValidator(s.Reference).Validate(file, s.GetPreferences().LatLong);
		foreach (ValidationIssue i in issues.OrderBy(i => i.Severity))
		{
			Console.WriteLine($"{i.Severity,-11} {i.Tab,-16} {i.FieldName,-32} {i.Message}");
		}
		Console.WriteLine($"{issues.Count(i => i.Severity == IssueSeverity.Required)} required, {issues.Count} total");
		return 0;
	}

	private static int Calc(string path)
	{
		DataInputOutputModel input = JsonConvert.DeserializeObject<DataInputOutputModel>(File.ReadAllText(path));
		input.CostAdjustments ??= new();
		input.AdditionalAdjustments ??= new();
		input.ElementPercentages ??= new();
		input.ResidentialData ??= new();
		input.CommercialData ??= new();
		DataInputOutputModel result = new Calculations().RunCalulations(input);
		Console.WriteLine(JsonConvert.SerializeObject(result, Formatting.Indented));
		return 0;
	}

	private static int Export(SdeStore s, string folder, IEnumerable<string> ids)
	{
		ExchangeService x = new ExchangeService(s);
		List<Guid> idList = ids.Select(Guid.Parse).ToList();
		Core.Exchange.SdeDataset data = idList.Count == 0 ? x.ReadDataset() : x.ReadDataset(idList);
		Directory.CreateDirectory(folder);
		(string written, IReadOnlyList<string> missing) = x.Export(data, folder);
		Console.WriteLine($"Exported {data.Properties.Count} structures, {data.Assessments.Count} assessments to {written}");
		foreach (string m in missing)
		{
			Console.WriteLine("  photo not found: " + m);
		}
		return 0;
	}

	private static int Import(SdeStore s, List<string> rest)
	{
		string mode = TakeOption(rest, "--existing") ?? "skip";
		ExistingRecordAction existing = Enum.Parse<ExistingRecordAction>(mode, ignoreCase: true);
		string path = Arg(rest, 0, "PATH");
		ExchangeService x = new ExchangeService(s);
		ImportResult result = path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) ? x.ImportMdb(path, existing) : x.ImportFolder(path, existing);
		Console.WriteLine(result);
		foreach (string w in result.Warnings)
		{
			Console.WriteLine("  warning: " + w);
		}
		return 0;
	}

	private static int Lookups(SdeStore s, int? type)
	{
		foreach (LookupRecord l in s.Reference.Lookups.Where(l => type == null || l.LookupTypeID == type).OrderBy(l => l.LookupTypeID).ThenBy(l => l.zOrder ?? int.MinValue))
		{
			Console.WriteLine($"{l.LookupTypeID,3} {l.LookupID,5}  {l.LookupFieldName}");
		}
		return 0;
	}

	private static decimal? Money(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : null;

	private static string Trunc(string s, int n) => s == null ? "" : s.Length <= n ? s : s[..(n - 1)] + "…";

	private static string Arg(List<string> a, int i, string name) =>
		i < a.Count ? a[i] : throw new ArgumentException($"Missing {name}. Run 'sde help'.");

	private static string TakeOption(List<string> a, string name)
	{
		int i = a.IndexOf(name);
		if (i < 0 || i + 1 >= a.Count)
		{
			return null;
		}
		string v = a[i + 1];
		a.RemoveRange(i, 2);
		return v;
	}
}
