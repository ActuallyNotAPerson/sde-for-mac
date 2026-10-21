using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Reports.Internal;
using SkiaSharp;
using Column = Sde.Reports.Internal.Column;

namespace Sde.Reports;

/// <summary>What to print on the event summary page besides the numbers.</summary>
public sealed class EventSummaryOptions
{
	/// <summary>Page title; "Event Summary" when blank.</summary>
	public string Title { get; init; }

	/// <summary>Shown under the title and in the footer.</summary>
	public string CommunityName { get; init; }

	/// <summary>A picture of the area (for example a screenshot of the map) to print instead of the dot plot.</summary>
	public string MapImagePath { get; init; }

	/// <summary>Date printed in the footer; default DateTime.Now.</summary>
	public DateTime? GeneratedOn { get; init; }

	/// <summary>How many streets the table lists.</summary>
	public int TopStreets { get; init; } = 8;
}

/// <summary>
/// A one-page overview of a disaster event for officials: headline numbers, how many structures fall in each
/// result class, the most affected streets, and a plot of where the structures are. Same fonts and styling as the
/// other reports.
/// </summary>
public static class EventSummaryReport
{
	/// <summary>Writes the page to a PDF file (overwriting it). The file is only replaced once rendering succeeded.</summary>
	public static void RenderToFile(EventSummary summary, EventSummaryOptions options, string path)
	{
		ArgumentNullException.ThrowIfNull(summary);
		ArgumentException.ThrowIfNullOrEmpty(path);
		options ??= new EventSummaryOptions();
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		Document doc = Build(summary, options);
		PdfDocumentRenderer renderer = new PdfDocumentRenderer { Document = doc };
		renderer.RenderDocument();
		using MemoryStream buffer = new MemoryStream();
		renderer.PdfDocument.Save(buffer, false);
		File.WriteAllBytes(path, buffer.ToArray());
	}

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private static Document Build(EventSummary s, EventSummaryOptions o)
	{
		string title = string.IsNullOrWhiteSpace(o.Title) ? "Event Summary" : o.Title.Trim();
		DateTime printed = o.GeneratedOn ?? DateTime.Now;
		Document doc = Theme.NewDocument(title, $"{s.TotalStructures} structure(s)");
		Section sec = Kit.NewSection(doc, landscape: false, "Event Summary", printed, string.IsNullOrWhiteSpace(o.CommunityName) ? null : o.CommunityName.Trim(), restartPageNumbers: false);

		Paragraph heading = sec.AddParagraph(title);
		heading.Format.Font.Size = 18;
		heading.Format.Font.Bold = true;
		heading.Format.Font.Color = Theme.Accent;
		List<string> sub = new();
		if (!string.IsNullOrWhiteSpace(o.CommunityName))
		{
			sub.Add(o.CommunityName.Trim());
		}
		if (!string.IsNullOrWhiteSpace(s.EventName) && s.EventName != title)
		{
			sub.Add(s.EventName.Trim());
		}
		string damage = Range("Damage occurred", s.DamageStart, s.DamageEnd);
		if (damage != null)
		{
			sub.Add(damage);
		}
		string inspected = Range("Inspected", s.InspectionStart, s.InspectionEnd);
		if (inspected != null)
		{
			sub.Add(inspected);
		}
		if (sub.Count > 0)
		{
			Paragraph p = sec.AddParagraph(string.Join("   |   ", sub));
			p.Style = Theme.NoteStyle;
			p.Format.Font.Size = 8.5;
			p.Format.SpaceBefore = 2;
		}
		Kit.Gap(sec, 10);

		Headline(sec, s);
		Kit.Gap(sec, 10);

		List<string[]> classRows = new();
		foreach (DeterminationClass c in new[] { DeterminationClass.Substantial, DeterminationClass.Close, DeterminationClass.NotSubstantial, DeterminationClass.Undetermined, DeterminationClass.Incomplete })
		{
			classRows.Add(new[] { Determinations.Label(c), Rule(c, s), s.Count(c).ToString("N0", Us), Share(s.Count(c), s.AssessmentCount) });
		}
		Kit.Data(sec, "Results", new[]
		{
			new Column("Result", 1.8), new Column("Rule", 2.7), new Column("Assessments", 1.4, ParagraphAlignment.Right), new Column("Share", 1.4, ParagraphAlignment.Right),
		}, classRows, new[] { new[] { "Total", "", s.AssessmentCount.ToString("N0", Us), s.AssessmentCount == 0 ? "" : "100%" } });
		Kit.Gap(sec, 10);

		List<string[]> streetRows = s.ByStreet.Take(Math.Max(1, o.TopStreets)).Select(g => new[]
		{
			g.Label, g.Count.ToString("N0", Us), g.Substantial.ToString("N0", Us), g.Close.ToString("N0", Us), Money(g.RepairCost),
		}).ToList();
		Kit.Data(sec, "Most affected streets", new[]
		{
			new Column("Street", 2.6), new Column("Assessments", 1.2, ParagraphAlignment.Right), new Column("Substantial", 1.1, ParagraphAlignment.Right),
			new Column("Close", 0.9, ParagraphAlignment.Right), new Column("Repair cost", 1.5, ParagraphAlignment.Right),
		}, streetRows);
		if (s.ByStreet.Count > streetRows.Count)
		{
			Kit.Note(sec, $"{s.ByStreet.Count - streetRows.Count} more street(s) not shown.");
		}
		Kit.Gap(sec, 10);

		Picture(sec, s, o);
		Kit.Note(sec, "Building value and repair cost add up only the assessments that have an amount. Results use the percentages in the table above.", 6);
		return doc;
	}

