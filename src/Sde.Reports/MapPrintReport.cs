using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Sde.Reports.Internal;
using SkiaSharp;

namespace Sde.Reports;

/// <summary>The shape of a legend swatch.</summary>
public enum MapLegendShape
{
	/// <summary>A round dot (structures and points).</summary>
	Dot,

	/// <summary>A short line.</summary>
	Line,

	/// <summary>A filled square with an outline.</summary>
	Area,
}

/// <summary>One line of the printed legend: a swatch ("#RRGGBB" or "#RRGGBBAA" colours) and what it stands for.</summary>
public sealed record MapLegendItem(string Label, MapLegendShape Shape, string Fill, string Stroke);

/// <summary>A group of legend lines under a heading (one per layer).</summary>
public sealed record MapLegendGroup(string Title, IReadOnlyList<MapLegendItem> Items);

/// <summary>Everything on a printed map page.</summary>
public sealed class MapPrintPage
{
	public string Title { get; init; }

	/// <summary>A line under the title (the event, the community), or null.</summary>
	public string Subtitle { get; init; }

	public bool Landscape { get; init; }

	/// <summary>The map picture, drawn for <see cref="MapPrintReport.MapFrame"/> at print resolution.</summary>
	public byte[] MapPng { get; init; }

	public IReadOnlyList<MapLegendGroup> Legend { get; init; } = Array.Empty<MapLegendGroup>();

	/// <summary>"1:4,800 (1 inch = 400 feet)".</summary>
	public string ScaleText { get; init; }

	/// <summary>The credits every source shown asks for, in one line.</summary>
	public string Attribution { get; init; }

	/// <summary>A warning to print under the map (tiles that never came), or null.</summary>
	public string Note { get; init; }

	public DateTime PrintedOn { get; init; } = DateTime.Now;
}

/// <summary>
/// A Letter-size page with a map on it, portrait or landscape: the title, the map (with its scale bar and north arrow
/// drawn in), a legend, the scale, the credits for every source shown, and the date. Same fonts, header and footer as
/// the other reports.
/// </summary>
public static class MapPrintReport
{
	/// <summary>The size of the map on the page, in inches: the picture should be drawn for this.</summary>
	public static (double Width, double Height) MapFrame(bool landscape) => landscape ? (7.25, 5.75) : (Kit.PortraitWidth, 6.1);

	/// <summary>Writes the page to a PDF file (overwriting it). The file is only replaced once rendering succeeded.</summary>
	public static void RenderToFile(MapPrintPage page, string path)
	{
		ArgumentNullException.ThrowIfNull(page);
		ArgumentException.ThrowIfNullOrEmpty(path);
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		PdfDocumentRenderer renderer = new PdfDocumentRenderer { Document = Build(page) };
		renderer.RenderDocument();
		using MemoryStream buffer = new MemoryStream();
		renderer.PdfDocument.Save(buffer, false);
		File.WriteAllBytes(path, buffer.ToArray());
	}

	private static Document Build(MapPrintPage page)
	{
		string title = string.IsNullOrWhiteSpace(page.Title) ? "Map" : page.Title.Trim();
		Document doc = Theme.NewDocument(title, "Map");
		Section sec = Kit.NewSection(doc, page.Landscape, "Map", page.PrintedOn, null, restartPageNumbers: false);

		Paragraph heading = sec.AddParagraph(title);
		heading.Format.Font.Size = 16;
		heading.Format.Font.Bold = true;
		heading.Format.Font.Color = Theme.Accent;
		Paragraph sub = sec.AddParagraph(string.Join("   |   ", new[] { page.Subtitle?.Trim(), "Printed " + page.PrintedOn.ToString("MMMM d, yyyy", Fmt.Us) }
			.Where(s => !string.IsNullOrEmpty(s))));
		sub.Style = Theme.NoteStyle;
		sub.Format.Font.Size = 8;
		sub.Format.SpaceBefore = 2;
		sub.Format.SpaceAfter = 6;

		(double mapWidth, double _) = MapFrame(page.Landscape);
		if (page.Landscape)
		{
			// The map on the left, the legend and the fine print on the right.
			double side = Kit.LandscapeWidth - mapWidth - Kit.Gutter;
			Table t = Kit.NewTable(sec, new[] { mapWidth, Kit.Gutter, side });
			Row row = t.AddRow();
			MapPicture(row.Cells[0].AddParagraph(), page, mapWidth);
			if (page.Note != null)
			{
				Warning(row.Cells[0].AddParagraph(page.Note));
			}
			Legend(row.Cells[2], page);
			FinePrint(row.Cells[2], page);
		}
		else
		{
			MapPicture(sec.AddParagraph(), page, mapWidth);
			if (page.Note != null)
			{
				Warning(sec.AddParagraph(page.Note));
			}
			Kit.Gap(sec, 8);
			// The legend on the left, the fine print on the right.
			double right = 2.6;
			Table t = Kit.NewTable(sec, new[] { Kit.PortraitWidth - right - Kit.Gutter, Kit.Gutter, right });
			Row row = t.AddRow();
			Legend(row.Cells[0], page);
			FinePrint(row.Cells[2], page);
		}
		return doc;
	}

