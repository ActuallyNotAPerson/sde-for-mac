using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace Sde.Reports.Internal;

/// <summary>
/// Residential / Non-Residential Summary: one page per assessment. Port of extracted/Reports/rptResidentialSummaryReport.rdlc
/// and rptNonResidentialSummary.rdlc (+ the NoPhotos variants); dataset from BLL ReportsService.GetSummaryResReports /
/// GetSummaryNonResReports (decomp/BLL/BLL.Service/ReportsService.cs:72,89).
/// The RDLC's "Percent Damaged" box (basis for value with its amount, percent damaged + determination, basis for
/// cost of repairs with its amount) is the colored panel at the top of the page.
/// </summary>
internal static class SummaryReport
{
	public static void Build(Document doc, IReadOnlyList<AssessmentView> items, PhotoLibrary photos, bool includePhotos, DateTime printed)
	{
		foreach (AssessmentView v in items)
		{
			string name = v.IsResidential ? "Residential Summary Report" : "Non-Residential Summary Report";
			Section sec = Kit.NewSection(doc, landscape: false, name, printed, footerCenter: null, restartPageNumbers: false);
			Page(sec, v, includePhotos ? photos.Primary(v.File) : null);
		}
	}

	private static void Page(Section sec, AssessmentView v, Photo photo)
	{
		Kit.Hero(sec, v, showAmounts: true);
		Kit.Gap(sec, 2);

		Panel address = new Panel("Structure Address", new[]
		{
			new Field("Owner's Name", v.OwnerName),
			new Field("Street Address", v.StreetAddress),
			new Field("City", v.City),
			new Field("County/Parish", v.County),
			new Field("State", v.State),
			new Field("Zip", v.Zip),
			new Field("Phone", v.P.PhoneNumber),
		}, 1.05, 2.45);
		Panel structure = new Panel("Structure Information", new[]
		{
			new Field("Year of Construction", v.P.YearOfConstruction?.ToString()),
			v.IsResidential ? new Field("Residence Type", v.Name(v.P.ResidenceTypeID)) : new Field("Use", v.Name(v.P.StructureUseID)),
			new Field("Quality", v.Name(v.P.StructureQualityID)),
		}, 1.05, 2.45);
		Panel subdivision = new Panel("Subdivision", new[]
		{
			new Field("Subdivision", v.P.Subdivision),
			new Field("Parcel #", v.P.ParcelNumber),
			new Field("Lot #", v.P.LotNumber),
			new Field("Elev. of Lowest Floor", v.FirstFloorElevation),
			new Field("Datum", v.P.Datum),
		}, 1.25, 2.25);
		Panel community = new Panel("Community", new[]
		{
			new Field("NFIP Community Name", v.P.NFIPCommunityName),
			new Field("NFIP Community ID #", v.P.NFIPCID),
			new Field("Latitude", Fmt.Raw(v.P.Latitude)),
			new Field("Longitude", Fmt.Raw(v.P.Longitude)),
		}, 1.25, 2.25);

		if (photo != null)
		{
			PhotoBeside(sec, photo, address, structure);
			Kit.Panels(sec, subdivision, community);
		}
		else
		{
			Kit.Panels(sec, address with { LabelWidth = 1.25, ValueWidth = 2.25 }, subdivision);
			Kit.Panels(sec, structure with { LabelWidth = 1.25, ValueWidth = 2.25 }, community);
		}

		double[] four = { 1.25, 2.25, 1.6, 1.9 };
		Kit.Fields(sec, "Damage Information", four, new[]
		{
			new[] { new Field("Date of Assessment", Fmt.Date(v.A.DateofInspection)), new Field("Date of Damage", Fmt.Date(v.A.DateDamageOccurred)) },
			new[] { new Field("Inspector Name", v.A.InspectorName), new Field("Cause of Damage", v.Name(v.A.CauseofDamageID)) },
			new[] { new Field("Inspector Phone", v.A.InspectorPhoneNumber), new Field("Duration of Flood", v.DurationOfFlood) },
			new[] { new Field(v.IsResidential ? "Residence Information" : "Structure Information", v.P.StructureInformation), new Field("Est. Depth of Flood Above Lowest Floor", Fmt.Number2(v.A.DepthOfFloodAboveFFE)) },
		});

		Kit.Stacked(sec, "NFIP Information", new[] { 1.3, 0.75, 1.3, 1.15, 1.1, 1.7 }, new[]
		{
			new Field("Firm Panel #", v.P.FIRMPanelNumber),
			new Field("Suffix", v.P.PanelSuffix),
			new Field("Date of FIRM Panel", Fmt.Date(v.P.FIRMPanelDate)),
			new Field("Firm Zone", v.Name(v.P.FIRMZone)),
			new Field("BFE", Fmt.Number2(v.P.BFE)),
			new Field("Regulatory Floodway", v.Name(v.P.RegulatoryFloodway)),
		});

		double[] summary = { 1.6, 1.6, 2.6, 1.2 };
		Kit.Fields(sec, "Damage Summary", summary, new[]
		{
			new[] { new Field("Replacement Cost", Fmt.Money(v.A.ReplacementCost)), new Field("Computed Damages", Fmt.Money(v.A.ComputedDamages)) },
			new[] { new Field("Depreciation %", Fmt.Percent1(v.A.DepreciationValue)), new Field("Percent of Existing Improvements and Repairs Pre-Disaster", Fmt.Percent1(v.A.ExistingImprovementPercent)) },
			new[] { new Field("Computed Actual Cash Value*", Fmt.Money(v.A.ComputedActualCashValue)), new Field("Repair/Reconstruction %", Fmt.Percent1(v.A.RepairReconstructPercent)) },
			new[] { new Field("Other Depreciation Explanation", v.A.DepreciationExplanation, Wide: true) },
		});
		Kit.Note(sec, "* Per FEMA Publication 213, Actual Cash Value may be used as Market Value.");

		Kit.Fields(sec, "Optional User Entered Data", summary, new[]
		{
			new[] { new Field("Professional Market Appraisal", Fmt.Money(v.A.MarketAppraisal)), new Field("Contractor Estimate", Fmt.Money(v.A.ContractorEstimate)) },
			new[] { new Field("Tax Assessed Value", Fmt.Money(v.A.TaxAssessedValue)), new Field("Community Estimate", Fmt.Money(v.A.CommunityEstimate)) },
			new[] { new Field("Factor Adjustment", Fmt.Plain(v.A.FactorAdjustment)) },
			new[] { new Field("Adjusted Tax Assessed Value", v.AdjustedTaxAssessedValue) },
		});

		Kit.Gap(sec, 4);
		Kit.Signature(sec, detail: false);
	}

