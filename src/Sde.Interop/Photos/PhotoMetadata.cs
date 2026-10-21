using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Sde.Interop.Photos;

/// <summary>What a photo's embedded (EXIF) data says: when it was taken and where.</summary>
/// <param name="TakenOn">Date and time the camera recorded, or null.</param>
/// <param name="Latitude">GPS latitude in degrees (south is negative), or null.</param>
/// <param name="Longitude">GPS longitude in degrees (west is negative), or null.</param>
public sealed record PhotoInfo(DateTime? TakenOn, double? Latitude, double? Longitude);

/// <summary>Reads the date and GPS position stored in a photo.</summary>
public static class PhotoMetadata
{
	/// <summary>
	/// Reads a photo's date and position. Photos with no, damaged or unsupported metadata return all nulls.
	/// A file that doesn't exist throws <see cref="FileNotFoundException"/>.
	/// </summary>
	public static PhotoInfo Read(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("The photo was not found.", path);
		}
		try
		{
			IReadOnlyList<MetadataExtractor.Directory> directories = ImageMetadataReader.ReadMetadata(path);

			DateTime? takenOn = null;
			ExifSubIfdDirectory sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
			if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out DateTime original))
			{
				takenOn = original;
			}
			else
			{
				ExifIfd0Directory ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
				if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out DateTime modified))
				{
					takenOn = modified;
				}
			}

			double? latitude = null;
			double? longitude = null;
			GpsDirectory gps = directories.OfType<GpsDirectory>().FirstOrDefault();
			GeoLocation? location = gps?.GetGeoLocation();
			if (location != null && !location.Value.IsZero
				&& location.Value.Latitude >= -90 && location.Value.Latitude <= 90 && location.Value.Longitude >= -180 && location.Value.Longitude <= 180)
			{
				latitude = location.Value.Latitude;
				longitude = location.Value.Longitude;
			}
			return new PhotoInfo(takenOn, latitude, longitude);
		}
		catch (Exception ex) when (ex is not FileNotFoundException)
		{
			return new PhotoInfo(null, null, null);
		}
	}
}
