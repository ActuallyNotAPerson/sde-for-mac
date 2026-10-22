using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Sde.Reports;
using SkiaSharp;

namespace Sde.App.Tests;

/// <summary>Printing the map: the sheet, the PDF, tiles at print resolution, the legend, and tiles that never come.</summary>
public class MapPrintTests
{
	private static async Task<(UiSession Ui, MapView View)> MapWithZones(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		MapTestSupport.SetUpPlaces(ui, Basemaps.UsgsTopo);
		MapView view = MapTestSupport.ShowMap(ui);
		await MapAnalysisTests.AddZones(ui);
		ui.Main.Map.AddFloodZonesCommand.Execute(null);
		view.Map.SetView(39.1066, -84.5060, 15.5);
		MapTestSupport.Settle(ui, view);
		return (ui, view);
	}

	[AvaloniaFact]
	public async Task Print_AsksTheTitleAndPage_SavesALetterPdf_AndOpensIt()
	{
		(UiSession ui, MapView view) = await MapWithZones();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Task printing = map.PrintMapCommand.ExecuteAsync(null);
			ui.Pump();
			PrintMapSheetViewModel sheet = Assert.IsType<PrintMapSheetViewModel>(ui.Main.Sheets.Current);
			Assert.Equal("Structures", sheet.MapTitle);
			Assert.Equal(1, sheet.OrientationIndex);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			sheet.MapTitle = "Mill Creek: flood damage";
			sheet.OrientationIndex = 0;
			string pdf = Path.Combine(ui.Folder, "Mill Creek- flood damage.pdf");
			ui.Dialogs.Answers.Enqueue(pdf);
			lock (FakeTiles.Requested)
			{
				FakeTiles.Requested.Clear();
			}
			sheet.PrintCommand.Execute(null);
			Stopwatch clock = Stopwatch.StartNew();
			while (!printing.IsCompleted && clock.ElapsedMilliseconds < 30000)
			{
				ui.Pump();
				Thread.Sleep(10);
			}
			await printing;

			Assert.True(File.Exists(pdf), map.Message + " | " + string.Join("; ", ui.Dialogs.Notifications));
			Assert.Contains(pdf, ui.Dialogs.Opened);
			Assert.Equal("Saved the map as Mill Creek- flood damage.pdf.", map.Message);
			using (PdfDocument doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
			{
				Assert.Equal(612, doc.Pages[0].Width.Point, 0.5);
				Assert.Equal(792, doc.Pages[0].Height.Point, 0.5);
			}

			// The flood zone overlay was fetched for print: FEMA's map service draws each piece for the page.
			List<string> asked;
			lock (FakeTiles.Requested)
			{
				asked = FakeTiles.Requested.ToList();
			}
			Assert.True(asked.Count > 0);
			Assert.All(asked, u => Assert.Contains("NFHL", u, StringComparison.Ordinal));
		}
	}

