using System.Globalization;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Data;
using Sde.Reports.Internal;

namespace Sde.Reports;

/// <summary>What goes into a submission folder.</summary>
public sealed class SubmissionOptions
{
	/// <summary>The report written for each assessment: <see cref="ReportKind.Summary"/> (one page) or <see cref="ReportKind.Detail"/>.</summary>
	public ReportKind PerAssessmentReport { get; init; } = ReportKind.Summary;

	public bool IncludePhotos { get; init; } = true;

	/// <summary>Also include assessments whose result is "Data Entry Incomplete" (left out by default).</summary>
	public bool IncludeIncomplete { get; init; }

	/// <summary>
	/// Also write a copy of each marked-up photo with its markup drawn on ("Marked-up photos"). FEMA's SDE package always
	/// keeps the original photos.
	/// </summary>
	public bool IncludeMarkedUpCopies { get; init; }

	/// <summary>The time used in the folder name and printed on the reports (default: now).</summary>
	public DateTime? GeneratedOn { get; init; }
}

/// <summary>How far <see cref="SubmissionFolder.Write"/> has got: <paramref name="Done"/> of <paramref name="Total"/> steps.</summary>
public sealed record SubmissionProgress(int Done, int Total, string Step);

public sealed class SubmissionResult
{
	/// <summary>The folder written, e.g. ".../SDE submission 2026-10-08 1430".</summary>
	public string Folder { get; init; }

	/// <summary>Assessments included (one report each).</summary>
	public int Assessments { get; init; }

	/// <summary>Incomplete assessments left out ("123 River Street (Data Entry Incomplete)").</summary>
	public List<string> Skipped { get; init; } = new();

