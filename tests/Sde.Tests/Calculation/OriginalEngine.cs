using Newtonsoft.Json;
#if HAS_ANSWER_KEY
using Orig = CalculationEngine;
#endif
using Port = Sde.Calculation;

namespace Sde.Tests.Calculation;

/// <summary>
/// Runs FEMA's untouched, decompiled CalculationEngine (reference/OriginalCalc) on macOS.
///
/// The original reads AppDomain.CurrentDomain.BaseDirectory + "JSON\\Lookup.json" (and "JSON\\REsidential.json").
/// On macOS a backslash is an ordinary filename character, so we place symlinks literally named
/// "JSON\Lookup.json" etc. in the test output folder, pointing at FEMA's real files in extracted/JSON.
/// That lets the original code run without changing a single line.
///
/// reference/OriginalCalc and decomp/ are made by tools/answer-key/setup.sh and aren't in the repo. Without them the
/// project builds without HAS_ANSWER_KEY and every test that needs the original engine is skipped.
/// </summary>
internal static class OriginalEngine
{
#if HAS_ANSWER_KEY
	private static readonly Lazy<bool> _ready = new(() =>
	{
		string bin = AppDomain.CurrentDomain.BaseDirectory;
		Link(bin, "JSON\\Lookup.json", "Lookup.json");
		Link(bin, "JSON\\Commercial.json", "Commercial.json");
		Link(bin, "JSON\\REsidential.json", "Residential.json");
		// Warm the original's static caches once (loads the 90 MB Residential.json).
		_ = new Orig.Calculations();
		return true;
	});

	private static void Link(string bin, string originalName, string femaFile)
	{
		string link = bin + originalName;
		if (!File.Exists(link))
		{
			File.CreateSymbolicLink(link, Path.Combine(TestPaths.FemaJson, femaFile));
		}
	}

	/// <summary>Loads the original engine, or skips the calling test when the answer key isn't set up.</summary>
	public static void EnsureReady()
	{
		_ = TestPaths.FemaJson;
		_ = _ready.Value;
	}

	/// <summary>Runs the original engine on a copy of <paramref name="input"/> and returns the result as JSON.</summary>
	public static string Run(Port.Models.DataInputOutputModel input)
	{
		EnsureReady();
		string json = JsonConvert.SerializeObject(input);
		Orig.Models.DataInputOutputModel model = JsonConvert.DeserializeObject<Orig.Models.DataInputOutputModel>(json);
		return JsonConvert.SerializeObject(new Orig.Calculations().RunCalulations(model));
	}
#else
	/// <summary>Skips the calling test: this build has no answer key.</summary>
	public static void EnsureReady() => Assert.Skip(TestPaths.AnswerKeyMissing);

	/// <summary>Skips the calling test: this build has no answer key.</summary>
	public static string Run(Port.Models.DataInputOutputModel input)
	{
		EnsureReady();
		return null;
	}
#endif

	/// <summary>Runs the Mac port on a copy of <paramref name="input"/> and returns the result as JSON.</summary>
	public static string RunPort(Port.Models.DataInputOutputModel input, Port.ElementTables tables = null)
	{
		string json = JsonConvert.SerializeObject(input);
		Port.Models.DataInputOutputModel model = JsonConvert.DeserializeObject<Port.Models.DataInputOutputModel>(json);
		return JsonConvert.SerializeObject(new Port.Calculations(tables ?? Port.ElementTables.Default).RunCalulations(model));
	}
}
