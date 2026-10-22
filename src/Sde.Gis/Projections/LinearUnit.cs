namespace Sde.Gis.Projections;

/// <summary>The length unit of a projected coordinate system's eastings and northings.</summary>
public sealed class LinearUnit
{
	public LinearUnit(string name, double metresPerUnit)
	{
		if (!(metresPerUnit > 0))
		{
			throw new ArgumentOutOfRangeException(nameof(metresPerUnit), "A unit must be a positive number of metres.");
		}
		Name = name;
		MetresPerUnit = metresPerUnit;
	}

	public static LinearUnit Metre { get; } = new LinearUnit("metre", 1.0);

	/// <summary>The US survey foot, exactly 1200/3937 metres. Most State Plane data in feet uses it.</summary>
	public static LinearUnit UsSurveyFoot { get; } = new LinearUnit("US survey foot", 1200.0 / 3937.0);

	/// <summary>The international foot, exactly 0.3048 metres (used by Arizona, Michigan, Montana, Oregon and others).</summary>
	public static LinearUnit InternationalFoot { get; } = new LinearUnit("foot", 0.3048);

	public string Name { get; }

	public double MetresPerUnit { get; }

	/// <summary>
	/// The unit for a conversion factor read from a file. Factors written with fewer digits (GDAL writes the US survey
	/// foot as 0.304800609601219) are snapped to the exact unit so the false easting lands on the right millimetre.
	/// </summary>
	public static LinearUnit FromFactor(string name, double metresPerUnit)
	{
		foreach (LinearUnit known in new[] { Metre, UsSurveyFoot, InternationalFoot })
		{
			if (Math.Abs(metresPerUnit - known.MetresPerUnit) < 1e-12)
			{
				return known;
			}
		}
		return new LinearUnit(string.IsNullOrWhiteSpace(name) ? "unit" : name, metresPerUnit);
	}

	public override string ToString() => Name;
}
