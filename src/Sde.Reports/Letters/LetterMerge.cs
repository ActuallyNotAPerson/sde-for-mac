using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Sde.Reports.Letters;

/// <summary>The result of <see cref="LetterMerge.Merge"/>.</summary>
public sealed class LetterMergeResult
{
	/// <summary>The letter written.</summary>
	public string OutputPath { get; init; }

	/// <summary>Field names that were filled in, in the order first found.</summary>
	public IReadOnlyList<string> FieldsUsed { get; init; } = Array.Empty<string>();

	/// <summary>
	/// Placeholders with no matching field, as written in the template (without the « »), in the order first found.
	/// They are left in the letter as they were so the user can see and fix them.
	/// </summary>
	public IReadOnlyList<string> UnknownPlaceholders { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Fills in a Word (.docx) letter template. Placeholders are field names in guillemets, e.g. «OwnerName» (type them
/// in Word with Option-\ and Option-Shift-\ on a Mac). They are replaced in the body, tables, headers, footers,
/// footnotes and endnotes. Field names are matched ignoring case and surrounding spaces.
/// <para>
/// Word often splits typed text into several runs (spell-check marks, edits, formatting changes), so a placeholder
/// can be spread over runs such as "«Owner" + "Name»". Each paragraph's text is read as one string; the replacement
/// goes into the run where the placeholder starts (so it keeps that run's formatting) and the rest of the placeholder
/// is removed from the runs that follow. A value with line breaks becomes lines in that run.
/// </para>
/// </summary>
public static class LetterMerge
{
	public const char OpenMark = '«';
	public const char CloseMark = '»';

	/// <summary>
	/// Copies <paramref name="templatePath"/> (.docx, or a .dotx Word template) to <paramref name="outputPath"/> with the
	/// placeholders filled in from <paramref name="fields"/>, overwriting any file there. The output is written to a
	/// temporary file next to it first, so a failure never leaves a half-written letter.
	/// </summary>
	public static LetterMergeResult Merge(string templatePath, IReadOnlyDictionary<string, string> fields, string outputPath)
	{
		ArgumentException.ThrowIfNullOrEmpty(templatePath);
		ArgumentException.ThrowIfNullOrEmpty(outputPath);
		if (!File.Exists(templatePath))
		{
			throw new FileNotFoundException($"The letter template \"{Path.GetFileName(templatePath)}\" was not found.", templatePath);
		}
		string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		Directory.CreateDirectory(dir);
		string temp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".docx.tmp");
		File.Copy(templatePath, temp);
		try
		{
			MergeState state = new MergeState(fields);
			using (WordprocessingDocument doc = OpenForEditing(temp, templatePath))
			{
				MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidDataException($"\"{Path.GetFileName(templatePath)}\" has no document body.");
				Process(main.Document, state);
				foreach (HeaderPart h in main.HeaderParts)
				{
					Process(h.Header, state);
				}
				foreach (FooterPart f in main.FooterParts)
				{
					Process(f.Footer, state);
				}
				Process(main.FootnotesPart?.Footnotes, state);
				Process(main.EndnotesPart?.Endnotes, state);
			}
			File.Move(temp, outputPath, overwrite: true);
			return new LetterMergeResult { OutputPath = outputPath, FieldsUsed = state.Used, UnknownPlaceholders = state.Unknown };
		}
		catch
		{
			TryDelete(temp);
			throw;
		}
	}

	/// <summary>Every placeholder in a template (body, headers, footers, footnotes, endnotes), without the « », in the order found.</summary>
	public static IReadOnlyList<string> Placeholders(string templatePath)
	{
		ArgumentException.ThrowIfNullOrEmpty(templatePath);
		List<string> found = new();
		using WordprocessingDocument doc = WordprocessingDocument.Open(templatePath, false);
		MainDocumentPart main = doc.MainDocumentPart;
		if (main == null)
		{
			return found;
		}
		List<OpenXmlElement> roots = new() { main.Document };
		roots.AddRange(main.HeaderParts.Select(h => (OpenXmlElement)h.Header));
		roots.AddRange(main.FooterParts.Select(f => (OpenXmlElement)f.Footer));
		roots.Add(main.FootnotesPart?.Footnotes);
		roots.Add(main.EndnotesPart?.Endnotes);
		foreach (OpenXmlElement root in roots.Where(r => r != null))
		{
			foreach (Paragraph p in root.Descendants<Paragraph>())
			{
				string text = string.Concat(TextsOf(p).Select(t => t.Text));
				int from = 0;
				while (NextPlaceholder(text, from, out int open, out int close))
				{
					string name = text.Substring(open + 1, close - open - 1).Trim();
					if (!found.Contains(name, StringComparer.OrdinalIgnoreCase))
					{
						found.Add(name);
					}
					from = close + 1;
				}
			}
		}
		return found;
	}

	private static WordprocessingDocument OpenForEditing(string path, string originalName)
	{
		WordprocessingDocument doc = WordprocessingDocument.Open(path, true);
		if (string.Equals(Path.GetExtension(originalName), ".dotx", StringComparison.OrdinalIgnoreCase))
		{
			doc.ChangeDocumentType(WordprocessingDocumentType.Document);
		}
		return doc;
	}

	private static void Process(OpenXmlElement root, MergeState state)
	{
		if (root == null)
		{
			return;
		}
		foreach (Paragraph p in root.Descendants<Paragraph>().ToList())
		{
			if (p.InnerText.Contains(OpenMark))
			{
				ProcessParagraph(p, state);
			}
		}
	}

	/// <summary>The paragraph's own text elements in order (not those of a paragraph nested inside it, such as in a text box).</summary>
	private static List<Text> TextsOf(Paragraph p) =>
		p.Descendants<Text>().Where(t => t.Ancestors<Paragraph>().FirstOrDefault() == p).ToList();

	/// <summary>Finds the next «…» at or after <paramref name="from"/>; a « inside an unclosed one starts over from the inner «.</summary>
	private static bool NextPlaceholder(string text, int from, out int open, out int close)
	{
		open = close = -1;
		while (from < text.Length)
		{
			open = text.IndexOf(OpenMark, from);
			if (open < 0)
			{
				return false;
			}
			close = text.IndexOf(CloseMark, open + 1);
			if (close < 0)
			{
				return false;
			}
			int inner = text.IndexOf(OpenMark, open + 1);
			if (inner >= 0 && inner < close)
			{
				from = inner;
				continue;
			}
			return true;
		}
		return false;
	}

	private static void ProcessParagraph(Paragraph p, MergeState state)
	{
		int from = 0;
		while (true)
		{
			List<Text> texts = TextsOf(p);
			int[] starts = new int[texts.Count];
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < texts.Count; i++)
			{
				starts[i] = sb.Length;
				sb.Append(texts[i].Text);
			}
			string all = sb.ToString();
			if (!NextPlaceholder(all, from, out int open, out int close))
			{
				return;
			}
			string raw = all.Substring(open + 1, close - open - 1);
			if (!state.TryGet(raw, out string value))
			{
				from = close + 1;
				continue;
			}
			Replace(texts, starts, open, close + 1, value);
			from = open + value.Replace("\r", "").Replace("\n", "").Length;
		}
	}

	/// <summary>Replaces characters [<paramref name="from"/>, <paramref name="to"/>) of the paragraph text with <paramref name="value"/>, in the first run.</summary>
	private static void Replace(List<Text> texts, int[] starts, int from, int to, string value)
	{
		int first = IndexAt(texts, starts, from);
		int last = IndexAt(texts, starts, to - 1);
		Text head = texts[first];
		string prefix = head.Text[..(from - starts[first])];
		string suffix;
		if (first == last)
		{
			suffix = head.Text[(to - starts[first])..];
		}
		else
		{
			suffix = "";
			for (int j = first + 1; j < last; j++)
			{
				Clear(texts[j]);
			}
			Text tail = texts[last];
			tail.Text = tail.Text[(to - starts[last])..];
			tail.Space = SpaceProcessingModeValues.Preserve;
			if (tail.Text.Length == 0)
			{
				Clear(tail);
			}
		}

		string[] lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		head.Text = prefix + lines[0] + (lines.Length == 1 ? suffix : "");
		head.Space = SpaceProcessingModeValues.Preserve;
		OpenXmlElement after = head;
		for (int i = 1; i < lines.Length; i++)
		{
			after = after.InsertAfterSelf(new Break());
			after = after.InsertAfterSelf(new Text(lines[i] + (i == lines.Length - 1 ? suffix : "")) { Space = SpaceProcessingModeValues.Preserve });
		}
	}

	private static int IndexAt(List<Text> texts, int[] starts, int position)
	{
		for (int i = texts.Count - 1; i >= 0; i--)
		{
			if (starts[i] <= position && texts[i].Text.Length > 0)
			{
				return i;
			}
		}
		return 0;
	}

	/// <summary>Empties a text element, and removes its run when nothing else is left in it.</summary>
	private static void Clear(Text t)
	{
		t.Text = "";
		if (t.Parent is Run run && run.ChildElements.All(c => c is RunProperties || c is Text { Text.Length: 0 }))
		{
			run.Remove();
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private sealed class MergeState
	{
		private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
		private readonly List<string> _used = new();
		private readonly List<string> _unknown = new();

		public MergeState(IReadOnlyDictionary<string, string> fields)
		{
			foreach (KeyValuePair<string, string> kv in fields ?? new Dictionary<string, string>())
			{
				if (!string.IsNullOrWhiteSpace(kv.Key))
				{
					_fields[kv.Key.Trim()] = kv.Value ?? "";
				}
			}
		}

		public IReadOnlyList<string> Used => _used;

		public IReadOnlyList<string> Unknown => _unknown;

		public bool TryGet(string raw, out string value)
		{
			string name = raw.Trim();
			if (_fields.TryGetValue(name, out value))
			{
				if (!_used.Contains(name, StringComparer.OrdinalIgnoreCase))
				{
					_used.Add(name);
				}
				return true;
			}
			if (!_unknown.Contains(name, StringComparer.Ordinal))
			{
				_unknown.Add(name);
			}
			return false;
		}
	}
}
