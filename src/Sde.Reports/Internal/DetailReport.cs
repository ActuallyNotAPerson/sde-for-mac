using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using Sde.Core.Records;

namespace Sde.Reports.Internal;

/// <summary>
/// Residential / Non-Residential Detail: every field of one assessment. Port of extracted/Reports/rptResidentialDetailReport.rdlc
/// and rptNonResidentialDetailReport.rdlc (+ NoPhotos); dataset from BLL ReportsService.GetDetailResReport /
/// GetDetailNonResReport (decomp/BLL/BLL.Service/ReportsService.cs:19,33). Each assessment is its own section with
/// page numbers restarting at 1 and the structure address in the footer, as each original detail report was a
/// separate document.
/// </summary>
internal static class DetailReport
{
	public static void Build(Document doc, IReadOnlyList<AssessmentView> items, PhotoLibrary photos, bool includePhotos, DateTime printed)
	{
		foreach (AssessmentView v in items)
		{
			string name = v.IsResidential ? "Residential Detail Report" : "Non-Residential Detail Report";
			Section sec = Kit.NewSection(doc, landscape: false, name, printed, footerCenter: v.StreetAddress, restartPageNumbers: true);
			Body(sec, v, photos, includePhotos);
		}
	}

	private static void Body(Section sec, AssessmentView v, PhotoLibrary photos, bool includePhotos)
	{
		PropertyRecord p = v.P;
		AssessmentRecord a = v.A;

		Kit.Hero(sec, v, showAmounts: true);
		Kit.Gap(sec, 2);

		Kit.Panels(sec,
			new Panel("Subdivision", new[]
			{
				new Field("Subdivision", p.Subdivision),
				new Field("Parcel #", p.ParcelNumber),
				new Field("Lot #", p.LotNumber),
				new Field("Elev. of Lowest Floor", v.FirstFloorElevation),
				new Field("Datum", p.Datum),
			}, 1.25, 2.25),
			new Panel("Community", new[]
			{
				new Field("NFIP Community Name", p.NFIPCommunityName),
				new Field("Latitude", Fmt.Raw(p.Latitude)),
				new Field("Longitude", Fmt.Raw(p.Longitude)),
			}, 1.25, 2.25));

		Kit.Panels(sec,
			new Panel("Structure Address", new[]
			{
				new Field("Owner's Name", v.OwnerName),
				new Field("Street Address", v.StreetAddress),
				new Field("City", v.City),
				new Field("County/Parish", v.County),
				new Field("State", v.State),
				new Field("Zip", v.Zip),
				new Field("Phone", p.PhoneNumber),
			}, 1.25, 2.25),
			new Panel("Mailing Address", new[]
			{
				new Field("Owner's Name", v.MailingOwnerName),
				new Field("Street Address", v.MailingStreetAddress),
				new Field("City", p.MailingCity),
				new Field("County/Parish", v.MailingCounty),
				new Field("State", v.MailingState),
				new Field("Zip", Fmt.Zip(p.MailingZipCode)),
				new Field("Phone", p.MailingPhoneNumber),
				new Field("Care Of", p.MailingCareOf),
			}, 1.25, 2.25));

		if (includePhotos)
		{
			Photos(sec, photos.Primary(v.File), photos.Additional(v.File));
		}

		StructureInformation(sec, v);
		CustomFields(sec, v);

		Kit.Stacked(sec, "Square Footage", new[] { 2.4, 2.45, 2.45 }, new[]
		{
			new Field("Total Sq. Ft.", Fmt.Plain(p.SquareFootage)),
			new Field("Geographic Adjustment", Fmt.Raw(a.GeographicAdjustment)),
			new Field("Base Cost Per Sq. Ft.", Fmt.Money(a.BaseCost)),
		});

		Kit.Data(sec, "Cost Adjustments", new[]
		{
			new Column("Adjustments", 2.6),
			new Column("Quantity", 1.0, ParagraphAlignment.Right),
			new Column("Unit", 0.9, ParagraphAlignment.Center),
			new Column("Unit Cost", 1.4, ParagraphAlignment.Right),
			new Column("Adjustment Cost", 1.4, ParagraphAlignment.Right),
		}, v.CostAdjustments.Select(c => new[]
		{
			v.Name(c.CostAdjustmentLookupID),
			Fmt.Plain(c.Quantity),
			c.Units ?? v.Lookup(c.CostAdjustmentLookupID)?.LookupValue1 ?? "",
			Fmt.Money(c.UnitCost),
			Fmt.Money(c.TotalCost),
		}));

		Kit.Data(sec, "Additional Adjustments", new[]
		{
			new Column("Adjustments", 3.5),
			new Column("Quantity", 1.0, ParagraphAlignment.Right),
			new Column("Unit Cost", 1.4, ParagraphAlignment.Right),
			new Column("Adjustment Cost", 1.4, ParagraphAlignment.Right),
		}, v.AdditionalAdjustments.Select(x => new[]
		{
			x.AdjustmentName ?? "",
			Fmt.Plain(x.Quantity),
			Fmt.Money(x.UnitCost),
			Fmt.Money(x.TotalCost),
		}));
		Table totals = Kit.Fields(sec, null, new[] { 3.2, 0.9, 1.75, 1.15 }, new[]
		{
			new[] { new Field("", ""), new Field("Total Adjustments *", Fmt.Money(a.TotalAdjustments)) },
			new[] { new Field("", ""), new Field("Total Replacement Cost", Fmt.Money(a.TotalReplacementCost)) },
			new[] { new Field("", ""), new Field("Replacement Cost Per Square Foot", Fmt.Money(a.ReplacementCostSqFt)) },
		});
		RightAlignTotals(totals);
		Kit.Note(sec, "* Total Adjustment calculation includes the Geographical Adjustment.");

		Kit.Fields(sec, "Computed Actual Cash Value", new[] { 1.75, 1.75, 1.6, 1.9 }, new[]
		{
			new[] { new Field("Cost Data Reference", a.CostData), new Field("Cost Data Date", Fmt.Date(a.CostDataDate)) },
			new[] { new Field("Depreciation Determination", v.Name(a.DepreciationRatingID)), new Field("Depreciation Percentage", Fmt.Percent2(a.DepreciationValue)) },
			new[] { new Field("Computed Actual Cash Value", Fmt.Money(a.ComputedActualCashValue)) },
			new[] { new Field("Other Depreciation Explanation", a.DepreciationExplanation, Wide: true) },
		});

		Kit.Data(sec, "Element Percentages", new[]
		{
			new Column("Element", 2.3),
			new Column("Percent Damaged", 1.2, ParagraphAlignment.Right),
			new Column("Element Percentage", 1.2, ParagraphAlignment.Right),
			new Column("Element Cost", 1.3, ParagraphAlignment.Right),
			new Column("Damage Values", 1.3, ParagraphAlignment.Right),
		}, v.Elements.Select(e => new[]
		{
			e.ElementName ?? "",
			Fmt.Percent1(e.UserDamage),
			Fmt.Percent1(e.ElementPercentage),
			Fmt.Money(e.ItemCost),
			Fmt.Money(e.DamageValues),
		}), new[]
		{
			new[] { "", "", "100%", "Total Replacement Cost", "Total Estimated Damages" },
			new[] { "", "", "", Fmt.Money(a.TotalReplacementCost), Fmt.Money(a.TotalEstimatedDamages) },
		});
		StyleElementTotals(sec);

		PercentDamaged(sec, v);

		Kit.Fields(sec, "Damage Summary", new[] { 1.75, 1.45, 2.6, 1.2 }, new[]
		{
			new[] { new Field("Replacement Cost", Fmt.Money(a.ReplacementCost)), new Field("Total Estimated Damages", Fmt.Money(a.TotalEstimatedDamages)) },
			new[] { new Field("Depreciation Percentage", Fmt.Percent2(a.DepreciationValue)), new Field("Percent of Existing Improvements and Repairs Pre-Disaster", Fmt.Percent1(a.ExistingImprovementPercent)) },
			new[] { new Field("Computed Actual Cash Value*", Fmt.Money(a.ComputedActualCashValue)), new Field("Repair/Reconstruction Percentage", Fmt.Percent1(a.RepairReconstructPercent)) },
		});
		Kit.Note(sec, "* Per FEMA Publication 213, Actual Cash Value may be used as Market Value.");

		Kit.Fields(sec, "Optional User Entered Data", new[] { 1.75, 1.45, 2.6, 1.2 }, new[]
		{
			new[] { new Field("Professional Market Appraisal", Fmt.Money(a.MarketAppraisal)), new Field("Contractor Estimate", Fmt.Money(a.ContractorEstimate)) },
			new[] { new Field("Tax Assessed Value", Fmt.Money(a.TaxAssessedValue)), new Field("Community Estimate", Fmt.Money(a.CommunityEstimate)) },
			new[] { new Field("Factor Adjustment", Fmt.Plain(a.FactorAdjustment)) },
			new[] { new Field("Adjusted Tax Assessed Value", v.AdjustedTaxAssessedValue) },
		});

		Kit.Gap(sec, 6);
		Kit.Signature(sec, detail: true);
	}

