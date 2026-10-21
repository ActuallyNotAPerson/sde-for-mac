using System.Globalization;
using Newtonsoft.Json;
using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>What putting one writer's changes onto someone else's newer copy of an assessment gave.</summary>
public sealed class MergeResult
{
	/// <summary>The newer copy with the changes put on it, recalculated.</summary>
	public AssessmentFile Merged { get; init; }

	/// <summary>
	/// Values both sides changed, to different values (the writer's own, as <see cref="FieldDiff"/>s from what both
	/// started with). <see cref="Merged"/> keeps the other side's value for these unless the merge was told to keep them.
	/// </summary>
	public IReadOnlyList<FieldDiff> Conflicts { get; init; }
}

/// <summary>
/// Puts the changes one writer made to an assessment onto a copy someone else saved in the meantime: the editor uses
/// it when its save is refused because another program saved the same assessment (see Sde.Data's
/// ConcurrencyException). Changes are found with <see cref="AssessmentDiff"/> against the copy both started from and
/// replayed through <see cref="FieldRules"/>, as if they had been made after the other writer's. Only values both sides
/// changed differently are conflicts. Photos never conflict: the ones added, removed, described or reordered are all
/// kept.
/// </summary>
public static class AssessmentMerge
{
	/// <summary>
	/// Replays what changed from <paramref name="baseline"/> to <paramref name="mine"/> onto a copy of
	/// <paramref name="theirs"/>. Conflicting values keep <paramref name="theirs"/> unless <paramref name="keepMine"/>.
	/// </summary>
	public static MergeResult Merge(AssessmentFile baseline, AssessmentFile mine, AssessmentFile theirs, ReferenceData reference,
		AssessmentCalculator calculator, bool keepMine = false)
	{
		List<FieldDiff> myChanges = AssessmentDiff.Compare(baseline, mine, reference).Where(d => !IsPhotoKey(d.Key)).ToList();
		Dictionary<string, string> theirValues = AssessmentDiff.Compare(baseline, theirs, reference)
			.Where(d => !IsPhotoKey(d.Key)).GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.Last().NewValue);
		List<FieldDiff> conflicts = myChanges.Where(d => theirValues.TryGetValue(d.Key, out string theirValue) && theirValue != d.NewValue).ToList();
		HashSet<string> skip = keepMine ? new HashSet<string>() : conflicts.Select(d => d.Key).ToHashSet();

