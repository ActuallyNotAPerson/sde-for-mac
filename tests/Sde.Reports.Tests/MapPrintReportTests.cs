using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Sde.Reports.Tests;

/// <summary>The printed map page: Letter portrait or landscape, with the map, legend, scale, sources and date.</summary>
public class MapPrintReportTests
{
	private static byte[] MapPicture(bool landscape)
	{
		(double w, double h) = MapPrintReport.MapFrame(landscape);
		using SKBitmap bitmap = new SKBitmap((int)(w * 50), (int)(h * 50));
		using SKCanvas canvas = new SKCanvas(bitmap);
		canvas.Clear(new SKColor(236, 232, 220));
		using SKPaint dot = new SKPaint { Color = new SKColor(0xC6, 0x28, 0x28), IsAntialias = true };
		canvas.DrawCircle(bitmap.Width / 2f, bitmap.Height / 2f, 8, dot);
		using SKData data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
		return data.ToArray();
	}

	private static MapPrintPage Page(bool landscape, string note = null) => new MapPrintPage
	{
		Title = "Mill Creek flood, June 2026",
		Subtitle = "Exampleville",
		Landscape = landscape,
		MapPng = MapPicture(landscape),
		Legend = new[]
		{
			new MapLegendGroup("Structures, by result", new[]
			{
				new MapLegendItem("Substantially damaged", MapLegendShape.Dot, "#C62828", "#FFFFFF"),
				new MapLegendItem("Not substantially damaged", MapLegendShape.Dot, "#2E8B4B", "#FFFFFF"),
			}),
			new MapLegendGroup("parcels", new[] { new MapLegendItem("AE", MapLegendShape.Area, "#E07A1F46", "#E07A1F") }),
			new MapLegendGroup("streams", new[] { new MapLegendItem("streams", MapLegendShape.Line, "#2F6FB3", "#2F6FB3") }),
		},
		ScaleText = "1:4,800 (1 inch = 400 feet)",
		Attribution = "Map data: U.S. Geological Survey, The National Map · Flood hazard zones: FEMA National Flood Hazard Layer",
		Note = note,
		PrintedOn = new DateTime(2026, 10, 22, 14, 30, 0),
	};

	[Theory]
	[InlineData(false, 612, 792)]
	[InlineData(true, 792, 612)]
	public void APage_IsLetterSize_InEitherOrientation_WithEverythingOnIt(bool landscape, double width, double height)
	{
		using TempDatabase db = new TempDatabase();
		string pdf = Path.Combine(db.Folder, landscape ? "map-landscape.pdf" : "map-portrait.pdf");
		MapPrintReport.RenderToFile(Page(landscape), pdf);

		using PdfDocument doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
		Assert.Equal(1, doc.PageCount);
		Assert.Equal(width, doc.Pages[0].Width.Point, 0.5);
		Assert.Equal(height, doc.Pages[0].Height.Point, 0.5);

		// The narrow legend column wraps lines; read the text as one run.
		string text = System.Text.RegularExpressions.Regex.Replace(Poppler.Text(pdf), @"\s+", " ");
		Assert.Contains("Mill Creek flood, June 2026", text);
		Assert.Contains("Exampleville | Printed October 22, 2026", text);
		Assert.Contains("Legend", text);
		Assert.Contains("Substantially damaged", text);
		Assert.Contains("Not substantially damaged", text);
		Assert.Contains("parcels", text);
		Assert.Contains("1:4,800 (1 inch = 400 feet)", text);
		Assert.Contains("North is up", text);
		Assert.Contains("Flood hazard zones: FEMA National Flood Hazard Layer", text);
		Assert.Contains("Thursday, October 22, 2026", text);
		Assert.DoesNotContain("—", text);

		// The map picture and the four legend swatches are on the page, the picture at its own size.
		List<(int Page, int Width, int Height)> images = Poppler.Images(pdf);
		Assert.Equal(1 + 4, images.Count);
		(double frameWidth, double _) = MapPrintReport.MapFrame(landscape);
		Assert.Contains(images, i => i.Width == (int)(frameWidth * 50));
	}

	[Fact]
	public void TilesThatNeverCame_AreNotedUnderTheMap()
	{
		using TempDatabase db = new TempDatabase();
		string pdf = Path.Combine(db.Folder, "map.pdf");
		MapPrintReport.RenderToFile(Page(false, "Some of the background map couldn't be downloaded in time (3 of 12 pieces), so parts of it are blank."), pdf);
		Assert.Contains("couldn't be downloaded in time (3 of 12 pieces)", Poppler.Text(pdf));
	}

	[Fact]
	public void LegendSwatches_AreDrawnInTheirColours()
	{
		using SKBitmap dot = SKBitmap.Decode(MapPrintReport.Swatch(new MapLegendItem("x", MapLegendShape.Dot, "#C62828", "#FFFFFF")));
		Assert.Equal(new SKColor(0xC6, 0x28, 0x28), dot.GetPixel(dot.Width / 2, dot.Height / 2));
		Assert.Equal(0, dot.GetPixel(1, 1).Alpha);
		using SKBitmap area = SKBitmap.Decode(MapPrintReport.Swatch(new MapLegendItem("x", MapLegendShape.Area, "#00FF0080", "#0000FF")));
		SKColor inside = area.GetPixel(area.Width / 2, area.Height / 2);
		Assert.InRange(inside.Alpha, 120, 136);
		Assert.Equal(new SKColor(0, 0, 255), area.GetPixel(4, area.Height / 2));
	}
}
