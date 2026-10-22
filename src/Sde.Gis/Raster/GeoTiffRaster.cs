using System.Globalization;
using BitMiracle.LibTiff.Classic;
using NetTopologySuite.Geometries;
using Sde.Gis.Projections;

namespace Sde.Gis.Raster;

/// <summary>
/// A single-band GeoTIFF (a depth grid, an elevation model) read with LibTiff.NET (New BSD) and our own GeoKey and
/// no-data parsing. It finds its place on the map from ModelTiepoint and ModelPixelScale (or ModelTransformation)
/// and its coordinate system from the EPSG code in its GeoKeys (State Plane, UTM, geographic NAD83 and WGS 84), and
/// gives the value at a WGS 84 point by bilinear interpolation. Blocks (strips or tiles) are read only when needed
/// and a few are kept, so large grids stay cheap. Not thread-safe: use one per thread.
/// </summary>
public sealed class GeoTiffRaster : IDisposable
{
	/// <summary>Compressions LibTiff.NET can't decode, with the names people know them by.</summary>
	private static readonly Dictionary<int, string> UnsupportedCompressions = new Dictionary<int, string>
	{
		[50000] = "ZSTD",
		[34887] = "LERC",
		[50001] = "WebP",
		[34925] = "LZMA",
		[50002] = "JPEG XL",
		[52546] = "JPEG XL",
	};

	private const int MaxCachedBlocks = 64;

	private readonly Tiff _tiff;
	private readonly string _path;
	private readonly bool _tiled;
	private readonly int _blockWidth;
	private readonly int _blockHeight;
	private readonly int _bytesPerSample;
	private readonly SampleFormat _format;
	private readonly double[] _forward;
	private readonly double[] _inverse;
	private readonly bool _pixelIsPoint;
	private readonly Dictionary<int, double[]> _blocks = new Dictionary<int, double[]>();
	private readonly LinkedList<int> _recent = new LinkedList<int>();

	private GeoTiffRaster(Tiff tiff, string path, int width, int height, CoordinateSystem cs, double? noData, int bits, SampleFormat format,
		bool tiled, int blockWidth, int blockHeight, double[] forward, bool pixelIsPoint)
	{
		_tiff = tiff;
		_path = path;
		Width = width;
		Height = height;
		CoordinateSystem = cs;
		NoData = noData;
		BitsPerSample = bits;
		_bytesPerSample = bits / 8;
		_format = format;
		_tiled = tiled;
		_blockWidth = blockWidth;
		_blockHeight = blockHeight;
		_forward = forward;
		_pixelIsPoint = pixelIsPoint;
		double det = forward[0] * forward[4] - forward[1] * forward[3];
		if (Math.Abs(det) < 1e-300)
		{
			throw new GisException("This GeoTIFF's position on the map can't be worked out (its pixel size is zero).", path);
		}
		// Inverse of x = a*i + b*j + c, y = d*i + e*j + f.
		_inverse = new[]
		{
			forward[4] / det, -forward[1] / det, (forward[1] * forward[5] - forward[4] * forward[2]) / det,
			-forward[3] / det, forward[0] / det, (forward[3] * forward[2] - forward[0] * forward[5]) / det,
		};
	}

	public int Width { get; }

	public int Height { get; }

	/// <summary>The coordinate system the grid is in.</summary>
	public CoordinateSystem CoordinateSystem { get; }

	/// <summary>The value that marks cells with no data (GDAL_NODATA), or null. NaN cells always count as no data.</summary>
	public double? NoData { get; }

	public int BitsPerSample { get; }

	/// <summary>Size of one cell in the grid's units (x and y).</summary>
	public (double X, double Y) CellSize => (Math.Sqrt(_forward[0] * _forward[0] + _forward[3] * _forward[3]), Math.Sqrt(_forward[1] * _forward[1] + _forward[4] * _forward[4]));

	/// <summary>The grid's extent in its own coordinates.</summary>
	public Envelope Extent
	{
		get
		{
			Envelope e = new Envelope();
			foreach ((double i, double j) in new[] { (0.0, 0.0), (Width, 0.0), (0.0, Height), ((double)Width, (double)Height) })
			{
				(double x, double y) = RasterToModel(i - (_pixelIsPoint ? 0.5 : 0), j - (_pixelIsPoint ? 0.5 : 0));
				e.ExpandToInclude(x, y);
			}
			return e;
		}
	}

