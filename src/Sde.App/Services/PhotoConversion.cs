using System.Diagnostics;

namespace Sde.App.Services;

/// <summary>
/// iPhones save photos as HEIC, which FEMA's SDE (Windows), the PDF reports and the thumbnails can't read. Such photos
/// are converted to JPEG with macOS's built-in <c>sips</c> as they are added; other formats are kept as they are.
/// </summary>
public static class PhotoConversion
{
	private static readonly string[] Convertible = { ".heic", ".heif" };

	/// <summary>
	/// The file to store: <paramref name="path"/> itself, or a JPEG copy in <paramref name="tempFolder"/>.
	/// Throws <see cref="IOException"/> when a conversion fails.
	/// </summary>
	public static string Compatible(string path, string tempFolder)
	{
		if (!Convertible.Contains(Path.GetExtension(path).ToLowerInvariant()))
		{
			return path;
		}
		Directory.CreateDirectory(tempFolder);
		string jpeg = Path.Combine(tempFolder, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N")[..8] + ".jpg");
		ProcessStartInfo psi = new ProcessStartInfo("/usr/bin/sips") { RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (string a in new[] { "-s", "format", "jpeg", "-s", "formatOptions", "90", path, "--out", jpeg })
		{
			psi.ArgumentList.Add(a);
		}
		using Process p = Process.Start(psi) ?? throw new IOException("Couldn't start sips to convert " + Path.GetFileName(path));
		string error = p.StandardError.ReadToEnd();
		p.WaitForExit(60_000);
		if (p.ExitCode != 0 || !File.Exists(jpeg))
		{
			throw new IOException($"Couldn't convert {Path.GetFileName(path)} to JPEG. {error.Trim()}");
		}
		return jpeg;
	}
}
