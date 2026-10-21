using ClosedXML.Excel;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>
/// "Export Data to Excel": one row per assessment, 218 text columns.
///
/// Port of decomp/wpfSDE/wpfSDE.ImportExport/ucExcelExport.xaml(.cs) (DevExpress grid exported with
/// TextExportMode.Text) fed by BLL AssessmentService.GetAllAssessments (decomp/BLL/BLL.Service/AssessmentService.cs:41-254).
/// Column headers and order are the grid's (ucExcelExport.xaml:38-687); values are the grid's display text:
/// lookup names instead of IDs, en-US money ("$1,234.57") and number ("1,450.00") masks, short dates, and blank
/// for zero in masked number columns (the masks' NullValue="0").
///
/// Original bugs fixed:
/// <list type="bullet">
/// <item>"Additional Cost Adjustments 1-5" were always blank: the data columns were named "…Name1" + i ("…Name11"…),
/// not "…Name" + i (AssessmentService.cs:94-100). Their Item Cost also showed the unit cost (line 199).</item>
/// <item>"Attachment Name 3" was bound to "AttachmenName3" and "Other Cause of Damage" to "CuaseOfDamageDescription"
/// (ucExcelExport.xaml), so both were always blank.</item>
/// <item>"Replacement Cost per Square Foot" was bound to BaseCost (a duplicate of "Base Cost"); it now shows ReplacementCostSqFt.</item>
/// <item>Only the first 7 cost-adjustment and first 5 additional-adjustment rows fit the columns; when there are more,
/// empty rows are dropped first so filled-in adjustments are not lost. "Total Adjustments" sums all of them.</item>
/// </list>
/// </summary>
public static class ExcelExport
{
	private sealed record Column(string Header, Func<Row, string> Value);

	private static readonly List<Column> Columns = BuildColumns();

	/// <summary>The 218 column headers, in the original's order.</summary>
	public static IReadOnlyList<string> Headers { get; } = Columns.Select(c => c.Header).ToList();

	/// <summary>Writes the assessments to an .xlsx workbook (one sheet, bold frozen header row).</summary>
	public static void Write(IEnumerable<AssessmentFile> assessments, ReferenceData reference, string xlsxPath)
	{
		using XLWorkbook wb = new XLWorkbook();
		IXLWorksheet ws = wb.Worksheets.Add("Sheet1");
		SheetWriter.WriteText(ws, Headers, Rows(assessments, reference));
		SheetWriter.Finish(ws, Headers.Count);
		wb.SaveAs(xlsxPath);
	}

	/// <summary>The display text of every cell, one list per assessment, in <see cref="Headers"/> order.</summary>
	public static List<IReadOnlyList<string>> Rows(IEnumerable<AssessmentFile> assessments, ReferenceData reference) =>
		assessments.Select(f =>
		{
			Row row = new Row(f, reference);
			return (IReadOnlyList<string>)Columns.Select(c => c.Value(row) ?? "").ToList();
		}).ToList();

	// ───────────────────────── Column definitions (ucExcelExport.xaml:38-687) ─────────────────────────

