using Sde.Core.Photos;
using SkiaSharp;

namespace Sde.Reports;

/// <summary>
/// Draws photo markup (water lines, arrows, boxes, ellipses, text) onto a picture with SkiaSharp: for reports, the
/// submission folder's marked-up copies, and thumbnails. Sizes scale with the picture's long edge, so a mark looks the
/// same on a thumbnail and a full-size copy.
/// </summary>
public static class PhotoMarkupRenderer
{
	/// <summary>Text height as a fraction of the picture's long edge.</summary>
	public const float TextSize = 0.032f;

	/// <summary>Draws the markup over a picture of <paramref name="width"/> × <paramref name="height"/> already drawn on the canvas at 0,0.</summary>
	public static void Draw(SKCanvas canvas, PhotoMarkup markup, float width, float height)
	{
		if (markup == null || markup.IsEmpty)
		{
			return;
		}
		float edge = Math.Max(width, height);
		using SKTypeface typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold) ?? SKTypeface.Default;
		using SKFont font = new SKFont(typeface, Math.Max(8f, edge * TextSize));
		foreach (MarkupShape s in markup.Shapes)
		{
			SKColor colour = SKColor.TryParse(s.Color, out SKColor c) ? c : SKColors.Red;
			float stroke = Math.Max(1.5f, (float)s.Width * edge);
			SKPoint a = new SKPoint((float)s.X1 * width, (float)s.Y1 * height);
			SKPoint b = new SKPoint((float)s.X2 * width, (float)s.Y2 * height);
			using SKPaint line = new SKPaint { Color = colour, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round };
			using SKPaint halo = new SKPaint { Color = new SKColor(255, 255, 255, 200), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke * 2.2f, StrokeCap = SKStrokeCap.Round };
			switch (s.Kind)
			{
				case MarkupKinds.WaterLine:
					using (SKPathEffect dash = SKPathEffect.CreateDash(new[] { stroke * 3, stroke * 2 }, 0))
					{
						canvas.DrawLine(a, b, halo);
						line.PathEffect = dash;
						canvas.DrawLine(a, b, line);
					}
					if (!string.IsNullOrWhiteSpace(s.Text))
					{
						Label(canvas, s.Text.Trim(), new SKPoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y) - stroke * 1.5f), font, colour);
					}
					break;
				case MarkupKinds.Arrow:
					using (SKPath head = ArrowHead(a, b, stroke))
					{
						canvas.DrawLine(a, b, halo);
						canvas.DrawLine(a, b, line);
						using SKPaint fill = new SKPaint { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };
						canvas.DrawPath(head, fill);
					}
					break;
				case MarkupKinds.Box:
					SKRect box = SKRect.Create(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
					canvas.DrawRect(box, halo);
					canvas.DrawRect(box, line);
					break;
				case MarkupKinds.Ellipse:
					SKRect oval = SKRect.Create(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
					canvas.DrawOval(oval, halo);
					canvas.DrawOval(oval, line);
					break;
				case MarkupKinds.Text:
					if (!string.IsNullOrWhiteSpace(s.Text))
					{
						Label(canvas, s.Text.Trim(), a, font, colour);
					}
					break;
			}
		}
	}

	/// <summary>Text on a white rounded box, its top-left corner at <paramref name="at"/> (kept inside the picture).</summary>
	private static void Label(SKCanvas canvas, string text, SKPoint at, SKFont font, SKColor colour)
	{
		float textWidth = font.MeasureText(text);
		float pad = font.Size * 0.3f;
		SKRect bounds = canvas.LocalClipBounds;
		float x = Math.Clamp(at.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - textWidth - pad * 2));
		float y = Math.Clamp(at.Y - font.Size - pad * 2, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - font.Size - pad * 2));
		SKRect box = SKRect.Create(x, y, textWidth + pad * 2, font.Size + pad * 2);
		using SKPaint back = new SKPaint { Color = new SKColor(255, 255, 255, 225), IsAntialias = true };
		canvas.DrawRoundRect(box, pad, pad, back);
		using SKPaint ink = new SKPaint { Color = colour, IsAntialias = true };
		canvas.DrawText(text, x + pad, y + pad + font.Size * 0.85f, SKTextAlign.Left, font, ink);
	}

	private static SKPath ArrowHead(SKPoint tail, SKPoint tip, float stroke)
	{
		float dx = tip.X - tail.X;
		float dy = tip.Y - tail.Y;
		float length = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dy * dy));
		float ux = dx / length;
		float uy = dy / length;
		float size = stroke * 4.5f;
		SKPath path = new SKPath();
		path.MoveTo(tip);
		path.LineTo(tip.X - ux * size - uy * size * 0.6f, tip.Y - uy * size + ux * size * 0.6f);
		path.LineTo(tip.X - ux * size + uy * size * 0.6f, tip.Y - uy * size - ux * size * 0.6f);
		path.Close();
		return path;
	}

	/// <summary>
	/// Writes a marked-up JPEG copy of a photo (upright, at most <paramref name="maxEdge"/> pixels on the long side).
	/// Returns false when the photo can't be read.
	/// </summary>
	public static bool WriteMarkedUpCopy(string photoPath, PhotoMarkup markup, string outputPath, int maxEdge = 2400)
	{
		(byte[] Jpeg, int Width, int Height)? prepared = Internal.PhotoPrep.Prepare(photoPath, markup, maxEdge);
		if (prepared is not (byte[] jpeg, _, _))
		{
			return false;
		}
		File.WriteAllBytes(outputPath, jpeg);
		return true;
	}
}
