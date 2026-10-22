using System.Text.Json;
using Sde.Core.Assessments;

namespace Sde.Mcp;

/// <summary>
/// The editor's field help (src/Sde.App/Help/field-help.json, embedded here as a linked copy), keyed by
/// <see cref="FieldCatalog"/> key, with the page of FEMA's SDE User Manual (P-784) that covers each field.
/// </summary>
internal static class FieldHelpCatalog
{
	private sealed class RawEntry
	{
		public string Text { get; set; }

		public int Page { get; set; }

		public string Label { get; set; }
	}

	private static readonly Lazy<IReadOnlyList<FieldHelpItem>> Entries = new Lazy<IReadOnlyList<FieldHelpItem>>(Load);

	/// <summary>Every field with help, in the order of the editor's steps.</summary>
	public static IReadOnlyList<FieldHelpItem> All => Entries.Value;

	public static FieldHelpItem Get(string key) => All.FirstOrDefault(e => e.Key == key);

	private static IReadOnlyList<FieldHelpItem> Load()
	{
		using Stream stream = typeof(FieldHelpCatalog).Assembly.GetManifestResourceStream("Sde.Mcp.field-help.json")
			?? throw new InvalidOperationException("The field help isn't built into Sde.Mcp.");
		Dictionary<string, RawEntry> raw = JsonSerializer.Deserialize<Dictionary<string, RawEntry>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
			?? new Dictionary<string, RawEntry>();
		List<FieldHelpItem> items = new List<FieldHelpItem>();
		foreach (FieldInfo f in FieldCatalog.All)
		{
			if (raw.TryGetValue(f.Key, out RawEntry e) && !string.IsNullOrWhiteSpace(e.Text))
			{
				string page = string.IsNullOrEmpty(e.Label) ? $"FEMA P-784, PDF page {e.Page}" : $"FEMA P-784, page {e.Label}";
				items.Add(new FieldHelpItem(f.Key, f.Label, FieldCatalog.Steps[f.Step - 1], e.Text, page));
			}
		}
		return items;
	}
}