	private static List<Column> BuildColumns()
	{
		List<Column> c = new()
		{
			new("Structure Type", r => r.P.StructureTypeID == 1 ? "Residential" : "Non-Residential"),
			new("Subdivision", r => r.P.Subdivision),
			new("Parcel", r => r.P.ParcelNumber),
			new("Lot", r => r.P.LotNumber),
			new("First Floor Elevation", r => PlainNonZero(r.P.FirstFloorElevation)),
			new("Datum", r => r.P.Datum),
			new("NFIP Community ID", r => r.P.NFIPCID),
			new("NFIP Community Name", r => r.P.NFIPCommunityName),
			new("Latitude", r => Coordinate(r.P.Latitude)),
			new("Longitude", r => Coordinate(r.P.Longitude)),
			new("Structure Owner First Name", r => r.P.OwnerFirstName),
			new("Structure Owner Last Name", r => r.P.OwnerLastName),
			new("Structure Building Number", r => r.P.StreetNumber),
			new("Structure Street Name", r => r.P.StreetName),
			new("Structure Street Suffix", r => r.Lookup(r.P.StreetSuffixID)),
			new("Structure City", r => r.P.City),
			new("Structure State", r => r.R.StateName(r.P.StateID)),
			new("Structure Zip Code", r => Zip(r.P.ZipCode)),
			new("Structure Main Phone", r => r.P.PhoneNumber),
			new("Structure Cell Phone", r => r.P.CellPhone),
			new("Structure County/Parish", r => r.R.CountyName(r.P.CountyID)),
			new("Mailing Owner First Name", r => r.P.MailingOwnerFirstName),
			new("Mailing Owner Last Name", r => r.P.MailingOwnerLastName),
			new("Mailing House Number", r => r.P.MailingStreetNumber),
			new("Mailing Street Name", r => r.P.MailingStreetName),
			new("Mailing Street Suffix", r => r.Lookup(r.P.MailingStreetSuffixID)),
			new("Mailing City", r => r.P.MailingCity),
			new("Mailing State", r => r.R.StateName(r.P.MailingStateID)),
			new("Mailing Zip Code", r => Zip(r.P.MailingZipCode)),
			new("Mailing Phone Number", r => r.P.MailingPhoneNumber),
			new("Mailing County/Parish", r => r.R.CountyName(r.P.MailingCountyID)),
			new("Mailing Care Of", r => r.P.MailingCareOf),
			new("Residential Type (Only for Residential Structures)", r => r.Lookup(r.P.ResidenceTypeID)),
			new("Foundation (Only for Residential Structures)", r => r.Lookup(r.P.FoundationTypeID)),
			new("Superstructure (Only for Residential Structures)", r => r.Lookup(r.P.SuperStructureTypeID)),
			new("Roof Covering (Only for Residential Structures)", r => r.Lookup(r.P.RoofCoveringTypeID)),
			new("Exterior Finish (Only for Residential Structures)", r => r.Lookup(r.P.ExteriorFinishTypeID)),
			new("Heating and Cooling (Only for Residential Structures)", r => r.Lookup(r.P.HVACTypeID)),
			new("Number of Stories", r => r.Lookup(r.P.StoryTypeID)),
			new("Structure Use (Only for Non-Residential Structures)", r => r.Lookup(r.P.StructureUseID)),
			new("Sprinkler System (Only for Non-Residential Structures)", r => r.Lookup(r.P.SprinklerSystem)),
			new("Conveyance (Only for Non-Residential Structures)", r => r.Lookup(r.P.Conveyance)),
			new("Year of Construction", r => r.P.YearOfConstruction?.ToString()),
			new("Quality", r => r.Lookup(r.P.StructureQualityID)),
			new("Structure Information", r => r.P.StructureInformation),
			new("Cause of Damage", r => r.Lookup(r.A.CauseofDamageID)),
			new("Date of Damage", r => Text.ShortDate(r.A.DateDamageOccurred)),
			new("Duration of Flood", r => Number2(r.A.DurationOfFlood)),
			new("Measure for Duration of Flood", r => r.Lookup(r.A.DurationOfFloodUnitsID)),
			new("Flood Elevation", r => PlainNonZero(r.A.DepthOfFlood)),
			new("Est. Depth of Flood Above Lowest Floor", r => Number2(r.A.DepthOfFloodAboveFFE)),
			new("Other Cause of Damage", r => r.A.CauseOfDamageDescription),
			new("Inspector Name", r => r.A.InspectorName),
			new("Assessment Date", r => Text.ShortDate(r.A.DateofInspection)),
			new("Inspector Phone Number", r => r.A.InspectorPhoneNumber),
			new("FIRM Panel Number", r => r.P.FIRMPanelNumber),
			new("Suffix", r => r.P.PanelSuffix),
			new("Date of FIRM Panel", r => Text.ShortDate(r.P.FIRMPanelDate)),
			new("FIRM Zone", r => r.Lookup(r.P.FIRMZone)),
			new("Base Flood Elevation", r => Number2(r.P.BFE)),
			new("Regulatory Floodway", r => r.Lookup(r.P.RegulatoryFloodway)),
			new("Community Information", r => r.P.CommunityInformation),
			new("Total Square Footage", r => Number2(r.P.SquareFootage)),
			new("Geographic Adjustment", r => Text.Plain(r.A.GeographicAdjustment)),
			new("Base Cost", r => Money(r.A.BaseCost)),
		};

		for (int i = 1; i <= 7; i++)
		{
			int k = i - 1;
			// The XAML says "Item Cost" for adjustments 1-3 and "Element Cost" for 4-7.
			string cost = i <= 3 ? "Item Cost" : "Element Cost";
			c.Add(new($"Cost Adjustment {i} Name", r => r.Cost(k) is { } x ? r.Lookup(x.CostAdjustmentLookupID) : ""));
			c.Add(new($"Cost Adjustment {i} Quantity", r => Number2(r.Cost(k)?.Quantity)));
			c.Add(new($"Cost Adjustment {i} Unit", r => r.Cost(k)?.Units));
			c.Add(new($"Cost Adjustment {i} Unit Cost", r => Money(r.Cost(k)?.UnitCost)));
			c.Add(new($"Cost Adjustment {i} {cost}", r => Money(r.Cost(k)?.TotalCost)));
		}

		for (int i = 1; i <= 5; i++)
		{
			int k = i - 1;
			c.Add(new($"Additional Cost Adjustments {i} Name", r => r.Extra(k)?.AdjustmentName));
			c.Add(new($"Additional Cost Adjustments {i} Quantity", r => Number2(r.Extra(k)?.Quantity)));
			c.Add(new($"Additional Cost Adjustments {i} Unit Cost", r => Money(r.Extra(k)?.UnitCost)));
			c.Add(new($"Additional Cost Adjustments {i} Item Cost", r => Money(r.Extra(k)?.TotalCost)));
		}

		c.AddRange(new Column[]
		{
			new("Replacement Cost", r => Money(r.A.ReplacementCost)),
			new("Cost Data Reference", r => r.A.CostData),
			new("Cost Data Date", r => Text.ShortDate(r.A.CostDataDate)),
			// Bound to "ComputedDamages", which GetAllAssessments overwrote with the sum of the adjustments (AssessmentService.cs:210).
			new("Total Adjustments", r => Money(r.TotalAdjustments)),
			new("Replacement Cost per Square Foot", r => Money(r.A.ReplacementCostSqFt)),
			new("Depreciation Determination", r => r.Lookup(r.A.DepreciationRatingID)),
			new("Depreciation Percentage", r => Number2(r.A.DepreciationValue)),
			new("Depreciation Explanation", r => r.A.DepreciationExplanation),
			new("Computed Actual Cash Value", r => Money(r.A.ComputedActualCashValue)),
		});

		for (int i = 1; i <= 12; i++)
		{
			int k = i - 1;
			c.Add(new($"Damage Element {i} Name", r => r.Element(k)?.ElementName));
			c.Add(new($"Damage Element {i} Percent Damaged", r => Number2(r.Element(k)?.UserDamage)));
			c.Add(new($"Damage Element {i} Element Percentage", r => Number2(r.Element(k)?.ElementPercentage)));
			c.Add(new($"Damage Element {i} Element Cost", r => r.Element(k) is { } e ? Money(Text.ParseDecimal(e.ItemCost) ?? 0m) : ""));
			c.Add(new($"Damage Element {i} Value", r => r.Element(k) is { } e ? Money(Text.ParseDecimal(e.DamageValues) ?? 0m) : ""));
		}

		c.AddRange(new Column[]
		{
			new("Computed Damages", r => Money(r.A.TotalEstimatedDamages)),
			new("Value of Structure", r => r.Lookup(r.A.ValueOfBuildingID)),
			new("Cost of Repair", r => r.Lookup(r.A.CostOfRepairsID)),
			new("Percent Damaged", r => Number2(r.A.PercentDamage)),
			new("Percent Pre-Existing Improvements", r => Number2(r.A.ExistingImprovementPercent)),
			new("Appraisal", r => Money(r.A.MarketAppraisal)),
			new("Tax Assessed Value", r => Money(r.A.TaxAssessedValue)),
			new("Tax Factor Adjustment", r => Number2(r.A.FactorAdjustment)),
			new("Tax Value", r => Money((r.A.TaxAssessedValue ?? 0m) * (r.A.FactorAdjustment ?? 0m))),
			new("Contractor Value", r => Money(r.A.ContractorEstimate)),
			new("Community Value", r => Money(r.A.CommunityEstimate)),
			new("Damage Message", r => r.A.DamageMessage == "Make Selection..." ? "" : r.A.DamageMessage),
			new("Damage Undetermined", r => r.A.PhysicalDamage ? "Yes" : "No"),
			new("Damage Undetermined Message", r => r.A.UndeterminedDamageTypeID is > 0 ? r.Lookup(r.A.UndeterminedDamageTypeID) : ""),
			new("Repair Reconstruction Percentage", r => r.A.RepairReconstructPercent is decimal d && d != 0m ? Text.Number1(d) : ""),
		});

		for (int i = 1; i <= 4; i++)
		{
			int k = i - 1;
			// The original put the file name in both Name and Path (AssessmentService.cs:166-167).
			c.Add(new($"Attachment Name {i}", r => r.Attachment(k)?.FileName));
			c.Add(new($"Attachment Path {i}", r => r.Attachment(k)?.FileName));
			c.Add(new($"Attachment Description {i}", r => r.Attachment(k)?.Description));
		}

		c.Add(new("PropertyID", r => r.P.PropertyID.ToString()));
		c.Add(new("AssessmentID", r => r.A.AssessmentID.ToString()));
		return c;
	}

