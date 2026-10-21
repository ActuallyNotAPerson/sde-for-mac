using System.Globalization;
using System.Text;
using Sde.Cli;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// docs/windows-crosscheck: what someone with FEMA's SDE on Windows needs to check this port against the real thing.
/// The case files there are entered here, exported as an SDE folder (SDE_1), and their results written to
/// expected.csv and WORKSHEET.md. Regenerate with SDE_WRITE_CROSSCHECK=1 dotnet test tests/Sde.Tests --filter CrossCheckKit.
/// The other tests keep the committed kit in step with the code.
/// </summary>
public class CrossCheckKitTests
{
	private static string Kit => Path.Combine(TestPaths.Root, "docs", "windows-crosscheck");

	private static IEnumerable<string> CaseFiles => Directory.GetFiles(Path.Combine(Kit, "cases"), "*.json").Order(StringComparer.Ordinal);

	private static readonly string[] Columns =
	{
		"Case", "Address", "Type", "SquareFootage", "BaseCost", "GeographicAdjustment", "DepreciationPercent",
		"TotalReplacementCost", "ComputedDamages", "ComputedActualCashValue", "ValueBasis", "RepairBasis", "PercentDamaged", "Determination",
	};

	private static string Num(decimal? d) => d?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";

	private static string[] Row(string caseName, AssessmentFile f, ReferenceData r)
	{
		PropertyRecord p = f.Property;
		AssessmentRecord a = f.Assessment;
		return new[]
		{
			caseName,
			string.Join(" ", new[] { p.StreetNumber, p.StreetName, r.LookupName(p.StreetSuffixID) }.Where(x => !string.IsNullOrEmpty(x))),
			p.StructureTypeID == 1 ? "Residential" : "Non-Residential",
			Num(p.SquareFootage), Num(a.BaseCost), Num(a.GeographicAdjustment), Num(a.DepreciationValue),
			Num(a.TotalReplacementCost), Num(a.TotalEstimatedDamages), Num(a.ComputedActualCashValue),
			r.LookupName(a.ValueOfBuildingID) ?? "", r.LookupName(a.CostOfRepairsID) ?? "",
			a.PercentDamage?.ToString("0.0", CultureInfo.InvariantCulture) + "%", a.DamageMessage,
		};
	}

	private static string Csv(IEnumerable<string[]> rows) =>
		string.Concat(rows.Select(r => string.Join(",", r.Select(c => c.Contains(',') || c.Contains('"') ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)) + "\n"));

	private static List<(string Case, AssessmentFile File)> EnterCases(SdeStore store) =>
		CaseFiles.Select(f => (Path.GetFileNameWithoutExtension(f), CaseInput.Add(store, f))).ToList();

	[Fact]
	public void CrossCheckKit()
	{
		if (Environment.GetEnvironmentVariable("SDE_WRITE_CROSSCHECK") != "1")
		{
			return;
		}
		using TempDatabase t = new TempDatabase();
		List<(string Case, AssessmentFile File)> cases = EnterCases(t.Store);
		ReferenceData r = t.Store.Reference;

		string sde = Path.Combine(Kit, "SDE_1");
		if (Directory.Exists(sde))
		{
			Directory.Delete(sde, recursive: true);
		}
		string written = new ExchangeService(t.Store).Export(new ExchangeService(t.Store).ReadDataset(), t.Folder).Folder;
		Directory.CreateDirectory(Path.Combine(sde, "photos"));
		File.Copy(Path.Combine(written, "Export.json"), Path.Combine(sde, "Export.json"));
		File.WriteAllText(Path.Combine(sde, "photos", ".keep"), "");

		File.WriteAllText(Path.Combine(Kit, "expected.csv"), Csv(new[] { Columns }.Concat(cases.Select(c => Row(c.Case, t.Store.LoadAssessment(c.File.Assessment.AssessmentID), r)))));
		File.WriteAllText(Path.Combine(Kit, "WORKSHEET.md"), Worksheet(cases.Select(c => (c.Case, t.Store.LoadAssessment(c.File.Assessment.AssessmentID))).ToList(), r));
	}