	/// <summary>The RDLC's "Structure/Damage/NFIP Information" box (fields differ for residential and non-residential).</summary>
	private static void StructureInformation(Section sec, AssessmentView v)
	{
		PropertyRecord p = v.P;
		AssessmentRecord a = v.A;
		List<Field> left;
		List<Field> right;
		if (v.IsResidential)
		{
			left = new()
			{
				new Field("Year of Construction", p.YearOfConstruction?.ToString()),
				new Field("Date of Damage", Fmt.Date(a.DateDamageOccurred)),
				new Field("Residence Type", v.Name(p.ResidenceTypeID)),
				new Field("Foundation", v.Name(p.FoundationTypeID)),
				new Field("Superstructure", v.Name(p.SuperStructureTypeID)),
				new Field("Roofing", v.Name(p.RoofCoveringTypeID)),
				new Field("Quality", v.Name(p.StructureQualityID)),
			};
			right = new()
			{
				new Field("Cause of Damage", v.Name(a.CauseofDamageID)),
				new Field("Duration of Flood", v.DurationOfFlood),
				new Field("Structure Type", "Residential"),
				new Field("Exterior Finish", v.Name(p.ExteriorFinishTypeID)),
				new Field("HVAC", v.Name(p.HVACTypeID)),
				new Field("Story", v.Name(p.StoryTypeID)),
				new Field("Flood Elevation", Fmt.Number2(a.DepthOfFlood)),
			};
		}
		else
		{
			left = new()
			{
				new Field("Year of Construction", p.YearOfConstruction?.ToString()),
				new Field("Date of Damage", Fmt.Date(a.DateDamageOccurred)),
				new Field("Number Of Stories", v.Name(p.StoryTypeID)),
				new Field("Sprinkler", v.Sprinkler),
				new Field("Conveyance", v.Conveyance),
				new Field("Quality", v.Name(p.StructureQualityID)),
			};
			right = new()
			{
				new Field("Cause of Damage", v.Name(a.CauseofDamageID)),
				new Field("Duration of Flood", v.DurationOfFlood),
				new Field("Structure Use", v.Name(p.StructureUseID)),
				new Field("Structure Type", "Non-Residential"),
				new Field("Flood Elevation", Fmt.Number2(a.DepthOfFlood)),
			};
		}
		left.Add(new Field("Date of Assessment", Fmt.Date(a.DateofInspection)));
		left.Add(new Field("Inspector Name", a.InspectorName));
		left.Add(new Field("Inspector Phone", a.InspectorPhoneNumber));
		right.Add(new Field("Est. Depth of Flood Above Lowest Floor", Fmt.Number2(a.DepthOfFloodAboveFFE)));
		right.Add(new Field("NFIP Community ID", p.NFIPCID));

		List<Field[]> rows = new();
		for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
		{
			rows.Add(i < right.Count && i < left.Count ? new[] { left[i], right[i] }
				: i < left.Count ? new[] { left[i] } : new[] { new Field("", ""), right[i] });
		}
		rows.Add(new[] { new Field(v.IsResidential ? "Residence Information" : "Structure Information", p.StructureInformation, Wide: true) });
		Kit.Fields(sec, "Structure/Damage/NFIP Information", new[] { 1.4, 2.1, 2.0, 1.5 }, rows);

		Kit.Stacked(sec, "NFIP Information", new[] { 1.3, 0.75, 1.3, 1.15, 1.1, 1.7 }, new[]
		{
			new Field("Firm Panel #", p.FIRMPanelNumber),
			new Field("Suffix", p.PanelSuffix),
			new Field("Date of FIRM Panel", Fmt.Date(p.FIRMPanelDate)),
			new Field("Firm Zone", v.Name(p.FIRMZone)),
			new Field("BFE", Fmt.Number2(p.BFE)),
			new Field("Regulatory Floodway", v.Name(p.RegulatoryFloodway)),
		}, new Field("Community Specific Information", p.CommunityInformation));
	}