	// ───────────────────────── Formats ─────────────────────────

	/// <summary>Masked money column: blank for null and for 0 (the mask's NullValue).</summary>
	private static string Money(decimal? v) => v is decimal d && d != 0m ? Text.Money(d) : "";

	/// <summary>Masked "###,###,##0.00" column: blank for null and for 0.</summary>
	private static string Number2(decimal? v) => v is decimal d && d != 0m ? Text.Number2(d) : "";

	/// <summary>Latitude/longitude ("0#.######" mask): up to 6 decimals; blank for null and 0.</summary>
	private static string Coordinate(decimal? v) => v is decimal d && d != 0m ? d.ToString("0.######", Text.Us) : "";

	/// <summary>Unmasked columns that GetAllAssessments blanked when zero (FirstFloorElevation, DepthOfFlood).</summary>
	private static string PlainNonZero(decimal? v) => v is decimal d && d != 0m ? Text.Plain(d) : "";

	/// <summary>Mask "00000-9999": a 9-digit ZIP shows as 12345-6789; anything else as entered.</summary>
	private static string Zip(string zip)
	{
		if (string.IsNullOrWhiteSpace(zip))
		{
			return "";
		}
		string digits = new string(zip.Where(char.IsDigit).ToArray());
		return digits.Length == 9 && zip.Trim().All(ch => char.IsDigit(ch) || ch == '-') ? digits[..5] + "-" + digits[5..] : zip.Trim();
	}

