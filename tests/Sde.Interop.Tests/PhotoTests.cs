using System.Text;
using Sde.Interop.Photos;

namespace Sde.Interop.Tests;

/// <summary>Builds tiny synthetic JPEG files whose only content is an EXIF block with a date and GPS position.</summary>
internal static class SyntheticJpeg
{
	public static byte[] Build(DateTime? takenOn, double? latitude, double? longitude)
	{
		MemoryStream tiff = new MemoryStream();
		BinaryWriter w = new BinaryWriter(tiff);
		bool hasGps = latitude != null && longitude != null;

		// Layout (little endian): header 8 bytes, IFD0 at 8, Exif IFD, date text, GPS IFD, GPS rationals.
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
		MemoryStream jpeg = new MemoryStream();
		jpeg.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 });
		int length = 2 + 6 + tiffBytes.Length;
		jpeg.WriteByte((byte)(length >> 8));
		jpeg.WriteByte((byte)(length & 0xFF));
		jpeg.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
		jpeg.Write(tiffBytes);
		jpeg.Write(new byte[] { 0xFF, 0xD9 });
		return jpeg.ToArray();
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

public class PhotoMetadataTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "sde-photo-" + Guid.NewGuid().ToString("N"));

	public PhotoMetadataTests()
	{
		Directory.CreateDirectory(_folder);
	}

	public void Dispose()
	{
		Directory.Delete(_folder, recursive: true);
	}

	private string Write(string name, byte[] bytes)
	{
		string path = Path.Combine(_folder, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[Fact]
	public void Read_ReturnsDateAndGps()
	{
		string path = Write("a.jpg", SyntheticJpeg.Build(new DateTime(2024, 6, 15, 14, 30, 5), 38.889912, -77.009111));
		PhotoInfo info = PhotoMetadata.Read(path);

		Assert.Equal(new DateTime(2024, 6, 15, 14, 30, 5), info.TakenOn);
		Assert.Equal(38.889912, info.Latitude.Value, 5);
		Assert.Equal(-77.009111, info.Longitude.Value, 5);
	}

	[Fact]
	public void Read_SouthEast_AreSigned()
	{
		PhotoInfo info = PhotoMetadata.Read(Write("b.jpg", SyntheticJpeg.Build(null, -33.8568, 151.2153)));
		Assert.Null(info.TakenOn);
		Assert.Equal(-33.8568, info.Latitude.Value, 4);
		Assert.Equal(151.2153, info.Longitude.Value, 4);
	}

	[Fact]
	public void Read_DateOnly_HasNoPosition()
	{
		PhotoInfo info = PhotoMetadata.Read(Write("c.jpg", SyntheticJpeg.Build(new DateTime(2023, 1, 2, 3, 4, 5), null, null)));
		Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5), info.TakenOn);
		Assert.Null(info.Latitude);
		Assert.Null(info.Longitude);
	}

	[Fact]
	public void Read_NoMetadata_Corrupt_AndEmptyFiles_ReturnNulls()
	{
		byte[] good = SyntheticJpeg.Build(new DateTime(2024, 1, 1), 10, 10);
		string[] paths =
		{
			Write("bare.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }),
			Write("text.jpg", Encoding.ASCII.GetBytes("this is not an image")),
			Write("empty.jpg", Array.Empty<byte>()),
			Write("truncated.jpg", good.Take(good.Length / 2).ToArray()),
		};
		foreach (string path in paths)
		{
			PhotoInfo info = PhotoMetadata.Read(path);
			Assert.Null(info.Latitude);
			Assert.Null(info.Longitude);
		}
		Assert.Null(PhotoMetadata.Read(paths[0]).TakenOn);
	}

	[Fact]
	public void Read_MissingFile_Throws()
	{
		Assert.Throws<FileNotFoundException>(() => PhotoMetadata.Read(Path.Combine(_folder, "nope.jpg")));
	}

	[Fact]
	public void Matcher_UsesGpsFromRealFiles()
	{
		string photo = Write("IMG_0001.jpg", SyntheticJpeg.Build(null, 38.8899, -77.0091));
		PhotoMatchCandidate near = new PhotoMatchCandidate(Guid.NewGuid(), "5 Oak St", null, null, 38.88995, -77.0091);
		PhotoMatchCandidate far = new PhotoMatchCandidate(Guid.NewGuid(), "6 Oak St", null, null, 38.9, -77.0);

		PhotoMatch match = PhotoFolderMatcher.Match(new[] { photo }, new[] { far, near }).Single();
		Assert.Same(near, match.Candidate);
		Assert.InRange(match.DistanceMeters.Value, 0, 10);
		Assert.StartsWith("GPS", match.Reason);
	}
}

public class PhotoFolderMatcherTests
{
	private static PhotoMatchCandidate Candidate(string address, string parcel = null, string recordId = null, double? lat = null, double? lon = null) =>
		new PhotoMatchCandidate(Guid.NewGuid(), address, parcel, recordId, lat, lon);

	private static PhotoMatch One(string path, params PhotoMatchCandidate[] candidates) =>
		PhotoFolderMatcher.Match(new[] { path }, candidates).Single();

	[Fact]
	public void RecordId_Wins_OverParcelAndAddress()
	{
		PhotoMatchCandidate byRecord = Candidate("1 A St", "P-1", "BP-2024-0017");
		PhotoMatchCandidate byAddress = Candidate("123 Main St", "999", "R9");
		PhotoMatch m = One("/x/bp 2024 0017 - 123 Main.JPG", byAddress, byRecord);
		Assert.Same(byRecord, m.Candidate);
		Assert.Contains("record ID", m.Reason);
	}

