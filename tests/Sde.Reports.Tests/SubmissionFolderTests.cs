using System.Text;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports.Internal;

namespace Sde.Reports.Tests;

/// <summary>The submission folder: a report per assessment, the two lists, the FEMA SDE package and results.csv.</summary>
public class SubmissionFolderTests : IDisposable
{
	private static readonly DateTime At = new DateTime(2026, 10, 8, 14, 30, 0);
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	private SdeStore Store => _db.Store;

	/// <summary>
	/// The ranch (75.0%, imported from an Accela export, with a photo), the office, a second, empty inspection of the ranch
	/// (Data Entry Incomplete), and another office at the same address.
	/// </summary>
	private (AssessmentFile Ranch, AssessmentFile Office, AssessmentFile RanchAgain, AssessmentFile OfficeTwin) Sample()
	{
		AssessmentFile ranch = Fixtures.Ranch(Store);
		Fixtures.AddPhoto(Store, ranch, Fixtures.SolidPng(_db.Folder, 400, 300, 200, 40, 40), isDefault: true, description: "Front");
		Store.SaveImport(new[]
		{
			new ImportedAssessment
			{
				File = Store.LoadAssessment(ranch.Assessment.AssessmentID),
				Link = new ExternalLinkRecord { Source = "Accela", RecordId = "BLD-2026-00123", InspectionId = "4471" },
			},
		});
		AssessmentFile office = Fixtures.Office(Store);
		AssessmentFile ranchAgain = Store.CreateAssessment(ranch.Property.PropertyID, applyDefaults: false);
		AssessmentFile twin = Fixtures.Office(Store);
		return (Store.LoadAssessment(ranch.Assessment.AssessmentID), office, ranchAgain, twin);
	}

	private static Guid[] Ids(params AssessmentFile[] files) => files.Select(f => f.Assessment.AssessmentID).ToArray();

	private string Out => Directory.CreateDirectory(Path.Combine(_db.Folder, "out")).FullName;

