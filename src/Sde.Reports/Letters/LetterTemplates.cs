using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Sde.Core.Records;

namespace Sde.Reports.Letters;

/// <summary>The kinds of owner letter, as used in file names ("123 Main Street (Substantial damage).docx").</summary>
public static class LetterKinds
{
	public const string SubstantialDamage = "Substantial damage";
	public const string NotSubstantiallyDamaged = "Not substantially damaged";
	public const string SubstantialImprovement = "Substantial improvement";

	/// <summary>
	/// The damage letter for an assessment's stored FEMA result: <see cref="SubstantialDamage"/> or
	/// <see cref="NotSubstantiallyDamaged"/>, or null when there is no determination yet (incomplete or undetermined).
	/// </summary>
	public static string For(AssessmentRecord assessment) => assessment?.DamageMessage switch
	{
		"Substantially Damaged" => SubstantialDamage,
		"Not Substantially Damaged" => NotSubstantiallyDamaged,
		_ => null,
	};
}

/// <summary>
/// Writes the three starter letter templates as editable Word documents. Communities change them in Word (or
/// Pages, saving as .docx) to suit their ordinance and letterhead; the app fills in the «Field» placeholders
/// (see <see cref="LetterFields"/>).
/// <para>
/// The wording is our own plain-English starting point, written in the spirit of the sample substantial damage
/// notice in FEMA P-758 (Substantial Improvement/Substantial Damage Desk Reference), Appendix D, a public-domain
/// federal document. It is not legal advice: each community must review and adapt these letters with its own
/// counsel before sending them, because the requirements, appeal rights and deadlines come from the local ordinance.
/// </para>
/// </summary>
public static class LetterTemplates
{
	public const string SubstantialDamageFile = "Substantial damage notice.docx";
	public const string NotSubstantiallyDamagedFile = "Not substantially damaged notice.docx";
	public const string SubstantialImprovementFile = "Substantial improvement determination.docx";

	private const string ReviewNote = "Starter letter from Substantial Damage Estimator. Review and adapt it with your community's counsel before use.";

	/// <summary>The starter template file name for a <see cref="LetterKinds"/> value.</summary>
	public static string FileName(string kind) => kind switch
	{
		LetterKinds.SubstantialDamage => SubstantialDamageFile,
		LetterKinds.NotSubstantiallyDamaged => NotSubstantiallyDamagedFile,
		LetterKinds.SubstantialImprovement => SubstantialImprovementFile,
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown letter kind."),
	};

	/// <summary>
	/// Writes the three starter templates into <paramref name="folder"/> (created if needed) and returns their paths.
	/// An existing template is kept, so a community's edits are never lost, unless <paramref name="overwrite"/> is true.
	/// </summary>
	public static IReadOnlyList<string> WriteStarterTemplates(string folder, bool overwrite = false)
	{
		ArgumentException.ThrowIfNullOrEmpty(folder);
		Directory.CreateDirectory(folder);
		List<string> paths = new();
		foreach ((string file, Action<Body> body) in new (string, Action<Body>)[]
		{
			(SubstantialDamageFile, SubstantialDamageBody),
			(NotSubstantiallyDamagedFile, NotSubstantiallyDamagedBody),
			(SubstantialImprovementFile, SubstantialImprovementBody),
		})
		{
			string path = Path.Combine(folder, file);
			if (overwrite || !File.Exists(path))
			{
				Write(path, Path.GetFileNameWithoutExtension(file), body);
			}
			paths.Add(path);
		}
		return paths;
	}

	// ───────── Letter text ─────────

	private static void SubstantialDamageBody(Body b)
	{
		Opening(b, "Substantial damage determination");
		P(b, "On «InspectionDate», «CommunityName» inspected the building at «PropertyAddress» for damage from the «CauseOfDamage» event of «DamageDate». "
			+ "Based on that inspection, we have determined that the building is substantially damaged.");
		Facts(b, true);
		P(b, "Our floodplain management regulations, which the community adopted to take part in the National Flood Insurance Program (NFIP), "
			+ "say a building is substantially damaged when the cost to restore it to its condition before the damage would equal or exceed "
			+ "50 percent of the building's market value before the damage. The estimated cost of repairs is «PercentDamaged» of the market value.");
		H(b, "What this means for you");
		P(b, "Before the building is repaired or rebuilt, it must be brought into compliance with the community's current floodplain management "
			+ "requirements. Depending on the building and its flood zone, this usually means raising the lowest floor to or above the required "
			+ "elevation, or, for some non-residential buildings, floodproofing. Moving or demolishing the building are other options.");
		P(b, "You need a permit from us before you start any repairs, including work you do yourself. Please do not begin work until the permit is issued.");
		H(b, "Flood insurance");
		P(b, "If the building has an NFIP flood insurance policy, it may be eligible for Increased Cost of Compliance (ICC) coverage, which helps pay "
			+ "the cost of bringing a substantially damaged building into compliance. Ask your insurance agent or claims adjuster about ICC and "
			+ "give them a copy of this letter.");
		H(b, "If you disagree");
		P(b, "If you believe our estimate of the market value or the cost of repairs is not correct, you may give us more information, such as an "
			+ "appraisal of the building (not including the land) by a licensed appraiser, or a detailed repair estimate from a licensed contractor. "
			+ "We will review it and let you know whether the determination changes.");
		Closing(b);
	}