	/// <summary>Photo files that were missing from the attachments folder (the SDE package lists them without a file).</summary>
	public IReadOnlyList<string> MissingPhotos { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One folder with everything a community sends after an event: a PDF report per assessment, the structure &amp;
/// percent damaged list and the community report, the FEMA SDE package (SDE_1: Export.json + photos, which FEMA's
/// SDE and the state can import), and results.csv (one row per assessment, for entering determinations back into
/// the system the inspections came from). Reports are named after the record ID when the assessment was imported.
/// </summary>
public static class SubmissionFolder
{
	public const string ReportsFolder = "Reports";
	public const string PercentDamagedFile = "Percent damaged list.pdf";
	public const string CommunityFile = "Community report.pdf";
	public const string ResultsFile = "results.csv";
	public const string MarkedUpFolder = "Marked-up photos";

	/// <summary>
	/// Writes a new "SDE submission &lt;date time&gt;" folder inside <paramref name="parentFolder"/>. Assessments are
	/// written in the order given. Cancelling (or a failure) removes the partly written folder.
	/// </summary>
	public static SubmissionResult Write(SdeStore store, IReadOnlyList<Guid> assessmentIds, string parentFolder, SubmissionOptions options = null,
		IProgress<SubmissionProgress> progress = null, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentException.ThrowIfNullOrEmpty(parentFolder);
		options ??= new SubmissionOptions();
		if (options.PerAssessmentReport is not (ReportKind.Summary or ReportKind.Detail))
		{
			throw new ArgumentException("The per-assessment report must be Summary or Detail.", nameof(options));
		}
		DateTime now = options.GeneratedOn ?? DateTime.Now;

		List<AssessmentFile> all = store.LoadAssessments(assessmentIds);
		List<AssessmentFile> files = new();
		List<string> skipped = new();
		foreach (AssessmentFile f in all)
		{
			if (!options.IncludeIncomplete && IsIncomplete(f))
			{
				skipped.Add($"{new AssessmentView(f, store.Reference).StreetAddress.OrIfEmpty("(no address)")} ({f.Assessment.DamageMessage ?? "Data Entry Incomplete"})");
				continue;
			}
			files.Add(f);
		}
		if (files.Count == 0)
		{
			throw new InvalidOperationException(all.Count == 0
				? "There are no assessments to put in the folder."
				: "Every assessment is still incomplete (Data Entry Incomplete). Finish them, or include incomplete assessments.");
		}

		string folder = UniqueFolder(Path.Combine(parentFolder, "SDE submission " + now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)));
		Directory.CreateDirectory(folder);
		try
		{
			int total = files.Count + 4, done = 0;
			void Step(string what)
			{
				ct.ThrowIfCancellationRequested();
				progress?.Report(new SubmissionProgress(done, total, what));
			}

			// One report per assessment.
			Dictionary<Guid, ExternalLinkRecord> links = store.GetExternalLinks().ToDictionary(l => l.AssessmentID);
			List<string> names = ReportNames(files, links, store);
			string reports = Path.Combine(folder, ReportsFolder);
			Directory.CreateDirectory(reports);
			for (int i = 0; i < files.Count; i++)
			{
				Step($"Report {i + 1} of {files.Count}: {Path.GetFileNameWithoutExtension(names[i])}");
				ReportGenerator.RenderToFile(Request(options.PerAssessmentReport, new[] { files[i] }, options, store, now), store.Reference, Path.Combine(reports, names[i]));
				done++;
			}

			// The two lists, for everything in the folder.
			Step("Structure & percent damaged list");
			ReportGenerator.RenderToFile(Request(ReportKind.PercentDamaged, files, options, store, now), store.Reference, Path.Combine(folder, PercentDamagedFile));
			done++;
			Step("Community report");
			ReportGenerator.RenderToFile(Request(ReportKind.Community, files, options, store, now), store.Reference, Path.Combine(folder, CommunityFile));
			done++;

			// The FEMA SDE package.
			Step("FEMA SDE package (SDE_1)");
			ExchangeService exchange = new ExchangeService(store);
			SdeDataset data = exchange.ReadDataset(files.Select(f => f.Assessment.AssessmentID));
			(_, IReadOnlyList<string> missing) = exchange.Export(data, folder);
			done++;

			Step("results.csv");
			ResultsCsv.Write(Path.Combine(folder, ResultsFile), files, links, names, store.Reference);
			done++;

			if (options.IncludeMarkedUpCopies)
			{
				WriteMarkedUpCopies(store, files, Path.Combine(folder, MarkedUpFolder));
			}
			progress?.Report(new SubmissionProgress(done, total, "Done"));

			return new SubmissionResult { Folder = folder, Assessments = files.Count, Skipped = skipped, MissingPhotos = missing };
		}
		catch
		{
			TryDelete(folder);
			throw;
		}
	}

	/// <summary>"Data Entry Incomplete" (or never calculated); a "damage undetermined" result counts as complete.</summary>
	public static bool IsIncomplete(AssessmentFile f) => f.Assessment.DamageMessage is null or "" or "Data Entry Incomplete";

	private static ReportRequest Request(ReportKind kind, IReadOnlyList<AssessmentFile> files, SubmissionOptions options, SdeStore store, DateTime now) => new()
	{
		Kind = kind,
		Assessments = files,
		IncludePhotos = options.IncludePhotos,
		PhotosFolder = store.AttachmentsFolder,
		GeneratedOn = now,
		PhotoMarkups = Markups(store, files),
	};

	/// <summary>The markup of these assessments' photos, by file name.</summary>
	public static Dictionary<string, string> Markups(SdeStore store, IEnumerable<AssessmentFile> files)
	{
		Dictionary<string, string> all = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (AssessmentFile f in files)
		{
			foreach (KeyValuePair<string, string> m in store.GetPhotoMarkups(f.Assessment.AssessmentID))
			{
				all[m.Key] = m.Value;
			}
		}
		return all;
	}

	/// <summary>A JPEG copy of each marked-up photo with its marks drawn on, named after the stored photo.</summary>
	private static void WriteMarkedUpCopies(SdeStore store, IReadOnlyList<AssessmentFile> files, string folder)
	{
		foreach (KeyValuePair<string, string> m in Markups(store, files))
		{
			Sde.Core.Photos.PhotoMarkup markup = Sde.Core.Photos.PhotoMarkup.Parse(m.Value);
			string photo = Path.Combine(store.AttachmentsFolder, m.Key);
			if (markup == null || markup.IsEmpty || !File.Exists(photo))
			{
				continue;
			}
			Directory.CreateDirectory(folder);
			PhotoMarkupRenderer.WriteMarkedUpCopy(photo, markup, Path.Combine(folder, Path.GetFileNameWithoutExtension(m.Key) + " (marked up).jpg"));
		}
	}

	/// <summary>
	/// "BLD-2026-00123 – 123 River Street.pdf" (the record ID when the assessment was imported), with the inspection date added
	/// when a structure has several assessments in the folder, and " (2)" for any name that's still taken.
	/// </summary>
	internal static List<string> ReportNames(IReadOnlyList<AssessmentFile> files, IReadOnlyDictionary<Guid, ExternalLinkRecord> links, SdeStore store)
	{
		HashSet<Guid> repeated = files.GroupBy(f => f.Property.PropertyID).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
		List<string> names = new();
		foreach (AssessmentFile f in files)
		{
			string address = new AssessmentView(f, store.Reference).StreetAddress.OrIfEmpty("No address");
			string name = links.TryGetValue(f.Assessment.AssessmentID, out ExternalLinkRecord link) ? $"{link.RecordId} – {address}" : address;
			if (repeated.Contains(f.Property.PropertyID))
			{
				name += f.Assessment.DateofInspection is DateTime d ? $" (inspected {d:yyyy-MM-dd})" : " (no inspection date)";
			}
			name = Sanitize(name);
			string unique = name;
			for (int n = 2; !used.Add(unique); n++)
			{
				unique = $"{name} ({n})";
			}
			names.Add(unique + ".pdf");
		}
		return names;
	}

	/// <summary>A file name that works in Finder (no "/" or ":"), at most 120 characters.</summary>
	internal static string Sanitize(string name)
	{
		string s = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(':', '-').Trim().TrimStart('.');
		return s.Length > 120 ? s[..120].TrimEnd() : s.Length == 0 ? "Report" : s;
	}

	internal static string UniqueFolder(string path)
	{
		string unique = path;
		for (int n = 2; Directory.Exists(unique) || File.Exists(unique); n++)
		{
			unique = $"{path} ({n})";
		}
		return unique;
	}

	internal static void TryDelete(string folder)
	{
		try
		{
			Directory.Delete(folder, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static string OrIfEmpty(this string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}