	// ───────────────────────── One assessment ─────────────────────────

	private sealed class Row
	{
		private readonly List<CostAdjustmentResultRecord> _costs;
		private readonly List<AdditionalAdjustmentRecord> _extras;
		private readonly List<ElementPercentageRecord> _elements;
		private readonly List<AttachmentRecord> _attachments;

		public Row(AssessmentFile file, ReferenceData reference)
		{
			R = reference;
			P = file.Property ?? new PropertyRecord();
			A = file.Assessment ?? new AssessmentRecord();
			List<CostAdjustmentResultRecord> costs = file.CostAdjustments ?? new();
			List<AdditionalAdjustmentRecord> extras = (file.AdditionalAdjustments ?? new()).OrderBy(x => x.zOrder ?? int.MaxValue).ToList();
			// AssessmentService.cs:177-212 summed the adjustments it showed; all rows are summed here.
			TotalAdjustments = costs.Sum(x => x.TotalCost ?? 0m) + extras.Sum(x => x.TotalCost ?? 0m);
			_costs = Fit(costs, 7, x => (x.Quantity ?? 0m) != 0m || (x.UnitCost ?? 0m) != 0m || (x.TotalCost ?? 0m) != 0m);
			_extras = Fit(extras, 5, x => !string.IsNullOrWhiteSpace(x.AdjustmentName) || (x.Quantity ?? 0m) != 0m || (x.UnitCost ?? 0m) != 0m || (x.TotalCost ?? 0m) != 0m);
			_elements = (file.ElementPercentages ?? new()).Take(12).ToList();
			_attachments = (file.Attachments ?? new()).Where(x => x.Active).Take(4).ToList();
		}

		public ReferenceData R { get; }
		public PropertyRecord P { get; }
		public AssessmentRecord A { get; }
		public decimal TotalAdjustments { get; }

		public string Lookup(int? id) => R.LookupName(id) ?? "";

		public CostAdjustmentResultRecord Cost(int i) => i < _costs.Count ? _costs[i] : null;
		public AdditionalAdjustmentRecord Extra(int i) => i < _extras.Count ? _extras[i] : null;
		public ElementPercentageRecord Element(int i) => i < _elements.Count ? _elements[i] : null;
		public AttachmentRecord Attachment(int i) => i < _attachments.Count ? _attachments[i] : null;

		/// <summary>Keeps the rows' order; when there are more rows than columns, empty rows go first.</summary>
		private static List<T> Fit<T>(List<T> rows, int max, Func<T, bool> hasData)
		{
			List<T> list = new(rows);
			for (int i = list.Count - 1; i >= 0 && list.Count > max; i--)
			{
				if (!hasData(list[i]))
				{
					list.RemoveAt(i);
				}
			}
			return list.Take(max).ToList();
		}
	}
}