	[Fact]
	public void Writes_ReportsListsPackageAndResults_LeavingOutIncompleteAssessments()
	{
		var s = Sample();
		List<SubmissionProgress> steps = new();
		SubmissionResult result = SubmissionFolder.Write(Store, Ids(s.Ranch, s.Office, s.RanchAgain, s.OfficeTwin), Out,
			new SubmissionOptions { GeneratedOn = At }, new Sync(steps.Add));

		Assert.Equal(Path.Combine(Out, "SDE submission 2026-10-08 1430"), result.Folder);
		Assert.Equal(3, result.Assessments);
		Assert.Equal(new[] { "123 River Street (Data Entry Incomplete)" }, result.Skipped);
		Assert.Empty(result.MissingPhotos);
		string[] reports = Directory.GetFiles(Path.Combine(result.Folder, "Reports")).Select(Path.GetFileName).ToArray();
		Assert.Equal(new[] { "400 Front Street (2).pdf", "400 Front Street.pdf", "BLD-2026-00123 – 123 River Street.pdf" }, reports.Order(StringComparer.Ordinal));
		Assert.Equal(new[] { "Community report.pdf", "Percent damaged list.pdf", "results.csv" },
			Directory.GetFiles(result.Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
		Assert.True(File.Exists(Path.Combine(result.Folder, "SDE_1", "Export.json")));
		Assert.Single(Directory.GetFiles(Path.Combine(result.Folder, "SDE_1", "photos")));

		string ranchPdf = Poppler.Text(Path.Combine(result.Folder, "Reports", "BLD-2026-00123 – 123 River Street.pdf"));
		Assert.Contains("123 River Street", ranchPdf);
		Assert.Contains("75.0%", ranchPdf);
		Assert.Contains("Substantially Damaged", ranchPdf);
		Assert.DoesNotContain("400 Front", ranchPdf);
		string list = Poppler.Text(Path.Combine(result.Folder, "Percent damaged list.pdf"));
		Assert.Contains("123 River Street", list);
		Assert.Contains("400 Front Street", list);

		// Progress: one step per report, then the lists, the package and results.csv.
		Assert.Equal(3 + 4 + 1, steps.Count);
		Assert.All(steps, p => Assert.Equal(7, p.Total));
		Assert.Equal("Report 1 of 3: BLD-2026-00123 – 123 River Street", steps[0].Step);
		Assert.Equal(new SubmissionProgress(7, 7, "Done"), steps[^1]);

		// A second folder at the same minute doesn't overwrite the first.
		SubmissionResult again = SubmissionFolder.Write(Store, Ids(s.Office), Out, new SubmissionOptions { GeneratedOn = At });
		Assert.Equal(result.Folder + " (2)", again.Folder);
	}

	[Fact]
	public void ThePackage_ImportsIntoAnotherDatabaseWithTheSameResults()
	{
		var s = Sample();
		SubmissionResult result = SubmissionFolder.Write(Store, Ids(s.Ranch, s.Office), Out, new SubmissionOptions { GeneratedOn = At, PerAssessmentReport = ReportKind.Detail });
		using TempDatabase state = new TempDatabase();
		ImportResult imported = new ExchangeService(state.Store).ImportFolder(Path.Combine(result.Folder, "SDE_1"), ExistingRecordAction.Skip);
		Assert.Equal((2, 2, 1), (imported.PropertiesAdded, imported.AssessmentsAdded, imported.PhotosCopied));
		foreach (AssessmentFile mine in new[] { s.Ranch, s.Office })
		{
			AssessmentRecord theirs = state.Store.LoadAssessment(mine.Assessment.AssessmentID).Assessment;
			Assert.Equal((mine.Assessment.PercentDamage, mine.Assessment.DamageMessage, mine.Assessment.ComputedActualCashValue),
				(theirs.PercentDamage, theirs.DamageMessage, theirs.ComputedActualCashValue));
		}
		// Import links stay on this Mac: the package is plain FEMA SDE data.
		Assert.Empty(state.Store.GetExternalLinks());
	}

	[Fact]
	public void ResultsCsv_HasOneRowPerAssessment_WithWhatTheReportsPrint()
	{
		var s = Sample();
		SubmissionResult result = SubmissionFolder.Write(Store, Ids(s.Ranch, s.Office), Out, new SubmissionOptions { GeneratedOn = At });
		byte[] bytes = File.ReadAllBytes(Path.Combine(result.Folder, "results.csv"));
		Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);                  // so Excel reads it as UTF-8
		List<string[]> rows = ParseCsv(Encoding.UTF8.GetString(bytes[3..]));
		Assert.Equal(ResultsCsv.Headers, rows[0]);
		Assert.Equal(3, rows.Count);
		Dictionary<string, string> ranch = rows[0].Zip(rows[1]).ToDictionary(x => x.First, x => x.Second);
		Assert.Equal(("Accela", "BLD-2026-00123"), (ranch["Source"], ranch["Record ID"]));
		Assert.Equal("4471", ranch["Inspection ID"]);
		Assert.Equal("123 River Street", ranch["Address"]);
		Assert.Equal(("OH", "43702", "092-0004-0118-00", "Pat Example"), (ranch["State"], ranch["ZIP"], ranch["Parcel number"], ranch["Owner"]));
		Assert.Equal(("2026-09-21", "Inspector A", "Residential"), (ranch["Inspection date"], ranch["Inspector"], ranch["Structure type"]));
		Assert.Equal(("Computed Actual Cash Value", "129189.26"), (ranch["Value of building based on"], ranch["Value of building"]));
		Assert.Equal(("Computed Damages", "96900.46"), (ranch["Cost of repairs based on"], ranch["Cost of repairs"]));
		Assert.Equal(("75.0", "Substantially Damaged"), (ranch["Percent damaged"], ranch["Determination"]));
		Assert.Equal(Path.Combine("Reports", "BLD-2026-00123 – 123 River Street.pdf"), ranch["Report"]);

		// The office row agrees with the report's own values.
		AssessmentView office = new AssessmentView(s.Office, Store.Reference);
		Dictionary<string, string> o = rows[0].Zip(rows[2]).ToDictionary(x => x.First, x => x.Second);
		Assert.Equal(("", "", "Non-Residential"), (o["Source"], o["Record ID"], o["Structure type"]));
		Assert.Equal(office.ValueOfBuilding, decimal.Parse(o["Value of building"]).ToString("C2", Fmt.Us));
		Assert.Equal(office.CostOfRepair, decimal.Parse(o["Cost of repairs"]).ToString("C2", Fmt.Us));
		Assert.Equal(office.DamageMessage, o["Determination"]);
	}

	[Fact]
	public void IncludingIncomplete_NamesEachInspectionOfAStructureByDate()
	{
		var s = Sample();
		SubmissionResult result = SubmissionFolder.Write(Store, Ids(s.Ranch, s.RanchAgain), Out, new SubmissionOptions { GeneratedOn = At, IncludeIncomplete = true, IncludePhotos = false });
		Assert.Empty(result.Skipped);
		Assert.Equal(new[] { $"123 River Street (inspected {DateTime.Today:yyyy-MM-dd}).pdf", "BLD-2026-00123 – 123 River Street (inspected 2026-09-21).pdf" },
			Directory.GetFiles(Path.Combine(result.Folder, "Reports")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
	}

	[Fact]
	public void Cancelling_RemovesThePartlyWrittenFolder()
	{
		var s = Sample();
		using CancellationTokenSource cancel = new CancellationTokenSource();
		Assert.Throws<OperationCanceledException>(() => SubmissionFolder.Write(Store, Ids(s.Ranch, s.Office, s.OfficeTwin), Out,
			new SubmissionOptions { GeneratedOn = At }, new Sync(p =>
			{
				if (p.Done == 1)
				{
					cancel.Cancel();
				}
			}), cancel.Token));
		Assert.Empty(Directory.GetFileSystemEntries(Out));
	}

	[Fact]
	public void NothingComplete_IsRefused()
	{
		var s = Sample();
		InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => SubmissionFolder.Write(Store, Ids(s.RanchAgain), Out));
		Assert.Contains("incomplete", e.Message);
		Assert.Empty(Directory.GetFileSystemEntries(Out));
	}

	/// <summary>Reports progress immediately (Progress&lt;T&gt; would post it later to the thread pool).</summary>
	private sealed class Sync(Action<SubmissionProgress> report) : IProgress<SubmissionProgress>
	{
		public void Report(SubmissionProgress value) => report(value);
	}

	/// <summary>RFC 4180: quoted fields, doubled quotes, CRLF rows.</summary>
	private static List<string[]> ParseCsv(string text)
	{
		List<string[]> rows = new();
		List<string> row = new();
		StringBuilder field = new StringBuilder();
		bool quoted = false;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (quoted)
			{
				if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
				{
					field.Append('"');
					i++;
				}
				else if (c == '"')
				{
					quoted = false;
				}
				else
				{
					field.Append(c);
				}
			}
			else if (c == '"')
			{
				quoted = true;
			}
			else if (c == ',')
			{
				row.Add(field.ToString());
				field.Clear();
			}
			else if (c == '\n')
			{
				row.Add(field.ToString().TrimEnd('\r'));
				field.Clear();
				rows.Add(row.ToArray());
				row.Clear();
			}
			else
			{
				field.Append(c);
			}
		}
		return rows;
	}
}
