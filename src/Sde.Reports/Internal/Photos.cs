using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Reports.Internal;

internal enum PhotoStatus
{
	/// <summary>The file exists and is an image (JPEG, PNG, BMP, GIF, WebP); <see cref="Photo.Path"/> holds the prepared JPEG.</summary>
	Ok,
	/// <summary>The assessment has no photo.</summary>
	None,
	/// <summary>An attachment row exists but its file is not in the photos folder.</summary>
	Missing,
	/// <summary>The file exists but cannot be read (TIFF, which SDE accepted, or a damaged file).</summary>
	Unsupported,
}

/// <summary>One photo slot on a report: a usable file, or the reason the placeholder is shown instead.</summary>
internal sealed class Photo
{
	public static readonly Photo NoPhoto = new Photo { Status = PhotoStatus.None };

	public PhotoStatus Status { get; init; }
	public string Path { get; init; }
	public int PixelWidth { get; init; }
	public int PixelHeight { get; init; }
	public string Description { get; init; }

	/// <summary>Placeholder text (FEMA's ImageNotAvailable images said "No Photo Available").</summary>
	public string PlaceholderText => Status switch
	{
		PhotoStatus.Missing => "Photo file not found",
		PhotoStatus.Unsupported => "Photo format not supported",
		_ => "No Photo Available",
	};
}

/// <summary>
/// Picks and checks the photos printed for an assessment.
/// <para>
/// Original: the summary, percent-damaged and community queries LEFT JOINed Attachment with
/// "(Attachment.DefaultImage OR Attachment.DefaultImage IS NULL)" (decomp/DAL/DAL.Services/ReportsService.cs:145,149,177,187,196),
/// so an assessment that had photos but none marked default matched no row and silently vanished from those
/// reports. The detail report took "TOP 2 … ORDER BY DefaultImage ASC, AttachmentID DESC" (line 203), which
/// relies on Jet storing True as -1. Here: the default photo if there is one, otherwise the first photo
/// (attachment order), explicitly — SQLite stores booleans as 0/1, so sorting on the column would invert it.
/// </para>
/// </summary>
internal sealed class PhotoLibrary
{
	private readonly string _folder;
	private readonly Dictionary<string, Photo> _cache = new(StringComparer.Ordinal);

	public PhotoLibrary(string folder)
	{
		_folder = folder;
	}

	/// <summary>Active attachments, the default photo first, otherwise in their existing (AttachmentID) order.</summary>
	public static IReadOnlyList<AttachmentRecord> Ordered(AssessmentFile file) =>
		(file.Attachments ?? new List<AttachmentRecord>())
			.Where(a => a != null && a.Active && !string.IsNullOrWhiteSpace(a.FileName))
			.OrderByDescending(a => a.DefaultImage)   // stable: ties keep list order
			.ToList();

	/// <summary>The photo printed on the summary, percent-damaged and community reports (and "Default Image" on the detail report).</summary>
	public Photo Primary(AssessmentFile file) => At(file, 0);

	/// <summary>The detail report's "Additional Image": the next photo after the primary one.</summary>
	public Photo Additional(AssessmentFile file) => At(file, 1);

	private Photo At(AssessmentFile file, int index)
	{
		IReadOnlyList<AttachmentRecord> list = Ordered(file);
		return index < list.Count ? Load(list[index]) : Photo.NoPhoto;
	}

	private Photo Load(AttachmentRecord a)
	{
		string path = string.IsNullOrEmpty(_folder) ? null : System.IO.Path.Combine(_folder, System.IO.Path.GetFileName(a.FileName));
		if (path == null || !File.Exists(path))
		{
			return new Photo { Status = PhotoStatus.Missing, Description = a.Description };
		}
		if (_cache.TryGetValue(path, out Photo cached))
		{
			return cached;
		}
		Photo photo;
		try
		{
			// Upright and print-sized (see PhotoPrep); MigraDoc takes the bytes as a "base64:" image source.
			photo = PhotoPrep.Prepare(path) is (byte[] jpeg, int w, int h)
				? new Photo { Status = PhotoStatus.Ok, Path = "base64:" + Convert.ToBase64String(jpeg), PixelWidth = w, PixelHeight = h, Description = a.Description }
				: new Photo { Status = PhotoStatus.Unsupported, Description = a.Description };
		}
		catch (Exception)
		{
			photo = new Photo { Status = PhotoStatus.Unsupported, Description = a.Description };
		}
		_cache[path] = photo;
		return photo;
	}
}