	[Fact]
	public void Parcel_MatchesIgnoringPunctuationAndCase()
	{
		PhotoMatchCandidate c = Candidate("1 A St", "12-345-678.9");
		PhotoMatch m = One("photos/12_345_678_9_front.jpg", Candidate("2 B St", "55"), c);
		Assert.Same(c, m.Candidate);
		Assert.Contains("parcel", m.Reason);
	}

	[Fact]
	public void Address_MatchesHouseNumberAndStreet()
	{
		PhotoMatchCandidate c = Candidate("123 Main St");
		Assert.Same(c, One("123_MAIN-street_front.jpg", Candidate("124 Main St"), Candidate("123 Maple Ave"), c).Candidate);
		Assert.Contains("address", One("123 main.jpg", c).Reason);
	}

	[Fact]
	public void Name_LongerKeyWins_AndEqualKeysFallThrough()
	{
		PhotoMatchCandidate shortId = Candidate("1 A St", recordId: "12");
		PhotoMatchCandidate longId = Candidate("2 B St", recordId: "1234");
		Assert.Same(longId, One("1234.jpg", shortId, longId).Candidate);

		PhotoMatchCandidate twinA = Candidate("3 C St", recordId: "77");
		PhotoMatchCandidate twinB = Candidate("4 D St", recordId: "77");
		Assert.Null(One("77.jpg", twinA, twinB).Candidate);
	}

	[Fact]
	public void NoNameAndNoGps_IsNoMatch()
	{
		PhotoMatch m = One("/definitely/missing/IMG_1.jpg", Candidate("1 A St", "5", "R1"));
		Assert.Null(m.Candidate);
		Assert.Equal("no match", m.Reason);
		Assert.Null(m.DistanceMeters);
	}

	[Fact]
	public void Results_AreInPhotoOrder()
	{
		PhotoMatchCandidate a = Candidate("1 A St", recordId: "AAA");
		PhotoMatchCandidate b = Candidate("2 B St", recordId: "BBB");
		IReadOnlyList<PhotoMatch> matches = PhotoFolderMatcher.Match(new[] { "BBB.jpg", "AAA.jpg" }, new[] { a, b });
		Assert.Equal(new[] { "BBB.jpg", "AAA.jpg" }, matches.Select(m => m.PhotoPath));
		Assert.Same(b, matches[0].Candidate);
		Assert.Same(a, matches[1].Candidate);
	}

	[Fact]
	public void Haversine_IsAboutRight()
	{
		// 0.001 degree of latitude is about 111 m.
		Assert.Equal(111.2, PhotoFolderMatcher.DistanceMeters(40, -75, 40.001, -75), 0);
		Assert.Equal(0, PhotoFolderMatcher.DistanceMeters(40, -75, 40, -75), 6);
	}
}

public class PhotoGpsMatchingTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "sde-photo-" + Guid.NewGuid().ToString("N"));

	public PhotoGpsMatchingTests()
	{
		Directory.CreateDirectory(_folder);
	}

	public void Dispose()
	{
		Directory.Delete(_folder, recursive: true);
	}

	private string Photo(double lat, double lon)
	{
		string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".jpg");
		File.WriteAllBytes(path, SyntheticJpeg.Build(null, lat, lon));
		return path;
	}

	private static PhotoMatchCandidate At(double lat, double lon) => new PhotoMatchCandidate(Guid.NewGuid(), "no street", null, null, lat, lon);

	[Fact]
	public void NearestWithinTolerance_IsChosen()
	{
		// 0.0001 degree of latitude is about 11 m.
		PhotoMatchCandidate close = At(40.0001, -75);
		PhotoMatchCandidate farther = At(40.0003, -75);
		PhotoMatch m = PhotoFolderMatcher.Match(new[] { Photo(40, -75) }, new[] { farther, close }).Single();
		Assert.Same(close, m.Candidate);
		Assert.InRange(m.DistanceMeters.Value, 9, 13);
	}

	[Fact]
	public void OutsideTolerance_IsNoMatch_AndToleranceIsAdjustable()
	{
		string photo = Photo(40, -75);
		PhotoMatchCandidate c = At(40.0005, -75); // about 56 m
		Assert.Equal("no match", PhotoFolderMatcher.Match(new[] { photo }, new[] { c }).Single().Reason);
		Assert.Same(c, PhotoFolderMatcher.Match(new[] { photo }, new[] { c }, toleranceMeters: 80).Single().Candidate);
	}

	[Fact]
	public void TwoStructuresWithinTenMetersOfEachOther_IsAmbiguous()
	{
		PhotoMatch m = PhotoFolderMatcher.Match(new[] { Photo(40, -75) }, new[] { At(40.00005, -75), At(40.00008, -75) }).Single();
		Assert.Null(m.Candidate);
		Assert.Equal("ambiguous: two structures within 10 m", m.Reason);
	}

	[Fact]
	public void TwoStructuresFartherApart_IsNotAmbiguous()
	{
		PhotoMatchCandidate a = At(40.00005, -75);
		PhotoMatchCandidate b = At(40, -75.0003); // about 26 m from the photo and 30+ m from a
		Assert.Same(a, PhotoFolderMatcher.Match(new[] { Photo(40, -75) }, new[] { a, b }).Single().Candidate);
	}

	[Fact]
	public void CandidatesWithoutGps_AreIgnored()
	{
		PhotoMatch m = PhotoFolderMatcher.Match(new[] { Photo(40, -75) }, new[] { Candidate() }).Single();
		Assert.Equal("no match", m.Reason);
	}

	private static PhotoMatchCandidate Candidate() => new PhotoMatchCandidate(Guid.NewGuid(), "x", null, null, null, null);
}
