using System.Globalization;
using System.Text.RegularExpressions;
using Sde.Core;
using Sde.Core.Records;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>Source text the import couldn't turn into an SDE value; it needs a translation in the profile.</summary>
/// <param name="Choices">FEMA's choices for the field, or null when a number is expected.</param>
public sealed record SourceUnmatched(string FieldKey, string FieldLabel, string Text, int Count, IReadOnlyList<string> Choices);

/// <summary>
/// Turns source text into SDE values. Every value is first <em>resolved</em> to a canonical text (a FEMA choice's
/// name, an invariant number, an ISO date…); the resolved text is what the import compares against the last import
/// and what it stores in the link, so re-importing an unchanged value is recognised however the source formats it.
/// </summary>
public static class SourceValues
{
	public const string Residential = "Residential";
	public const string NonResidential = "Non-Residential";

	/// <summary>
	/// The canonical text for a source value, after the field's translations. Null when <paramref name="text"/> is
	/// blank; false when it can't be used (not one of FEMA's choices, not a number…).
	/// </summary>
	public static bool TryResolve(SourceFieldInfo field, string text, SourceFieldMap map, ReferenceData reference, out string resolved)
	{
		resolved = null;
		string value = Text.Clean(text);
		if (value == null)
		{
			return true;
		}
		if (map?.Translate != null && map.Translate.TryGetValue(value, out string translated) && Text.Clean(translated) is string t)
		{
			value = t;
		}
		switch (field.Kind)
		{
			case SourceFieldKind.Text:
				resolved = value;
				return true;
			case SourceFieldKind.Property when field.LookupType == 0:
				resolved = value;
				return true;
			case SourceFieldKind.Property:
			case SourceFieldKind.Lookup:
				resolved = MatchLookup(value, field.LookupType, reference)?.LookupFieldName;
				return resolved != null;
			case SourceFieldKind.Stories:
				if (Text.TryParseDecimal(Regex.Replace(value, @"(?i)\s*(stor(y|ies)|floors?)$", ""), out decimal stories) && stories > 0)
				{
					resolved = Number(stories);
					return true;
				}
				resolved = MatchLookup(value, LookupTypes.Stories, reference)?.LookupFieldName;
				return resolved != null;
			case SourceFieldKind.StructureType:
				resolved = StructureType(value);
				return resolved != null;
			case SourceFieldKind.YesNo:
				resolved = YesNo(value) is bool b ? (b ? "Yes" : "No") : null;
				return resolved != null;
			case SourceFieldKind.Number:
			case SourceFieldKind.Percent:
				if (Text.TryParseDecimal(value, out decimal d))
				{
					resolved = Number(d);
					return true;
				}
				return false;
			case SourceFieldKind.Date:
				if (Text.TryParseDate(value, out DateTime date))
				{
					resolved = date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
					return true;
				}
				return false;
			case SourceFieldKind.Depth:
				if (Text.TryParseMeasure(value, out decimal depth, out string unit) && unit is null or "ft" or "in")
				{
					resolved = Number(unit == "in" ? depth / 12m : depth);
					return true;
				}
				return false;
			case SourceFieldKind.Duration:
				if (Text.TryParseMeasure(value, out decimal duration, out string units) && units is null or "hours" or "days")
				{
					resolved = Number(duration) + (units == null ? "" : " " + units);
					return true;
				}
				return false;
			default:
				return false;
		}
	}

