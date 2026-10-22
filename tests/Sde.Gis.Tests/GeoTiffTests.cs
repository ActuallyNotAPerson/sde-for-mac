using System.Buffers.Binary;
using System.Globalization;
using BitMiracle.LibTiff.Classic;
using Sde.Gis.Projections;
using Sde.Gis.Raster;

namespace Sde.Gis.Tests;

/// <summary>Single-band GeoTIFFs written in the test with LibTiff.NET: State Plane and geographic, strips and tiles, compressions, no-data.</summary>
public class GeoTiffTests
{
	// A 4 by 3 grid of 100 ft cells in Ohio South (US survey feet), top-left corner at (1531900, 594300).
	private const double X0 = 1531900;
	private const double Y0 = 594300;
	private const double Cell = 100;
	private static readonly short[] OhioSouthKeys = { 1, 1, 0, 3, 1024, 0, 1, 1, 1025, 0, 1, 1, 3072, 0, 1, 3735 };

	private static double Linear(int c, int r) => 10 * r + c;

	/// <summary>Writes a one-band GeoTIFF with LibTiff.NET.</summary>
	private static void Write(string path, int width, int height, Func<int, int, double> value, short[] geoKeys, double[] tiepoint, double[] scale,
		int bits = 32, SampleFormat format = SampleFormat.IEEEFP, Compression compression = Compression.NONE, int tile = 0, string noData = null,
		bool bigEndian = false, bool predictor = false, int samples = 1)
	{
		GeoTiffTags.EnsureRegistered();
		using Tiff t = Tiff.Open(path, bigEndian ? "wb" : "wl");
		t.SetField(TiffTag.IMAGEWIDTH, width);
		t.SetField(TiffTag.IMAGELENGTH, height);
		t.SetField(TiffTag.BITSPERSAMPLE, bits);
		t.SetField(TiffTag.SAMPLESPERPIXEL, samples);
		t.SetField(TiffTag.SAMPLEFORMAT, format);
		t.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
		t.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
		t.SetField(TiffTag.COMPRESSION, compression);
		if (predictor)
		{
			t.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL);
		}
		if (tile > 0)
		{
			t.SetField(TiffTag.TILEWIDTH, tile);
			t.SetField(TiffTag.TILELENGTH, tile);
		}
		else
		{
			t.SetField(TiffTag.ROWSPERSTRIP, 2);
		}
		t.SetField(GeoTiffTags.ModelPixelScale, 3, scale);
		t.SetField(GeoTiffTags.ModelTiepoint, 6, tiepoint);
		t.SetField(GeoTiffTags.GeoKeyDirectory, geoKeys.Length, geoKeys);
		if (noData != null)
		{
			t.SetField(GeoTiffTags.GdalNoData, noData);
		}
		int bytes = bits / 8;
		byte[] Cells(int c0, int r0, int w, int h)
		{
			byte[] buf = new byte[w * h * bytes * samples];
			for (int r = 0; r < h; r++)
			{
				for (int c = 0; c < w; c++)
				{
					double v = c0 + c < width && r0 + r < height ? value(c0 + c, r0 + r) : 0;
					for (int s = 0; s < samples; s++)
					{
						int o = ((r * w + c) * samples + s) * bytes;
						byte[] b = (format, bits) switch
						{
							(SampleFormat.IEEEFP, 32) => BitConverter.GetBytes((float)v),
							(SampleFormat.IEEEFP, 64) => BitConverter.GetBytes(v),
							(SampleFormat.INT, 16) => BitConverter.GetBytes((short)v),
							(SampleFormat.UINT, 16) => BitConverter.GetBytes((ushort)v),
							(SampleFormat.UINT, 8) => new[] { (byte)v },
							_ => BitConverter.GetBytes((int)v),
						};
						Buffer.BlockCopy(b, 0, buf, o, bytes);
					}
				}
			}
			return buf;
		}
		if (tile > 0)
		{
			for (int r = 0; r < height; r += tile)
			{
				for (int c = 0; c < width; c += tile)
				{
					byte[] buf = Cells(c, r, tile, tile);
					t.WriteEncodedTile(t.ComputeTile(c, r, 0, 0), buf, buf.Length);
				}
			}
		}
		else
		{
			for (int r = 0; r < height; r++)
			{
				t.WriteScanline(Cells(0, r, width, 1), r);
			}
		}
		t.WriteDirectory();
	}

	private static string OhioGrid(TempFolder temp, string name = "depth.tif", string noData = "-9999", bool bigEndian = false, Compression compression = Compression.NONE)
	{
		string path = temp.Path(name);
		Write(path, 4, 3, (c, r) => c == 3 && r == 2 ? -9999 : Linear(c, r), OhioSouthKeys, new[] { 0.0, 0, 0, X0, Y0, 0 }, new[] { Cell, Cell, 0 },
			noData: noData, bigEndian: bigEndian, compression: compression);
		return path;
	}

	/// <summary>The WGS 84 point at cell-centre coordinates (c, r) of the Ohio grid.</summary>
	private static (double Lon, double Lat) At(double c, double r) =>
		CoordinateSystem.FromEpsg(3735).ToWgs84(X0 + (c + 0.5) * Cell, Y0 - (r + 0.5) * Cell);

	[Fact]
	public void StatePlaneGrid_IsPlacedAndSampled()
	{
		using TempFolder temp = new TempFolder();
		using GeoTiffRaster grid = GeoTiffRaster.Open(OhioGrid(temp));
		Assert.Equal(4, grid.Width);
		Assert.Equal(3, grid.Height);
		Assert.Equal(3735, grid.CoordinateSystem.EpsgCode);
		Assert.Equal(-9999, grid.NoData);
		Assert.Equal((Cell, Cell), grid.CellSize);
		Assert.Equal(X0, grid.Extent.MinX);
		Assert.Equal(Y0, grid.Extent.MaxY);
		Assert.Equal(12.0, grid.ValueAt(2, 1));
		Assert.Null(grid.ValueAt(3, 2));

		// The grid is linear (10 r + c), so bilinear interpolation gives it back exactly between cell centres.
		foreach ((double c, double r) in new[] { (0.0, 0.0), (1.25, 0.5), (2.0, 1.0), (0.5, 1.75), (1.9, 0.1) })
		{
			(double lon, double lat) = At(c, r);
			Assert.Equal(10 * r + c, grid.Sample(lon, lat).Value, 1e-6);
		}
	}

	[Fact]
	public void NoDataAndEdges()
	{
		using TempFolder temp = new TempFolder();
		using GeoTiffRaster grid = GeoTiffRaster.Open(OhioGrid(temp));
		(double lon, double lat) = At(3, 2);
		Assert.Null(grid.Sample(lon, lat));
		// Near the no-data cell: the other three neighbours are reweighted (0.36, 0.24, 0.24 of 0.84).
		(lon, lat) = At(2.4, 1.4);
		Assert.Equal((0.36 * 12 + 0.24 * 13 + 0.24 * 22) / 0.84, grid.Sample(lon, lat).Value, 1e-6);
		// Inside the edge cells, beyond the outer centres, values are held at the edge.
		(lon, lat) = At(-0.4, 0);
		Assert.Equal(0.0, grid.Sample(lon, lat).Value, 1e-6);
		(lon, lat) = At(-0.6, 0);
		Assert.Null(grid.Sample(lon, lat));
		(lon, lat) = At(1, 3.2);
		Assert.Null(grid.Sample(lon, lat));
	}

	[Fact]
	public void BigEndianAndDeflate_GiveTheSameValues()
	{
		using TempFolder temp = new TempFolder();
		using GeoTiffRaster big = GeoTiffRaster.Open(OhioGrid(temp, "big.tif", bigEndian: true));
		using GeoTiffRaster deflate = GeoTiffRaster.Open(OhioGrid(temp, "deflate.tif", compression: Compression.ADOBE_DEFLATE));
		using GeoTiffRaster lzw = GeoTiffRaster.Open(OhioGrid(temp, "lzw.tif", compression: Compression.LZW));
		for (int r = 0; r < 3; r++)
		{
			for (int c = 0; c < 3; c++)
			{
				Assert.Equal(Linear(c, r), big.ValueAt(c, r));
				Assert.Equal(Linear(c, r), deflate.ValueAt(c, r));
				Assert.Equal(Linear(c, r), lzw.ValueAt(c, r));
			}
		}
	}

	[Fact]
	public void GeographicTiledLzw_WithPixelIsPoint()
	{
		using TempFolder temp = new TempFolder();
		string path = temp.Path("elev.tif");
		short[] keys = { 1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 2, 2048, 0, 1, 4269 };
		// 40 by 30 cells of 0.001 degrees, int16, tiled 16 by 16, LZW with the horizontal predictor; cell (0,0) centred on (-84.05, 39.65).
		Write(path, 40, 30, (c, r) => 200 + 2 * c - r, keys, new[] { 0.0, 0, 0, -84.05, 39.65, 0 }, new[] { 0.001, 0.001, 0 },
			bits: 16, format: SampleFormat.INT, compression: Compression.LZW, tile: 16, predictor: true);
		using GeoTiffRaster grid = GeoTiffRaster.Open(path);
		Assert.Equal(4269, grid.CoordinateSystem.EpsgCode);
		Assert.Null(grid.NoData);
		Assert.Equal(200.0, grid.Sample(-84.05, 39.65).Value, 1e-6);
		// Pixel-is-point: the tie point is cell (0,0)'s centre, so (17, 20) is a cell centre, not a corner.
		Assert.Equal(200 + 2 * 17 - 20, grid.Sample(-84.05 + 0.017, 39.65 - 0.020).Value, 1e-6);
		Assert.Equal(200 + 2 * 33.5 - 25.25, grid.Sample(-84.05 + 0.0335, 39.65 - 0.02525).Value, 1e-6);
		Assert.Equal(-84.0505, grid.ExtentWgs84.MinX, 1e-9);
		Assert.Equal(39.6505, grid.ExtentWgs84.MaxY, 1e-9);
	}

	[Theory]
	[InlineData(50000, "ZSTD")]
	[InlineData(34887, "LERC")]
	[InlineData(50001, "WebP")]
	[InlineData(34925, "LZMA")]
	public void UnreadableCompression_SaysWhatToDo(int code, string name)
	{
		using TempFolder temp = new TempFolder();
		string path = OhioGrid(temp);
		SetCompressionTag(path, code);
		GisException ex = Assert.Throws<GisException>(() => GeoTiffRaster.Open(path));
		Assert.Equal($"This GeoTIFF uses {name} compression, which SDE can't read. Save it again with LZW or Deflate compression (or none) in QGIS first.", ex.Message);
	}

	[Fact]
	public void UnsupportedFiles_AreRefusedInPlainEnglish()
	{
		using TempFolder temp = new TempFolder();
		string rgb = temp.Path("rgb.tif");
		Write(rgb, 4, 3, Linear, OhioSouthKeys, new[] { 0.0, 0, 0, X0, Y0, 0 }, new[] { Cell, Cell, 0 }, bits: 8, format: SampleFormat.UINT, samples: 3);
		Assert.Equal("This GeoTIFF has 3 bands. SDE reads single-band grids, such as depth or elevation grids.", Assert.Throws<GisException>(() => GeoTiffRaster.Open(rgb)).Message);

		string nad27 = temp.Path("nad27.tif");
		Write(nad27, 4, 3, Linear, new short[] { 1, 1, 0, 2, 1024, 0, 1, 1, 3072, 0, 1, 32023 }, new[] { 0.0, 0, 0, X0, Y0, 0 }, new[] { Cell, Cell, 0 });
		Assert.Equal("This file's coordinate system (NAD27) isn't supported. Reproject it to NAD83 or WGS 84 in QGIS first.", Assert.Throws<UnsupportedCoordinateSystemException>(() => GeoTiffRaster.Open(nad27)).Message);

		string custom = temp.Path("custom.tif");
		Write(custom, 4, 3, Linear, new short[] { 1, 1, 0, 2, 1024, 0, 1, 1, 3072, 0, 1, 32767 }, new[] { 0.0, 0, 0, X0, Y0, 0 }, new[] { Cell, Cell, 0 });
		Assert.Throws<UnsupportedCoordinateSystemException>(() => GeoTiffRaster.Open(custom));

		File.WriteAllText(temp.Path("text.tif"), "not a tiff");
		Assert.Throws<GisException>(() => GeoTiffRaster.Open(temp.Path("text.tif")));
	}

	[Fact]
	public void Placement_MatchesGdal()
	{
		string tool = Gdal.Tool("gdallocationinfo");
		Assert.SkipWhen(tool == null, "GDAL (gdallocationinfo) isn't installed here.");
		using TempFolder temp = new TempFolder();
		string path = OhioGrid(temp, compression: Compression.LZW);
		using GeoTiffRaster grid = GeoTiffRaster.Open(path);
		foreach ((double c, double r) in new[] { (0.2, 0.3), (2.6, 1.1), (1.5, 2.9) })
		{
			double x = X0 + c * Cell;
			double y = Y0 - r * Cell;
			(int code, string output) = Gdal.Run(tool, "-valonly", "-geoloc", path, x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture));
			Assert.True(code == 0, output);
			double gdal = double.Parse(output.Trim().Split('\n')[^1], CultureInfo.InvariantCulture);
			Assert.Equal(gdal, grid.ValueAt((int)Math.Floor(c), (int)Math.Floor(r)));
		}
	}

	/// <summary>Rewrites the Compression tag (259) in a little-endian TIFF's first directory.</summary>
	private static void SetCompressionTag(string path, int value)
	{
		byte[] b = File.ReadAllBytes(path);
		Assert.Equal((byte)'I', b[0]);
		int ifd = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
		int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(ifd));
		for (int k = 0; k < count; k++)
		{
			int entry = ifd + 2 + k * 12;
			if (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(entry)) == 259)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(entry + 8), (ushort)value);
				File.WriteAllBytes(path, b);
				return;
			}
		}
		Assert.Fail("No Compression tag.");
	}
}
