using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;

namespace Sde.Reports.Internal;

/// <summary>A label and its value. <see cref="Wide"/> lets the value run across the rest of the row.</summary>
internal readonly record struct Field(string Label, string Value, bool Wide = false);

/// <summary>A titled column of fields (one of the RDLC's bordered rectangles).</summary>
internal sealed record Panel(string Title, IReadOnlyList<Field> Fields, double LabelWidth, double ValueWidth);

/// <summary>A data-table column.</summary>
internal readonly record struct Column(string Header, double Width, ParagraphAlignment Align = ParagraphAlignment.Left);

/// <summary>
/// Layout building blocks. Everything is a borderless MigraDoc table (MigraDoc cannot nest tables), with
/// hairline rules under each field and an accent rule under each section title. Widths are in inches.
/// </summary>
internal static class Kit
{
	public const double PortraitWidth = 7.3;     // Letter, 0.6in side margins
	public const double LandscapeWidth = 10.0;   // Letter landscape, 0.5in side margins
	public const double Gutter = 0.3;

	private const double CellPadX = 3;           // points
	private const double CellPadY = 1.5;

	// ───────── Pages ─────────

	/// <summary>
	/// A new section with the page header ("Substantial Damage Estimator" + report name) and footer
	/// (the original's print date in format "D", optional center text, "Page X of Y").
	/// </summary>
	public static Section NewSection(Document doc, bool landscape, string reportName, DateTime printed, string footerCenter, bool restartPageNumbers)
	{
		Section sec = doc.AddSection();
		PageSetup ps = sec.PageSetup;
		// Explicit paper size (MigraDoc's Orientation swap is easy to get wrong): 8.5 × 11, or 11 × 8.5 for landscape.
		ps.Orientation = Orientation.Portrait;
		ps.PageWidth = Unit.FromInch(landscape ? 11 : 8.5);
		ps.PageHeight = Unit.FromInch(landscape ? 8.5 : 11);
		ps.LeftMargin = ps.RightMargin = Unit.FromInch(landscape ? 0.5 : 0.6);
		ps.TopMargin = Unit.FromInch(0.72);
		ps.BottomMargin = Unit.FromInch(0.66);
		ps.HeaderDistance = Unit.FromInch(0.4);
		ps.FooterDistance = Unit.FromInch(0.36);
		if (restartPageNumbers)
		{
			ps.StartingNumber = 1;
		}
		double width = landscape ? LandscapeWidth : PortraitWidth;

		Paragraph h = sec.Headers.Primary.AddParagraph();
		h.Format.TabStops.AddTabStop(Unit.FromInch(width), TabAlignment.Right);
		h.Format.Borders.Bottom.Width = 0.6;
		h.Format.Borders.Bottom.Color = Theme.Accent;
		h.Format.Borders.DistanceFromBottom = 3;
		FormattedText brand = h.AddFormattedText("Substantial Damage Estimator");
		brand.Bold = true;
		brand.Color = Theme.Accent;
		brand.Size = 8.5;
		h.AddTab();
		FormattedText name = h.AddFormattedText(reportName);
		name.Color = Theme.Muted;
		name.Size = 8.5;

		Paragraph f = sec.Footers.Primary.AddParagraph();
		f.Format.TabStops.AddTabStop(Unit.FromInch(width / 2), TabAlignment.Center);
		f.Format.TabStops.AddTabStop(Unit.FromInch(width), TabAlignment.Right);
		f.Format.Borders.Top.Width = 0.5;
		f.Format.Borders.Top.Color = Theme.Rule;
		f.Format.Borders.DistanceFromTop = 3;
		f.AddText(Fmt.LongDate(printed));
		f.AddTab();
		if (!string.IsNullOrEmpty(footerCenter))
		{
			f.AddFormattedText(footerCenter).Bold = true;
		}
		f.AddTab();
		f.AddText("Page ");
		f.AddPageField();
		f.AddText(" of ");
		if (restartPageNumbers)
		{
			f.AddSectionPagesField();
		}
		else
		{
			f.AddNumPagesField();
		}
		return sec;
	}

	/// <summary>Vertical space between blocks.</summary>
	public static void Gap(Section sec, double points)
	{
		Paragraph p = sec.AddParagraph();
		p.Format.Font.Size = 1;
		p.Format.SpaceBefore = Unit.FromPoint(Math.Max(0, points - 1));
	}

