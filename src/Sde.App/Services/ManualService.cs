using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Sde.App.Services;

/// <summary>
/// FEMA's SDE User Manual (FEMA P-784). The PDF is not part of the source; the packaging script copies it into the app
/// bundle when a copy is supplied. Preview ignores "#page=" links, so opening the manual at a page means writing a small
/// PDF of that page and the next one and opening that.
/// </summary>
public sealed class ManualService(IDialogs dialogs)
{
	public const string FileName = "SDE-User-Manual.pdf";

	/// <summary>Points at a copy of the manual to use instead (development, testing, or a copy kept elsewhere).</summary>
	public const string EnvironmentVariable = "SDE_MANUAL_PDF";

	/// <summary>Where FEMA publishes the Substantial Damage Estimator and its manual.</summary>
	public const string PublishedUrl = "https://www.fema.gov/emergency-managers/risk-management/building-science/substantial-damage-estimator-tool";

	/// <summary>
	/// The manual to open: the file named by SDE_MANUAL_PDF, the copy inside the app (Contents/Resources), or, when
	/// running from the source tree, docs/reference/SDE-User-Manual.pdf. Null when none exists.
	/// </summary>
	public string FindPdf()
	{
		string env = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
		{
			return env;
		}
		string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", FileName));
		if (File.Exists(bundled))
		{
			return bundled;
		}
		DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
		for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
		{
			string dev = Path.Combine(dir.FullName, "docs", "reference", FileName);
			if (File.Exists(dev))
			{
				return dev;
			}
		}
		return null;
	}

	public bool IsAvailable => FindPdf() != null;

	/// <summary>Said when the manual is not here, in place of "Open the manual".</summary>
	public static string MissingMessage =>
		"The FEMA SDE User Manual isn't included with this copy of SDE. You can read it on FEMA's website.";

	/// <summary>
	/// Writes a PDF of <paramref name="page"/> (1 is the cover) and the page after it to a temporary file and returns its
	/// path. Throws if the manual is missing or has no such page.
	/// </summary>
	public string WriteExcerpt(int page)
	{
		string pdf = FindPdf() ?? throw new FileNotFoundException(MissingMessage);
		using PdfDocument source = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
		if (page < 1 || page > source.PageCount)
		{
			throw new ArgumentOutOfRangeException(nameof(page), $"The manual has {source.PageCount} pages.");
		}
		using PdfDocument excerpt = new PdfDocument();
		excerpt.Info.Title = $"SDE User Manual, PDF page {page}";
		excerpt.AddPage(source.Pages[page - 1]);
		if (page < source.PageCount)
		{
			excerpt.AddPage(source.Pages[page]);
		}
		string folder = Path.Combine(Path.GetTempPath(), "SDE-Manual");
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, $"SDE User Manual page {page}.pdf");
		excerpt.Save(path);
		return path;
	}

	/// <summary>
	/// Opens the manual at a page (a two-page excerpt). Returns false when the manual isn't here. If the excerpt can't be
	/// made, the whole manual is opened instead.
	/// </summary>
	public bool OpenPage(int page)
	{
		if (!IsAvailable)
		{
			return false;
		}
		try
		{
			dialogs.Open(WriteExcerpt(page));
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or PdfReaderException or NotImplementedException)
		{
			AppLog.Write("Couldn't make a manual excerpt; opening the whole manual", ex);
			dialogs.Open(FindPdf());
		}
		return true;
	}

	/// <summary>Opens the whole manual. Returns false when it isn't here.</summary>
	public bool OpenFull()
	{
		string pdf = FindPdf();
		if (pdf == null)
		{
			return false;
		}
		dialogs.Open(pdf);
		return true;
	}

	/// <summary>Opens FEMA's page for the tool in the browser.</summary>
	public void OpenPublishedPage() => dialogs.Open(PublishedUrl);
}