	[AvaloniaFact]
	public async Task ThePicture_HasTheDotsInPrintColours_TheLegendAndTheScale()
	{
		(UiSession ui, MapView view) = await MapWithZones();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			MapPrintScene scene = map.PrintScene();
			Assert.Equal(new[] { "Structures, by result", "FEMA flood zones", "flood zones" }, scene.Legend.Select(g => g.Title));
			MapLegendGroup structures = scene.Legend[0];
			MapLegendItem substantial = Assert.Single(structures.Items);
			Assert.Equal("Substantially damaged", substantial.Label);
			Avalonia.Media.Color red = MapTestSupport.ThemeColour("StatusSubstantialFg");
			Assert.Equal($"#{red.R:X2}{red.G:X2}{red.B:X2}FF", substantial.Fill);
			Assert.Single(scene.Legend[2].Items);

			(double w, double h) = MapPrintReport.MapFrame(landscape: true);
			MapPrintImage image = await MapPrinter.RenderAsync(scene, map.Tiles, w, h, TimeSpan.FromSeconds(20), CancellationToken.None);
			Assert.Equal((int)Math.Round(w * MapPrinter.Dpi), image.PixelWidth);
			Assert.Equal((int)Math.Round(h * MapPrinter.Dpi), image.PixelHeight);
			Assert.Equal(0, image.TilesMissing);
			Assert.True(image.TilesWanted > 0);
			Assert.Equal(16, image.TileZoom);
			Assert.InRange(image.Scale, 3000, 30000);
			Assert.Matches(@"^1:[\d,]+ \(1 inch = [\d,]+ feet\)$", MapViewModel.ScaleText(image));

			// River's dot, in the light theme's colour, where the map puts it.
			using SKBitmap bitmap = SKBitmap.Decode(image.Png);
			MapPointViewModel river = map.Visible.Single(p => p.Address.Contains("River ", StringComparison.Ordinal));
			(double cx, double cy, double resolution) = view.Map.ViewInMetres;
			(double rx, double ry) = WebMercator.ToMetres(river.Longitude, river.Latitude);
			double perPixel = Math.Max(scene.ScreenWidth * resolution / (w * 96), scene.ScreenHeight * resolution / (h * 96)) / (MapPrinter.Dpi / 96.0);
			int px = (int)Math.Round(image.PixelWidth / 2.0 + (rx - cx) / perPixel);
			int py = (int)Math.Round(image.PixelHeight / 2.0 - (ry - cy) / perPixel);
			SKColor dot = bitmap.GetPixel(px, py);
			Assert.True(Math.Abs(dot.Red - red.R) < 12 && Math.Abs(dot.Green - red.G) < 12 && Math.Abs(dot.Blue - red.B) < 12, dot.ToString());
			// A corner is the background map's paper, not white.
			SKColor corner = bitmap.GetPixel(image.PixelWidth / 2, 4);
			Assert.True(Math.Abs(corner.Red - FakeTiles.Paper.Red) < 14 && Math.Abs(corner.Blue - FakeTiles.Paper.Blue) < 14, corner.ToString());
		}
	}

	[AvaloniaFact]
	public async Task TilesThatNeverCome_AreLeftBlank_AndNotedOnThePage()
	{
		(UiSession ui, MapView view) = await MapWithZones();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			// A tile server that never answers the flood zone tiles.
			TileCache slow = new TileCache(Path.Combine(ui.Folder, "slow-tiles"), async (url, cancel) =>
			{
				if (url.Contains("NFHL", StringComparison.Ordinal))
				{
					await Task.Delay(Timeout.Infinite, cancel);
				}
				return FakeTiles.PaperTile();
			});
			(double w, double h) = MapPrintReport.MapFrame(landscape: false);
			Stopwatch clock = Stopwatch.StartNew();
			MapPrintImage image = await MapPrinter.RenderAsync(map.PrintScene(), slow, w, h, TimeSpan.FromMilliseconds(400), CancellationToken.None);
			clock.Stop();
			Assert.True(image.TilesMissing > 0);
			Assert.True(image.TilesMissing < image.TilesWanted);
			Assert.True(clock.ElapsedMilliseconds < 10000, clock.ElapsedMilliseconds + " ms");
			Assert.StartsWith($"Some of the background map couldn't be downloaded in time ({image.TilesMissing} of {image.TilesWanted} pieces)", MapViewModel.TileNote(image));
		}
	}

	[AvaloniaFact]
	public async Task ScreenshotOfAPrintedPage()
	{
		(UiSession ui, MapView view) = await MapWithZones();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.ApplyStyle(map.Layers.Single(l => l.Name == "flood zones"), new LayerStyleSheetViewModel.Result(
				Sde.Gis.Styles.LayerStyle.Categorized(map.Layers.Single(l => l.Name == "flood zones").Data, "FLD_ZONE"), null, null, null));
			map.ShowAddressLabels = true;
			view.Map.SetView(39.1066, -84.5060, 16.3);
			MapTestSupport.Settle(ui, view);
			string pdf = Path.Combine(ui.Folder, "map.pdf");
			Task<MapPrintImage> printing = map.PrintToAsync(new PrintMapSheetViewModel.Result("Mill Creek: flood damage", true), pdf);
			Stopwatch clock = Stopwatch.StartNew();
			while (!printing.IsCompleted && clock.ElapsedMilliseconds < 30000)
			{
				ui.Pump();
				Thread.Sleep(10);
			}
			MapPrintImage image = await printing;
			Assert.True(image != null, string.Join("\n", Directory.Exists(Path.Combine(ui.Folder, "Logs")) ? Directory.GetFiles(Path.Combine(ui.Folder, "Logs")).Select(File.ReadAllText) : new[] { "no logs" }));
			bool forDocs = OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SDE_WRITE_SCREENSHOTS") == "1";
			string dir = forDocs ? Path.Combine(TestPaths.Root, "docs", "screenshots") : Path.Combine(Path.GetTempPath(), "sde-screenshots");
			Directory.CreateDirectory(dir);
			string target = Path.Combine(dir, "56j-map-print.png");
			string pdftoppm = new[] { "/opt/homebrew/bin/pdftoppm", "/usr/local/bin/pdftoppm", "/usr/bin/pdftoppm" }.FirstOrDefault(File.Exists);
			if (pdftoppm != null)
			{
				// The page itself, at 110 dpi.
				ProcessStartInfo psi = new ProcessStartInfo(pdftoppm) { UseShellExecute = false };
				foreach (string a in new[] { "-png", "-r", "110", "-singlefile", pdf, Path.Combine(ui.Folder, "page") })
				{
					psi.ArgumentList.Add(a);
				}
				using Process p = Process.Start(psi)!;
				p.WaitForExit();
				Assert.Equal(0, p.ExitCode);
				File.Copy(Path.Combine(ui.Folder, "page.png"), target, overwrite: true);
			}
			else
			{
				// Without poppler, the map picture.
				File.WriteAllBytes(target, image.Png);
			}
		}
	}
}