	/// <summary>The photo on the left, the structure address and structure information panels on the right.</summary>
	private static void PhotoBeside(Section sec, Photo photo, Panel top, Panel bottom)
	{
		const double photoWidth = 3.5;
		const double photoHeight = 2.2;
		Table t = Kit.NewTable(sec, new[] { photoWidth, Kit.Gutter, top.LabelWidth, top.ValueWidth });
		List<Row> rows = new();
		Row title1 = t.AddRow();
		Kit.TitleCell(title1, 2, 2, top.Title);
		rows.Add(title1);
		foreach (Field f in top.Fields)
		{
			rows.Add(FieldRow(t, f));
		}
		Row title2 = t.AddRow();
		Kit.TitleCell(title2, 2, 2, bottom.Title);
		rows.Add(title2);
		foreach (Field f in bottom.Fields)
		{
			rows.Add(FieldRow(t, f));
		}
		Cell photoCell = title1.Cells[0];
		photoCell.MergeDown = rows.Count - 1;
		photoCell.VerticalAlignment = VerticalAlignment.Bottom;
		Kit.Photo(photoCell, photo, photoWidth, photoHeight, align: ParagraphAlignment.Left);
		title1.KeepWith = rows.Count - 1;
	}

	private static Row FieldRow(Table t, Field f)
	{
		Row row = t.AddRow();
		Kit.LabelCell(row.Cells[2], f.Label);
		Kit.ValueCell(row.Cells[3], f.Value);
		return row;
	}
}
