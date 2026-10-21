using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace Sde.Reports.Internal;

/// <summary>
/// The two landscape list reports, grouped NFIP community → structure → assessment like the RDLC's nested lists
/// (groups on NFIPCID, PropertyID, AssessmentID; groups appear in the order their first assessment was given).
/// <list type="bullet">
/// <item>Structure and Percent Damage (extracted/Reports/rptPercentDamaged.rdlc; BLL ReportsService.GetPercentDamagedReports,
/// decomp/BLL/BLL.Service/ReportsService.cs:132).</item>
/// <item>Community (extracted/Reports/rptCommunityRes.rdlc, used for residential and non-residential;
/// BLL ReportsService.GetCommunityReports, line 47).</item>
/// </list>
/// </summary>
internal static class ListReports
{
	private const double PhotoWidth = 1.95;
	private const double PhotoHeight = 1.3;

	private sealed record Group(string Key, string Name, List<List<AssessmentView>> Properties);

	public static void BuildPercentDamaged(Document doc, IReadOnlyList<AssessmentView> items, PhotoLibrary photos, bool includePhotos, DateTime printed)
	{
		const string name = "SDE Structure and Percent Damage Report";
		Section sec = Kit.NewSection(doc, landscape: true, name, printed, null, restartPageNumbers: false);
		List<Column> cols = includePhotos
			? new() { new("Assessment Date", 1.15), new("Owner Name", 1.9), new("City & State", 1.8), new("County/Parish", 1.5), new("Percent Damaged", 1.55, ParagraphAlignment.Right), new("", PhotoWidth + 0.15, ParagraphAlignment.Right) }
			: new() { new("Assessment Date", 1.3), new("Owner Name", 2.8), new("City & State", 2.5), new("County/Parish", 1.8), new("Percent Damaged", 1.6, ParagraphAlignment.Right) };
		Render(sec, name, items, cols, (row, v) =>
		{
			Kit.ValueCell(row.Cells[0], Fmt.Date(v.A.DateofInspection), rule: false);
			Kit.ValueCell(row.Cells[1], v.OwnerName, rule: false);
			Kit.ValueCell(row.Cells[2], Fmt.Join(", ", v.City, v.State), rule: false);
			Kit.ValueCell(row.Cells[3], v.County, rule: false);
			Percent(row.Cells[4], v);
			if (includePhotos)
			{
				row.Cells[5].Format.Alignment = ParagraphAlignment.Right;
				Kit.Photo(row.Cells[5], photos.Primary(v.File), PhotoWidth, PhotoHeight);
			}
		});
	}

	public static void BuildCommunity(Document doc, IReadOnlyList<AssessmentView> items, PhotoLibrary photos, bool includePhotos, DateTime printed)
	{
		const string name = "SDE Community Report";
		Section sec = Kit.NewSection(doc, landscape: true, name, printed, null, restartPageNumbers: false);
		List<Column> cols = includePhotos
			? new() { new("Owner Name", 1.4), new("Assessment Date", 0.85), new("Type of Structure", 1.1), new("Basis for Value of Structure", 1.45), new("Basis for Cost of Repairs", 1.3), new("Computed Actual Cash Value", 1.0, ParagraphAlignment.Right), new("Percent Damaged", 1.2, ParagraphAlignment.Right), new("", 1.7, ParagraphAlignment.Right) }
			: new() { new("Owner Name", 1.85), new("Assessment Date", 1.0), new("Type of Structure", 1.5), new("Basis for Value of Structure", 1.7), new("Basis for Cost of Repairs", 1.6), new("Computed Actual Cash Value", 1.15, ParagraphAlignment.Right), new("Percent Damaged", 1.2, ParagraphAlignment.Right) };
		Render(sec, name, items, cols, (row, v) =>
		{
			Kit.ValueCell(row.Cells[0], v.OwnerName, rule: false);
			Kit.ValueCell(row.Cells[1], Fmt.Date(v.A.DateofInspection), rule: false);
			Kit.ValueCell(row.Cells[2], v.TypeOfStructure, rule: false);
			Basis(row.Cells[3], v.ValueOfBuilding, v.BasisValue);
			Basis(row.Cells[4], v.CostOfRepair, v.BasisCost);
			Kit.ValueCell(row.Cells[5], Fmt.Money(v.A.ComputedActualCashValue), rule: false, align: ParagraphAlignment.Right);
			Percent(row.Cells[6], v);
			if (includePhotos)
			{
				row.Cells[7].Format.Alignment = ParagraphAlignment.Right;
				Kit.Photo(row.Cells[7], photos.Primary(v.File), 1.6, 1.07);
			}
		});
	}

