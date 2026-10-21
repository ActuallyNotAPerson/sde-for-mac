using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Reports.Letters;

namespace Sde.Reports.Tests;

/// <summary>Owner letters: placeholder merge (split runs, headers, footers, tables), fields, starter templates, batches.</summary>
public class LetterTests : IDisposable
{
	private static readonly DateTime At = new DateTime(2026, 10, 20, 14, 30, 0);
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_db.Folder, name)).FullName;

	private static Paragraph Para(params Run[] runs) => new Paragraph(runs);

	private static Run R(string text, bool bold = false)
	{
		Run r = new Run();
		if (bold)
		{
			r.AppendChild(new RunProperties(new Bold()));
		}
		r.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
		return r;
	}

	/// <summary>A small .docx with the given body, header and footer paragraphs.</summary>
	private static string MakeDocx(string path, IEnumerable<OpenXmlElement> body, Paragraph header = null, Paragraph footer = null)
	{
		using WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
		MainDocumentPart main = doc.AddMainDocumentPart();
		Body b = new Body(body);
		SectionProperties sp = new SectionProperties();
		if (header != null)
		{
			HeaderPart h = main.AddNewPart<HeaderPart>();
			h.Header = new Header(header);
			sp.AppendChild(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(h) });
		}
		if (footer != null)
		{
			FooterPart f = main.AddNewPart<FooterPart>();
			f.Footer = new Footer(footer);
			sp.AppendChild(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(f) });
		}
		b.AppendChild(sp);
		main.Document = new Document(b);
		return path;
	}

	/// <summary>All the text of a .docx: body, then headers, then footers, one line per paragraph.</summary>
	private static string AllText(string path)
	{
		using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
		MainDocumentPart main = doc.MainDocumentPart;
		IEnumerable<OpenXmlElement> roots = new OpenXmlElement[] { main.Document }
			.Concat(main.HeaderParts.Select(h => h.Header))
			.Concat(main.FooterParts.Select(f => f.Footer));
		return string.Join("\n", roots.SelectMany(r => r.Descendants<Paragraph>()).Select(p => p.InnerText));
	}

	private static readonly Dictionary<string, string> Sample = new()
	{
		["OwnerName"] = "Pat Sample",
		["PropertyAddress"] = "12 Example Street",
		["ParcelNumber"] = "000-111-222",
		["CommunityName"] = "Sample Community",
		["OfficePhone"] = "(000) 555-0100",
		["PercentDamaged"] = "52.3%",
	};

	[Fact]
	public void SplitPlaceholder_IsReplaced_KeepingTheFirstRunsFormatting()
	{
		string template = MakeDocx(Path.Combine(_db.Folder, "split.docx"), new[]
		{
			Para(R("Dear "), R("«Own", bold: true), R("er"), R("Name»"), R(":")),
			Para(R("«"), R("Parcel"), R("Number» and «PercentDamaged»")),
		});
		string output = Path.Combine(_db.Folder, "out", "split.docx");

		LetterMergeResult result = LetterMerge.Merge(template, Sample, output);

		Assert.Equal(output, result.OutputPath);
		Assert.Empty(result.UnknownPlaceholders);
		Assert.Equal(new[] { "OwnerName", "ParcelNumber", "PercentDamaged" }, result.FieldsUsed);
		using WordprocessingDocument doc = WordprocessingDocument.Open(output, false);
		List<Paragraph> paras = doc.MainDocumentPart.Document.Body.Elements<Paragraph>().ToList();
		Assert.Equal("Dear Pat Sample:", paras[0].InnerText);
		Assert.Equal("000-111-222 and 52.3%", paras[1].InnerText);
		Run named = paras[0].Elements<Run>().Single(r => r.InnerText.Contains("Pat Sample"));
		Assert.NotNull(named.RunProperties?.Bold);
		Assert.DoesNotContain(paras[0].Elements<Run>(), r => r.InnerText == "er");
		Assert.Single(Directory.GetFiles(Path.GetDirectoryName(output)));   // no temporary file left behind
	}

	[Fact]
	public void HeaderFooterAndTablePlaceholders_AreReplaced_AndCaseIsIgnored()
	{
		Table table = new Table(new TableRow(
			new TableCell(Para(R("Parcel"))),
			new TableCell(Para(R("«parcel"), R("number»")))));
		string template = MakeDocx(Path.Combine(_db.Folder, "parts.docx"), new OpenXmlElement[] { Para(R("Body «OwnerName»")), table },
			header: Para(R("«Community"), R("Name»")),
			footer: Para(R("Call «OfficePhone»")));
		string output = Path.Combine(_db.Folder, "parts-out.docx");

		LetterMergeResult result = LetterMerge.Merge(template, Sample, output);

		Assert.Empty(result.UnknownPlaceholders);
		string text = AllText(output);
		Assert.Contains("Body Pat Sample", text);
		Assert.Contains("Parcel000-111-222", text.Replace("\n", ""));
		Assert.Contains("Sample Community", text);
		Assert.Contains("Call (000) 555-0100", text);
		Assert.DoesNotContain("«", text);
	}

	[Fact]
	public void UnknownPlaceholder_IsLeftAndReported()
	{
		string template = MakeDocx(Path.Combine(_db.Folder, "unknown.docx"), new[]
		{
			Para(R("Hello «OwnerName», see «AppealDead"), R("line» and «AppealDeadline» again. Not a field: « and »")),
		});
		string output = Path.Combine(_db.Folder, "unknown-out.docx");

		LetterMergeResult result = LetterMerge.Merge(template, Sample, output);

		Assert.Equal(new[] { "AppealDeadline", "and" }, result.UnknownPlaceholders);
		Assert.Equal(new[] { "OwnerName" }, result.FieldsUsed);
		Assert.Equal("Hello Pat Sample, see «AppealDeadline» and «AppealDeadline» again. Not a field: « and »", AllText(output));
		Assert.Equal(new[] { "OwnerName", "AppealDeadline", "and" }, LetterMerge.Placeholders(template));
	}

	[Fact]
	public void MultiLineValue_BecomesLineBreaks()
	{
		string template = MakeDocx(Path.Combine(_db.Folder, "lines.docx"), new[] { Para(R("To: «MailingAddress». End")) });
		string output = Path.Combine(_db.Folder, "lines-out.docx");

		LetterMerge.Merge(template, new Dictionary<string, string> { ["MailingAddress"] = "1 Sample Road\nSample City, OH 00000" }, output);

		using WordprocessingDocument doc = WordprocessingDocument.Open(output, false);
		Paragraph p = doc.MainDocumentPart.Document.Body.Elements<Paragraph>().First();
		Assert.Equal("To: 1 Sample RoadSample City, OH 00000. End", p.InnerText);
		Assert.Single(p.Descendants<Break>());
	}

	[Fact]
	public void MissingTemplate_IsAPlainError()
	{
		FileNotFoundException e = Assert.Throws<FileNotFoundException>(() => LetterMerge.Merge(Path.Combine(_db.Folder, "nope.docx"), Sample, Path.Combine(_db.Folder, "x.docx")));
		Assert.Contains("nope.docx", e.Message);
	}

	[Fact]
	public void StarterTemplates_OpenAndUseOnlyKnownFields()
	{
		string folder = Folder("templates");
		IReadOnlyList<string> paths = LetterTemplates.WriteStarterTemplates(folder);

		Assert.Equal(new[] { LetterTemplates.SubstantialDamageFile, LetterTemplates.NotSubstantiallyDamagedFile, LetterTemplates.SubstantialImprovementFile },
			paths.Select(Path.GetFileName));
		foreach (string path in paths)
		{
			IReadOnlyList<string> placeholders = LetterMerge.Placeholders(path);
			Assert.Contains("Today", placeholders);
			Assert.Contains("MailingAddress", placeholders);
			Assert.Contains("PropertyAddress", placeholders);
			Assert.Contains("CommunityName", placeholders);   // in the header
			Assert.Contains("OfficePhone", placeholders);     // in the footer
			Assert.All(placeholders, name => Assert.Contains(name, LetterFields.Names));
			string text = AllText(path);
			Assert.DoesNotContain("\u2014", text);
			using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
			Assert.NotEmpty(doc.MainDocumentPart.Document.Body.Descendants<Table>());
			Assert.Contains("counsel", doc.PackageProperties.Description);
		}
		Assert.Contains("PercentDamaged", LetterMerge.Placeholders(paths[0]));
		Assert.Contains("is substantially damaged", AllText(paths[0]));
		Assert.Contains("is not substantially damaged", AllText(paths[1]));
		Assert.Contains("SIDeterminationWording", LetterMerge.Placeholders(paths[2]));
	}

	[Fact]
	public void StarterTemplates_KeepACommunitysEdits_UnlessOverwriting()
	{
		string folder = Folder("templates");
		string edited = Path.Combine(folder, LetterTemplates.SubstantialDamageFile);
		MakeDocx(edited, new[] { Para(R("Our own letter «OwnerName»")) });

		LetterTemplates.WriteStarterTemplates(folder);
		Assert.Equal("Our own letter «OwnerName»", AllText(edited));

		LetterTemplates.WriteStarterTemplates(folder, overwrite: true);
		Assert.Contains("substantially damaged", AllText(edited));
	}

	[Fact]
	public void Fields_ForAnAssessment_FillTheStarterTemplateCompletely()
	{
		AssessmentFile f = SampleCases.RanchFlood(_db.Store);
		f.Property.ParcelNumber = "000-111-222";
		f.Property.MailingStreetNumber = null;
		f.Property.MailingStreetName = null;
		decimal? percent = f.Assessment.PercentDamage;
		string message = f.Assessment.DamageMessage;
		LetterContext context = new LetterContext
		{
			CommunityName = "Sample Community", OfficeName = "Floodplain Office", OfficeAddress = "1 Civic Plaza\nSample City, OH 00000",
			OfficePhone = "(000) 555-0100", OfficeEmail = "floodplain@example.org", OfficialName = "A. Official", OfficialTitle = "Floodplain Administrator",
			Today = new DateTime(2026, 10, 20), Status = "Final", Extra = new Dictionary<string, string> { ["AppealDeadline"] = "November 19, 2026" },
		};

		IReadOnlyDictionary<string, string> fields = LetterFields.For(f, _db.Store.Reference, context);

		Assert.All(LetterFields.Names, n => Assert.True(fields.ContainsKey(n), n));
		Assert.Equal("October 20, 2026", fields["Today"]);
		Assert.Equal("Sample Community", fields["CommunityName"]);
		Assert.Equal(fields["PropertyAddress"], fields["MailingStreet"]);   // no mailing address: the structure's own
		Assert.Equal("Final", fields["Status"]);
		Assert.Equal("November 19, 2026", fields["appealdeadline"]);
		Assert.Equal(f.Assessment.PercentDamage?.ToString("0.0") + "%", fields["PercentDamaged"]);
		Assert.Equal(message, fields["DamageDetermination"]);
		Assert.Equal(percent, f.Assessment.PercentDamage);   // only read
		Assert.Equal(message, f.Assessment.DamageMessage);

		string folder = Folder("templates");
		string template = LetterTemplates.WriteStarterTemplates(folder)[LetterKinds.For(f.Assessment) == LetterKinds.SubstantialDamage ? 0 : 1];
		string output = Path.Combine(_db.Folder, "letter.docx");
		LetterMergeResult result = LetterMerge.Merge(template, fields, output);
		Assert.Empty(result.UnknownPlaceholders);
		string text = AllText(output);
		Assert.DoesNotContain("«", text);
		Assert.Contains("Floodplain Administrator", text);
		Assert.Contains(fields["PropertyAddress"], text);
	}

	[Fact]
	public void Fields_ForAnImprovement_UseTheSiResult()
	{
		PropertyRecord p = new PropertyRecord { PropertyID = Guid.NewGuid(), OwnerFirstName = "Pat", OwnerLastName = "Sample", StreetNumber = "12", StreetName = "Example", StateID = 36,
			MailingStreetNumber = "PO Box 1", MailingCity = "Sample City", MailingStateID = 36, MailingZipCode = "000001234", MailingCareOf = "c/o Agent" };
		ImprovementProjectRecord project = new ImprovementProjectRecord { ProjectID = Guid.NewGuid(), PropertyID = p.PropertyID, PermitNumber = "BP-1", Description = "Addition", Cost = 55000m, ProjectDate = new DateTime(2026, 6, 1) };
		SiResult si = ImprovementCalculator.Evaluate(project, null, 100000m);

		IReadOnlyDictionary<string, string> fields = LetterFields.For(p, null, _db.Store.Reference, new LetterContext { Improvement = si });

		Assert.Equal("BP-1", fields["PermitNumber"]);
		Assert.Equal("$55,000.00", fields["ImprovementCost"]);
		Assert.Equal("$100,000.00", fields["MarketValue"]);
		Assert.Equal("55.0%", fields["ImprovementPercent"]);
		Assert.Equal("50%", fields["SIThreshold"]);
		Assert.Equal("is a substantial improvement", fields["SIDeterminationWording"]);
		Assert.Equal("", fields["PercentDamaged"]);
		Assert.Equal("c/o Agent\nPO Box 1\nSample City, OH 00000-1234", fields["MailingAddress"]);
	}

	[Fact]
	public void LetterKinds_FollowTheStoredDetermination()
	{
		Assert.Equal(LetterKinds.SubstantialDamage, LetterKinds.For(new AssessmentRecord { DamageMessage = "Substantially Damaged" }));
		Assert.Equal(LetterKinds.NotSubstantiallyDamaged, LetterKinds.For(new AssessmentRecord { DamageMessage = "Not Substantially Damaged" }));
		Assert.Null(LetterKinds.For(new AssessmentRecord { DamageMessage = "Data Entry Incomplete" }));
		Assert.Equal(LetterTemplates.SubstantialImprovementFile, LetterTemplates.FileName(LetterKinds.SubstantialImprovement));
	}

	private string Template()
	{
		string path = Path.Combine(_db.Folder, "batch-template.docx");
		return File.Exists(path) ? path : MakeDocx(path, new[] { Para(R("Dear «OwnerName», about «PropertyAddress». «Mystery»")) });
	}

	private static LetterItem Item(string address, string kind, string owner) => new()
	{
		Address = address,
		Kind = kind,
		Fields = new Dictionary<string, string> { ["OwnerName"] = owner, ["PropertyAddress"] = address },
	};

	[Fact]
	public void Batch_WritesOneLetterEach_WithUniqueNames()
	{
		string parent = Folder("batch");
		Directory.CreateDirectory(Path.Combine(parent, "Letters 2026-10-20 1430"));
		LetterItem[] items =
		{
			Item("12 Example Street", LetterKinds.SubstantialDamage, "Pat Sample"),
			Item("12 Example Street", LetterKinds.SubstantialDamage, "Lee Sample"),
			Item("14 Example Street", LetterKinds.NotSubstantiallyDamaged, "Kim Sample"),
			Item("7/9 Sample: Court", LetterKinds.SubstantialDamage, "Ash Sample"),
			Item("", LetterKinds.SubstantialDamage, "Ray Sample"),
		};
		List<LetterProgress> steps = new();

		LetterBatchResult result = LetterBatch.Write(items, _ => Template(), parent, new Sync(steps.Add), now: At);

		Assert.Equal(Path.Combine(parent, "Letters 2026-10-20 1430 (2)"), result.Folder);
		Assert.Equal(new[]
		{
			"12 Example Street (Substantial damage).docx",
			"12 Example Street (Substantial damage) (2).docx",
			"14 Example Street (Not substantially damaged).docx",
			"79 Sample- Court (Substantial damage).docx",
			"No address (Substantial damage).docx",
		}, result.Files.Select(f => Path.GetFileName(f.Path)));
		Assert.All(result.Files, f => Assert.True(File.Exists(f.Path)));
		Assert.Contains("Dear Lee Sample, about 12 Example Street.", AllText(result.Files[1].Path));
		Assert.Equal(new[] { "Mystery" }, result.UnknownPlaceholders);
		Assert.Equal(6, steps.Count);
		Assert.Equal("Done", steps[^1].Step);
	}

	[Fact]
	public void Batch_Cancelled_RemovesThePartlyWrittenFolder()
	{
		string parent = Folder("batch");
		LetterItem[] items = { Item("1 Sample Road", "A", "X"), Item("2 Sample Road", "A", "Y"), Item("3 Sample Road", "A", "Z") };
		using CancellationTokenSource cancel = new CancellationTokenSource();

		Assert.ThrowsAny<OperationCanceledException>(() => LetterBatch.Write(items, _ => Template(), parent, new Sync(p =>
		{
			if (p.Done == 1)
			{
				cancel.Cancel();
			}
		}), cancel.Token, At));

		Assert.Empty(Directory.GetFileSystemEntries(parent));
	}

	[Fact]
	public void Batch_Failure_RemovesThePartlyWrittenFolder()
	{
		string parent = Folder("batch");
		LetterItem[] items = { Item("1 Sample Road", "A", "X"), Item("2 Sample Road", "B", "Y") };

		InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => LetterBatch.Write(items, i => i.Kind == "A" ? Template() : null, parent, now: At));

		Assert.Contains("2 Sample Road (B)", e.Message);
		Assert.Empty(Directory.GetFileSystemEntries(parent));
	}

	/// <summary>Reports progress immediately (Progress&lt;T&gt; would post it later to the thread pool).</summary>
	private sealed class Sync(Action<LetterProgress> report) : IProgress<LetterProgress>
	{
		public void Report(LetterProgress value) => report(value);
	}
}
