using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Xunit;

namespace Sde.TestSupport;

/// <summary>
/// Facts about FEMA's file formats taken from the decompiled code: the property order of each DAL model (which is
/// the key order of Export.json) and the 218 Excel export headers. They are committed in tests/fixtures/fema-facts so
/// the format tests run without the answer key; when decomp/ is present, FemaFactsTests checks the snapshot against it.
/// </summary>
public static class FemaFacts
{
	/// <summary>The models whose property order is snapshotted.</summary>
	public static readonly string[] DalModels =
	{
		"PropertyModel", "AssessmentModel", "AttachmentModel", "ElementPercentageModel", "CostAdjustmentResultModel",
		"AdditionalAdjustmentModel", "CustomFieldResultModel", "ShapesExportModel", "SDEExportModel",
	};

	private static string DalModelsFile => Path.Combine(TestPaths.FemaFacts, "dal-models.json");

	private static string ExcelHeadersFile => Path.Combine(TestPaths.FemaFacts, "excel-export-headers.json");

	/// <summary>A DAL model's public properties as (type, name), in declaration order, from the snapshot.</summary>
	public static List<(string Type, string Name)> DalModel(string model)
	{
		Dictionary<string, List<string[]>> all = JsonConvert.DeserializeObject<Dictionary<string, List<string[]>>>(File.ReadAllText(DalModelsFile));
		return all[model].Select(p => (p[0], p[1])).ToList();
	}

	/// <summary>FEMA's Excel export headers, in order, from the snapshot.</summary>
	public static List<string> ExcelHeaders() => JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(ExcelHeadersFile));

	/// <summary>A DAL model's properties read from decomp/DAL/DAL.Models (skips when decomp/ isn't there).</summary>
	public static List<(string Type, string Name)> DalModelFromDecomp(string model)
	{
		string source = File.ReadAllText(Path.Combine(RequireDecomp(), "DAL", "DAL.Models", model + ".cs"));
		return Regex.Matches(source, @"public\s+([\w<>?.]+)\s+(\w+)\s*\{\s*get;\s*set;\s*\}")
			.Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
	}

	/// <summary>The headers from the original grid's XAML (decomp/wpfSDE/wpfSDE.ImportExport/ucExcelExport.xaml).</summary>
	public static List<string> ExcelHeadersFromDecomp()
	{
		string xaml = File.ReadAllText(Path.Combine(RequireDecomp(), "wpfSDE", "wpfSDE.ImportExport", "ucExcelExport.xaml"));
		return Regex.Matches(xaml, "<dxg:GridColumn\\s+Header=\"([^\"]*)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
	}

	/// <summary>Rewrites the snapshot from decomp/ (SDE_WRITE_FEMA_FACTS=1 dotnet test --filter FemaFacts).</summary>
	public static void WriteSnapshot()
	{
		Directory.CreateDirectory(TestPaths.FemaFacts);
		Dictionary<string, List<string[]>> models = new Dictionary<string, List<string[]>>();
		foreach (string model in DalModels)
		{
			models[model] = DalModelFromDecomp(model).Select(p => new[] { p.Type, p.Name }).ToList();
		}
		File.WriteAllText(DalModelsFile, JsonConvert.SerializeObject(models, Formatting.Indented) + "\n");
		File.WriteAllText(ExcelHeadersFile, JsonConvert.SerializeObject(ExcelHeadersFromDecomp(), Formatting.Indented) + "\n");
	}

	private static string RequireDecomp()
	{
		Assert.SkipWhen(TestPaths.Decomp == null, TestPaths.AnswerKeyMissing);
		return TestPaths.Decomp;
	}
}
