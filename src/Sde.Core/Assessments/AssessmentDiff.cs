using System.Globalization;
using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>
/// One changed value between two versions of an assessment. <see cref="Key"/> is a <see cref="FieldCatalog"/> key, or
/// "Element.Foundation", "CostAdjustment.123.Quantity", "Additional.2.Name", "Custom.1", "Photo" for the parts of an
/// assessment that aren't single fields. Values are <see cref="FieldCatalog.Canonical"/> text.
/// </summary>
public sealed record FieldDiff(string Key, string Label, string OldValue, string NewValue);

/// <summary>
/// Compares two versions of an assessment field by field, for edit history and for knowing whether a locked
/// assessment would change. Calculated values (replacement cost, percent damaged…) are left out: they follow from
/// the inputs, which are compared.
/// </summary>
public static class AssessmentDiff
{
	public const string PhotoKey = "Photo";

	public static List<FieldDiff> Compare(AssessmentFile before, AssessmentFile after, ReferenceData reference)
	{
		List<FieldDiff> diffs = new();
		void Add(string key, string label, object oldValue, object newValue)
		{
			string o = FieldCatalog.Canonical(oldValue);
			string n = FieldCatalog.Canonical(newValue);
			if (o != n)
			{
				diffs.Add(new FieldDiff(key, label, o, n));
			}
		}

		foreach (FieldInfo f in FieldCatalog.All)
		{
			Add(f.Key, f.Label, FieldCatalog.GetValue(before, f), FieldCatalog.GetValue(after, f));
		}
		Add("Property." + nameof(PropertyRecord.SquareFootage), "Square footage", before.Property.SquareFootage, after.Property.SquareFootage);

		// % damaged per element, matched by name (the rows are re-created on every save).
		Dictionary<string, decimal> oldElements = before.ElementPercentages.GroupBy(e => e.ElementName ?? "").ToDictionary(g => g.Key, g => g.First().UserDamage);
		Dictionary<string, decimal> newElements = after.ElementPercentages.GroupBy(e => e.ElementName ?? "").ToDictionary(g => g.Key, g => g.First().UserDamage);
		foreach (string name in oldElements.Keys.Union(newElements.Keys))
		{
			Add(FieldCatalog.ElementKey(name), name + " damage %",
				oldElements.TryGetValue(name, out decimal o) ? o : null,
				newElements.TryGetValue(name, out decimal n) ? n : null);
		}

		// Cost adjustments, matched by FEMA's adjustment.
		Dictionary<int, CostAdjustmentResultRecord> oldCosts = before.CostAdjustments.GroupBy(c => c.CostAdjustmentLookupID).ToDictionary(g => g.Key, g => g.First());
		Dictionary<int, CostAdjustmentResultRecord> newCosts = after.CostAdjustments.GroupBy(c => c.CostAdjustmentLookupID).ToDictionary(g => g.Key, g => g.First());
		foreach (int id in oldCosts.Keys.Union(newCosts.Keys))
		{
			string name = reference.LookupName(id) ?? "Cost adjustment " + id.ToString(CultureInfo.InvariantCulture);
			oldCosts.TryGetValue(id, out CostAdjustmentResultRecord o);
			newCosts.TryGetValue(id, out CostAdjustmentResultRecord n);
			Add($"CostAdjustment.{id}.Quantity", name + " quantity", o?.Quantity, n?.Quantity);
			Add($"CostAdjustment.{id}.UnitCost", name + " unit cost", o?.UnitCost, n?.UnitCost);
		}

		// Additional adjustments, matched by position.
		Dictionary<int, AdditionalAdjustmentRecord> oldExtra = before.AdditionalAdjustments.GroupBy(a => a.zOrder ?? 0).ToDictionary(g => g.Key, g => g.First());
		Dictionary<int, AdditionalAdjustmentRecord> newExtra = after.AdditionalAdjustments.GroupBy(a => a.zOrder ?? 0).ToDictionary(g => g.Key, g => g.First());
		foreach (int z in oldExtra.Keys.Union(newExtra.Keys))
		{
			oldExtra.TryGetValue(z, out AdditionalAdjustmentRecord o);
			newExtra.TryGetValue(z, out AdditionalAdjustmentRecord n);
			string label = "Additional adjustment " + z.ToString(CultureInfo.InvariantCulture);
			Add($"Additional.{z}.Name", label + " name", o?.AdjustmentName, n?.AdjustmentName);
			Add($"Additional.{z}.Quantity", label + " quantity", o?.Quantity, n?.Quantity);
			Add($"Additional.{z}.UnitCost", label + " unit cost", o?.UnitCost, n?.UnitCost);
		}

		// Custom fields, labelled with their current names.
		Dictionary<Guid, string> oldCustom = before.CustomFields.GroupBy(c => c.CustomFieldID).ToDictionary(g => g.Key, g => g.First().Result);
		Dictionary<Guid, string> newCustom = after.CustomFields.GroupBy(c => c.CustomFieldID).ToDictionary(g => g.Key, g => g.First().Result);
		foreach (Guid id in oldCustom.Keys.Union(newCustom.Keys))
		{
			CustomFieldRecord cf = reference.CustomFields.FirstOrDefault(c => c.CustomFieldID == id);
			Add("Custom." + (cf?.zOrder ?? 0).ToString(CultureInfo.InvariantCulture), cf?.CustomField ?? "Custom field",
				oldCustom.GetValueOrDefault(id), newCustom.GetValueOrDefault(id));
		}

		// Photos, matched by their stored file name.
		Dictionary<string, AttachmentRecord> oldPhotos = before.Attachments.Where(a => a.Active).GroupBy(a => a.FileName ?? "").ToDictionary(g => g.Key, g => g.First());
		Dictionary<string, AttachmentRecord> newPhotos = after.Attachments.Where(a => a.Active).GroupBy(a => a.FileName ?? "").ToDictionary(g => g.Key, g => g.First());
		foreach (string file in oldPhotos.Keys.Union(newPhotos.Keys))
		{
			oldPhotos.TryGetValue(file, out AttachmentRecord o);
			newPhotos.TryGetValue(file, out AttachmentRecord n);
			if (o == null)
			{
				diffs.Add(new FieldDiff(PhotoKey, "Photo added", null, PhotoName(n)));
			}
			else if (n == null)
			{
				diffs.Add(new FieldDiff(PhotoKey, "Photo removed", PhotoName(o), null));
			}
			else
			{
				Add(PhotoKey + ".Description", "Photo description", o.Description, n.Description);
				if (n.DefaultImage && !o.DefaultImage)
				{
					diffs.Add(new FieldDiff(PhotoKey + ".Report", "Report photo", null, PhotoName(n)));
				}
			}
		}

		// The order of the photos both versions have (the order reports print them in).
		List<AttachmentRecord> oldOrder = before.Attachments.Where(a => a.Active && newPhotos.ContainsKey(a.FileName ?? "")).ToList();
		List<AttachmentRecord> newOrder = after.Attachments.Where(a => a.Active && oldPhotos.ContainsKey(a.FileName ?? "")).ToList();
		if (!oldOrder.Select(a => a.FileName).SequenceEqual(newOrder.Select(a => a.FileName)))
		{
			diffs.Add(new FieldDiff(PhotoKey + ".Order", "Photo order", string.Join(", ", oldOrder.Select(PhotoName)), string.Join(", ", newOrder.Select(PhotoName))));
		}
		return diffs;
	}

	/// <summary>Compares only a structure's fields (a save that doesn't involve an assessment, e.g. an import of coordinates).</summary>
	public static List<FieldDiff> CompareProperty(PropertyRecord before, PropertyRecord after)
	{
		List<FieldDiff> diffs = new();
		foreach (FieldInfo f in FieldCatalog.All.Where(f => f.Model == FieldModel.Property))
		{
			string o = FieldCatalog.Canonical(f.RecordProperty.GetValue(before));
			string n = FieldCatalog.Canonical(f.RecordProperty.GetValue(after));
			if (o != n)
			{
				diffs.Add(new FieldDiff(f.Key, f.Label, o, n));
			}
		}
		string os = FieldCatalog.Canonical(before.SquareFootage);
		string ns = FieldCatalog.Canonical(after.SquareFootage);
		if (os != ns)
		{
			diffs.Add(new FieldDiff("Property." + nameof(PropertyRecord.SquareFootage), "Square footage", os, ns));
		}
		return diffs;
	}

	private static string PhotoName(AttachmentRecord a) => string.IsNullOrWhiteSpace(a.Description) ? "Photo" : a.Description;
}