	/// <summary>The grid's extent in WGS 84 (the corners and edge midpoints converted).</summary>
	public Envelope ExtentWgs84
	{
		get
		{
			Envelope e = Extent;
			Envelope result = new Envelope();
			for (int k = 0; k <= 4; k++)
			{
				double t = k / 4.0;
				foreach ((double x, double y) in new[] { (e.MinX + t * e.Width, e.MinY), (e.MinX + t * e.Width, e.MaxY), (e.MinX, e.MinY + t * e.Height), (e.MaxX, e.MinY + t * e.Height) })
				{
					(double lon, double lat) = CoordinateSystem.ToWgs84(x, y);
					result.ExpandToInclude(lon, lat);
				}
			}
			return result;
		}
	}

	/// <summary>
	/// Opens a GeoTIFF. Throws <see cref="GisException"/> for files that aren't single-band GeoTIFFs SDE can read (with
	/// a plain-English reason, such as an unsupported compression), and <see cref="UnsupportedCoordinateSystemException"/>
	/// for coordinate systems it can't convert.
	/// </summary>
	public static GeoTiffRaster Open(string path)
	{
		GeoTiffTags.EnsureRegistered();
		if (!File.Exists(path))
		{
			throw new GisException("The GeoTIFF file couldn't be found.", path);
		}
		GeoTiffTags.TakeErrors();
		Tiff tiff = Tiff.Open(path, "r");
		if (tiff == null)
		{
			throw new GisException("This file isn't a TIFF SDE can read.", $"{path}: {string.Join("; ", GeoTiffTags.TakeErrors())}");
		}
		try
		{
			return Create(tiff, path);
		}
		catch
		{
			tiff.Dispose();
			throw;
		}
	}