	/// <summary>
	/// "Custom Field" boxes (Custom Field Name / Custom Field Value). The original had exactly three boxes; every
	/// custom field is listed here.
	/// </summary>
	private static void CustomFields(Section sec, AssessmentView v)
	{
		Kit.Data(sec, "Custom Fields", new[]
		{
			new Column("Custom Field Name", 2.6),
			new Column("Custom Field Value", 4.7),
		}, v.CustomFields.Select(c => new[] { c.Name, c.Value }));
	}

	/// <summary>Default image and additional image side by side, captioned as in the RDLC.</summary>
	private static void Photos(Section sec, Photo first, Photo second)
	{
		const double w = 3.5;
		const double h = 2.6;
		Table t = Kit.NewTable(sec, new[] { w, Kit.Gutter, w });
		Row title = t.AddRow();
		Kit.TitleCell(title, 0, 3, "Photos");
		Row row = t.AddRow();
		row.TopPadding = Unit.FromPoint(5);
		row.VerticalAlignment = VerticalAlignment.Bottom;
		Kit.Photo(row.Cells[0], first, w, h, "(Default Image)");
		Kit.Photo(row.Cells[2], second, w, h, "(Additional Image)");
		title.KeepWith = 1;
	}

	/// <summary>The RDLC's "Percent Damaged" box: the two bases, the percentage (12pt) and the determination.</summary>
	private static void PercentDamaged(Section sec, AssessmentView v)
	{
		Table t = Kit.NewTable(sec, new[] { 2.45, 2.4, 2.45 });
		Row title = t.AddRow();
		Kit.TitleCell(title, 0, 3, "Percent Damaged");
		Row labels = t.AddRow();
		labels.TopPadding = Unit.FromPoint(5);
		Kit.LabelCell(labels.Cells[0], "Basis for Value of Structure", rule: false);
		Kit.LabelCell(labels.Cells[1], "Percent Damaged", rule: false, align: ParagraphAlignment.Center);
		Kit.LabelCell(labels.Cells[2], "Basis for Cost of Repairs", rule: false, align: ParagraphAlignment.Right);
		Row values = t.AddRow();
		values.VerticalAlignment = VerticalAlignment.Top;
		Kit.ValueCell(values.Cells[0], v.BasisValue, rule: false);
		Kit.ValueCell(values.Cells[2], v.BasisCost, rule: false, align: ParagraphAlignment.Right);
		Color ink = Theme.ToneInk(v.Determination);
		Paragraph pct = values.Cells[1].AddParagraph(Fmt.Percent1(v.A.PercentDamage));
		pct.Format.Alignment = ParagraphAlignment.Center;
		pct.Format.Font.Size = 14;
		pct.Format.Font.Bold = true;
		pct.Format.Font.Color = ink;
		Paragraph msg = values.Cells[1].AddParagraph(v.DamageMessage);
		msg.Format.Alignment = ParagraphAlignment.Center;
		msg.Format.Font.Bold = true;
		msg.Format.Font.Size = 9;
		msg.Format.Font.Color = ink;
		values.Cells[1].Shading.Color = Theme.ToneFill(v.Determination);
		values.TopPadding = Unit.FromPoint(4);
		values.BottomPadding = Unit.FromPoint(5);
		title.KeepWith = 2;
	}

