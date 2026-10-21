using System.Globalization;

namespace Sde.Reports.Letters;

/// <summary>One letter to write.</summary>
public sealed class LetterItem
{
	/// <summary>The structure's street address, used in the file name ("No address" when blank).</summary>
	public string Address { get; init; }

	/// <summary>The kind of letter (<see cref="LetterKinds"/>), used in the file name and to pick the template.</summary>
	public string Kind { get; init; }

	/// <summary>The field values (usually from <see cref="LetterFields.For(Sde.Core.Assessments.AssessmentFile, Sde.Core.ReferenceData, LetterContext)"/>).</summary>
	public IReadOnlyDictionary<string, string> Fields { get; init; }

	/// <summary>The assessment the letter is about, if any (for the letter log).</summary>
	public Guid? AssessmentID { get; init; }

	/// <summary>The structure the letter is about.</summary>
	public Guid? PropertyID { get; init; }
}

/// <summary>How far <see cref="LetterBatch.Write"/> has got: <paramref name="Done"/> of <paramref name="Total"/> letters.</summary>
public sealed record LetterProgress(int Done, int Total, string Step);

/// <summary>One letter written by <see cref="LetterBatch.Write"/>.</summary>
public sealed class LetterBatchFile
{
	public LetterItem Item { get; init; }

	/// <summary>The letter file.</summary>
	public string Path { get; init; }

	/// <summary>The template it was made from.</summary>
	public string Template { get; init; }

	/// <summary>Placeholders the template has that are not fields; they were left in the letter.</summary>
	public IReadOnlyList<string> UnknownPlaceholders { get; init; } = Array.Empty<string>();
}

/// <summary>The result of <see cref="LetterBatch.Write"/>.</summary>
public sealed class LetterBatchResult
{
	/// <summary>The folder written, e.g. ".../Letters 2026-10-20 1430".</summary>
	public string Folder { get; init; }

	/// <summary>The letters, in the order of the items.</summary>
	public IReadOnlyList<LetterBatchFile> Files { get; init; } = Array.Empty<LetterBatchFile>();

	/// <summary>Every unknown placeholder found in any template, once each.</summary>
	public IReadOnlyList<string> UnknownPlaceholders => Files.SelectMany(f => f.UnknownPlaceholders).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Writes a folder of owner letters, one .docx per item: "Letters yyyy-MM-dd HHmm/&lt;address&gt; (&lt;kind&gt;).docx".
/// Cancelling (or a failure) removes the partly written folder, as <see cref="SubmissionFolder"/> does.
/// </summary>
public static class LetterBatch
{
	/// <summary>
	/// Writes the letters into a new "Letters &lt;date time&gt;" folder inside <paramref name="parentFolder"/> (" (2)" is
	/// added when that name is taken). <paramref name="templatePicker"/> returns the template path for each item.
	/// Two letters with the same address and kind get " (2)", " (3)" and so on.
	/// </summary>
	/// <param name="now">The time used in the folder name (default: now).</param>
	public static LetterBatchResult Write(IReadOnlyList<LetterItem> items, Func<LetterItem, string> templatePicker, string parentFolder,
		IProgress<LetterProgress> progress = null, CancellationToken ct = default, DateTime? now = null)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(templatePicker);
		ArgumentException.ThrowIfNullOrEmpty(parentFolder);
		if (items.Count == 0)
		{
			throw new InvalidOperationException("There are no letters to write.");
		}
		ct.ThrowIfCancellationRequested();

		DateTime at = now ?? DateTime.Now;
		string folder = SubmissionFolder.UniqueFolder(Path.Combine(parentFolder, "Letters " + at.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)));
		Directory.CreateDirectory(folder);
		try
		{
			List<LetterBatchFile> files = new();
			HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < items.Count; i++)
			{
				ct.ThrowIfCancellationRequested();
				LetterItem item = items[i] ?? throw new ArgumentException("A letter item is missing.", nameof(items));
				string name = FileName(item, used);
				progress?.Report(new LetterProgress(i, items.Count, $"Letter {i + 1} of {items.Count}: {System.IO.Path.GetFileNameWithoutExtension(name)}"));

				string template = templatePicker(item);
				if (string.IsNullOrEmpty(template))
				{
					throw new InvalidOperationException($"There is no letter template for \"{Describe(item)}\".");
				}
				LetterMergeResult merged = LetterMerge.Merge(template, item.Fields, System.IO.Path.Combine(folder, name));
				files.Add(new LetterBatchFile { Item = item, Path = merged.OutputPath, Template = template, UnknownPlaceholders = merged.UnknownPlaceholders });
			}
			ct.ThrowIfCancellationRequested();
			progress?.Report(new LetterProgress(items.Count, items.Count, "Done"));
			return new LetterBatchResult { Folder = folder, Files = files };
		}
		catch
		{
			SubmissionFolder.TryDelete(folder);
			throw;
		}
	}

	/// <summary>"&lt;address&gt; (&lt;kind&gt;).docx", made safe for Finder, with " (2)" for a name already used in this batch.</summary>
	internal static string FileName(LetterItem item, HashSet<string> used)
	{
		string name = SubmissionFolder.Sanitize(Describe(item));
		string unique = name;
		for (int n = 2; !used.Add(unique); n++)
		{
			unique = $"{name} ({n})";
		}
		return unique + ".docx";
	}

	private static string Describe(LetterItem item)
	{
		string address = string.IsNullOrWhiteSpace(item.Address) ? "No address" : item.Address.Trim();
		return string.IsNullOrWhiteSpace(item.Kind) ? address : $"{address} ({item.Kind.Trim()})";
	}
}