	private static void NotSubstantiallyDamagedBody(Body b)
	{
		Opening(b, "Substantial damage determination");
		P(b, "On «InspectionDate», «CommunityName» inspected the building at «PropertyAddress» for damage from the «CauseOfDamage» event of «DamageDate». "
			+ "Based on that inspection, we have determined that the building is not substantially damaged.");
		Facts(b, true);
		P(b, "A building is substantially damaged when the cost to restore it to its condition before the damage would equal or exceed 50 percent "
			+ "of its market value before the damage. The estimated cost of repairs is «PercentDamaged» of the market value, which is below that level.");
		H(b, "What this means for you");
		P(b, "You may repair the building to its condition before the damage. You still need a permit from us before you start repairs, and the "
			+ "work must meet the building codes that apply. Please keep receipts and records of the work.");
		P(b, "This determination is based on the damage we saw and the repair costs we estimated. If the repairs turn out to cost much more, or if "
			+ "you add improvements while you repair, contact us first: the work may need a new determination.");
		Closing(b);
	}

	private static void SubstantialImprovementBody(Body b)
	{
		Opening(b, "Substantial improvement determination");
		P(b, "We have reviewed the proposed work at «PropertyAddress» (permit «PermitNumber»): «ProjectDescription». "
			+ "Based on that review, we have determined that the work «SIDeterminationWording».");
		Table t = NewTable();
		Row(t, "Property", "«PropertyAddress», «PropertyCityStateZip»");
		Row(t, "Parcel number", "«ParcelNumber»");
		Row(t, "Flood zone", "«FloodZone»");
		Row(t, "Cost of improvements counted", "«ImprovementCost»");
		Row(t, "Market value of the building", "«MarketValue»");
		Row(t, "Cost as a percent of market value", "«ImprovementPercent»");
		Row(t, "Period counted", "«LookbackPeriod»");
		b.AppendChild(t);
		P(b, "");
		P(b, "Our floodplain management regulations say that an improvement is a substantial improvement when its cost equals or exceeds "
			+ "«SIThreshold» of the market value of the building (not including the land) before the work starts.");
		H(b, "What this means for you");
		P(b, "If the work is a substantial improvement, the whole building must be brought into compliance with the community's current floodplain "
			+ "management requirements as part of the project. This usually means raising the lowest floor to or above the required elevation, or, "
			+ "for some non-residential buildings, floodproofing. If it is not, the work may go ahead under the permit, as long as it meets the "
			+ "building codes that apply.");
		P(b, "If the scope or cost of the work changes, contact us before you continue: the change may need a new determination.");
		H(b, "If you disagree");
		P(b, "If you believe the cost or the market value we used is not correct, you may give us more information, such as an appraisal of the "
			+ "building by a licensed appraiser or a detailed cost estimate from a licensed contractor. We will review it and let you know whether "
			+ "the determination changes.");
		Closing(b);
	}

	/// <summary>Date, address block, subject line and greeting.</summary>
	private static void Opening(Body b, string subject)
	{
		P(b, "«Today»");
		P(b, "");
		P(b, "«MailingName»");
		P(b, "«MailingAddress»");
		P(b, "");
		Paragraph re = P(b, "Re: " + subject + ", «PropertyAddress», «PropertyCityStateZip» (parcel «ParcelNumber»)");
		Bold(re);
		P(b, "");
		P(b, "Dear «OwnerName»:");
	}

	/// <summary>The facts of the determination in a two-column table.</summary>
	private static void Facts(Body b, bool damage)
	{
		Table t = NewTable();
		Row(t, "Property", "«PropertyAddress», «PropertyCityStateZip»");
		Row(t, "Parcel number", "«ParcelNumber»");
		Row(t, "Flood zone", "«FloodZone»");
		if (damage)
		{
			Row(t, "Date of damage", "«DamageDate»");
			Row(t, "Date of inspection", "«InspectionDate»");
			Row(t, "Market value of the building", "«BuildingValue»");
			Row(t, "Estimated cost of repairs", "«RepairCost»");
			Row(t, "Percent damaged", "«PercentDamaged»");
		}
		b.AppendChild(t);
		P(b, "");
	}

