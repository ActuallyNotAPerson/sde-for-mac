using System.Net;
using System.Text;
using SkiaSharp;

namespace Sde.App.Tests;

/// <summary>Synthetic field photos: real (small) JPEG pictures with an EXIF block holding a date and GPS position.</summary>
internal static class FieldPhotos
{
	/// <summary>A 480×360 JPEG of a plain colour with a darker band, plus the given EXIF date and position.</summary>
	public static byte[] Jpeg(SKColor colour, DateTime? takenOn = null, double? latitude = null, double? longitude = null)
	{
		using SKBitmap bitmap = new SKBitmap(480, 360);
		using (SKCanvas canvas = new SKCanvas(bitmap))
		{
			canvas.Clear(colour);
			using SKPaint band = new SKPaint { Color = colour.WithAlpha(255).WithRed((byte)(colour.Red / 2)).WithGreen((byte)(colour.Green / 2)).WithBlue((byte)(colour.Blue / 2)) };
			canvas.DrawRect(0, 240, 480, 120, band);
		}
		using SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
		byte[] picture = data.ToArray();
		if (takenOn == null && latitude == null)
		{
			return picture;
		}
		byte[] exif = Exif(takenOn, latitude, longitude);
		using MemoryStream jpeg = new MemoryStream();
		jpeg.Write(picture, 0, 2);                         // SOI
		jpeg.Write(exif);                                  // APP1 "Exif"
		jpeg.Write(picture, 2, picture.Length - 2);        // the rest of the picture
		return jpeg.ToArray();
	}

	/// <summary>An APP1 segment (marker, length, "Exif\0\0", little-endian TIFF) with the date and GPS position.</summary>
	private static byte[] Exif(DateTime? takenOn, double? latitude, double? longitude)
	{
		MemoryStream tiff = new MemoryStream();
		BinaryWriter w = new BinaryWriter(tiff);
		bool hasGps = latitude != null && longitude != null;
		int ifd0Entries = (takenOn != null ? 1 : 0) + (hasGps ? 1 : 0);
		int ifd0Size = 2 + ifd0Entries * 12 + 4;
		int exifOffset = 8 + ifd0Size;
		int exifSize = takenOn != null ? 2 + 12 + 4 : 0;
		int dateOffset = exifOffset + exifSize;
		int dateSize = takenOn != null ? 20 : 0;
		int gpsOffset = dateOffset + dateSize;
		int gpsSize = hasGps ? 2 + 4 * 12 + 4 : 0;
		int latOffset = gpsOffset + gpsSize;
		int lonOffset = latOffset + 24;

		w.Write(new byte[] { (byte)'I', (byte)'I', 0x2A, 0x00 });
		w.Write(8);
		w.Write((short)ifd0Entries);
		if (takenOn != null)
		{
			Entry(w, 0x8769, 4, 1, exifOffset);
		}
		if (hasGps)
		{
			Entry(w, 0x8825, 4, 1, gpsOffset);
		}
		w.Write(0);
		if (takenOn != null)
		{
			w.Write((short)1);
			Entry(w, 0x9003, 2, 20, dateOffset);
			w.Write(0);
			w.Write(Encoding.ASCII.GetBytes(takenOn.Value.ToString("yyyy:MM:dd HH:mm:ss")));
			w.Write((byte)0);
		}
		if (hasGps)
		{
			w.Write((short)4);
			InlineAscii(w, 0x0001, latitude.Value >= 0 ? 'N' : 'S');
			Entry(w, 0x0002, 5, 3, latOffset);
			InlineAscii(w, 0x0003, longitude.Value >= 0 ? 'E' : 'W');
			Entry(w, 0x0004, 5, 3, lonOffset);
			w.Write(0);
			DegreesMinutesSeconds(w, Math.Abs(latitude.Value));
			DegreesMinutesSeconds(w, Math.Abs(longitude.Value));
		}
		byte[] tiffBytes = tiff.ToArray();
		MemoryStream segment = new MemoryStream();
		segment.Write(new byte[] { 0xFF, 0xE1 });
		int length = 2 + 6 + tiffBytes.Length;
		segment.WriteByte((byte)(length >> 8));
		segment.WriteByte((byte)(length & 0xFF));
		segment.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
		segment.Write(tiffBytes);
		return segment.ToArray();
	}

	private static void Entry(BinaryWriter w, int tag, int type, int count, int value)
	{
		w.Write((short)tag);
		w.Write((short)type);
		w.Write(count);
		w.Write(value);
	}

	private static void InlineAscii(BinaryWriter w, int tag, char value)
	{
		w.Write((short)tag);
		w.Write((short)2);
		w.Write(2);
		w.Write(new byte[] { (byte)value, 0, 0, 0 });
	}

	private static void DegreesMinutesSeconds(BinaryWriter w, double degrees)
	{
		int d = (int)degrees;
		double minutesFull = (degrees - d) * 60;
		int m = (int)minutesFull;
		double seconds = (minutesFull - m) * 60;
		w.Write(d);
		w.Write(1);
		w.Write(m);
		w.Write(1);
		w.Write((int)Math.Round(seconds * 10000));
		w.Write(10000);
	}
}

/// <summary>Answers the online services from the recorded fixtures (tests never touch the network).</summary>
internal sealed class FakeOnline : HttpMessageHandler
{
	private readonly Func<Uri, (HttpStatusCode Status, string Body)> _answer;

	public FakeOnline(Func<Uri, (HttpStatusCode, string)> answer) => _answer = answer;

	public List<string> Urls { get; } = new();

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		lock (Urls)
		{
			Urls.Add(request.RequestUri.AbsoluteUri);
		}
		(HttpStatusCode status, string body) = _answer(request.RequestUri);
		return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
	}

	public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "online", name));

	/// <summary>The Census geocoder finds the address; the flood map says zone AE with a BFE, the panel and the community.</summary>
	public static FakeOnline Recorded(string zone = "nfhl-zone-ae.json") => new FakeOnline(uri =>
	{
		string file = uri.Host.Contains("geocoding", StringComparison.Ordinal) ? "census-capitol-match.json"
			: uri.AbsolutePath.Contains("/28/query", StringComparison.Ordinal) ? zone
			: uri.AbsolutePath.Contains("/3/query", StringComparison.Ordinal) ? "nfhl-panel.json"
			: uri.AbsolutePath.Contains("/22/query", StringComparison.Ordinal) ? "nfhl-community.json"
			: null;
		return file == null ? (HttpStatusCode.NotFound, "{}") : (HttpStatusCode.OK, Fixture(file));
	});

	/// <summary>Every request fails with a server error.</summary>
	public static FakeOnline Down() => new FakeOnline(_ => (HttpStatusCode.ServiceUnavailable, "<html>Service Unavailable</html>"));
}