	private static GeoTiffRaster Create(Tiff tiff, string path)
	{
		int compression = Int(tiff, TiffTag.COMPRESSION) ?? 1;
		if (UnsupportedCompressions.TryGetValue(compression, out string name))
		{
			throw new GisException($"This GeoTIFF uses {name} compression, which SDE can't read. Save it again with LZW or Deflate compression (or none) in QGIS first.", $"{path}: compression {compression}");
		}
		if (!tiff.IsCodecConfigured((Compression)compression))
		{
			throw new GisException($"This GeoTIFF uses a compression SDE can't read (number {compression}). Save it again with LZW or Deflate compression (or none) in QGIS first.", $"{path}: compression {compression}");
		}
		int width = Int(tiff, TiffTag.IMAGEWIDTH) ?? 0;
		int height = Int(tiff, TiffTag.IMAGELENGTH) ?? 0;
		int samples = Int(tiff, TiffTag.SAMPLESPERPIXEL) ?? 1;
		int bits = Int(tiff, TiffTag.BITSPERSAMPLE) ?? 8;
		SampleFormat format = (SampleFormat)(Int(tiff, TiffTag.SAMPLEFORMAT) ?? 1);
		if (width <= 0 || height <= 0)
		{
			throw new GisException("This GeoTIFF has no cells.", path);
		}
		if (samples != 1)
		{
			throw new GisException($"This GeoTIFF has {samples} bands. SDE reads single-band grids, such as depth or elevation grids.", path);
		}
		bool supported = format switch
		{
			SampleFormat.UINT or SampleFormat.INT => bits is 8 or 16 or 32,
			SampleFormat.IEEEFP => bits is 32 or 64,
			_ => false,
		};
		if (!supported)
		{
			throw new GisException("This GeoTIFF's cells are in a number format SDE can't read. Save it as 32-bit floating point in QGIS first.", $"{path}: {bits}-bit {format}");
		}

		bool tiled = tiff.IsTiled();
		int blockWidth = tiled ? Int(tiff, TiffTag.TILEWIDTH) ?? width : width;
		int blockHeight = tiled ? Int(tiff, TiffTag.TILELENGTH) ?? height : Math.Min(height, Int(tiff, TiffTag.ROWSPERSTRIP) ?? height);

		GeoKeys keys = GeoKeys.Read(tiff);
		double[] forward = Georeference(tiff, path);
		CoordinateSystem cs = keys.CoordinateSystem(path);
		double? noData = null;
		string noDataText = Text(tiff, GeoTiffTags.GdalNoData);
		if (!string.IsNullOrWhiteSpace(noDataText))
		{
			string t = noDataText.Trim().TrimEnd('\0');
			if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double nd))
			{
				noData = nd;
			}
			else if (t.Equals("nan", StringComparison.OrdinalIgnoreCase))
			{
				noData = double.NaN;
			}
		}
		return new GeoTiffRaster(tiff, path, width, height, cs, noData, bits, format, tiled, blockWidth, blockHeight, forward, keys.PixelIsPoint);
	}

	/// <summary>The value at a WGS 84 point by bilinear interpolation of the four nearest cell centres, or null outside the grid or on no-data.</summary>
	public double? Sample(double longitude, double latitude)
	{
		(double x, double y) = CoordinateSystem.FromWgs84(longitude, latitude);
		return SampleModel(x, y);
	}

	/// <summary>
	/// The value at a point in the grid's own coordinates. Null when the point is outside the grid or its nearest cell
	/// has no data; neighbouring no-data cells are left out of the interpolation and the others reweighted.
	/// </summary>
	public double? SampleModel(double x, double y)
	{
		(double i, double j) = ModelToRaster(x, y);
		// Cell-centre coordinates: cell (c, r) has its centre at (c, r).
		double c = _pixelIsPoint ? i : i - 0.5;
		double r = _pixelIsPoint ? j : j - 0.5;
		if (c < -0.5 || r < -0.5 || c >= Width - 0.5 || r >= Height - 0.5)
		{
			return null;
		}
		int nearestC = Math.Clamp((int)Math.Floor(c + 0.5), 0, Width - 1);
		int nearestR = Math.Clamp((int)Math.Floor(r + 0.5), 0, Height - 1);
		if (ValueAt(nearestC, nearestR) == null)
		{
			return null;
		}
		int c0 = (int)Math.Floor(c);
		int r0 = (int)Math.Floor(r);
		double fx = c - c0;
		double fy = r - r0;
		double sum = 0;
		double weights = 0;
		for (int dy = 0; dy <= 1; dy++)
		{
			for (int dx = 0; dx <= 1; dx++)
			{
				double w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
				if (w <= 0)
				{
					continue;
				}
				int cc = Math.Clamp(c0 + dx, 0, Width - 1);
				int rr = Math.Clamp(r0 + dy, 0, Height - 1);
				if (ValueAt(cc, rr) is double v)
				{
					sum += w * v;
					weights += w;
				}
			}
		}
		return weights > 0 ? sum / weights : null;
	}

	/// <summary>The value of one cell (column and row from the top left), or null for no data.</summary>
	public double? ValueAt(int column, int row)
	{
		if (column < 0 || row < 0 || column >= Width || row >= Height)
		{
			return null;
		}
		int blockX = column / _blockWidth;
		int blockY = row / _blockHeight;
		int key = _tiled ? _tiff.ComputeTile(column, row, 0, 0) : _tiff.ComputeStrip(row, 0);
		double[] block = Block(key);
		double v = block[(row - blockY * _blockHeight) * _blockWidth + (column - blockX * _blockWidth)];
		if (double.IsNaN(v) || (NoData is double nd && (v == nd || (double.IsNaN(nd) && double.IsNaN(v)))))
		{
			return null;
		}
		if (NoData is double n && _format == SampleFormat.IEEEFP && BitsPerSample == 32 && (float)v == (float)n)
		{
			return null;
		}
		return v;
	}

	/// <summary>Grid coordinates (pixel corners at whole numbers) to the grid's own map coordinates.</summary>
	public (double X, double Y) RasterToModel(double i, double j) =>
		(_forward[0] * i + _forward[1] * j + _forward[2], _forward[3] * i + _forward[4] * j + _forward[5]);

	/// <summary>The grid's own map coordinates to grid coordinates (pixel corners at whole numbers).</summary>
	public (double I, double J) ModelToRaster(double x, double y) =>
		(_inverse[0] * x + _inverse[1] * y + _inverse[2], _inverse[3] * x + _inverse[4] * y + _inverse[5]);

	public void Dispose() => _tiff.Dispose();

	private double[] Block(int key)
	{
		if (_blocks.TryGetValue(key, out double[] cached))
		{
			_recent.Remove(key);
			_recent.AddFirst(key);
			return cached;
		}
		int size = _tiled ? _tiff.TileSize() : _tiff.StripSize();
		byte[] buffer = new byte[size];
		GeoTiffTags.TakeErrors();
		int read = _tiled ? _tiff.ReadEncodedTile(key, buffer, 0, size) : _tiff.ReadEncodedStrip(key, buffer, 0, size);
		if (read < 0)
		{
			throw new GisException("Part of this GeoTIFF couldn't be read. The file may be damaged.", $"{_path}: block {key}: {string.Join("; ", GeoTiffTags.TakeErrors())}");
		}
		int count = _blockWidth * _blockHeight;
		double[] values = new double[count];
		for (int k = 0; k < count && (k + 1) * _bytesPerSample <= read; k++)
		{
			int o = k * _bytesPerSample;
			values[k] = (_format, BitsPerSample) switch
			{
				(SampleFormat.UINT, 8) => buffer[o],
				(SampleFormat.INT, 8) => (sbyte)buffer[o],
				(SampleFormat.UINT, 16) => BitConverter.ToUInt16(buffer, o),
				(SampleFormat.INT, 16) => BitConverter.ToInt16(buffer, o),
				(SampleFormat.UINT, 32) => BitConverter.ToUInt32(buffer, o),
				(SampleFormat.INT, 32) => BitConverter.ToInt32(buffer, o),
				(SampleFormat.IEEEFP, 32) => BitConverter.ToSingle(buffer, o),
				_ => BitConverter.ToDouble(buffer, o),
			};
		}
		_blocks[key] = values;
		_recent.AddFirst(key);
		if (_recent.Count > MaxCachedBlocks)
		{
			_blocks.Remove(_recent.Last.Value);
			_recent.RemoveLast();
		}
		return values;
	}

	/// <summary>The affine transform (a, b, c, d, e, f) from grid to map coordinates: from ModelTransformation, or ModelTiepoint and ModelPixelScale.</summary>
	private static double[] Georeference(Tiff tiff, string path)
	{
		double[] transform = Doubles(tiff, GeoTiffTags.ModelTransformation);
		if (transform != null && transform.Length >= 16)
		{
			return new[] { transform[0], transform[1], transform[3], transform[4], transform[5], transform[7] };
		}
		double[] tie = Doubles(tiff, GeoTiffTags.ModelTiepoint);
		double[] scale = Doubles(tiff, GeoTiffTags.ModelPixelScale);
		if (tie == null || tie.Length < 6 || scale == null || scale.Length < 2)
		{
			throw new GisException("This TIFF has no map position (no GeoTIFF tie point and pixel size), so it can't be placed on the map.", path);
		}
		if (tie.Length > 6)
		{
			throw new GisException("This GeoTIFF is placed by several control points, which SDE can't use. Warp it to a grid in QGIS first.", path);
		}
		double i0 = tie[0], j0 = tie[1], x0 = tie[3], y0 = tie[4];
		return new[] { scale[0], 0.0, x0 - i0 * scale[0], 0.0, -scale[1], y0 + j0 * scale[1] };
	}

	internal static int? Int(Tiff tiff, TiffTag tag)
	{
		FieldValue[] v = tiff.GetField(tag);
		return v == null || v.Length == 0 ? null : v[0].ToInt();
	}

	internal static double[] Doubles(Tiff tiff, TiffTag tag)
	{
		FieldValue[] v = tiff.GetField(tag);
		if (v == null || v.Length == 0)
		{
			return null;
		}
		return v.Length > 1 ? v[1].ToDoubleArray() : v[0].ToDoubleArray();
	}

	internal static string Text(Tiff tiff, TiffTag tag)
	{
		FieldValue[] v = tiff.GetField(tag);
		if (v == null || v.Length == 0)
		{
			return null;
		}
		return v.Length > 1 && v[1].Value is byte[] bytes ? System.Text.Encoding.ASCII.GetString(bytes) : v[0].ToString();
	}
}

