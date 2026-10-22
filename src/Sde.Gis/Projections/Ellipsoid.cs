namespace Sde.Gis.Projections;

/// <summary>The shape of the earth a coordinate system is drawn on: its semi-major axis and flattening.</summary>
public sealed class Ellipsoid
{
	public Ellipsoid(string name, double semiMajorAxis, double inverseFlattening)
	{
		if (!(semiMajorAxis > 0))
		{
			throw new ArgumentOutOfRangeException(nameof(semiMajorAxis), "The semi-major axis must be positive.");
		}
		Name = name;
		SemiMajorAxis = semiMajorAxis;
		InverseFlattening = inverseFlattening;
		Flattening = inverseFlattening > 0 ? 1.0 / inverseFlattening : 0.0;
		EccentricitySquared = Flattening * (2.0 - Flattening);
		Eccentricity = Math.Sqrt(EccentricitySquared);
	}

	/// <summary>GRS 1980, the ellipsoid of NAD83 and all its realizations.</summary>
	public static Ellipsoid Grs80 { get; } = new Ellipsoid("GRS 1980", 6378137.0, 298.257222101);

	/// <summary>WGS 84.</summary>
	public static Ellipsoid Wgs84 { get; } = new Ellipsoid("WGS 84", 6378137.0, 298.257223563);

	/// <summary>Clarke 1866, the ellipsoid of NAD27. Only used to check the projection formulas against EPSG's worked examples.</summary>
	public static Ellipsoid Clarke1866 { get; } = new Ellipsoid("Clarke 1866", 6378206.4, 294.978698213898);

	/// <summary>Airy 1830 (Great Britain). Only used to check the Transverse Mercator formulas against EPSG's worked example.</summary>
	public static Ellipsoid Airy1830 { get; } = new Ellipsoid("Airy 1830", 6377563.396, 299.3249646);

	/// <summary>A sphere of the given radius (flattening zero).</summary>
	public static Ellipsoid Sphere(double radius) => new Ellipsoid("Sphere", radius, 0.0);

	public string Name { get; }

	/// <summary>a, in metres.</summary>
	public double SemiMajorAxis { get; }

	/// <summary>1/f; zero for a sphere.</summary>
	public double InverseFlattening { get; }

	/// <summary>f.</summary>
	public double Flattening { get; }

	/// <summary>e².</summary>
	public double EccentricitySquared { get; }

	/// <summary>e.</summary>
	public double Eccentricity { get; }

	/// <summary>b, in metres.</summary>
	public double SemiMinorAxis => SemiMajorAxis * (1.0 - Flattening);

	/// <summary>True when this is GRS 1980 or WGS 84 (they differ by 0.1 mm in the semi-minor axis).</summary>
	public bool IsGrs80OrWgs84 => Math.Abs(SemiMajorAxis - 6378137.0) < 0.01 && Math.Abs(InverseFlattening - 298.2572) < 0.001;

	public override string ToString() => Name;
}