	public static Table NewTable(Section sec, IEnumerable<double> widths)
	{
		Table t = sec.AddTable();
		t.Borders.Visible = false;
		t.LeftPadding = Unit.FromPoint(CellPadX);
		t.RightPadding = Unit.FromPoint(CellPadX);
		t.TopPadding = Unit.FromPoint(CellPadY);
		t.BottomPadding = Unit.FromPoint(CellPadY);
		t.Rows.LeftIndent = Unit.FromPoint(-CellPadX);   // text aligns with the margin, not the cell edge
		foreach (double w in widths)
		{
			t.AddColumn(Unit.FromInch(w));
		}
		return t;
	}

	// ───────── Cells ─────────

	/// <summary>A section title in the first row of a block, spanning <paramref name="span"/> columns.</summary>
	public static void TitleCell(Row row, int col, int span, string title)
	{
		Cell c = row.Cells[col];
		if (span > 1)
		{
			c.MergeRight = span - 1;
		}
		c.AddParagraph(title ?? "").Style = Theme.TitleStyle;
		c.Borders.Bottom.Width = 0.8;
		c.Borders.Bottom.Color = Theme.Accent;
		row.TopPadding = Unit.FromPoint(7.5);
		row.BottomPadding = Unit.FromPoint(2.2);
		row.VerticalAlignment = VerticalAlignment.Bottom;
	}

	public static void LabelCell(Cell c, string text, bool rule = true, ParagraphAlignment align = ParagraphAlignment.Left)
	{
		Paragraph p = c.AddParagraph(text ?? "");
		p.Style = Theme.LabelStyle;
		p.Format.Alignment = align;
		p.Format.SpaceBefore = Unit.FromPoint(0.9);   // 7pt label on the 8pt value's baseline
		if (rule)
		{
			Hairline(c);
		}
	}

	public static void ValueCell(Cell c, string text, bool rule = true, bool bold = false, ParagraphAlignment align = ParagraphAlignment.Left)
	{
		Paragraph p = c.AddParagraph(text ?? "");
		p.Style = Theme.ValueStyle;
		p.Format.Alignment = align;
		if (bold)
		{
			p.Format.Font.Bold = true;
		}
		if (rule)
		{
			Hairline(c);
		}
	}

	public static void Hairline(Cell c)
	{
		c.Borders.Bottom.Width = 0.4;
		c.Borders.Bottom.Color = Theme.Rule;
	}

	private static void FieldCells(Row row, int col, Field f, int valueSpan = 1)
	{
		LabelCell(row.Cells[col], f.Label);
		Cell v = row.Cells[col + 1];
		if (valueSpan > 1)
		{
			v.MergeRight = valueSpan - 1;
		}
		ValueCell(v, f.Value);
	}

	/// <summary>
	/// Keeps a short block on one page; a long one may break, but its title stays with the first rows.
	/// MigraDoc keeps a row with the next <see cref="Row.KeepWith"/> rows; heading-format rows always travel
	/// with the first body row, so the rule is set on the first body row.
	/// </summary>
	private static void KeepTogether(Table t, int maxRows = 10)
	{
		int first = 0;
		while (first < t.Rows.Count && t.Rows[first].HeadingFormat)
		{
			first++;
		}
		int body = t.Rows.Count - first;
		if (body <= 1)
		{
			return;
		}
		if (first == 0)
		{
			// A plain title row: keep it with the rows below.
			t.Rows[0].KeepWith = t.Rows.Count <= maxRows ? t.Rows.Count - 1 : Math.Min(3, t.Rows.Count - 1);
		}
		else
		{
			t.Rows[first].KeepWith = body <= maxRows ? body - 1 : Math.Min(2, body - 1);
		}
	}

	// ───────── Blocks ─────────

	/// <summary>Side-by-side titled panels of label/value rows (the RDLC's paired rectangles).</summary>
	public static Table Panels(Section sec, params Panel[] panels)
	{
		List<double> widths = new();
		for (int i = 0; i < panels.Length; i++)
		{
			if (i > 0)
			{
				widths.Add(Gutter);
			}
			widths.Add(panels[i].LabelWidth);
			widths.Add(panels[i].ValueWidth);
		}
		Table t = NewTable(sec, widths);
		Row title = t.AddRow();
		for (int i = 0; i < panels.Length; i++)
		{
			TitleCell(title, i * 3, 2, panels[i].Title);
		}
		int rows = panels.Max(p => p.Fields.Count);
		for (int r = 0; r < rows; r++)
		{
			Row row = t.AddRow();
			for (int i = 0; i < panels.Length; i++)
			{
				if (r < panels[i].Fields.Count)
				{
					FieldCells(row, i * 3, panels[i].Fields[r]);
				}
			}
		}
		KeepTogether(t);
		return t;
	}