/// <summary>The GeoKeys of a GeoTIFF (GeoKeyDirectoryTag with its double and ASCII parameters) and the coordinate system they name.</summary>
internal sealed class GeoKeys
{
	public const int ModelTypeKey = 1024;
	public const int RasterTypeKey = 1025;
	public const int GeographicTypeKey = 2048;
	public const int GeodeticDatumKey = 2050;
	public const int ProjectedCsTypeKey = 3072;
	public const int UserDefined = 32767;

	private readonly Dictionary<int, object> _values = new Dictionary<int, object>();

	public bool PixelIsPoint => Short(RasterTypeKey) == 2;

	public int? Short(int key) => _values.TryGetValue(key, out object v) && v is int i ? i : null;

	public string Ascii(int key) => _values.TryGetValue(key, out object v) ? v as string : null;

	public static GeoKeys Read(Tiff tiff)
	{
		GeoKeys keys = new GeoKeys();
		FieldValue[] dir = tiff.GetField(GeoTiffTags.GeoKeyDirectory);
		if (dir == null || dir.Length == 0)
		{
			return keys;
		}
		short[] raw = dir.Length > 1 ? dir[1].ToShortArray() : dir[0].ToShortArray();
		if (raw == null || raw.Length < 4)
		{
			return keys;
		}
		ushort[] d = raw.Select(s => (ushort)s).ToArray();
		double[] doubles = GeoTiffRaster.Doubles(tiff, GeoTiffTags.GeoDoubleParams) ?? Array.Empty<double>();
		string ascii = GeoTiffRaster.Text(tiff, GeoTiffTags.GeoAsciiParams) ?? "";
		int count = d[3];
		for (int k = 0; k < count && 4 + k * 4 + 3 < d.Length; k++)
		{
			int id = d[4 + k * 4];
			int location = d[4 + k * 4 + 1];
			int n = d[4 + k * 4 + 2];
			int offset = d[4 + k * 4 + 3];
			if (location == 0)
			{
				keys._values[id] = offset;
			}
			else if (location == (int)GeoTiffTags.GeoDoubleParams && offset < doubles.Length)
			{
				keys._values[id] = doubles[offset];
			}
			else if (location == (int)GeoTiffTags.GeoAsciiParams && offset < ascii.Length)
			{
				keys._values[id] = ascii.Substring(offset, Math.Min(n, ascii.Length - offset)).TrimEnd('|', '\0');
			}
		}
		return keys;
	}

