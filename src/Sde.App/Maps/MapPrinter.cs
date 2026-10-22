using System.Globalization;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Rendering;
using Mapsui.Rendering.Skia;
using Mapsui.Widgets;
using SkiaSharp;
using Sde.Gis.Styles;

namespace Sde.App.Maps;

/// <summary>The map as it is on screen, for printing: where it is, how far in, and what it shows (in print colours).</summary>
public sealed class MapPrintScene
{
	/// <summary>The middle of the view, in Web Mercator metres.</summary>
	public double CenterX { get; init; }

	public double CenterY { get; init; }

	/// <summary>Metres per screen pixel.</summary>
	public double Resolution { get; init; }

	/// <summary>The size of the map on screen, in pixels: the printed map shows at least this area.</summary>
	public double ScreenWidth { get; init; }

	public double ScreenHeight { get; init; }

	public Basemap Basemap { get; init; } = Basemaps.None;

	/// <summary>Visible tile overlays (FEMA's flood zones), bottom first.</summary>
	public IReadOnlyList<MapTileLayer> Overlays { get; init; } = Array.Empty<MapTileLayer>();

	/// <summary>Visible layers of features, bottom first.</summary>
	public IReadOnlyList<MapFeatureLayer> Layers { get; init; } = Array.Empty<MapFeatureLayer>();

	/// <summary>The structure dots, with fills in the light theme's colours (paper is white).</summary>
	public IReadOnlyList<MapMarker> Markers { get; init; } = Array.Empty<MapMarker>();

	/// <summary>How structure labels look, or null for none.</summary>
	public LabelStyle MarkerLabels { get; init; }

	/// <summary>The legend: the structures, then each layer shown (top first).</summary>
	public IReadOnlyList<Sde.Reports.MapLegendGroup> Legend { get; init; } = Array.Empty<Sde.Reports.MapLegendGroup>();
}

/// <summary>The printed map's picture, and what went into it.</summary>
public sealed class MapPrintImage
{
	public byte[] Png { get; init; }

	public int PixelWidth { get; init; }

	public int PixelHeight { get; init; }

	/// <summary>Map tiles needed for the background and overlays, and how many never came (blank on the page).</summary>
	public int TilesWanted { get; init; }

	public int TilesMissing { get; init; }

	/// <summary>The scale denominator at the middle of the map (4800 for 1:4,800).</summary>
	public double Scale { get; init; }

	/// <summary>Feet of ground per inch of paper at the middle of the map.</summary>
	public double FeetPerInch { get; init; }

	/// <summary>The zoom level of the background tiles used.</summary>
	public int TileZoom { get; init; }
}

/// <summary>
/// Draws the map for a printed page at print resolution (200 dots per inch), off the screen: the background map and
/// overlays from tiles fetched at the zoom that print resolution needs (waiting for them up to a time limit; any that
/// never come are counted and left blank), the layers and structure dots drawn by the map engine at that resolution
/// (symbols and labels the same physical size as on a 96-dpi screen), then a scale bar and a north arrow.
/// </summary>
public static class MapPrinter
{
	/// <summary>Dots per inch of the printed map.</summary>
	public const int Dpi = 200;

	/// <summary>How long printing waits for map tiles before leaving the missing ones blank.</summary>
	public static readonly TimeSpan TileTimeout = TimeSpan.FromSeconds(30);

	private const double DipsPerInch = 96;

	/// <summary>The colours the engine draws with on paper.</summary>
	private static MapTheme PaperTheme => new MapTheme
	{
		Background = Mapsui.Styles.Color.White,
		DotOutline = Mapsui.Styles.Color.White,
		Accent = new Mapsui.Styles.Color(30, 90, 168),
		Text = new Mapsui.Styles.Color(22, 25, 29),
		Halo = Mapsui.Styles.Color.White,
	};