	private static void RightAlignTotals(Table t)
	{
		foreach (Row r in t.Rows)
		{
			r.Cells[0].Borders.Bottom.Visible = false;
			r.Cells[1].Borders.Bottom.Visible = false;
			r.Cells[3].Format.Alignment = ParagraphAlignment.Right;
			r.Cells[4].Format.Alignment = ParagraphAlignment.Right;
			foreach (Paragraph p in r.Cells[4].Elements.OfType<Paragraph>())
			{
				p.Format.Alignment = ParagraphAlignment.Right;
				p.Format.Font.Bold = true;
			}
			foreach (Paragraph p in r.Cells[3].Elements.OfType<Paragraph>())
			{
				p.Format.Alignment = ParagraphAlignment.Right;
			}
		}
	}

	/// <summary>The element table's footer: captions small and muted, amounts bold.</summary>
	private static void StyleElementTotals(Section sec)
	{
		Table t = sec.Elements.OfType<Table>().Last();
		Row captions = t.Rows[t.Rows.Count - 2];
		captions.TopPadding = Unit.FromPoint(4);
		for (int i = 0; i < captions.Cells.Count; i++)
		{
			foreach (Paragraph p in captions.Cells[i].Elements.OfType<Paragraph>())
			{
				p.Style = Theme.LabelStyle;
				p.Format.Font.Bold = i == 2;
				p.Format.Font.Color = i == 2 ? Theme.Ink : Theme.Muted;
				p.Format.Alignment = ParagraphAlignment.Right;
			}
		}
		captions.KeepWith = 1;
	}
}