	/// <summary>The coordinate system the keys name; throws <see cref="UnsupportedCoordinateSystemException"/> for ones SDE can't convert.</summary>
	public CoordinateSystem CoordinateSystem(string path)
	{
		int? model = Short(ModelTypeKey);
		int? projected = Short(ProjectedCsTypeKey);
		int? geographic = Short(GeographicTypeKey);
		if (model == 3)
		{
			throw new UnsupportedCoordinateSystemException("a geocentric system", $"{path}: GTModelTypeGeoKey 3.");
		}
		if (projected is int code && (model == 1 || model == null))
		{
			if (code == UserDefined)
			{
				throw new UnsupportedCoordinateSystemException("a custom projection with no EPSG code", $"{path}: ProjectedCSTypeGeoKey is user-defined.");
			}
			return Projections.CoordinateSystem.FromEpsg(code);
		}
		if (geographic is int g && g != UserDefined)
		{
			return Projections.CoordinateSystem.FromEpsg(g);
		}
		if (geographic == UserDefined)
		{
			int? datum = Short(GeodeticDatumKey);
			if (datum is 6326)
			{
				return Projections.CoordinateSystem.Wgs84;
			}
			if (datum is 6269 or 6152 or 6318 or 6759)
			{
				return Projections.CoordinateSystem.Nad83;
			}
			if (datum is 6267)
			{
				throw new UnsupportedCoordinateSystemException("NAD27", $"{path}: GeogGeodeticDatumGeoKey 6267.");
			}
			throw new UnsupportedCoordinateSystemException(datum is int dd ? $"datum EPSG:{dd}" : "an unnamed datum", $"{path}: user-defined geographic system.");
		}
		if (model == 2)
		{
			return Projections.CoordinateSystem.Wgs84;
		}
		throw new UnsupportedCoordinateSystemException("unknown", $"{path}: no GeoKeys name a coordinate system.");
	}
}
