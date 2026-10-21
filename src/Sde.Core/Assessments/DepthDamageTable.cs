using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>Which buildings a depth table is for: FEMA's two structure types.</summary>
public enum DepthTableBuildingType
{
	Residential = 1,
	NonResidential = 2,
}

/// <summary>One row of a depth table: at this depth of water above the lowest floor, this % damage for each element.</summary>
public sealed class DepthDamageRow
{
	/// <summary>Feet of water above the lowest floor (FEMA's DepthOfFloodAboveFFE).</summary>
	public decimal DepthFt { get; set; }

	/// <summary>% damaged (0 to 100) by element name. An element left out (or null) has no value at this depth.</summary>
	public Dictionary<string, decimal?> Percents { get; set; } = new Dictionary<string, decimal?>(StringComparer.Ordinal);
}

/// <summary>
/// A community's own depth-damage table: for a building type, the % damage it expects for each building element at
/// each depth of water above the lowest floor. SDE ships none; a community enters its own. Suggestions from it are
/// only ever a starting point the inspector confirms.
/// </summary>
public sealed class DepthDamageTable
{
	public string Name { get; set; }

	public DepthTableBuildingType BuildingType { get; set; } = DepthTableBuildingType.Residential;

	public List<DepthDamageRow> Rows { get; set; } = new List<DepthDamageRow>();

	/// <summary>True when the table is for this structure's type (residential or non-residential).</summary>
	public bool AppliesTo(PropertyRecord property) => property?.StructureTypeID == (int)BuildingType;

	/// <summary>
	/// The element names a table for <paramref name="type"/> can give values for: every element the Damage step shows
	/// for that kind of building (for residential, site-built and manufactured houses together), in FEMA's order.
	/// </summary>
	public static IReadOnlyList<string> ElementNames(DepthTableBuildingType type, ReferenceData reference)
	{
		IEnumerable<int> ids = type == DepthTableBuildingType.Residential
			? AssessmentCalculator.ResidentialElements.Concat(AssessmentCalculator.ManufacturedElements)
			: AssessmentCalculator.NonResidentialElements;
		return ids.Distinct().Select(id => reference.LookupName(id)).Where(n => !string.IsNullOrEmpty(n)).ToList();
	}

	/// <summary>
	/// The % damage for one element at <paramref name="depthFt"/>, or null when no row gives it a value. Between two
	/// rows the value is interpolated in a straight line; below the first row or above the last it is that row's value.
	/// Rounded to two decimals, as the Damage step stores it.
	/// </summary>
	public decimal? Percent(string elementName, decimal depthFt)
	{
		List<(decimal Depth, decimal Percent)> points = Rows
			.Where(r => r.Percents != null && r.Percents.TryGetValue(elementName, out decimal? p) && p != null)
			.Select(r => (r.DepthFt, Math.Clamp(r.Percents[elementName].Value, 0m, 100m)))
			.OrderBy(p => p.Item1)
			.ToList();
		if (points.Count == 0)
		{
			return null;
		}
		if (depthFt <= points[0].Depth)
		{
			return Math.Round(points[0].Percent, 2);
		}
		if (depthFt >= points[^1].Depth)
		{
			return Math.Round(points[^1].Percent, 2);
		}
		for (int i = 1; i < points.Count; i++)
		{
			(decimal d0, decimal p0) = points[i - 1];
			(decimal d1, decimal p1) = points[i];
			if (depthFt <= d1)
			{
				decimal value = d1 == d0 ? p1 : p0 + (p1 - p0) * (depthFt - d0) / (d1 - d0);
				return Math.Round(value, 2);
			}
		}
		return Math.Round(points[^1].Percent, 2);
	}

	/// <summary>Suggested % damage by element name for the given depth (only elements the table has values for).</summary>
	public Dictionary<string, decimal> Suggest(IEnumerable<string> elementNames, decimal depthFt)
	{
		Dictionary<string, decimal> result = new Dictionary<string, decimal>(StringComparer.Ordinal);
		foreach (string name in elementNames)
		{
			if (Percent(name, depthFt) is decimal p)
			{
				result[name] = p;
			}
		}
		return result;
	}
}
