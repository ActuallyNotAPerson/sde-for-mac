using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Sde.App.ViewModels.Editor;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>
/// A community-sized database: after a flood a co-op may assess hundreds of structures. The list must stay quick to
/// load and search, and editing must stay instant (every change recalculates). Bounds are generous; on an M-series
/// Mac the measured times are a small fraction of them.
/// </summary>
public class ScaleTests
{
	/// <summary>Fills the database with <paramref name="count"/> copies of the sample ranch (new IDs, different street numbers), via an import.</summary>
	private static void Populate(SdeStore store, int count)
	{
		SampleCases.RanchFlood(store);
		ExchangeService x = new ExchangeService(store);
		string one = SdeExchangeFolder.Serialize(x.ReadDataset());
		Regex guid = new Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");
		SdeDataset all = new SdeDataset();
		for (int i = 1; i < count; i++)
		{
			Dictionary<string, string> ids = new();
			SdeDataset copy = SdeExchangeFolder.Deserialize(guid.Replace(one, m => ids.TryGetValue(m.Value, out string g) ? g : ids[m.Value] = Guid.NewGuid().ToString()));
			copy.Properties[0].StreetNumber = (1000 + i).ToString();
			copy.ElementPercentages.ForEach(e => e.UserDamage = (i * 7 + e.CalcID * 13) % 21 * 5);
			all.Properties.AddRange(copy.Properties);
			all.Assessments.AddRange(copy.Assessments);
			all.ElementPercentages.AddRange(copy.ElementPercentages);
			all.CostAdjustments.AddRange(copy.CostAdjustments);
			all.AdditionalAdjustments.AddRange(copy.AdditionalAdjustments);
		}
		x.Import(all, null, ExistingRecordAction.Skip);
	}

	[AvaloniaFact]
	public void AThousandStructures_ListLoadsSearchesAndOpensQuickly()
	{
		using UiSession ui = new UiSession();
		Populate(ui.Store, 1000);

		Stopwatch sw = Stopwatch.StartNew();
		ui.Services.RaiseDataChanged();
		ui.Pump();
		TimeSpan load = sw.Elapsed;
		Assert.Equal(1000, ui.Main.Structures.Rows.Count);
		Assert.True(load < TimeSpan.FromSeconds(3), $"list load {load.TotalMilliseconds:0} ms");

		sw.Restart();
		ui.Main.Structures.Search = "1234 River";
		ui.Pump();
		TimeSpan search = sw.Elapsed;
		Assert.Single(ui.Main.Structures.Rows);
		Assert.True(search < TimeSpan.FromSeconds(1), $"search {search.TotalMilliseconds:0} ms");
		ui.Main.Structures.Search = "";
		ui.Pump();
		ui.Snapshot("02-structures-1000");

		sw.Restart();
		ui.Main.OpenAssessment(ui.Main.Structures.Rows[500].AssessmentID!.Value);
		ui.Pump();
		Assert.NotNull(ui.Main.Editor);
		Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"open {sw.Elapsed.TotalMilliseconds:0} ms");
		TestContext.Current.TestOutputHelper?.WriteLine($"1,000 structures: list {load.TotalMilliseconds:0} ms, search {search.TotalMilliseconds:0} ms, open {sw.Elapsed.TotalMilliseconds:0} ms");
	}

	[AvaloniaFact]
	public void Editing_RecalculatesInstantly()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		AssessmentEditorViewModel e = ui.Main.Editor;

		const int edits = 200;
		Stopwatch sw = Stopwatch.StartNew();
		for (int i = 0; i < edits; i++)
		{
			e.Elements[i % e.Elements.Count].UserDamage = i % 21 * 5; // what dragging a slider does
		}
		ui.Pump();
		double perEdit = sw.Elapsed.TotalMilliseconds / edits;
		Assert.True(perEdit < 25, $"{perEdit:0.0} ms per edit");
		TestContext.Current.TestOutputHelper?.WriteLine($"{perEdit:0.00} ms per edit (recalculation included)");
		e.Save();
		Assert.Equal(e.PercentText, $"{ui.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment.PercentDamage:0.0}%");
	}
}
