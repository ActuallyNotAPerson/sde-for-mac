using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Reports.Letters;

namespace Sde.Reports.Tests;

/// <summary>The compliance and ICC tracker (C7): its letters, PDF and CSV.</summary>
public class ComplianceReportTests : IDisposable
{
	private static readonly DateTime Today = new DateTime(2027, 3, 10);
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	/// <summary>The ranch, substantially damaged, with an open case past its deadline and a paid ICC claim.</summary>
	private ComplianceCaseRecord OverdueCase(out AssessmentFile file)
	{
		file = SampleCases.RanchFlood(_db.Store);
		ComplianceCaseRecord c = _db.Store.OpenComplianceCase(file.Assessment.AssessmentID);
		c.Path = CompliancePaths.Elevate;
		c.PermitNumber = "P-0001";
		c.Deadline = new DateTime(2027, 3, 1);
		c.IccClaimNumber = "ICC-123";
		c.IccStatus = IccStatuses.Paid;
		c.IccAmount = 30000m;
		return _db.Store.SaveComplianceCase(c);
	}

	private static string AllText(string path)
	{
		using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
		return string.Join("\n", doc.MainDocumentPart.Document.Descendants<Paragraph>().Select(p => p.InnerText));
	}

	[Fact]
	public void ComplianceTemplates_UseOnlyKnownFields_AndFillFromTheCase()
	{
		ComplianceCaseRecord c = OverdueCase(out AssessmentFile file);
		string folder = Path.Combine(_db.Folder, "templates");
		IReadOnlyList<string> paths = LetterTemplates.WriteComplianceTemplates(folder);

		Assert.Equal(LetterKinds.Compliance.Select(LetterTemplates.FileName), paths.Select(Path.GetFileName));
		foreach (string path in paths)
		{
			IReadOnlyList<string> placeholders = LetterMerge.Placeholders(path);
			Assert.Contains("ComplianceDeadline", placeholders);
			Assert.All(placeholders, name => Assert.Contains(name, LetterFields.Names));
			Assert.DoesNotContain("—", AllText(path));
		}
		Assert.Contains("IccClaimNumber", LetterMerge.Placeholders(paths[1]));

		IReadOnlyDictionary<string, string> fields = LetterFields.For(file, _db.Store.Reference, new LetterContext { Compliance = c });
		Assert.Equal("March 1, 2027", fields["ComplianceDeadline"]);
		Assert.Equal("Elevate", fields["CompliancePath"]);
		Assert.Equal("ICC-123", fields["IccClaimNumber"]);
		Assert.Equal("$30,000.00", fields["IccAmount"]);
		LetterMergeResult merged = LetterMerge.Merge(paths[1], fields, Path.Combine(_db.Folder, "icc.docx"));
		Assert.Empty(merged.UnknownPlaceholders);
		string text = AllText(merged.OutputPath);
		Assert.Contains("ICC-123", text);
		Assert.Contains("P-0001", text);
		Assert.DoesNotContain("«", text);

		// Without a case, the compliance fields are blank, not missing.
		Assert.Equal("", LetterFields.For(file, _db.Store.Reference)["ComplianceDeadline"]);
	}

	[Fact]
	public void LoggingAComplianceLetter_GoesInTheLetterLogAndTheTimeline()
	{
		ComplianceCaseRecord c = OverdueCase(out AssessmentFile file);
		ComplianceEntryRecord entry = _db.Store.LogComplianceLetter(c.CaseID, LetterKinds.DeadlineReminder, "/letters/reminder.docx");

		LetterLogRecord letter = Assert.Single(_db.Store.GetLetters(file.Assessment.AssessmentID));
		Assert.Equal(LetterKinds.DeadlineReminder, letter.Kind);
		Assert.Equal(letter.LetterID.ToString("D"), entry.LetterID);
		Assert.Contains(_db.Store.GetComplianceEntries(c.CaseID), e => e.Kind == "Letter" && e.Text.Contains("Deadline reminder"));
	}

	[Fact]
	public void TheReport_ListsOpenCasesFirst_MarksOverdue_AndWritesACsv()
	{
		OverdueCase(out AssessmentFile file);
		AssessmentFile second = SampleCases.RanchFlood(_db.Store);
		ComplianceCaseRecord closed = _db.Store.OpenComplianceCase(second.Assessment.AssessmentID);
		closed.Status = ComplianceStatuses.Closed;
		closed.ClosedReason = "Demolished";
		closed.Notes = "=HYPERLINK(\"x\")";
		_db.Store.SaveComplianceCase(closed);

		List<ComplianceRow> rows = ComplianceReport.Rows(_db.Store, Today);
		Assert.Equal(2, rows.Count);
		Assert.True(rows[0].Case.IsOpen);
		Assert.True(rows[0].IsOverdue);
		Assert.Equal(9, rows[0].DaysOverdue);
		Assert.False(rows[1].IsOverdue);

		string csv = Path.Combine(_db.Folder, "out", "compliance.csv");
		ComplianceReport.WriteCsv(rows, csv);
		byte[] bytes = File.ReadAllBytes(csv);
		Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
		string[] lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(3, lines.Length);
		Assert.StartsWith("Address,City,Parcel number,Owner,Kind,Path", lines[0]);
		Assert.Contains("2027-03-01,9,ICC-123,Paid,30000.00", lines[1]);
		Assert.Contains("'=HYPERLINK", lines[2]);   // a formula is never run by a spreadsheet

		string pdf = Path.Combine(_db.Folder, "out", "compliance.pdf");
		ComplianceReport.RenderToFile(rows, new ComplianceReportOptions { CommunityName = "Sample Community", GeneratedOn = Today }, pdf);
		string text = Poppler.Text(pdf);
		Assert.Contains("Compliance and ICC", text);
		Assert.Contains("Open cases", text);
		Assert.Contains("Closed cases", text);
		Assert.Contains("overdue 9 d", text);
		Assert.Contains("$30,000", text);
	}
}
