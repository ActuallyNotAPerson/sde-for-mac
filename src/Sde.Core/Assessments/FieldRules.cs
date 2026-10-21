using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>A new value for one field (by <see cref="FieldInfo.Key"/>), as bulk edit, copy-from and the phone form send it.</summary>
public sealed record FieldChange(string Key, object Value);

/// <summary>
/// What else changes when a field changes: the original application's dependent resets (a new structure type clears
/// the stories and use, a cause other than flood clears the flood details, a condition rating sets depreciation…).
/// The editor and every other way of changing fields go through here, so they all behave the same.
/// </summary>
public static class FieldRules
{
	/// <summary>Applies the rules after <paramref name="member"/> (a record property name) was changed on <paramref name="file"/>.</summary>
	public static void AfterChange(AssessmentFile file, string member, AssessmentCalculator calculator)
	{
		PropertyRecord p = file.Property;
		AssessmentRecord a = file.Assessment;
		switch (member)
		{
			case nameof(PropertyRecord.StoryTypeID) when p.StructureTypeID == 2:
				// Original: changing stories resets sprinkler/conveyance (ucStructureDamage.comboBoxStory_SelectedIndexChanged).
				p.SprinklerSystem = 0;
				p.Conveyance = 0;
				break;
			case nameof(PropertyRecord.StructureTypeID):
				p.StoryTypeID = null;
				p.StructureUseID = null;
				break;
			case nameof(AssessmentRecord.CauseofDamageID):
				if (!(a.CauseofDamageID is LookupIds.CauseFlood or LookupIds.CauseFloodAndWind))
				{
					a.DurationOfFlood = null;
					a.DurationOfFloodUnitsID = null;
					a.DepthOfFlood = null;
					a.DepthOfFloodAboveFFE = null;
				}
				if (a.CauseofDamageID != LookupIds.CauseOther)
				{
					a.CauseOfDamageDescription = null;
				}
				break;
			case nameof(AssessmentRecord.DepreciationRatingID):
				calculator.ApplyDepreciationRating(file, a.DepreciationRatingID);
				break;
		}
		// Depreciation depends on stories/use for non-residential.
		if (member is nameof(PropertyRecord.StoryTypeID) or nameof(PropertyRecord.StructureUseID) or nameof(PropertyRecord.StructureTypeID)
			&& a.DepreciationRatingID is int rating && rating != LookupIds.DepreciationOther)
		{
			calculator.ApplyDepreciationRating(file, rating);
		}
	}

	/// <summary>
	/// Sets several fields in order, applying the rules after each, then recalculates. Unknown keys are skipped and
	/// returned, so a caller can say which values weren't used.
	/// </summary>
	public static IReadOnlyList<string> Apply(AssessmentFile file, IEnumerable<FieldChange> changes, AssessmentCalculator calculator)
	{
		List<string> unknown = new();
		foreach (FieldChange change in changes)
		{
			FieldInfo f = FieldCatalog.Get(change.Key);
			if (f == null)
			{
				unknown.Add(change.Key);
				continue;
			}
			FieldCatalog.SetValue(file, f, change.Value);
			AfterChange(file, f.Member, calculator);
		}
		calculator.Recalculate(file);
		return unknown;
	}
}