	/// <summary>Each case still gives the results in expected.csv.</summary>
	[Fact]
	public void CasesStillGiveTheExpectedResults()
	{
		if (Environment.GetEnvironmentVariable("SDE_WRITE_CROSSCHECK") == "1")
		{
			return; // the kit is being rewritten
		}
		List<string[]> expected = File.ReadAllLines(Path.Combine(Kit, "expected.csv")).Skip(1)
			.Select(l => CsvRow(l)).ToList();
		using TempDatabase t = new TempDatabase();
		List<(string Case, AssessmentFile File)> cases = EnterCases(t.Store);
		Assert.Equal(expected.Count, cases.Count);
		foreach ((string name, AssessmentFile f) in cases)
		{
			string[] want = expected.Single(e => e[0] == name);
			Assert.Equal(want, Row(name, t.Store.LoadAssessment(f.Assessment.AssessmentID), t.Store.Reference));
		}
	}

	/// <summary>The committed SDE_1 folder imports and recalculates to the same results.</summary>
	[Fact]
	public void ExportFolderImportsWithTheSameResults()
	{
		if (Environment.GetEnvironmentVariable("SDE_WRITE_CROSSCHECK") == "1")
		{
			return; // the kit is being rewritten
		}
		using TempDatabase t = new TempDatabase();
		SdeDataset data = SdeExchangeFolder.Read(Path.Combine(Kit, "SDE_1"));
		ImportResult result = new ExchangeService(t.Store).ImportFolder(Path.Combine(Kit, "SDE_1"), ExistingRecordAction.Skip);
		Assert.Empty(result.Warnings);
		Assert.Equal(CaseFiles.Count(), data.Assessments.Count);
		foreach (AssessmentRecord a in data.Assessments)
		{
			AssessmentRecord now = t.Store.LoadAssessment(a.AssessmentID).Assessment;
			Assert.Equal((a.PercentDamage, a.DamageMessage, a.TotalReplacementCost, a.TotalEstimatedDamages), (now.PercentDamage, now.DamageMessage, now.TotalReplacementCost, now.TotalEstimatedDamages));
		}
	}

