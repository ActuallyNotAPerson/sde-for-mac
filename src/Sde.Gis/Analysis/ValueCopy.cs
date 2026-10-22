using System.Globalization;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Gis.Features;

namespace Sde.Gis.Analysis;

/// <summary>What copying a polygon's value would do for one structure.</summary>
public sealed class ValueCopyRow
{
	/// <summary>The structure's point (its id is the caller's own).</summary>
	public Feature Point { get; init; }

	/// <summary>How the point was matched: inside one polygon, inside several (the smallest is used), or none.</summary>
	public JoinOutcome Outcome { get; init; }

	/// <summary>The polygon whose value is copied, or null.</summary>
	public Feature Polygon { get; init; }

	/// <summary>How many polygons the point is inside.</summary>
	public int AreaCount { get; init; }

	/// <summary>The polygon's value as a person reads it ("AE", "12-345-678"), or null when there is none.</summary>
	public string NewText { get; init; }

	/// <summary>The value to give <see cref="FieldInput"/> (for FIRM zone, the id of FEMA's choice), or null when nothing can be copied.</summary>
	public string InputValue { get; init; }

	/// <summary>Why nothing can be copied for this structure, in plain English, or null.</summary>
	public string Problem { get; init; }

	public bool CanCopy => InputValue != null && Problem == null;
}

/// <summary>
/// "Copy values onto structures": for each structure point, the polygon it falls in and that polygon's value turned
/// into a value for a structure field: text as it is, numbers and dates checked, a FIRM zone code mapped to FEMA's
/// choice (AE, A1 thru A99, X and so on, see <see cref="FirmZones"/>), any other choice field matched by its name.
/// Nothing is written here; the caller shows the rows and applies the ticked ones through <see cref="FieldInput"/>.
/// </summary>
public static class ValueCopy
{
	/// <summary>The structure fields values can be copied into: text, numbers, dates and choices (not the coordinates).</summary>
	public static IReadOnlyList<FieldInfo> TargetFields { get; } = FieldCatalog.All
		.Where(f => f.Kind is FieldKind.Text or FieldKind.MultilineText or FieldKind.Number or FieldKind.Date or FieldKind.Lookup)
		.Where(f => f.Member is not (nameof(PropertyRecord.Latitude) or nameof(PropertyRecord.Longitude)))
		.ToList();

	/// <summary>
	/// Matches each point to the polygon it is in and works out the value to copy from <paramref name="sourceField"/>
	/// into <paramref name="target"/>. Points in no polygon, polygons with no value, and values that don't suit the
	/// field (a word for a number, a zone FEMA has no choice for) come back with a <see cref="ValueCopyRow.Problem"/>.
	/// </summary>
	public static List<ValueCopyRow> Plan(IEnumerable<Feature> points, IEnumerable<Feature> polygons, string sourceField, FieldInfo target, ReferenceData reference)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(reference);
		List<ValueCopyRow> rows = new List<ValueCopyRow>();
		foreach (PointJoinResult match in SpatialJoin.PointsInPolygons(points, polygons))
		{
			if (match.Polygon == null)
			{
				rows.Add(new ValueCopyRow { Point = match.Point, Outcome = match.Outcome, Problem = "It isn't inside any of the areas." });
				continue;
			}
			object raw = match.Polygon[sourceField];
			(string text, string input, string problem) = Convert(raw, target, reference);
			rows.Add(new ValueCopyRow
			{
				Point = match.Point,
				Outcome = match.Outcome,
				Polygon = match.Polygon,
				AreaCount = match.Matches.Count,
				NewText = text,
				InputValue = input,
				Problem = problem,
			});
		}
		return rows;
	}

	/// <summary>
	/// A polygon's value as text to show, as the value for <see cref="FieldInput"/>, and why it can't be used (null
	/// when it can).
	/// </summary>
	public static (string Text, string Input, string Problem) Convert(object raw, FieldInfo target, ReferenceData reference)
	{
		string text = raw == null ? null : FieldValues.Format(raw).Trim();
		if (string.IsNullOrEmpty(text))
		{
			return (null, null, "The area has no value in that field.");
		}
		switch (target.Kind)
		{
			case FieldKind.Number:
				if (raw is long or double || decimal.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out _))
				{
					decimal n = raw switch
					{
						long l => l,
						double d => (decimal)d,
						_ => decimal.Parse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture),
					};
					return (text, n.ToString(CultureInfo.InvariantCulture), null);
				}
				return (text, null, $"{target.Label} needs a number, not \"{text}\".");
			case FieldKind.Date:
				if (raw is DateTime date)
				{
					return (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
				}
				return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
					? (text, parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null)
					: (text, null, $"{target.Label} needs a date, not \"{text}\".");
			case FieldKind.Lookup:
				LookupRecord choice = target.LookupType == LookupTypes.FirmZone
					? FirmZones.Find(text, reference)
					: reference.OfType(target.LookupType).FirstOrDefault(l => string.Equals(l.LookupFieldName?.Trim(), text, StringComparison.OrdinalIgnoreCase));
				return choice == null
					? (text, null, target.LookupType == LookupTypes.FirmZone
						? $"\"{text}\" isn't a flood zone FEMA's form has a choice for."
						: $"\"{text}\" isn't one of the choices for {target.Label}.")
					: (choice.LookupFieldName, choice.LookupID.ToString(CultureInfo.InvariantCulture), null);
			default:
				return text.Length > FieldInput.MaxTextLength
					? (text, null, $"The value is too long for {target.Label}.")
					: (text, text, null);
		}
	}
}