		AssessmentFile merged = Copy(theirs);
		ApplyValues(merged, mine, myChanges.Select(d => d.Key).Where(k => !skip.Contains(k)).ToList(), reference, calculator);
		MergePhotos(baseline, mine, merged);
		calculator.Recalculate(merged);
		return new MergeResult { Merged = merged, Conflicts = conflicts };
	}

	/// <summary>
	/// Copies the values of <paramref name="keys"/> (AssessmentDiff keys) from <paramref name="source"/> onto
	/// <paramref name="target"/>, applying the editor's rules after each, then recalculates. Used for "Keep mine" after
	/// a conflict.
	/// </summary>
	public static void ApplyValues(AssessmentFile target, AssessmentFile source, IReadOnlyList<string> keys, ReferenceData reference, AssessmentCalculator calculator)
	{
		List<FieldInfo> fields = new List<FieldInfo>();
		foreach (string key in keys)
		{
			FieldInfo f = FieldCatalog.Get(key);
			if (f != null)
			{
				FieldCatalog.SetValue(target, f, FieldCatalog.GetValue(source, f));
				FieldRules.AfterChange(target, f.Member, calculator);
				fields.Add(f);
				continue;
			}
			ApplyRowValue(target, source, key, reference);
		}
		// A rule run for a later field may have reset an earlier one this writer set on purpose (a new structure type
		// clears the stories, which were then chosen again): their own values win.
		foreach (FieldInfo f in fields)
		{
			FieldCatalog.SetValue(target, f, FieldCatalog.GetValue(source, f));
		}
		calculator.Recalculate(target);
	}

	private static bool IsPhotoKey(string key) => key == AssessmentDiff.PhotoKey || key.StartsWith(AssessmentDiff.PhotoKey + ".", StringComparison.Ordinal);

	/// <summary>The parts of an assessment that aren't single fields: square footage, elements, adjustments, custom fields.</summary>
	private static void ApplyRowValue(AssessmentFile target, AssessmentFile source, string key, ReferenceData reference)
	{
		string[] parts = key.Split('.');
		if (key == "Property." + nameof(PropertyRecord.SquareFootage))
		{
			target.Property.SquareFootage = source.Property.SquareFootage;
		}
		else if (parts[0] == "Element" && parts.Length >= 2)
		{
			string name = key["Element.".Length..];
			ElementPercentageRecord from = source.ElementPercentages.FirstOrDefault(e => (e.ElementName ?? "") == name);
			ElementPercentageRecord to = target.ElementPercentages.FirstOrDefault(e => (e.ElementName ?? "") == name);
			if (from == null)
			{
				target.ElementPercentages.RemoveAll(e => (e.ElementName ?? "") == name);
			}
			else if (to == null)
			{
				target.ElementPercentages.Add(Copy(from));
			}
			else
			{
				to.UserDamage = from.UserDamage;
			}
		}
		else if (parts[0] == "CostAdjustment" && parts.Length == 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lookupId))
		{
			CostAdjustmentResultRecord from = source.CostAdjustments.FirstOrDefault(c => c.CostAdjustmentLookupID == lookupId);
			CostAdjustmentResultRecord to = target.CostAdjustments.FirstOrDefault(c => c.CostAdjustmentLookupID == lookupId);
			if (from == null)
			{
				target.CostAdjustments.RemoveAll(c => c.CostAdjustmentLookupID == lookupId);
				return;
			}
			if (to == null)
			{
				target.CostAdjustments.Add(Copy(from));
				return;
			}
			if (parts[2] == "Quantity")
			{
				to.Quantity = from.Quantity;
			}
			else
			{
				to.UnitCost = from.UnitCost;
			}
		}
		else if (parts[0] == "Additional" && parts.Length == 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
		{
			AdditionalAdjustmentRecord from = source.AdditionalAdjustments.FirstOrDefault(a => (a.zOrder ?? 0) == z);
			AdditionalAdjustmentRecord to = target.AdditionalAdjustments.FirstOrDefault(a => (a.zOrder ?? 0) == z);
			if (from == null)
			{
				target.AdditionalAdjustments.RemoveAll(a => (a.zOrder ?? 0) == z);
				return;
			}
			if (to == null)
			{
				target.AdditionalAdjustments.Add(Copy(from));
				return;
			}
			switch (parts[2])
			{
				case "Name":
					to.AdjustmentName = from.AdjustmentName;
					break;
				case "Quantity":
					to.Quantity = from.Quantity;
					break;
				default:
					to.UnitCost = from.UnitCost;
					break;
			}
		}
		else if (parts[0] == "Custom" && parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int order))
		{
			CustomFieldRecord field = reference.CustomFields.FirstOrDefault(c => c.zOrder == order);
			if (field == null)
			{
				return;
			}
			CustomFieldResultRecord from = source.CustomFields.FirstOrDefault(c => c.CustomFieldID == field.CustomFieldID);
			CustomFieldResultRecord to = target.CustomFields.FirstOrDefault(c => c.CustomFieldID == field.CustomFieldID);
			if (to == null && from != null)
			{
				target.CustomFields.Add(Copy(from));
			}
			else if (to != null)
			{
				to.Result = from?.Result;
			}
		}
	}

	/// <summary>Photos this writer added, removed, described, made the report photo or reordered, applied to the merged copy.</summary>
	private static void MergePhotos(AssessmentFile baseline, AssessmentFile mine, AssessmentFile merged)
	{
		Dictionary<string, AttachmentRecord> before = baseline.Attachments.Where(a => a.Active && a.FileName != null).GroupBy(a => a.FileName).ToDictionary(g => g.Key, g => g.First());
		Dictionary<string, AttachmentRecord> after = mine.Attachments.Where(a => a.Active && a.FileName != null).GroupBy(a => a.FileName).ToDictionary(g => g.Key, g => g.First());
		foreach (AttachmentRecord added in mine.Attachments.Where(a => a.Active && a.FileName != null && !before.ContainsKey(a.FileName)))
		{
			if (!merged.Attachments.Any(a => a.FileName == added.FileName))
			{
				AttachmentRecord copy = Copy(added);
				copy.AssessmentID = merged.Assessment.AssessmentID;
				merged.Attachments.Add(copy);
			}
		}
		foreach (string removed in before.Keys.Where(f => !after.ContainsKey(f)))
		{
			foreach (AttachmentRecord a in merged.Attachments.Where(a => a.FileName == removed && a.Active))
			{
				a.Active = false;
				a.DefaultImage = false;
			}
		}
		foreach ((string file, AttachmentRecord now) in after)
		{
			AttachmentRecord target = merged.Attachments.FirstOrDefault(a => a.FileName == file && a.Active);
			if (target == null)
			{
				continue;
			}
			if (before.TryGetValue(file, out AttachmentRecord was) && was.Description != now.Description)
			{
				target.Description = now.Description;
			}
			if (now.DefaultImage && (was == null || !was.DefaultImage))
			{
				foreach (AttachmentRecord a in merged.Attachments)
				{
					a.DefaultImage = ReferenceEquals(a, target);
				}
			}
		}
		// The order: when this writer reordered the photos both copies had, their order wins for those photos.
		List<string> oldOrder = baseline.Attachments.Where(a => a.Active && a.FileName != null && after.ContainsKey(a.FileName)).Select(a => a.FileName).ToList();
		List<string> newOrder = mine.Attachments.Where(a => a.Active && a.FileName != null && before.ContainsKey(a.FileName)).Select(a => a.FileName).ToList();
		if (!oldOrder.SequenceEqual(newOrder))
		{
			List<AttachmentRecord> active = merged.Attachments.Where(a => a.Active).ToList();
			List<AttachmentRecord> ordered = active.OrderBy(a =>
			{
				int i = newOrder.IndexOf(a.FileName);
				return i < 0 ? int.MaxValue : i;
			}).ToList();
			List<AttachmentRecord> inactive = merged.Attachments.Where(a => !a.Active).ToList();
			merged.Attachments.Clear();
			merged.Attachments.AddRange(ordered);
			merged.Attachments.AddRange(inactive);
		}
	}

	private static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
}