	private static string Rule(DeterminationClass c, EventSummary s) => c switch
	{
		DeterminationClass.Substantial => $"{Pct(s.SubstantialPercent)} or more damaged",
		DeterminationClass.Close => $"{Pct(s.ClosePercent)} up to {Pct(s.SubstantialPercent)}",
		DeterminationClass.NotSubstantial => $"Under {Pct(s.ClosePercent)}",
		DeterminationClass.Undetermined => "Damage could not be determined",
		_ => "Data entry incomplete",
	};

	private static string Pct(decimal v) => v.ToString("0.##", Us) + "%";

	private static string Share(int part, int whole) => whole == 0 ? "" : (100.0 * part / whole).ToString("0.0", Us) + "%";

	private static string Money(decimal v) => v.ToString("C0", Us);

	private static string Range(string label, DateTime? from, DateTime? to)
	{
		if (from == null)
		{
			return null;
		}
		string a = from.Value.ToString("MMM d, yyyy", Us);
		string b = to.Value.ToString("MMM d, yyyy", Us);
		return a == b ? $"{label} {a}" : $"{label} {a} to {b}";
	}

	/// <summary>Four big numbers across the page.</summary>
	private static void Headline(Section sec, EventSummary s)
	{
		double w = Kit.PortraitWidth / 4;
		Table t = Kit.NewTable(sec, new[] { w, w, w, w });
		Row numbers = t.AddRow();
		Row labels = t.AddRow();
		Row notes = t.AddRow();
		(string Value, string Label, string Note)[] tiles =
		{
			(s.TotalStructures.ToString("N0", Us), "Structures", $"{s.AssessedStructures:N0} assessed, {s.NotYetAssessed:N0} not yet"),
			(s.Count(DeterminationClass.Substantial).ToString("N0", Us), "Substantially damaged", Share(s.Count(DeterminationClass.Substantial), s.AssessmentCount) + " of assessments"),
			(Money(s.TotalBuildingValue), "Building value", "of assessed structures"),
			(Money(s.TotalRepairCost), "Repair cost", "of assessed structures"),
		};
		for (int i = 0; i < 4; i++)
		{
			Paragraph n = numbers.Cells[i].AddParagraph(tiles[i].Value);
			n.Format.Font.Size = 17;
			n.Format.Font.Bold = true;
			n.Format.Font.Color = i == 1 ? Theme.SubstantialInk : Theme.Accent;
			Paragraph l = labels.Cells[i].AddParagraph(tiles[i].Label);
			l.Style = Theme.LabelStyle;
			l.Format.Font.Bold = true;
			Paragraph d = notes.Cells[i].AddParagraph(tiles[i].Note);
			d.Style = Theme.NoteStyle;
			foreach (Row r in new[] { numbers, labels, notes })
			{
				r.Cells[i].Shading.Color = Theme.Faint;
			}
		}
		t.Rows.LeftIndent = 0;
	}

	private static void Picture(Section sec, EventSummary s, EventSummaryOptions o)
	{
		const double maxWidth = Kit.PortraitWidth;
		const double maxHeight = 3.3;
		byte[] png = null;
		string mapPath = null;
		if (!string.IsNullOrWhiteSpace(o.MapImagePath) && File.Exists(o.MapImagePath))
		{
			mapPath = o.MapImagePath;
			png = PngBytes(mapPath);
		}
		else
		{
			png = DotPlot(s.Points, 1500, 750);
		}
		if (png == null)
		{
			return;
		}
		using (SKBitmap bmp = SKBitmap.Decode(png))
		{
			double scale = Math.Min(maxWidth / bmp.Width, maxHeight / bmp.Height);
			Paragraph title = sec.AddParagraph(mapPath == null ? "Where the structures are" : "Map");
			title.Style = Theme.TitleStyle;
			Paragraph p = sec.AddParagraph();
			p.Format.SpaceBefore = 3;
			MigraDoc.DocumentObjectModel.Shapes.Image img = p.AddImage("base64:" + Convert.ToBase64String(png));
			img.LockAspectRatio = true;
			img.Width = Unit.FromInch(bmp.Width * scale);
		}
		if (mapPath == null)
		{
			Kit.Note(sec, "Each dot is one assessed structure, placed by latitude and longitude. There is no basemap.", 3);
		}
	}