	/// <summary>
	/// A titled block of label/value pairs laid out in rows. <paramref name="pairWidths"/> is label, value[, label, value…]
	/// (a gutter goes between pairs); each row holds up to one field per pair; a <see cref="Field.Wide"/> field takes
	/// the rest of its row.
	/// </summary>
	public static Table Fields(Section sec, string title, double[] pairWidths, IEnumerable<Field[]> rows)
	{
		List<double> widths = new();
		for (int i = 0; i < pairWidths.Length; i += 2)
		{
			if (i > 0)
			{
				widths.Add(Gutter);
			}
			widths.Add(pairWidths[i]);
			widths.Add(pairWidths[i + 1]);
		}
		Table t = NewTable(sec, widths);
		if (title != null)
		{
			TitleCell(t.AddRow(), 0, widths.Count, title);
		}
		foreach (Field[] fields in rows)
		{
			Row row = t.AddRow();
			int col = 0;
			foreach (Field f in fields)
			{
				int span = f.Wide ? widths.Count - col - 1 : 1;
				FieldCells(row, col, f, span);
				col += 1 + span + 1;   // skip the gutter
			}
		}
		KeepTogether(t);
		return t;
	}

	/// <summary>
	/// A titled row of fields with labels above values (the RDLC's NFIP Information strip), optionally followed by
	/// <paramref name="wide"/> label/value rows whose value spans the remaining columns.
	/// </summary>
	public static Table Stacked(Section sec, string title, double[] widths, IReadOnlyList<Field> fields, params Field[] wide)
	{
		Table t = NewTable(sec, widths);
		if (title != null)
		{
			TitleCell(t.AddRow(), 0, widths.Length, title);
		}
		Row labels = t.AddRow();
		labels.BottomPadding = Unit.FromPoint(0.5);
		Row values = t.AddRow();
		for (int i = 0; i < fields.Count; i++)
		{
			LabelCell(labels.Cells[i], fields[i].Label, rule: false);
			ValueCell(values.Cells[i], fields[i].Value);
		}
		foreach (Field f in wide)
		{
			Row row = t.AddRow();
			row.TopPadding = Unit.FromPoint(3);
			row.Cells[0].MergeRight = 1;
			LabelCell(row.Cells[0], f.Label);
			row.Cells[2].MergeRight = widths.Length - 3;
			ValueCell(row.Cells[2], f.Value);
		}
		KeepTogether(t);
		return t;
	}

	/// <summary>A titled data table with a shaded header row (repeated after a page break) and optional total rows.</summary>
	public static Table Data(Section sec, string title, IReadOnlyList<Column> columns, IEnumerable<string[]> rows, IEnumerable<string[]> totals = null)
	{
		Table t = NewTable(sec, columns.Select(c => c.Width));
		if (title != null)
		{
			Row tr = t.AddRow();
			TitleCell(tr, 0, columns.Count, title);
			tr.HeadingFormat = true;
		}
		Row head = t.AddRow();
		head.HeadingFormat = true;
		head.Shading.Color = Theme.Faint;
		for (int i = 0; i < columns.Count; i++)
		{
			Paragraph p = head.Cells[i].AddParagraph(columns[i].Header);
			p.Style = Theme.LabelStyle;
			p.Format.Font.Bold = true;
			p.Format.Alignment = columns[i].Align;
			Hairline(head.Cells[i]);
		}
		int count = 0;
		foreach (string[] values in rows)
		{
			Row row = t.AddRow();
			for (int i = 0; i < columns.Count; i++)
			{
				ValueCell(row.Cells[i], i < values.Length ? values[i] : "", align: columns[i].Align, bold: i == 0);
			}
			count++;
		}
		if (count == 0)
		{
			Row row = t.AddRow();
			row.Cells[0].MergeRight = columns.Count - 1;
			Paragraph p = row.Cells[0].AddParagraph("None");
			p.Style = Theme.NoteStyle;
			Hairline(row.Cells[0]);
		}
		foreach (string[] values in totals ?? Array.Empty<string[]>())
		{
			Row row = t.AddRow();
			for (int i = 0; i < columns.Count; i++)
			{
				ValueCell(row.Cells[i], i < values.Length ? values[i] : "", rule: false, bold: true, align: columns[i].Align);
			}
		}
		KeepTogether(t, 14);
		return t;
	}

