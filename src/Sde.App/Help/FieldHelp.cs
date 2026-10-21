using System.Text.Json;
using Sde.Core.Assessments;

namespace Sde.App.Help;

/// <summary>
/// What to enter in one field and why it matters, with the page of FEMA's SDE User Manual (FEMA P-784) that covers it.
/// </summary>
/// <param name="Key">The <see cref="FieldCatalog"/> key, such as "Property.StreetName".</param>
/// <param name="Label">The field's name as the editor shows it.</param>
/// <param name="Step">The editor step it is on, such as "Location &amp; owner".</param>
/// <param name="Text">Plain-English help.</param>
/// <param name="Page">The PDF page of the manual (the first page is the cover).</param>
/// <param name="PageLabel">The page number printed on that page, such as "3-30" (null when it has none).</param>
public sealed record FieldHelpEntry(string Key, string Label, string Step, string Text, int Page, string PageLabel)
{
	/// <summary>"page 3-30", or "PDF page 51" when the page has no printed number.</summary>
	public string PageText => string.IsNullOrEmpty(PageLabel) ? $"PDF page {Page}" : $"page {PageLabel}";
}

/// <summary>
/// The field help shipped with the app (Help/field-help.json, keyed by <see cref="FieldCatalog"/> key). A test keeps
/// every catalog field covered.
/// </summary>
public static class FieldHelp
{
	private sealed class RawEntry
	{
		public string Text { get; set; }

		public int Page { get; set; }

		public string Label { get; set; }
	}

	private static readonly Lazy<IReadOnlyDictionary<string, FieldHelpEntry>> Entries = new(Load);

	/// <summary>Every field with help, in the order of the editor's steps.</summary>
	public static IReadOnlyList<FieldHelpEntry> All => Ordered.Value;

	private static readonly Lazy<IReadOnlyList<FieldHelpEntry>> Ordered = new(() =>
		FieldCatalog.All.Select(f => Get(f.Key)).Where(e => e != null).ToList());

	/// <summary>The help for a field key, or null when there is none.</summary>
	public static FieldHelpEntry Get(string key) => key != null && Entries.Value.TryGetValue(key, out FieldHelpEntry e) ? e : null;

	public static bool Has(string key) => Get(key) != null;

	private static IReadOnlyDictionary<string, FieldHelpEntry> Load()
	{
		Dictionary<string, FieldHelpEntry> result = new(StringComparer.Ordinal);
		using Stream stream = typeof(FieldHelp).Assembly.GetManifestResourceStream("field-help.json");
		if (stream == null)
		{
			return result;
		}
		Dictionary<string, RawEntry> raw = JsonSerializer.Deserialize<Dictionary<string, RawEntry>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
		foreach (KeyValuePair<string, RawEntry> pair in raw ?? new Dictionary<string, RawEntry>())
		{
			FieldInfo field = FieldCatalog.Get(pair.Key);
			if (field == null || string.IsNullOrWhiteSpace(pair.Value.Text))
			{
				continue;
			}
			result[pair.Key] = new FieldHelpEntry(pair.Key, field.Label, FieldCatalog.Steps[field.Step - 1], pair.Value.Text, pair.Value.Page, pair.Value.Label);
		}
		return result;
	}
}
