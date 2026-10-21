using System.Text;

namespace Sde.Field;

/// <summary>Checks that an uploaded file really is a photo SDE can keep, by its first bytes rather than its name.</summary>
public static class PhotoUpload
{
	/// <summary>The largest photo accepted (25 MB).</summary>
	public const long MaxBytes = 25L * 1024 * 1024;

	private static readonly string[] HeifBrands = { "heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1" };

	/// <summary>
	/// The extension to store a photo under (".jpg", ".png" or ".heic"), from its first bytes, or null when it isn't a
	/// JPEG, PNG or HEIC/HEIF image.
	/// </summary>
	public static string DetectExtension(ReadOnlySpan<byte> head)
	{
		if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
		{
			return ".jpg";
		}
		if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
		{
			return ".png";
		}
		// ISO base media: a box size, then "ftyp" and the major brand.
		if (head.Length >= 12 && Encoding.ASCII.GetString(head[4..8]) == "ftyp")
		{
			string brand = Encoding.ASCII.GetString(head[8..12]).ToLowerInvariant();
			if (HeifBrands.Contains(brand))
			{
				return ".heic";
			}
		}
		return null;
	}
}