	private static void Render(Section sec, string reportName, IReadOnlyList<AssessmentView> items, IReadOnlyList<Column> cols, Action<Row, AssessmentView> fill)
	{
		List<Group> groups = GroupByCommunity(items);
		if (groups.Count == 0)
		{
			Kit.Note(sec, "No assessments to report.", 12);
			return;
		}
		Paragraph heading = sec.AddParagraph(reportName);
		heading.Format.Font.Size = 15;
		heading.Format.Font.Bold = true;
		int structures = groups.Sum(g => g.Properties.Count);
		Paragraph counts = sec.AddParagraph(
			$"{Plural(items.Count, "assessment")} of {Plural(structures, "structure")} in {Plural(groups.Count, "NFIP community", "NFIP communities")}");
		counts.Style = Theme.LabelStyle;
		counts.Format.Font.Size = 8;
		counts.Format.SpaceBefore = 2;
		counts.Format.SpaceAfter = 4;

		bool first = true;
		foreach (Group g in groups)
		{
			if (!first)
			{
				Kit.Gap(sec, 14);
			}
			first = false;
			Table t = Kit.NewTable(sec, cols.Select(c => c.Width));

			Row title = t.AddRow();
			title.HeadingFormat = true;
			Cell tc = title.Cells[0];
			tc.MergeRight = cols.Count - 1;
			Paragraph tp = tc.AddParagraph();
			FormattedText lbl = tp.AddFormattedText("Community NFIP ID and Name:  ");
			lbl.Color = Theme.Muted;
			lbl.Size = 9;
			FormattedText val = tp.AddFormattedText(Fmt.Join("  ", string.IsNullOrEmpty(g.Key) ? "Not Provided" : g.Key, g.Name));
			val.Bold = true;
			val.Size = 11;
			val.Color = Theme.Accent;
			tc.Borders.Bottom.Width = 1;
			tc.Borders.Bottom.Color = Theme.Accent;
			title.BottomPadding = Unit.FromPoint(3);

			Row head = t.AddRow();
			head.HeadingFormat = true;
			for (int i = 0; i < cols.Count; i++)
			{
				Paragraph p = head.Cells[i].AddParagraph(cols[i].Header);
				p.Style = Theme.LabelStyle;
				p.Format.Font.Bold = true;
				p.Format.Alignment = cols[i].Align;
				Kit.Hairline(head.Cells[i]);
			}
			head.TopPadding = Unit.FromPoint(4);
			head.VerticalAlignment = VerticalAlignment.Bottom;

			foreach (List<AssessmentView> property in g.Properties)
			{
				AssessmentView p0 = property[0];
				Row pr = t.AddRow();
				pr.Shading.Color = Theme.Faint;
				pr.TopPadding = Unit.FromPoint(3.5);
				pr.BottomPadding = Unit.FromPoint(3.5);
				Cell pc = pr.Cells[0];
				pc.MergeRight = cols.Count - 1;
				Paragraph pp = pc.AddParagraph(string.IsNullOrEmpty(p0.FullAddress) ? "(No address)" : p0.FullAddress);
				pp.Format.Font.Bold = true;
				pp.Format.Font.Size = 9;
				int added = 0;
				foreach (AssessmentView v in property)
				{
					Row row = t.AddRow();
					row.TopPadding = Unit.FromPoint(5);
					row.BottomPadding = Unit.FromPoint(5);
					row.VerticalAlignment = VerticalAlignment.Top;
					fill(row, v);
					added++;
					string explanation = v.A.DepreciationExplanation;
					if (!string.IsNullOrWhiteSpace(explanation))
					{
						Row er = t.AddRow();
						er.TopPadding = Unit.FromPoint(0);
						Cell ec = er.Cells[0];
						ec.MergeRight = cols.Count - 1;
						Paragraph ep = ec.AddParagraph();
						ep.Style = Theme.LabelStyle;
						ep.AddFormattedText("Other Depreciation Explanation:  ").Bold = true;
						FormattedText et = ep.AddFormattedText(explanation.Trim());
						et.Color = Theme.Ink;
						row.KeepWith = 1;
						added++;
					}
					foreach (Cell c in t.Rows[t.Rows.Count - 1].Cells)
					{
						Kit.Hairline(c);
					}
				}
				pr.KeepWith = Math.Min(added, 2);
			}
		}
	}

	private static string Plural(int n, string one, string many = null) => $"{n:#,##0} {(n == 1 ? one : many ?? one + "s")}";

	/// <summary>Percent damaged (bold, in the determination color) with the determination underneath.</summary>
	private static void Percent(Cell c, AssessmentView v)
	{
		Color ink = Theme.ToneInk(v.Determination);
		Paragraph p = c.AddParagraph(Fmt.Percent1(v.A.PercentDamage));
		p.Format.Alignment = ParagraphAlignment.Right;
		p.Format.Font.Bold = true;
		p.Format.Font.Size = 11;
		p.Format.Font.Color = ink;
		if (!string.IsNullOrEmpty(v.DamageMessage))
		{
			Paragraph m = c.AddParagraph(v.DamageMessage);
			m.Format.Alignment = ParagraphAlignment.Right;
			m.Format.Font.Size = 6.5;
			m.Format.Font.Color = ink;
		}
	}

	/// <summary>The community report's basis columns: the amount (ValueOfBuilding / CostOfRepair) over the basis name.</summary>
	private static void Basis(Cell c, string amount, string basis)
	{
		Paragraph a = c.AddParagraph(amount ?? "");
		a.Format.Font.Bold = true;
		Paragraph b = c.AddParagraph(basis ?? "");
		b.Style = Theme.LabelStyle;
	}

	private static List<Group> GroupByCommunity(IReadOnlyList<AssessmentView> items)
	{
		List<Group> groups = new();
		foreach (AssessmentView v in items)
		{
			string key = v.P.NFIPCID?.Trim() ?? "";
			Group g = groups.FirstOrDefault(x => x.Key == key);
			if (g == null)
			{
				g = new Group(key, v.P.NFIPCommunityName?.Trim() ?? "", new List<List<AssessmentView>>());
				groups.Add(g);
			}
			List<AssessmentView> property = g.Properties.FirstOrDefault(p => p[0].P.PropertyID == v.P.PropertyID);
			if (property == null)
			{
				g.Properties.Add(new List<AssessmentView> { v });
			}
			else
			{
				property.Add(v);
			}
		}
		// The RDLC printed First(NFIPCommunityName) of the group; take the first non-empty one.
		return groups.Select(g => g with
		{
			Name = g.Properties.SelectMany(p => p).Select(v => v.P.NFIPCommunityName?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "",
		}).ToList();
	}
}
