using BitMiracle.LibTiff.Classic;

namespace Sde.Gis.Raster;

/// <summary>
/// The GeoTIFF and GDAL TIFF tags (which LibTiff.NET doesn't know by itself), registered with LibTiff.NET so they can
/// be read and written, and a quiet error handler so LibTiff.NET never writes to the console.
/// </summary>
public static class GeoTiffTags
{
	public const TiffTag ModelPixelScale = (TiffTag)33550;
	public const TiffTag ModelTiepoint = (TiffTag)33922;
	public const TiffTag ModelTransformation = (TiffTag)34264;
	public const TiffTag GeoKeyDirectory = (TiffTag)34735;
	public const TiffTag GeoDoubleParams = (TiffTag)34736;
	public const TiffTag GeoAsciiParams = (TiffTag)34737;
	public const TiffTag GdalMetadata = (TiffTag)42112;
	public const TiffTag GdalNoData = (TiffTag)42113;

	private static readonly object Gate = new object();
	private static bool _registered;
	private static Tiff.TiffExtendProc _parent;

	[ThreadStatic]
	private static List<string> _errors;

	/// <summary>Registers the tags and the quiet error handler once per process. Safe to call often.</summary>
	public static void EnsureRegistered()
	{
		lock (Gate)
		{
			if (_registered)
			{
				return;
			}
			_parent = Tiff.SetTagExtender(Extend);
			Tiff.SetErrorHandler(new QuietErrorHandler());
			_registered = true;
		}
	}

	/// <summary>LibTiff.NET's errors on this thread since the last call (cleared by reading).</summary>
	internal static List<string> TakeErrors()
	{
		List<string> errors = _errors ?? new List<string>();
		_errors = null;
		return errors;
	}

	private static void Extend(Tiff tif)
	{
		TiffFieldInfo[] fields =
		{
			new TiffFieldInfo(ModelPixelScale, -1, -1, TiffType.DOUBLE, FieldBit.Custom, true, true, "ModelPixelScaleTag"),
			new TiffFieldInfo(ModelTiepoint, -1, -1, TiffType.DOUBLE, FieldBit.Custom, true, true, "ModelTiepointTag"),
			new TiffFieldInfo(ModelTransformation, -1, -1, TiffType.DOUBLE, FieldBit.Custom, true, true, "ModelTransformationTag"),
			new TiffFieldInfo(GeoKeyDirectory, -1, -1, TiffType.SHORT, FieldBit.Custom, true, true, "GeoKeyDirectoryTag"),
			new TiffFieldInfo(GeoDoubleParams, -1, -1, TiffType.DOUBLE, FieldBit.Custom, true, true, "GeoDoubleParamsTag"),
			new TiffFieldInfo(GeoAsciiParams, -1, -1, TiffType.ASCII, FieldBit.Custom, true, false, "GeoASCIIParamsTag"),
			new TiffFieldInfo(GdalMetadata, -1, -1, TiffType.ASCII, FieldBit.Custom, true, false, "GDALMetadata"),
			new TiffFieldInfo(GdalNoData, -1, -1, TiffType.ASCII, FieldBit.Custom, true, false, "GDALNoDataValue"),
		};
		tif.MergeFieldInfo(fields, fields.Length);
		_parent?.Invoke(tif);
	}

	private sealed class QuietErrorHandler : TiffErrorHandler
	{
		public override void ErrorHandler(Tiff tif, string method, string format, params object[] args) => Record(method, format, args);

		public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) => Record(method, format, args);

		public override void WarningHandler(Tiff tif, string method, string format, params object[] args)
		{
		}

		public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args)
		{
		}

		private static void Record(string method, string format, object[] args)
		{
			string message;
			try
			{
				message = args == null || args.Length == 0 ? format : string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);
			}
			catch (FormatException)
			{
				message = format;
			}
			(_errors ??= new List<string>()).Add($"{method}: {message}");
		}
	}
}
