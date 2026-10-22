using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.Views;
using Sde.Core.Records;
using SkiaSharp;

namespace Sde.App.Tests;

/// <summary>
/// Map tiles made on the spot, so no UI test goes online: every tile cache made without its own downloader (the Map
/// page's) gets these. Basemap tiles are a soft paper colour with a faint grid; FEMA flood zone tiles are transparent
/// with a blue band, like a flood zone along a river.
/// </summary>
internal static class FakeTiles
{
	public static readonly SKColor Paper = new SKColor(236, 232, 220);
	public static readonly SKColor Grid = new SKColor(214, 208, 192);
	public static readonly SKColor Flood = new SKColor(70, 130, 220, 150);

	/// <summary>Every web address asked for, in order (tests clear it first).</summary>
	public static List<string> Requested { get; } = new List<string>();

	[ModuleInitializer]
	internal static void UseFakeTiles() => TileCache.DefaultDownload = Download;

	public static Task<byte[]> Download(string url, CancellationToken cancel)
	{
		lock (Requested)
		{
			Requested.Add(url);
		}
		return Task.FromResult(url.Contains("NFHL", StringComparison.Ordinal) ? FloodTile(url) : PaperTile());
	}

	private static byte[] _paper;

	public static byte[] PaperTile()
	{
		if (_paper != null)
		{
			return _paper;
		}
		using SKBitmap bitmap = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
		using SKCanvas canvas = new SKCanvas(bitmap);
		canvas.Clear(Paper);
		using SKPaint line = new SKPaint { Color = Grid, StrokeWidth = 1, IsAntialias = false };
		for (int i = 0; i <= 256; i += 64)
		{
			canvas.DrawLine(i, 0, i, 256, line);
			canvas.DrawLine(0, i, 256, i, line);
		}
		using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
		return _paper = data.ToArray();
	}

	/// <summary>A transparent tile with a blue band (512 px when the address asks for a Retina tile).</summary>
	public static byte[] FloodTile(string url)
	{
		int size = url.Contains("size=512", StringComparison.Ordinal) ? 512 : 256;
		using SKBitmap bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
		using SKCanvas canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.Transparent);
		using SKPaint band = new SKPaint { Color = Flood, IsAntialias = true };
		float s = size / 256f;
		canvas.DrawRect(0, 96 * s, size, 64 * s, band);
		using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
		return data.ToArray();
	}
}

/// <summary>Opening the Map page with structures placed, and reading pixels back.</summary>
internal static class MapTestSupport
{
	/// <summary>The three sample structures, two with coordinates (River and Mill Creek), the Riverside office without.</summary>
	public static void SetUpPlaces(UiSession ui, Basemap basemap = null)
	{
		ScreenTourTests.AddSamples(ui.Store);
		foreach (PropertyRecord p in ui.Store.GetProperties())
		{
			if (p.StreetName == "River")
			{
				p.Latitude = 39.1031m;
				p.Longitude = -84.5120m;
				ui.Store.SaveProperty(p);
			}
			else if (p.StreetName == "Mill Creek")
			{
				p.Latitude = 39.1100m;
				p.Longitude = -84.5000m;
				ui.Store.SaveProperty(p);
			}
		}
		ui.Services.RaiseDataChanged();
		ui.Main.Map.Basemap = basemap ?? Basemaps.None;
	}

	public static MapView ShowMap(UiSession ui)
	{
		ui.Main.ShowCommand.Execute(Page.Map);
		ui.Pump();
		return ui.Window.GetVisualDescendants().OfType<MapView>().Single();
	}

	/// <summary>Lets the map fetch what the view needs (tiles, rasterized layers) and draw it.</summary>
	public static void Settle(UiSession ui, MapView view, int timeoutMs = 10000)
	{
		ui.Pump();
		Task load = view.Map.LoadAsync();
		System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		while (!load.IsCompleted && clock.ElapsedMilliseconds < timeoutMs)
		{
			ui.Pump();
			Thread.Sleep(5);
		}
		for (int i = 0; i < 8; i++)
		{
			ui.Pump();
			Thread.Sleep(10);
		}
	}

	/// <summary>A point of the map control in the window's coordinates (for mouse events).</summary>
	public static Point InWindow(UiSession ui, MapView view, Point onMap) => view.Map.TranslatePoint(onMap, ui.Window) ?? onMap;

	public static WriteableBitmap Capture(UiSession ui)
	{
		ui.Pump();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		return ui.Window.CaptureRenderedFrame();
	}

	/// <summary>The colour of one pixel of a captured frame (in device pixels; the headless window renders at 1x).</summary>
	public static Avalonia.Media.Color At(WriteableBitmap frame, double x, double y)
	{
		using ILockedFramebuffer fb = frame.Lock();
		int px = (int)Math.Round(x);
		int py = (int)Math.Round(y);
		int v = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address, py * fb.RowBytes + px * 4);
		byte b0 = (byte)v, b1 = (byte)(v >> 8), b2 = (byte)(v >> 16), b3 = (byte)(v >> 24);
		return fb.Format == PixelFormat.Rgba8888
			? Avalonia.Media.Color.FromArgb(b3, b0, b1, b2)
			: Avalonia.Media.Color.FromArgb(b3, b2, b1, b0);
	}

	/// <summary>True when two colours are within a few levels on every channel.</summary>
	public static bool Near(Avalonia.Media.Color a, Avalonia.Media.Color b, int tolerance = 14) =>
		Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

	/// <summary>The colour of an app resource in the theme showing now.</summary>
	public static Avalonia.Media.Color ThemeColour(string key)
	{
		Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object value), key);
		return ((Avalonia.Media.ISolidColorBrush)value).Color;
	}
}