	private static void Closing(Body b)
	{
		P(b, "If you have questions, please contact «OfficialName» at «OfficePhone» or «OfficeEmail». Please keep this letter with your property records.");
		P(b, "");
		P(b, "Sincerely,");
		P(b, "");
		P(b, "");
		P(b, "«OfficialName»");
		P(b, "«OfficialTitle»");
		P(b, "«OfficeName», «CommunityName»");
	}

	// ───────── Document building ─────────

	private static void Write(string path, string title, Action<Body> fill)
	{
		string temp = path + ".tmp";
		try
		{
			using (WordprocessingDocument doc = WordprocessingDocument.Create(temp, WordprocessingDocumentType.Document))
			{
				doc.PackageProperties.Title = title;
				doc.PackageProperties.Description = ReviewNote;
				doc.PackageProperties.Creator = "Substantial Damage Estimator";

				MainDocumentPart main = doc.AddMainDocumentPart();
				Styles(main);

				HeaderPart header = main.AddNewPart<HeaderPart>();
				header.Header = new Header(
					Para("«CommunityName»", bold: true, size: 28),
					Para("«OfficeName»"),
					Para("«OfficeAddress»"));
				FooterPart footer = main.AddNewPart<FooterPart>();
				footer.Footer = new Footer(Para("«OfficeName»  |  «OfficePhone»  |  «OfficeEmail»", size: 18));

				Body body = new Body();
				fill(body);
				body.AppendChild(new SectionProperties(
					new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
					new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
					new PageSize { Width = 12240U, Height = 15840U },
					new PageMargin { Top = 1440, Bottom = 1440, Left = 1440U, Right = 1440U, Header = 720U, Footer = 720U, Gutter = 0U }));
				main.Document = new Document(body);
			}
			File.Move(temp, path, overwrite: true);
		}
		catch
		{
			if (File.Exists(temp))
			{
				File.Delete(temp);
			}
			throw;
		}
	}

	/// <summary>Body text 11 pt, a little space after each paragraph.</summary>
	private static void Styles(MainDocumentPart main)
	{
		StyleDefinitionsPart part = main.AddNewPart<StyleDefinitionsPart>();
		part.Styles = new Styles(
			new DocDefaults(
				new RunPropertiesDefault(new RunPropertiesBaseStyle(
					new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri", EastAsia = "Calibri" },
					new FontSize { Val = "22" },
					new FontSizeComplexScript { Val = "22" },
					new Languages { Val = "en-US" })),
				new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
					new SpacingBetweenLines { After = "60", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),
			new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
			new Style(new StyleName { Val = "Table Grid" }, new BasedOn { Val = "TableNormal" },
				new StyleTableProperties(new TableBorders(
					new TopBorder { Val = BorderValues.Single, Size = 4U },
					new BottomBorder { Val = BorderValues.Single, Size = 4U },
					new LeftBorder { Val = BorderValues.Single, Size = 4U },
					new RightBorder { Val = BorderValues.Single, Size = 4U },
					new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U },
					new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U })))
			{ Type = StyleValues.Table, StyleId = "TableGrid" });
	}

	private static Paragraph P(Body b, string text) => b.AppendChild(Para(text));

	/// <summary>A section heading inside the letter.</summary>
	private static void H(Body b, string text)
	{
		Paragraph p = Para(text, bold: true);
		p.ParagraphProperties = new ParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "160", After = "40" });
		b.AppendChild(p);
	}

	private static Paragraph Para(string text, bool bold = false, int? size = null)
	{
		RunProperties rp = new RunProperties();
		if (bold)
		{
			rp.AppendChild(new Bold());
		}
		if (size is int s)
		{
			rp.AppendChild(new FontSize { Val = s.ToString() });
		}
		Run r = new Run();
		if (rp.HasChildren)
		{
			r.AppendChild(rp);
		}
		r.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
		return new Paragraph(r);
	}

	private static void Bold(Paragraph p)
	{
		foreach (Run r in p.Elements<Run>())
		{
			r.RunProperties ??= new RunProperties();
			r.RunProperties.Bold = new Bold();
		}
	}

	private static Table NewTable() => new Table(
		new TableProperties(new TableStyle { Val = "TableGrid" }, new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }),
		new TableGrid(new GridColumn { Width = "3600" }, new GridColumn { Width = "5760" }));

	private static void Row(Table t, string label, string value)
	{
		t.AppendChild(new TableRow(
			new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "3600" }), Para(label, bold: true)),
			new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "5760" }), Para(value))));
	}
}