	private static void MapPicture(Paragraph p, MapPrintPage page, double width)
	{
		if (page.MapPng == null)
		{
			return;
		}
		MigraDoc.DocumentObjectModel.Shapes.Image image = p.AddImage("base64:" + Convert.ToBase64String(page.MapPng));
		image.LockAspectRatio = true;
		image.Width = Unit.FromInch(width);
		image.LineFormat.Color = Theme.Rule;
		image.LineFormat.Width = 0.75;
	}

	private static void Warning(Paragraph p)
	{
		p.Style = Theme.NoteStyle;
		p.Format.Font.Color = Theme.OtherInk;
		p.Format.SpaceBefore = 3;
	}

	private static void Legend(Cell cell, MapPrintPage page)
	{
		Paragraph title = cell.AddParagraph("Legend");
		title.Style = Theme.TitleStyle;
		title.Format.SpaceAfter = 3;
		foreach (MapLegendGroup group in page.Legend.Where(g => g.Items.Count > 0))
		{
			Paragraph heading = cell.AddParagraph(group.Title ?? "");
			heading.Format.Font.Bold = true;
			heading.Format.SpaceBefore = 4;
			heading.Format.SpaceAfter = 1;
			foreach (MapLegendItem item in group.Items)
			{
				Paragraph line = cell.AddParagraph();
				line.Format.SpaceBefore = 1;
				MigraDoc.DocumentObjectModel.Shapes.Image swatch = line.AddImage("base64:" + Convert.ToBase64String(Swatch(item)));
				swatch.Width = Unit.FromPoint(14);
				swatch.Height = Unit.FromPoint(9.5);
				line.AddText("  " + (item.Label ?? ""));
			}
		}
	}

	private static void FinePrint(Cell cell, MapPrintPage page)
	{
		if (!string.IsNullOrWhiteSpace(page.ScaleText))
		{
			Paragraph title = cell.AddParagraph("Scale");
			title.Style = Theme.TitleStyle;
			title.Format.SpaceBefore = page.Landscape ? 10 : 0;
			cell.AddParagraph(page.ScaleText).Format.SpaceBefore = 2;
			Paragraph about = cell.AddParagraph("At the middle of the map. North is up.");
			about.Style = Theme.NoteStyle;
		}
		if (!string.IsNullOrWhiteSpace(page.Attribution))
		{
			Paragraph title = cell.AddParagraph("Sources");
			title.Style = Theme.TitleStyle;
			title.Format.SpaceBefore = 8;
			Paragraph credits = cell.AddParagraph(page.Attribution);
			credits.Style = Theme.NoteStyle;
			credits.Format.SpaceBefore = 2;
		}
	}

	/// <summary>A small picture of a legend swatch (drawn at 4x for print).</summary>
	internal static byte[] Swatch(MapLegendItem item)
	{
		const int w = 56;
		const int h = 38;
		using SKBitmap bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
		using SKCanvas canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.Transparent);
		SKColor fill = Parse(item.Fill, new SKColor(0x80, 0x80, 0x80));
		SKColor stroke = Parse(item.Stroke, new SKColor(0x40, 0x40, 0x40));
		using SKPaint fillPaint = new SKPaint { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
		using SKPaint strokePaint = new SKPaint { Color = stroke, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
		switch (item.Shape)
		{
			case MapLegendShape.Dot:
				canvas.DrawCircle(w / 2f, h / 2f, 15, fillPaint);
				using (SKPaint ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 })
				{
					canvas.DrawCircle(w / 2f, h / 2f, 15, ring);
				}
				break;
			case MapLegendShape.Line:
				strokePaint.StrokeWidth = 7;
				strokePaint.StrokeCap = SKStrokeCap.Round;
				canvas.DrawLine(6, h / 2f, w - 6, h / 2f, strokePaint);
				break;
			default:
				SKRect r = new SKRect(4, 4, w - 4, h - 4);
				canvas.DrawRect(r, fillPaint);
				canvas.DrawRect(r, strokePaint);
				break;
		}
		using SKImage image = SKImage.FromBitmap(bitmap);
		using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
		return data.ToArray();
	}

	/// <summary>"#RRGGBB" or "#RRGGBBAA" as a Skia colour.</summary>
	private static SKColor Parse(string hex, SKColor fallback)
	{
		string h = (hex ?? "").Trim().TrimStart('#');
		if (h.Length is not (6 or 8) || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint v))
		{
			return fallback;
		}
		return h.Length == 6
			? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
			: new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
	}
}
