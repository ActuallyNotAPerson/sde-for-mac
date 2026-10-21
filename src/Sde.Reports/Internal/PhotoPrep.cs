using SkiaSharp;

namespace Sde.Reports.Internal;

/// <summary>
/// Makes a photo fit for a PDF: turned upright (phone cameras store portrait photos sideways plus an EXIF
/// orientation flag, which PDF viewers ignore), shrunk to <see cref="MaxEdge"/> pixels on the long side (a 12 MP
/// photo printed 3 inches wide doesn't need 4,000 pixels), and re-encoded as JPEG. The file on disk is not changed.
/// FEMA's ReportViewer embedded photos as they were, so its PDFs could run to hundreds of megabytes.
/// SkiaSharp also reads GIF and WebP, which PDFsharp can't embed directly.
/// </summary>
internal static class PhotoPrep
{
	/// <summary>Long edge in pixels: about 300 dpi at the largest size a report prints a photo (about 5 inches).</summary>
	public const int MaxEdge = 1600;

	public const int JpegQuality = 85;

	/// <summary>The upright, resized JPEG and its size, or null when the file isn't an image Skia can read.</summary>
	public static (byte[] Jpeg, int Width, int Height)? Prepare(string path)
	{
		using SKCodec codec = SKCodec.Create(path);
		if (codec == null)
		{
			return null;
		}
		using SKBitmap decoded = SKBitmap.Decode(codec);
		if (decoded == null || decoded.Width == 0 || decoded.Height == 0)
		{
			return null;
		}

		SKEncodedOrigin origin = codec.EncodedOrigin;
		double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(decoded.Width, decoded.Height));
		int w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
		int h = Math.Max(1, (int)Math.Round(decoded.Height * scale));
		bool quarterTurn = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
		int outW = quarterTurn ? h : w;
		int outH = quarterTurn ? w : h;

		using SKBitmap output = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);
		using (SKCanvas canvas = new SKCanvas(output))
		{
			canvas.Clear(SKColors.White); // JPEG has no transparency; transparent PNG areas print white
			canvas.SetMatrix(Orientation(origin, outW, outH));
			using SKImage image = SKImage.FromBitmap(decoded);
			canvas.DrawImage(image, SKRect.Create(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
		}
		using SKData data = output.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
		return data == null ? null : (data.ToArray(), outW, outH);
	}

	/// <summary>Maps the stored image (drawn at 0,0) onto the upright output, for each EXIF orientation (1–8).</summary>
	private static SKMatrix Orientation(SKEncodedOrigin origin, int outW, int outH) => origin switch
	{
		// x' = ScaleX·x + SkewX·y + TransX;  y' = SkewY·x + ScaleY·y + TransY
		SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, outW, 0, 1, 0, 0, 0, 1),        // 2: mirrored
		SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, outW, 0, -1, outH, 0, 0, 1), // 3: upside down
		SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, outH, 0, 0, 1),      // 4: mirrored, upside down
		SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),             // 5: transposed
		SKEncodedOrigin.RightTop => new SKMatrix(0, -1, outW, 1, 0, 0, 0, 0, 1),        // 6: turn 90° clockwise (phone held upright)
		SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, outW, -1, 0, outH, 0, 0, 1), // 7: transverse
		SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, outH, 0, 0, 1),      // 8: turn 90° anticlockwise
		_ => SKMatrix.Identity,
	};
}