	/// <summary>
	/// The map picture for a frame of <paramref name="widthInches"/> by <paramref name="heightInches"/>, showing at least
	/// the area on screen around the same middle.
	/// </summary>
	public static async Task<MapPrintImage> RenderAsync(MapPrintScene scene, TileCache tiles, double widthInches, double heightInches, TimeSpan tileTimeout, CancellationToken cancel)
	{
		ArgumentNullException.ThrowIfNull(scene);
		int pxW = (int)Math.Round(widthInches * Dpi);
		int pxH = (int)Math.Round(heightInches * Dpi);
		float density = (float)(Dpi / DipsPerInch);
		double dipW = widthInches * DipsPerInch;
		double dipH = heightInches * DipsPerInch;
		// Metres per print DIP: the screen's area fits the frame (more shows along the other side).
		double resolution = Math.Max(scene.ScreenWidth * scene.Resolution / dipW, scene.ScreenHeight * scene.Resolution / dipH);
		if (!(resolution > 0))
		{
			resolution = scene.Resolution > 0 ? scene.Resolution : WebMercator.Resolution(15);
		}
		double metresPerPixel = resolution / density;
		double minX = scene.CenterX - dipW / 2 * resolution;
		double maxY = scene.CenterY + dipH / 2 * resolution;
		double maxX = minX + pxW * metresPerPixel;
		double minY = maxY - pxH * metresPerPixel;
		double latitude = WebMercator.FromMetres(scene.CenterX, scene.CenterY).Latitude;
		double groundMetresPerPixel = metresPerPixel * Math.Cos(latitude * Math.PI / 180);

		using SKBitmap bitmap = new SKBitmap(pxW, pxH, SKColorType.Rgba8888, SKAlphaType.Premul);
		using SKCanvas canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.White);

		// Background and overlays, at print resolution, waiting for the tiles up to the time limit.
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		limit.CancelAfter(tileTimeout);
		int wanted = 0;
		int missing = 0;
		int zoom = 0;
		if (tiles != null && scene.Basemap?.HasTiles == true)
		{
			(int w, int m, int z) = await DrawTilesAsync(canvas, scene.Basemap, tiles, minX, maxX, minY, maxY, metresPerPixel, 1f, limit.Token);
			wanted += w;
			missing += m;
			zoom = z;
		}
		foreach (MapTileLayer overlay in scene.Overlays.Where(o => o.Visible && (o.Display?.IsVisibleAt(Scale(resolution, latitude)) ?? true)))
		{
			if (tiles != null)
			{
				(int w, int m, int _) = await DrawTilesAsync(canvas, overlay.Source, tiles, minX, maxX, minY, maxY, metresPerPixel, (float)(overlay.Display?.ClampedOpacity ?? 1), limit.Token);
				wanted += w;
				missing += m;
			}
		}
		cancel.ThrowIfCancellationRequested();

		// Layers and structures, drawn by the engine at print resolution.
		DrawVectors(canvas, scene, resolution, dipW, dipH, density, Scale(resolution, latitude));

		DrawScaleBar(canvas, pxW, pxH, groundMetresPerPixel);
		DrawNorthArrow(canvas, pxW);