	/// <summary>A small explanatory line (footnotes such as the FEMA 213 note).</summary>
	public static Paragraph Note(Section sec, string text, double spaceBefore = 3)
	{
		Paragraph p = sec.AddParagraph(text);
		p.Style = Theme.NoteStyle;
		p.Format.SpaceBefore = Unit.FromPoint(spaceBefore);
		return p;
	}

	// ───────── Hero ─────────

	/// <summary>
	/// Top of the summary/detail page: the structure address in large type, and the determination panel —
	/// percent damaged (Assessment.PercentDamage, already a percentage) and Assessment.DamageMessage, with the
	/// value and repair cost it was computed from (the RDLC's "Percent Damaged" box).
	/// </summary>
	public static Table Hero(Section sec, AssessmentView v, bool showAmounts)
	{
		Table t = NewTable(sec, new[] { 2.7, 0.15, 2.25, 2.2 });
		Row row = t.AddRow();
		row.VerticalAlignment = VerticalAlignment.Center;

		Cell a = row.Cells[0];
		Paragraph street = a.AddParagraph(string.IsNullOrEmpty(v.StreetAddress) ? "(No street address)" : v.StreetAddress);
		street.Format.Font.Size = 15;
		street.Format.Font.Bold = true;
		Paragraph city = a.AddParagraph(v.CityStateZip);
		city.Format.Font.Size = 9.5;
		city.Format.SpaceBefore = 1;
		Paragraph meta = a.AddParagraph();
		meta.Format.SpaceBefore = 6;
		meta.Style = Theme.LabelStyle;
		AddMeta(meta, "Owner", v.OwnerName);
		AddMeta(meta, "NFIP Community", Fmt.Join(" ", v.P.NFIPCID, v.P.NFIPCommunityName));
		AddMeta(meta, "Date of Assessment", Fmt.Date(v.A.DateofInspection));

		Color ink = Theme.ToneInk(v.Determination);
		Color fill = Theme.ToneFill(v.Determination);

		Cell d = row.Cells[2];
		d.Shading.Color = fill;
		d.Borders.Left.Width = 3;
		d.Borders.Left.Color = ink;
		Paragraph cap = d.AddParagraph("Percent Damaged");
		cap.Format.Font.Size = 7;
		cap.Format.Font.Bold = true;
		cap.Format.Font.Color = ink;
		Paragraph pct = d.AddParagraph(v.A.PercentDamage is decimal ? Fmt.Percent1(v.A.PercentDamage) : "–");
		pct.Format.Font.Size = 24;
		pct.Format.Font.Bold = true;
		pct.Format.Font.Color = ink;
		Paragraph msg = d.AddParagraph(string.IsNullOrEmpty(v.DamageMessage) ? "Not calculated" : v.DamageMessage);
		msg.Format.Font.Size = 9;
		msg.Format.Font.Bold = true;
		msg.Format.Font.Color = ink;
		Pad(d, 10, 8);

		Cell b = row.Cells[3];
		b.Shading.Color = fill;
		AddBasis(b, "Basis for Value of Structure", showAmounts ? v.ValueOfBuilding : null, v.BasisValue, 0);
		AddBasis(b, "Basis for Cost of Repairs", showAmounts ? v.CostOfRepair : null, v.BasisCost, 7);
		Pad(b, 4, 8);
		return t;
	}

	private static void AddMeta(Paragraph p, string label, string value)
	{
		if (p.Elements.Count > 0)
		{
			p.AddLineBreak();
		}
		p.AddText(label + "  ");
		FormattedText f = p.AddFormattedText(string.IsNullOrEmpty(value) ? "–" : value);
		f.Color = Theme.Ink;
		f.Size = 8;
	}

	private static void AddBasis(Cell c, string label, string amount, string basis, double spaceBefore)
	{
		Paragraph l = c.AddParagraph(label);
		l.Style = Theme.LabelStyle;
		l.Format.SpaceBefore = Unit.FromPoint(spaceBefore);
		if (amount != null)
		{
			Paragraph a = c.AddParagraph(amount);
			a.Format.Font.Size = 10;
			a.Format.Font.Bold = true;
		}
		Paragraph b = c.AddParagraph(string.IsNullOrEmpty(basis) ? "–" : basis);
		b.Format.Font.Size = amount != null ? 7.5 : 9;
		if (amount == null)
		{
			b.Format.Font.Bold = true;
		}
	}

