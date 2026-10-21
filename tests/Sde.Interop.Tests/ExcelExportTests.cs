using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Interop.Tests;

/// <summary>Excel export: FEMA's 218 columns, display text, fixed binding bugs.</summary>
public class ExcelExportTests
{
	/// <summary>
	/// The headers, in order, from the original grid's XAML (decomp/wpfSDE/wpfSDE.ImportExport/ucExcelExport.xaml,
	/// snapshotted in tests/fixtures/fema-facts).
	/// </summary>
	private static List<string> XamlHeaders() => FemaFacts.ExcelHeaders();

	[Fact]
	public void Headers_AreTheOriginal218InOrder()
	{
		List<string> fema = XamlHeaders();
		Assert.Equal(218, fema.Count);
		Assert.Equal(fema, ExcelExport.Headers);
	}

	[Fact]
	public void Write_SampleAssessment_HasDisplayValues()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile saved = SampleCases.RanchFlood(t.Store);
		AssessmentFile f = t.Store.LoadAssessment(saved.Assessment.AssessmentID);
		// In memory only (the export never touches the photo files): fields the original always exported blank.
		f.Assessment.CauseOfDamageDescription = "Levee overtopped";
		f.Attachments = Enumerable.Range(1, 3).Select(i => new AttachmentRecord
		{
			AttachmentID = i, AssessmentID = f.Assessment.AssessmentID, FileName = $"photo{i}.jpg", Description = $"Photo {i}", Active = true,
		}).ToList();

		string path = Path.Combine(t.Folder, "export.xlsx");
		ExcelExport.Write(new[] { f }, t.Store.Reference, path);

		using XLWorkbook wb = new XLWorkbook(path);
		IXLWorksheet ws = wb.Worksheet(1);
		List<string> header = Enumerable.Range(1, 218).Select(c => ws.Cell(1, c).GetString()).ToList();
		Assert.Equal(XamlHeaders(), header);
		Assert.True(ws.Cell(1, 219).IsEmpty());
		Assert.True(ws.Cell(1, 1).Style.Font.Bold);
		Assert.Equal(1, ws.SheetView.SplitRow);
		Assert.Equal(2, ws.LastRowUsed().RowNumber());

		Dictionary<string, string> row = new();
		for (int c = 1; c <= 218; c++)
		{
			row.TryAdd(header[c - 1], ws.Cell(2, c).GetString());
		}
		Assert.Equal("Residential", row["Structure Type"]);
		Assert.Equal("Pat", row["Structure Owner First Name"]);
		Assert.Equal("Street", row["Structure Street Suffix"]);
		Assert.Equal("Ohio", row["Structure State"]);
		Assert.Equal("43702", row["Structure Zip Code"]);
		Assert.Equal("Single Family Residence", row["Residential Type (Only for Residential Structures)"]);
		Assert.Equal("Crawlspace", row["Foundation (Only for Residential Structures)"]);
		Assert.Equal("One Story (Standard)", row["Number of Stories"]);
		Assert.Equal("1962", row["Year of Construction"]);
		Assert.Equal("Flood", row["Cause of Damage"]);
		Assert.Equal("9/14/2026", row["Date of Damage"]);
		Assert.Equal("2.00", row["Duration of Flood"]);
		Assert.Equal("Days", row["Measure for Duration of Flood"]);
		Assert.Equal("1,450.00", row["Total Square Footage"]);
		Assert.Equal("0.93", row["Geographic Adjustment"]);
		Assert.Equal("$118.75", row["Base Cost"]);
		Assert.Equal("", row["Latitude"]);                                  // null → blank
		Assert.Equal("4 - Average Condition", row["Depreciation Determination"]);
		Assert.Equal("75.00", row["Percent Damaged"]);
		Assert.Equal(f.Assessment.DamageMessage, row["Damage Message"]);
		Assert.Equal("No", row["Damage Undetermined"]);
		Assert.Equal("Computed Actual Cash Value", row["Value of Structure"]);
		Assert.Equal("Computed Damages", row["Cost of Repair"]);
		Assert.Equal(f.Property.PropertyID.ToString(), row["PropertyID"]);
		Assert.Equal(f.Assessment.AssessmentID.ToString(), row["AssessmentID"]);

		// Standard cost adjustments in the saved order; Garage is the 6th for a single-family residence.
		Assert.Equal("Garage", row["Cost Adjustment 6 Name"]);
		Assert.Equal("240.00", row["Cost Adjustment 6 Quantity"]);
		Assert.Equal("Sq Ft", row["Cost Adjustment 6 Unit"]);
		Assert.Equal("$32.50", row["Cost Adjustment 6 Unit Cost"]);
		Assert.Equal("$7,800.00", row["Cost Adjustment 6 Element Cost"]);

		// Fixed: additional adjustments (always blank in the original; item cost showed the unit cost).
		Assert.Equal("Shed", row["Additional Cost Adjustments 1 Name"]);
		Assert.Equal("1.00", row["Additional Cost Adjustments 1 Quantity"]);
		Assert.Equal("$2,500.00", row["Additional Cost Adjustments 1 Unit Cost"]);
		Assert.Equal("$2,500.00", row["Additional Cost Adjustments 1 Item Cost"]);
		Assert.Equal("", row["Additional Cost Adjustments 2 Name"]);
		Assert.Equal("$10,300.00", row["Total Adjustments"]);

		// Fixed: "Other Cause of Damage" and "Attachment Name 3".
		Assert.Equal("Levee overtopped", row["Other Cause of Damage"]);
		Assert.Equal("photo3.jpg", row["Attachment Name 3"]);
		Assert.Equal("photo3.jpg", row["Attachment Path 3"]);
		Assert.Equal("Photo 3", row["Attachment Description 3"]);
		Assert.Equal("", row["Attachment Name 4"]);

		// Elements: names in saved order, % damaged, money.
		ElementPercentageRecord interior = f.ElementPercentages.Single(e => e.ElementName == "Interior Finish");
		int n = f.ElementPercentages.IndexOf(interior) + 1;
		Assert.Equal("Interior Finish", row[$"Damage Element {n} Name"]);
		Assert.Equal("90.00", row[$"Damage Element {n} Percent Damaged"]);
		Assert.Matches(@"^\$[\d,]+\.\d\d$", row[$"Damage Element {n} Element Cost"]);
		Assert.Matches(@"^\$[\d,]+\.\d\d$", row["Computed Damages"]);
		Assert.Matches(@"^\$[\d,]+\.\d\d$", row["Replacement Cost per Square Foot"]);
	}

	[Fact]
	public void Rows_ZeroMoneyIsBlank_NegativeInParentheses()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		f.Assessment.ContractorEstimate = 0m;
		f.Assessment.CommunityEstimate = -12.5m;
		f.Property.ZipCode = "437021234";
		IReadOnlyList<string> row = ExcelExport.Rows(new[] { f }, t.Store.Reference).Single();
		string Value(string header) => row[ExcelExport.Headers.ToList().IndexOf(header)];
		Assert.Equal("", Value("Contractor Value"));
		Assert.Equal("($12.50)", Value("Community Value"));
		Assert.Equal("43702-1234", Value("Structure Zip Code"));
	}
}