	/// <summary>
	/// A FEMA choice by name, ignoring case, spaces and punctuation ("Slab on grade" → "Slab - on - Grade"); then
	/// without the "(Standard)" note; then a condition rating by its number ("4") or words ("Average"); then Y/N for
	/// yes/no choices; then a FIRM zone written as "Zone AE".
	/// </summary>
	public static LookupRecord MatchLookup(string text, int lookupType, ReferenceData reference)
	{
		string n = SourceFields.Norm(text);
		if (n.Length == 0)
		{
			return null;
		}
		IReadOnlyList<LookupRecord> choices = reference.OfType(lookupType);
		LookupRecord hit = choices.FirstOrDefault(l => SourceFields.Norm(l.LookupFieldName) == n)
			?? choices.FirstOrDefault(l => SourceFields.Norm(Regex.Replace(l.LookupFieldName ?? "", @"\(.*?\)", "")) == n);
		if (hit != null)
		{
			return hit;
		}
		if (lookupType == LookupTypes.DepreciationRating)
		{
			Match digit = Regex.Match(text.Trim(), @"^(\d)\b");
			if (digit.Success)
			{
				return choices.FirstOrDefault(l => (l.LookupFieldName ?? "").StartsWith(digit.Groups[1].Value + " ", StringComparison.Ordinal));
			}
			return choices.FirstOrDefault(l => Regex.Replace(l.LookupFieldName ?? "", @"^\d+\s*-\s*", "") is string words
				&& (SourceFields.Norm(words) == n || SourceFields.Norm(Regex.Replace(words, @"\s*Condition$", "")) == n));
		}
		if (choices.Any(l => l.LookupFieldName is "Yes") && YesNo(text) is bool yes)
		{
			return choices.FirstOrDefault(l => l.LookupFieldName == (yes ? "Yes" : "No"));
		}
		if (lookupType == LookupTypes.FirmZone && n.StartsWith("zone", StringComparison.Ordinal))
		{
			return choices.FirstOrDefault(l => SourceFields.Norm(l.LookupFieldName) == n[4..]);
		}
		return null;
	}

	/// <summary>The choices offered for a translatable field (names), or null when it takes a number.</summary>
	public static IReadOnlyList<string> Choices(SourceFieldInfo field, ReferenceData reference) => field.Kind switch
	{
		SourceFieldKind.StructureType => new[] { Residential, NonResidential },
		SourceFieldKind.YesNo => new[] { "Yes", "No" },
		SourceFieldKind.Lookup or SourceFieldKind.Stories or SourceFieldKind.Property when field.LookupType != 0 =>
			reference.OfType(field.LookupType).Select(l => l.LookupFieldName).ToList(),
		_ => null,
	};

	/// <summary>
	/// The distinct mapped values (and invalid constants) that can't be used as they are, with how many rows have each,
	/// in field order. Translating them in the profile makes them importable.
	/// </summary>
	public static List<SourceUnmatched> Unmatched(SourceShaped shaped, SourceProfile profile, ReferenceData reference)
	{
		List<SourceUnmatched> result = new();
		SourceTable table = shaped.Table;
		foreach (SourceFieldInfo field in SourceFields.All.Where(f => f.Translatable))
		{
			SourceFieldMap map = profile[field.Key];
			if (map == null)
			{
				continue;
			}
			Dictionary<string, (string Text, int Count)> bad = new(StringComparer.OrdinalIgnoreCase);
			int column = table.IndexOf(map.Column);
			if (column >= 0)
			{
				foreach (SourceRow row in table.Rows)
				{
					string text = Text.Clean(row[column]);
					if (text != null && !TryResolve(field, text, map, reference, out _))
					{
						bad[text] = bad.TryGetValue(text, out var x) ? (x.Text, x.Count + 1) : (text, 1);
					}
				}
			}
			if (Text.Clean(map.Constant) is string constant && !TryResolve(field, constant, map, reference, out _) && !bad.ContainsKey(constant))
			{
				bad[constant] = (constant, 0);
			}
			IReadOnlyList<string> choices = Choices(field, reference);
			result.AddRange(bad.Values.OrderByDescending(v => v.Count).ThenBy(v => v.Text, StringComparer.OrdinalIgnoreCase)
				.Select(v => new SourceUnmatched(field.Key, field.Label, v.Text, v.Count, choices)));
		}
		return result;
	}

	internal static string StructureType(string text) => SourceFields.Norm(text) switch
	{
		"residential" or "res" or "r" or "residence" or "1" => Residential,
		"nonresidential" or "nonres" or "nr" or "commercial" or "2" => NonResidential,
		_ => null,
	};

	internal static bool? YesNo(string text) => SourceFields.Norm(text) switch
	{
		"yes" or "y" or "true" or "t" or "x" or "1" or "checked" => true,
		"no" or "n" or "false" or "f" or "0" or "unchecked" => false,
		_ => null,
	};

	internal static string Number(decimal d) => d.ToString("0.############################", CultureInfo.InvariantCulture);

	internal static decimal ParseNumber(string resolved) => decimal.Parse(resolved, NumberStyles.Number, CultureInfo.InvariantCulture);
}
