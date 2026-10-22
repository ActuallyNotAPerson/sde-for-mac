using Avalonia.Platform;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Rendering;
using Mapsui.Rendering.Skia;
using Mapsui.Rendering.Skia.SkiaStyles;
using Mapsui.Styles;
using SkiaSharp;

namespace Sde.App.Maps;

/// <summary>
/// A text label on the map, drawn by <see cref="MapLabelRenderer"/> in the app's own Inter font. The engine's own label
/// style asks the system for a font by name, which on Linux goes through fontconfig and on a Mac falls back to the
/// system font; this one always uses the Inter the app embeds, so labels look the same everywhere.
/// </summary>
public sealed class MapLabelStyle : BaseStyle
{
	/// <summary>The label of a feature; null or empty draws nothing.</summary>
	public Func<IFeature, string> Text { get; init; }

	/// <summary>Text size in points.</summary>
	public double Size { get; init; } = 11;

	public Color Color { get; init; } = Color.Black;

	/// <summary>The outline around the letters that keeps them readable over anything; null for none.</summary>
	public Color? Halo { get; init; } = Color.White;

	public double HaloWidth { get; init; } = 1.5;

	/// <summary>How far above the feature's label point the text sits, in points (for labels on dots).</summary>
	public double Rise { get; init; }

	/// <summary>Where the label goes for each feature, in Web Mercator metres; null uses the feature's middle.</summary>
	public Func<IFeature, MPoint> Anchor { get; init; }
}

/// <summary>
/// Draws <see cref="MapLabelStyle"/> labels with the embedded Inter font, centred on their point, with a halo. A label
/// that would overlap one already drawn in the same frame is left out, so dense layers stay readable.
/// </summary>
public sealed class MapLabelRenderer : ISkiaStyleRenderer
{
	private static readonly Lazy<SKTypeface> Inter = new Lazy<SKTypeface>(LoadInter);
	private readonly object _gate = new object();
	private readonly List<SKRect> _taken = new List<SKRect>();
	private long _iteration = -1;

	/// <summary>Registers the renderer with the map engine (once; safe to call often).</summary>
	public static void Register()
	{
		if (Interlocked.Exchange(ref _registered, 1) == 0)
		{
			MapRenderer.RegisterStyleRenderer(typeof(MapLabelStyle), new MapLabelRenderer());
		}
	}

	private static int _registered;

	/// <summary>The typeface labels use: Inter Medium from the app, or the default typeface if it can't be read.</summary>
	public static SKTypeface Typeface => Inter.Value;

	public bool Draw(SKCanvas canvas, Viewport viewport, ILayer layer, IFeature feature, IStyle style, RenderService renderService, long iteration)
	{
		if (style is not MapLabelStyle label || label.Text == null)
		{
			return false;
		}
		string text = label.Text(feature);
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		MPoint at = label.Anchor?.Invoke(feature) ?? feature.Extent?.Centroid;
		if (at == null)
		{
			return false;
		}
		(double sx, double sy) = viewport.WorldToScreenXY(at.X, at.Y);
		using SKFont font = new SKFont(Typeface, (float)label.Size);
		float width = font.MeasureText(text);
		font.GetFontMetrics(out SKFontMetrics metrics);
		float x = (float)sx - width / 2;
		// Centred on the point, or (for labels on dots) sitting above it.
		float baseline = label.Rise > 0
			? (float)(sy - label.Rise) - metrics.Descent
			: (float)sy - (metrics.Ascent + metrics.Descent) / 2;
		SKRect box = new SKRect(x - 2, baseline + metrics.Ascent - 1, x + width + 2, baseline + metrics.Descent + 1);
		lock (_gate)
		{
			if (iteration != _iteration)
			{
				_iteration = iteration;
				_taken.Clear();
			}
			foreach (SKRect r in _taken)
			{
				if (r.IntersectsWith(box))
				{
					return false;
				}
			}
			_taken.Add(box);
		}
		float opacity = (float)layer.Opacity * label.Opacity;
		if (label.Halo is Color halo && label.HaloWidth > 0)
		{
			using SKPaint stroke = new SKPaint
			{
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = (float)(label.HaloWidth * 2),
				StrokeJoin = SKStrokeJoin.Round,
				Color = ToSkia(halo, opacity),
			};
			canvas.DrawText(text, x, baseline, font, stroke);
		}
		using SKPaint fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ToSkia(label.Color, opacity) };
		canvas.DrawText(text, x, baseline, font, fill);
		return true;
	}

	private static SKColor ToSkia(Color c, float opacity) =>
		new SKColor((byte)c.R, (byte)c.G, (byte)c.B, (byte)Math.Clamp((int)Math.Round(c.A * opacity), 0, 255));

	private static SKTypeface LoadInter()
	{
		try
		{
			using Stream stream = AssetLoader.Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Medium.ttf"));
			using MemoryStream copy = new MemoryStream();
			stream.CopyTo(copy);
			copy.Position = 0;
			return SKTypeface.FromStream(copy) ?? SKTypeface.Default;
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException or ArgumentException)
		{
			return SKTypeface.Default;
		}
	}
}