	/// <summary>Re-encodes any image Skia can read as PNG (PDFsharp is happiest with PNG and JPEG), or null.</summary>
	private static byte[] PngBytes(string path)
	{
		using SKBitmap bmp = SKBitmap.Decode(path);
		if (bmp == null)
		{
			return null;
		}
		using SKImage image = SKImage.FromBitmap(bmp);
		using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
		return data?.ToArray();
	}

	/// <summary>The fill used for a result class in the dot plot.</summary>
	public static SKColor DotColor(DeterminationClass c) => c switch
	{
		DeterminationClass.Substantial => new SKColor(0xC6, 0x28, 0x28),
		DeterminationClass.Close => new SKColor(0xF0, 0x8C, 0x00),
		DeterminationClass.NotSubstantial => new SKColor(0x2E, 0x8B, 0x4B),
		_ => new SKColor(0x8A, 0x94, 0xA3),
	};

	/// <summary>
	/// A PNG dot plot of the structure locations coloured by result class, or null when there are none. East is right
	/// and north is up; longitude is scaled by the cosine of the latitude so distances look right. No basemap.
	/// </summary>
	public static byte[] DotPlot(IReadOnlyList<EventPoint> points, int width, int height)
	{
		if (points == null || points.Count == 0)
		{
			return null;
		}
		double meanLat = points.Average(p => (double)p.Latitude);
		double kx = Math.Cos(meanLat * Math.PI / 180.0);
		double minX = points.Min(p => (double)p.Longitude) * kx;
		double maxX = points.Max(p => (double)p.Longitude) * kx;
		double minY = points.Min(p => (double)p.Latitude);
		double maxY = points.Max(p => (double)p.Latitude);
		double spanX = Math.Max(maxX - minX, 1e-5);
		double spanY = Math.Max(maxY - minY, 1e-5);

		const float margin = 24f;
		const float legendHeight = 44f;
		float areaW = width - 2 * margin;
		float areaH = height - 2 * margin - legendHeight;
		double scale = Math.Min(areaW / spanX, areaH / spanY);
		float offX = margin + (float)((areaW - spanX * scale) / 2);
		float offY = margin + (float)((areaH - spanY * scale) / 2);

		using SKBitmap bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
		using SKCanvas canvas = new SKCanvas(bitmap);
		canvas.Clear(new SKColor(0xF3, 0xF5, 0xF8));
		using SKPaint frame = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0xD8, 0xDD, 0xE4), StrokeWidth = 2, IsAntialias = true };
		canvas.DrawRect(1, 1, width - 2, height - 2, frame);

		float radius = points.Count > 2000 ? 5f : points.Count > 300 ? 7f : 10f;
		using SKPaint fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
		using SKPaint edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 1.5f, IsAntialias = true };
		// Least important first so substantial structures are never hidden under other dots.
		foreach (EventPoint p in points.OrderBy(p => Rank(p.Class)))
		{
			float x = offX + (float)(((double)p.Longitude * kx - minX) * scale);
			float y = offY + (float)((maxY - (double)p.Latitude) * scale);
			fill.Color = DotColor(p.Class);
			canvas.DrawCircle(x, y, radius, fill);
			canvas.DrawCircle(x, y, radius, edge);
		}

		// Legend along the bottom.
		using SKTypeface face = SKTypeface.Default;
		using SKFont font = new SKFont(face, 22f);
		using SKPaint text = new SKPaint { Color = new SKColor(0x1B, 0x22, 0x2C), IsAntialias = true };
		float lx = margin;
		float ly = height - margin - 6f;
		foreach (DeterminationClass c in new[] { DeterminationClass.Substantial, DeterminationClass.Close, DeterminationClass.NotSubstantial, DeterminationClass.Undetermined })
		{
			int n = points.Count(p => c == DeterminationClass.Undetermined ? p.Class is DeterminationClass.Undetermined or DeterminationClass.Incomplete : p.Class == c);
			string label = (c == DeterminationClass.Undetermined ? "Incomplete or undetermined" : Determinations.Label(c)) + " (" + n.ToString("N0", Us) + ")";
			fill.Color = DotColor(c);
			canvas.DrawCircle(lx + 9, ly - 8, 9, fill);
			canvas.DrawText(label, lx + 26, ly, font, text);
			lx += 26 + font.MeasureText(label) + 36;
		}

		using SKImage image = SKImage.FromBitmap(bitmap);
		using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
		return data.ToArray();
	}

	private static int Rank(DeterminationClass c) => c switch
	{
		DeterminationClass.Incomplete => 0,
		DeterminationClass.Undetermined => 1,
		DeterminationClass.NotSubstantial => 2,
		DeterminationClass.Close => 3,
		_ => 4,
	};
}