	private static string[] CsvRow(string line)
	{
		List<string> cells = new();
		StringBuilder cell = new();
		bool quoted = false;
		for (int i = 0; i < line.Length; i++)
		{
			char ch = line[i];
			if (quoted && ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
			else if (ch == '"') { quoted = !quoted; }
			else if (ch == ',' && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
			else { cell.Append(ch); }
		}
		cells.Add(cell.ToString());
		return cells.ToArray();
	}

	/// <summary>What to type into FEMA's SDE, screen by screen, and what it should show.</summary>
	private static string Worksheet(List<(string Case, AssessmentFile File)> cases, ReferenceData r)
	{
		StringBuilder md = new();
		md.AppendLine("# Hand-entry worksheet");
		md.AppendLine();
		md.AppendLine("Generated from `cases/*.json` by `CrossCheckKitTests`; don't edit it by hand. Enter each case in FEMA's SDE 3.0 (Windows) with the values below, tab by tab, then compare the **Output Summary** with the expected results. Fields not listed can be left blank. See README.md.");
		foreach ((string name, AssessmentFile f) in cases)
		{
			PropertyRecord p = f.Property;
			AssessmentRecord a = f.Assessment;
			string Name(int? id) => r.LookupName(id);
			void Line(string label, object value)
			{
				string text = value switch
				{
					null => null,
					decimal d => d.ToString("#,##0.####", CultureInfo.InvariantCulture),
					DateTime dt => dt.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
					bool b => b ? "Yes" : null,
					_ => value.ToString(),
				};
				if (!string.IsNullOrEmpty(text))
				{
					md.AppendLine($"| {label} | {text} |");
				}
			}
			md.AppendLine();
			md.AppendLine($"## {name}: {string.Join(" ", new[] { p.StreetNumber, p.StreetName, Name(p.StreetSuffixID) }.Where(x => !string.IsNullOrEmpty(x)))}");
			md.AppendLine();
			md.AppendLine(p.StructureTypeID == 1 ? "**Add New Residential Assessment.**" : "**Add New Non-Residential Assessment.**");
			md.AppendLine();
			md.AppendLine("| Field | Value |");
			md.AppendLine("|---|---|");
			md.AppendLine("| **Address tab** | |");
			Line("Owner first / last name", string.Join(" ", new[] { p.OwnerFirstName, p.OwnerLastName }.Where(x => !string.IsNullOrEmpty(x))));
			Line("Street number / name / suffix", string.Join(" ", new[] { p.StreetNumber, p.StreetName, Name(p.StreetSuffixID) }.Where(x => !string.IsNullOrEmpty(x))));
			Line("City / State / Zip", string.Join(", ", new[] { p.City, r.StateName(p.StateID), p.ZipCode }.Where(x => !string.IsNullOrEmpty(x))));
			md.AppendLine("| **Structure/Damage/NFIP tab** | |");
			Line("NFIP Community ID", p.NFIPCID);
			Line("Story", Name(p.StoryTypeID));
			Line("Residence Type", Name(p.ResidenceTypeID));
			Line("Structure Use", Name(p.StructureUseID));
			Line("Sprinkler System", Name(p.SprinklerSystem));
			Line("Conveyance", Name(p.Conveyance));
			Line("Foundation", Name(p.FoundationTypeID));
			Line("Superstructure", Name(p.SuperStructureTypeID));
			Line("Roof Covering", Name(p.RoofCoveringTypeID));
			Line("Exterior Finish", Name(p.ExteriorFinishTypeID));
			Line("HVAC System", Name(p.HVACTypeID));
			Line("Year of Construction", p.YearOfConstruction);
			Line("Quality", Name(p.StructureQualityID));
			Line("Inspector Name", a.InspectorName);
			Line("Date Damage Occurred", a.DateDamageOccurred);
			Line("Cause of Damage", Name(a.CauseofDamageID));
			Line("Damage Undetermined", a.PhysicalDamage ? "Yes: " + Name(a.UndeterminedDamageTypeID) : null);
			if (!a.PhysicalDamage)
			{
				md.AppendLine("| **Cost tab** | |");
				Line("Base Cost", a.BaseCost);
				Line("Geographic Adjustment", a.GeographicAdjustment);
				Line("Total Square Footage (enter directly)", p.SquareFootage);
				foreach (CostAdjustmentResultRecord c in f.CostAdjustments.Where(c => (c.Quantity ?? 0) != 0))
				{
					Line($"Cost adjustment: {Name(c.CostAdjustmentLookupID)}", $"{c.Quantity:0.##} × ${c.UnitCost:#,##0.00}");
				}
				foreach (AdditionalAdjustmentRecord x in f.AdditionalAdjustments.Where(x => !string.IsNullOrEmpty(x.AdjustmentName)))
				{
					Line($"Additional adjustment: {x.AdjustmentName}", $"{x.Quantity:0.##} × ${x.UnitCost:#,##0.00}");
				}
				Line("Depreciation Rating", Name(a.DepreciationRatingID));
				Line("Depreciation Percentage", a.DepreciationRatingID == 389 ? a.DepreciationValue : null);
				Line("Depreciation explanation", a.DepreciationRatingID == 389 ? a.DepreciationExplanation : null);
				md.AppendLine("| **Element Percentages tab** (% damaged) | |");
				foreach (ElementPercentageRecord e in f.ElementPercentages.Where(e => e.UserDamage != 0))
				{
					Line(e.ElementName, e.UserDamage.ToString("0.#", CultureInfo.InvariantCulture) + " %");
				}
				md.AppendLine("| **Output Summary tab** | |");
				Line("Professional Market Appraisal", a.MarketAppraisal);
				Line("Tax Assessed Value", a.TaxAssessedValue);
				Line("Tax Factor Adjustment", a.FactorAdjustment);
				Line("Contractor Estimate", a.ContractorEstimate);
				Line("Community Estimate", a.CommunityEstimate);
				Line("Basis for value of Structure", Name(a.ValueOfBuildingID));
				Line("Basis for cost of Repairs/Improvements", Name(a.CostOfRepairsID));
			}
			md.AppendLine();
			md.AppendLine("**Expected:**");
			md.AppendLine();
			md.AppendLine("| Output | Value |");
			md.AppendLine("|---|---|");
			if (!a.PhysicalDamage)
			{
				Line("Replacement Cost (Cost tab)", a.TotalReplacementCost);
				Line("Depreciation Percentage", a.DepreciationValue);
				Line("Computed Actual Cash Value", a.ComputedActualCashValue);
				Line("Computed Damages", a.TotalEstimatedDamages);
			}
			Line("Percent Damaged", a.PercentDamage?.ToString("0.0", CultureInfo.InvariantCulture) + " %");
			Line("Determination", a.DamageMessage);
		}
		return md.ToString();
	}
}