		using SKImage image = SKImage.FromBitmap(bitmap);
		using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
		return new MapPrintImage
		{
			Png = data.ToArray(),
			PixelWidth = pxW,
			PixelHeight = pxH,
			TilesWanted = wanted,
			TilesMissing = missing,
			Scale = groundMetresPerPixel * Dpi / 0.0254,
			FeetPerInch = groundMetresPerPixel * Dpi / 0.3048,
			TileZoom = zoom,
		};
	}

	/// <summary>The scale denominator for a resolution in metres per DIP (96 per inch) at a latitude.</summary>
	private static double Scale(double resolution, double latitude) => WebMercator.Scale(resolution, latitude);

	/// <summary>
	/// Fetches the tiles of one source that cover the frame, at the zoom closest to print resolution (no further in
	/// than the source goes), and draws the ones that came. Returns how many were wanted, how many are missing, and the zoom.
	/// </summary>
	private static async Task<(int Wanted, int Missing, int Zoom)> DrawTilesAsync(SKCanvas canvas, Basemap source, TileCache cache, double minX, double maxX, double minY, double maxY,
		double metresPerPixel, float opacity, CancellationToken cancel)
	{
		int scale = source.HasRetinaTiles ? 2 : 1;
		int z = (int)Math.Clamp(Math.Round(Math.Log2(WebMercator.Zoom0Resolution / scale / metresPerPixel)), 0, source.MaxZoom);
		double tileMetres = 2 * WebMercator.HalfWorld / (1L << z);
		int last = (1 << z) - 1;
		int x0 = Math.Clamp((int)Math.Floor((minX + WebMercator.HalfWorld) / tileMetres), 0, last);
		int x1 = Math.Clamp((int)Math.Floor((maxX + WebMercator.HalfWorld) / tileMetres), 0, last);
		int y0 = Math.Clamp((int)Math.Floor((WebMercator.HalfWorld - maxY) / tileMetres), 0, last);
		int y1 = Math.Clamp((int)Math.Floor((WebMercator.HalfWorld - minY) / tileMetres), 0, last);
		List<(int X, int Y, Task<TileFetch> Fetch)> fetches = new List<(int, int, Task<TileFetch>)>();
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				fetches.Add((x, y, cache.FetchAsync(source, z, x, y, scale, cancel)));
			}
		}
		try
		{
			await Task.WhenAll(fetches.Select(f => f.Fetch)).WaitAsync(cancel).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// The time limit: whatever came is drawn, the rest is counted as missing.
		}
		int missing = 0;
		using SKPaint paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * Math.Clamp(opacity, 0, 1))) };
		SKSamplingOptions sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
		foreach ((int x, int y, Task<TileFetch> fetch) in fetches)
		{
			byte[] bytes = fetch.IsCompletedSuccessfully ? fetch.Result.Bytes : null;
			using SKImage tile = bytes == null ? null : SKImage.FromEncodedData(bytes);
			if (tile == null)
			{
				missing++;
				continue;
			}
			double left = (-WebMercator.HalfWorld + x * tileMetres - minX) / metresPerPixel;
			double top = (maxY - (WebMercator.HalfWorld - y * tileMetres)) / metresPerPixel;
			double size = tileMetres / metresPerPixel;
			// A hair of overlap hides seams between tiles drawn at a fractional scale.
			SKRect dest = new SKRect((float)left, (float)top, (float)(left + size + 0.5), (float)(top + size + 0.5));
			canvas.DrawImage(tile, dest, sampling, paint);
		}
		return (fetches.Count, missing, z);
	}

	/// <summary>The layers and structures, drawn by the map engine onto the picture at print resolution.</summary>
	private static void DrawVectors(SKCanvas canvas, MapPrintScene scene, double resolution, double dipW, double dipH, float density, double scale)
	{
		MapTheme theme = PaperTheme;
		List<BuiltFeatureLayer> built = new List<BuiltFeatureLayer>();
		List<ILayer> layers = new List<ILayer>();
		List<ILayer> labels = new List<ILayer>();
		try
		{
			foreach (MapFeatureLayer spec in scene.Layers.Where(l => l.Visible && (l.Display?.IsVisibleAt(scale) ?? true)))
			{
				BuiltFeatureLayer b = BuiltFeatureLayer.Build(spec, theme, density);
				built.Add(b);
				// The features themselves, never raster tiles of them: print is drawn once, sharp.
				b.Source.Opacity = spec.Display?.ClampedOpacity ?? 1;
				b.Edits.Opacity = b.Source.Opacity;
				layers.Add(b.Source);
				layers.Add(b.Edits);
				if (b.Labels != null && spec.Labels.IsVisibleAt(scale))
				{
					labels.Add(b.Labels);
				}
			}
			layers.AddRange(labels);
			MemoryLayer dots = new MemoryLayer("Structures") { Style = null, Features = StructureDots.Dots(scene.Markers, theme) };
			layers.Add(dots);
			if (scene.MarkerLabels != null && scene.MarkerLabels.IsVisibleAt(scale))
			{
				layers.Add(StructureDots.LabelLayer(StructureDots.LabelPoints(scene.Markers), scene.MarkerLabels, theme));
			}
			foreach (ILayer layer in layers)
			{
				layer.Attribution.Enabled = false;
			}
			MapLabelRenderer.Register();
			using RenderService service = new RenderService();
			MapRenderer renderer = new MapRenderer();
			Viewport viewport = new Viewport(scene.CenterX, scene.CenterY, resolution, 0, dipW, dipH);
			canvas.Save();
			canvas.Scale(density, density);
			renderer.Render(canvas, viewport, layers, Array.Empty<IWidget>(), service, null);
			canvas.Restore();
		}
		finally
		{
			foreach (BuiltFeatureLayer b in built)
			{
				b.Dispose();
			}
		}
	}

	/// <summary>The bar's length: a round number of feet (under half a mile) or miles, about an inch and a half on paper.</summary>
	public static (double Metres, string Label) ScaleBarLength(double groundMetresPerPixel)
	{
		double target = 1.5 * Dpi * groundMetresPerPixel;
		double feet = target / 0.3048;
		double[] feetSteps = { 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000 };
		double[] mileSteps = { 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500 };
		if (feet < 2640)
		{
			double f = feetSteps.LastOrDefault(s => s <= feet);
			f = f == 0 ? feetSteps[0] : f;
			return (f * 0.3048, f.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " ft");
		}
		double miles = feet / 5280;
		double mi = mileSteps.LastOrDefault(s => s <= miles);
		mi = mi == 0 ? mileSteps[0] : mi;
		return (mi * 1609.344, mi.ToString("0.#", CultureInfo.InvariantCulture) + (mi == 1 ? " mile" : " miles"));
	}

	private static void DrawScaleBar(SKCanvas canvas, int pxW, int pxH, double groundMetresPerPixel)
	{
		(double metres, string label) = ScaleBarLength(groundMetresPerPixel);
		float bar = (float)(metres / groundMetresPerPixel);
		float unit = Dpi / 72f;
		float pad = 7 * unit;
		float barHeight = 4 * unit;
		using SKFont font = new SKFont(MapLabelRenderer.Typeface, 7.5f * unit);
		float textWidth = font.MeasureText(label);
		float boxW = bar + textWidth + 3 * pad;
		float boxH = barHeight + 2 * pad;
		float left = 10 * unit;
		float top = pxH - 10 * unit - boxH;
		using SKPaint box = new SKPaint { Color = SKColors.White.WithAlpha(225), IsAntialias = true };
		canvas.DrawRoundRect(new SKRect(left, top, left + boxW, top + boxH), 3 * unit, 3 * unit, box);
		using SKPaint ink = new SKPaint { Color = new SKColor(0x1B, 0x22, 0x2C), IsAntialias = true };
		using SKPaint white = new SKPaint { Color = SKColors.White, IsAntialias = true };
		using SKPaint outline = new SKPaint { Color = ink.Color, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f * unit, IsAntialias = true };
		float x = left + pad;
		float y = top + pad;
		// Four segments, dark and light in turn.
		for (int i = 0; i < 4; i++)
		{
			SKRect segment = new SKRect(x + bar * i / 4, y, x + bar * (i + 1) / 4, y + barHeight);
			canvas.DrawRect(segment, i % 2 == 0 ? ink : white);
		}
		canvas.DrawRect(new SKRect(x, y, x + bar, y + barHeight), outline);
		font.GetFontMetrics(out SKFontMetrics m);
		canvas.DrawText(label, x + bar + pad, y + barHeight / 2 - (m.Ascent + m.Descent) / 2, font, ink);
	}

	private static void DrawNorthArrow(SKCanvas canvas, int pxW)
	{
		float unit = Dpi / 72f;
		float size = 26 * unit;
		float right = pxW - 10 * unit;
		float top = 10 * unit;
		SKRect frame = new SKRect(right - size, top, right, top + size * 1.25f);
		using SKPaint box = new SKPaint { Color = SKColors.White.WithAlpha(225), IsAntialias = true };
		canvas.DrawRoundRect(frame, 3 * unit, 3 * unit, box);
		using SKPaint ink = new SKPaint { Color = new SKColor(0x1B, 0x22, 0x2C), IsAntialias = true };
		float cx = frame.MidX;
		using SKFont font = new SKFont(MapLabelRenderer.Typeface, 9 * unit);
		font.GetFontMetrics(out SKFontMetrics m);
		float nWidth = font.MeasureText("N");
		canvas.DrawText("N", cx - nWidth / 2, top + 3 * unit - m.Ascent, font, ink);
		float arrowTop = top + 5 * unit - m.Ascent;
		float arrowBottom = frame.Bottom - 4 * unit;
		using SKPath arrow = new SKPath();
		arrow.MoveTo(cx, arrowTop);
		arrow.LineTo(cx + 6 * unit, arrowBottom);
		arrow.LineTo(cx, arrowBottom - 4 * unit);
		arrow.LineTo(cx - 6 * unit, arrowBottom);
		arrow.Close();
		canvas.DrawPath(arrow, ink);
	}
}