	/// <summary>
	/// MigraDoc pads per table/row, so cell padding is emulated with paragraph indents (set on each paragraph:
	/// a paragraph style overrides the cell's format) and row padding.
	/// </summary>
	private static void Pad(Cell c, double x, double y)
	{
		foreach (Paragraph p in c.Elements.OfType<Paragraph>())
		{
			p.Format.LeftIndent = Unit.FromPoint(x);
			p.Format.RightIndent = Unit.FromPoint(x / 2);
		}
		c.Row.TopPadding = Unit.FromPoint(y);
		c.Row.BottomPadding = Unit.FromPoint(y);
	}

	// ───────── Photos ─────────

	/// <summary>
	/// A photo scaled to fit a box (the RDLC's Sizing=FitProportional), centered; or a neutral placeholder box
	/// with "No Photo Available" (FEMA's ImageNotAvailable images) or why the file could not be used.
	/// </summary>
	public static void Photo(Cell cell, Photo photo, double maxWidth, double maxHeight, string caption = null, ParagraphAlignment align = ParagraphAlignment.Center)
	{
		if (photo.Status == PhotoStatus.Ok)
		{
			double scale = Math.Min(maxWidth / photo.PixelWidth, maxHeight / photo.PixelHeight);
			Paragraph p = cell.AddParagraph();
			p.Format.Alignment = align;
			Image img = p.AddImage(photo.Path);
			img.LockAspectRatio = true;
			img.Width = Unit.FromInch(photo.PixelWidth * scale);
		}
		else
		{
			TextFrame frame = cell.AddTextFrame();
			frame.Width = Unit.FromInch(maxWidth);
			frame.Height = Unit.FromInch(maxHeight);
			frame.FillFormat.Color = Theme.Faint;
			frame.LineFormat.Color = Theme.Rule;
			frame.LineFormat.Width = 0.5;
			frame.MarginTop = Unit.FromInch(Math.Max(0, maxHeight / 2 - 0.09));
			Paragraph p = frame.AddParagraph(photo.PlaceholderText);
			p.Format.Alignment = ParagraphAlignment.Center;
			p.Format.Font.Size = 7.5;
			p.Format.Font.Color = Theme.Muted;
		}
		if (!string.IsNullOrEmpty(caption))
		{
			Paragraph c = cell.AddParagraph(caption);
			c.Style = Theme.LabelStyle;
			c.Format.Font.Bold = true;
			c.Format.Alignment = ParagraphAlignment.Center;
			c.Format.SpaceBefore = 3;
		}
	}

	// ───────── Signature ─────────

	/// <summary>
	/// The "Authorized Local Official :" signature lines. Summary: Signature | Printed Name side by side;
	/// detail: Signature + (Date) and Print Name + (Date) on two lines.
	/// </summary>
	public static Table Signature(Section sec, bool detail)
	{
		Table t;
		if (!detail)
		{
			t = NewTable(sec, new[] { 1.55, 2.0, Gutter, 1.55, 1.9 });
			Row line = t.AddRow();
			line.TopPadding = Unit.FromPoint(16);
			line.VerticalAlignment = VerticalAlignment.Bottom;
			SignatureLabel(line.Cells[0]);
			SignatureLine(line.Cells[1]);
			SignatureLabel(line.Cells[3]);
			SignatureLine(line.Cells[4]);
			Row caps = t.AddRow();
			Caption(caps.Cells[1], "Signature");
			Caption(caps.Cells[4], "Printed Name");
		}
		else
		{
			t = NewTable(sec, new[] { 1.7, 3.6, Gutter, 1.7 });
			foreach (string what in new[] { "Signature", "Print Name" })
			{
				Row line = t.AddRow();
				line.TopPadding = Unit.FromPoint(18);
				line.VerticalAlignment = VerticalAlignment.Bottom;
				SignatureLabel(line.Cells[0]);
				SignatureLine(line.Cells[1]);
				SignatureLine(line.Cells[3]);
				Row caps = t.AddRow();
				Caption(caps.Cells[1], what);
				Caption(caps.Cells[3], "(Date)");
			}
		}
		t.Rows[0].KeepWith = t.Rows.Count - 1;
		return t;
	}

	private static void SignatureLabel(Cell c)
	{
		Paragraph p = c.AddParagraph("Authorized Local Official :");
		p.Format.Font.Bold = true;
		p.Format.Font.Size = 8;
	}

	private static void SignatureLine(Cell c)
	{
		c.Borders.Bottom.Width = 0.7;
		c.Borders.Bottom.Color = Theme.Ink;
	}

	private static void Caption(Cell c, string text) => c.AddParagraph(text).Style = Theme.LabelStyle;
}
