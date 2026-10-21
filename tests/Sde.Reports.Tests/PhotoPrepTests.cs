using Sde.Reports.Internal;
using SkiaSharp;

namespace Sde.Reports.Tests;

/// <summary>Photos go into PDFs upright and print-sized, whatever the camera stored.</summary>
public class PhotoPrepTests : IDisposable
{
	private readonly string _folder = Directory.CreateTempSubdirectory("sde-photoprep-").FullName;

	public void Dispose() => Directory.Delete(_folder, recursive: true);

	/// <summary>
	/// A 3000×2000 JPEG, red in the top-left quarter, with an EXIF orientation tag. Orientation 6 is how phones store
	/// a photo taken upright: the pixels are sideways and the viewer is told to turn them 90° clockwise.
	/// </summary>
	private string SidewaysJpeg(ushort orientation)
	{
		using SKBitmap bmp = new SKBitmap(3000, 2000);
		using (SKCanvas c = new SKCanvas(bmp))
		{
			c.Clear(SKColors.White);
			c.DrawRect(0, 0, 1500, 1000, new SKPaint { Color = SKColors.Red });
		}
		using SKData jpeg = bmp.Encode(SKEncodedImageFormat.Jpeg, 90);
		byte[] plain = jpeg.ToArray();

		// Insert an APP1 "Exif" segment after the SOI marker: big-endian TIFF header, one IFD entry (0x0112 Orientation).
		byte[] tiff =
		{
			(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,         // header, IFD at offset 8
			0, 1,                                              // one entry
			0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0, // Orientation, SHORT, 1
			0, 0, 0, 0,                                        // no next IFD
		};
		byte[] exif = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
		int length = exif.Length + 2;
		byte[] app1 = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(exif).ToArray();
		string path = Path.Combine(_folder, $"photo-{orientation}.jpg");
		File.WriteAllBytes(path, plain.Take(2).Concat(app1).Concat(plain.Skip(2)).ToArray());
		return path;
	}

	private static SKColor Pixel(byte[] jpeg, int x, int y)
	{
		using SKBitmap b = SKBitmap.Decode(jpeg);
		return b.GetPixel(x, y);
	}

	private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

	[Fact]
	public void PhoneHeldUpright_IsTurnedUpright_AndShrunk()
	{
		(byte[] jpeg, int w, int h) = PhotoPrep.Prepare(SidewaysJpeg(6))!.Value;
		Assert.Equal((1067, 1600), (w, h)); // portrait now, long edge 1600
		using (SKBitmap b = SKBitmap.Decode(jpeg))
		{
			Assert.Equal((w, h), (b.Width, b.Height));
		}
		// Turning 90° clockwise moves the stored top-left (red) quarter to the top-right.
		Assert.True(IsRed(Pixel(jpeg, w - 50, 50)));
		Assert.False(IsRed(Pixel(jpeg, 50, 50)));
		Assert.True(jpeg.Length < 400_000, $"{jpeg.Length} bytes");
	}

	[Theory]
	[InlineData((ushort)1, false, false, false)] // as stored: red top-left
	[InlineData((ushort)3, false, true, true)]   // upside down: red bottom-right
	[InlineData((ushort)8, true, true, false)]   // turned anticlockwise: red bottom-left
	public void EveryOrientation_PutsTheCornerWhereTheViewerWouldSeeIt(ushort orientation, bool portrait, bool redAtBottom, bool redAtRight)
	{
		(byte[] jpeg, int w, int h) = PhotoPrep.Prepare(SidewaysJpeg(orientation))!.Value;
		Assert.Equal(portrait, h > w);
		int x = redAtRight ? w - 50 : 50, y = redAtBottom ? h - 50 : 50;
		Assert.True(IsRed(Pixel(jpeg, x, y)), $"orientation {orientation}: expected red at ({x},{y})");
	}

	[Fact]
	public void SmallPhoto_KeepsItsSize()
	{
		string path = Path.Combine(_folder, "small.png");
		using (SKBitmap bmp = new SKBitmap(640, 480))
		using (SKData png = bmp.Encode(SKEncodedImageFormat.Png, 100))
		{
			File.WriteAllBytes(path, png.ToArray());
		}
		(byte[] _, int w, int h) = PhotoPrep.Prepare(path)!.Value;
		Assert.Equal((640, 480), (w, h));
	}

	[Fact]
	public void NotAnImage_IsNull()
	{
		string path = Path.Combine(_folder, "notes.jpg");
		File.WriteAllText(path, "not a photo");
		Assert.Null(PhotoPrep.Prepare(path));
	}
}
