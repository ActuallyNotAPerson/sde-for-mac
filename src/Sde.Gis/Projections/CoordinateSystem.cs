using System.Globalization;
using System.Text;

namespace Sde.Gis.Projections;

/// <summary>
/// A coordinate system SDE can convert to and from WGS 84 longitude and latitude: geographic (NAD83 or WGS 84) or
/// projected with one of the <see cref="ProjectionMethod"/>s. NAD83 (and its realizations: HARN, NSRS2007, 2011) and
/// WGS 84 are treated as identical, which is within 1 to 2 metres in the United States; nothing else is accepted.
/// Build one from a .prj file with <see cref="FromWkt"/> or from an EPSG code with <see cref="FromEpsg"/>.
/// </summary>
public sealed class CoordinateSystem
{
	private CoordinateSystem(string name, string datum, Ellipsoid ellipsoid, ProjectionDefinition definition, LinearUnit unit, int? epsg, string wkt)
	{
		Name = name;
		Datum = datum;
		Ellipsoid = ellipsoid;
		Definition = definition;
		Projection = definition?.Create(ellipsoid);
		Unit = definition == null ? null : unit ?? LinearUnit.Metre;
		EpsgCode = epsg;
		Wkt = wkt;
	}

	/// <summary>WGS 84 longitude and latitude (EPSG 4326), the system every layer is stored in.</summary>
	public static CoordinateSystem Wgs84 { get; } = new CoordinateSystem("WGS 84", "WGS 84", Ellipsoid.Wgs84, null, null, 4326, null);

	/// <summary>NAD83 longitude and latitude (EPSG 4269).</summary>
	public static CoordinateSystem Nad83 { get; } = new CoordinateSystem("NAD83", "NAD83", Ellipsoid.Grs80, null, null, 4269, null);

	/// <summary>WGS 84 / Pseudo-Mercator (EPSG 3857), the web map grid.</summary>
	public static CoordinateSystem WebMercator { get; } = new CoordinateSystem("WGS 84 / Pseudo-Mercator", "WGS 84", Ellipsoid.Wgs84,
		new ProjectionDefinition { Method = ProjectionMethod.WebMercator }, LinearUnit.Metre, 3857, null);

	/// <summary>A geographic (longitude and latitude) system on the named datum.</summary>
	public static CoordinateSystem Geographic(string name, string datum, Ellipsoid ellipsoid, int? epsg = null, string wkt = null) =>
		new CoordinateSystem(name, datum, ellipsoid, null, null, epsg, wkt);

	/// <summary>A projected system.</summary>
	public static CoordinateSystem Projected(string name, string datum, Ellipsoid ellipsoid, ProjectionDefinition definition, LinearUnit unit,
		int? epsg = null, string wkt = null)
	{
		ArgumentNullException.ThrowIfNull(definition);
		return new CoordinateSystem(name, datum, ellipsoid, definition, unit, epsg, wkt);
	}

	/// <summary>Reads a .prj file's text (ESRI or OGC WKT1). Throws <see cref="UnsupportedCoordinateSystemException"/> for anything SDE can't convert.</summary>
	public static CoordinateSystem FromWkt(string wkt) => PrjParser.Parse(wkt);

	/// <summary>A system by EPSG code (geographic NAD83 and WGS 84, State Plane, UTM, Web Mercator, CONUS Albers). Throws <see cref="UnsupportedCoordinateSystemException"/> otherwise.</summary>
	public static CoordinateSystem FromEpsg(int code) => Epsg.Get(code);

	/// <summary>The name given in the file or the EPSG table, such as "NAD83 / Ohio South (ftUS)".</summary>
	public string Name { get; }

	/// <summary>The datum in short form: "NAD83", "NAD83(HARN)", "NAD83(2011)", "WGS 84" and so on.</summary>
	public string Datum { get; }

	public Ellipsoid Ellipsoid { get; }

	/// <summary>The projection's parameters; null when the system is geographic.</summary>
	public ProjectionDefinition Definition { get; }

	/// <summary>The projection; null when the system is geographic.</summary>
	public MapProjection Projection { get; }

	/// <summary>The unit of eastings and northings; null when the system is geographic.</summary>
	public LinearUnit Unit { get; }

	/// <summary>True for longitude and latitude, which need no conversion.</summary>
	public bool IsGeographic => Definition == null;

	public int? EpsgCode { get; }

	/// <summary>The WKT this system was read from, or null when it was built another way.</summary>
	public string Wkt { get; }

	/// <summary>Easting and northing (in <see cref="Unit"/>), or longitude and latitude, to WGS 84 longitude and latitude.</summary>
	public (double Longitude, double Latitude) ToWgs84(double x, double y)
	{
		if (Projection == null)
		{
			return (x, y);
		}
		double m = Unit.MetresPerUnit;
		return Projection.Inverse(x * m, y * m);
	}

	/// <summary>WGS 84 longitude and latitude to easting and northing (in <see cref="Unit"/>), or longitude and latitude.</summary>
	public (double X, double Y) FromWgs84(double longitude, double latitude)
	{
		if (Projection == null)
		{
			return (longitude, latitude);
		}
		(double x, double y) = Projection.Forward(longitude, latitude);
		double m = Unit.MetresPerUnit;
		return (x / m, y / m);
	}

	/// <summary>
	/// An ESRI-style WKT for a .prj file: the original text when this system was read from one, otherwise one written
	/// from the parameters (the form ArcGIS and QGIS read).
	/// </summary>
	public string ToEsriWkt()
	{
		if (!string.IsNullOrWhiteSpace(Wkt))
		{
			return Wkt;
		}
		string geog = EsriGeographic(Datum, Ellipsoid);
		if (Definition == null)
		{
			return geog;
		}
		ProjectionDefinition d = Definition;
		double m = Unit.MetresPerUnit;
		StringBuilder s = new StringBuilder();
		s.Append("PROJCS[\"").Append(EsriName(Name)).Append("\",").Append(geog).Append(',');
		void P(string n, double v) => s.Append("PARAMETER[\"").Append(n).Append("\",").Append(Num(v)).Append("],");
		switch (d.Method)
		{
			case ProjectionMethod.TransverseMercator:
				s.Append("PROJECTION[\"Transverse_Mercator\"],");
				P("False_Easting", d.FalseEasting / m);
				P("False_Northing", d.FalseNorthing / m);
				P("Central_Meridian", d.CentralMeridian);
				P("Scale_Factor", d.ScaleFactor);
				P("Latitude_Of_Origin", d.LatitudeOfOrigin);
				break;
			case ProjectionMethod.LambertConformalConic2SP:
				s.Append("PROJECTION[\"Lambert_Conformal_Conic\"],");
				P("False_Easting", d.FalseEasting / m);
				P("False_Northing", d.FalseNorthing / m);
				P("Central_Meridian", d.CentralMeridian);
				P("Standard_Parallel_1", d.StandardParallel1);
				P("Standard_Parallel_2", d.StandardParallel2);
				P("Latitude_Of_Origin", d.LatitudeOfOrigin);
				break;
			case ProjectionMethod.LambertConformalConic1SP:
				s.Append("PROJECTION[\"Lambert_Conformal_Conic\"],");
				P("False_Easting", d.FalseEasting / m);
				P("False_Northing", d.FalseNorthing / m);
				P("Central_Meridian", d.CentralMeridian);
				P("Standard_Parallel_1", d.LatitudeOfOrigin);
				P("Scale_Factor", d.ScaleFactor);
				P("Latitude_Of_Origin", d.LatitudeOfOrigin);
				break;
			case ProjectionMethod.AlbersEqualArea:
				s.Append("PROJECTION[\"Albers\"],");
				P("False_Easting", d.FalseEasting / m);
				P("False_Northing", d.FalseNorthing / m);
				P("Central_Meridian", d.CentralMeridian);
				P("Standard_Parallel_1", d.StandardParallel1);
				P("Standard_Parallel_2", d.StandardParallel2);
				P("Latitude_Of_Origin", d.LatitudeOfOrigin);
				break;
			case ProjectionMethod.WebMercator:
				s.Append("PROJECTION[\"Mercator_Auxiliary_Sphere\"],");
				P("False_Easting", d.FalseEasting / m);
				P("False_Northing", d.FalseNorthing / m);
				P("Central_Meridian", d.CentralMeridian);
				P("Standard_Parallel_1", 0.0);
				P("Auxiliary_Sphere_Type", 0.0);
				break;
		}
		string unitName = ReferenceEquals(Unit, LinearUnit.UsSurveyFoot) ? "Foot_US" : ReferenceEquals(Unit, LinearUnit.InternationalFoot) ? "Foot" : m == 1.0 ? "Meter" : EsriName(Unit.Name);
		s.Append("UNIT[\"").Append(unitName).Append("\",").Append(Num(m)).Append("]]");
		return s.ToString();
	}

	public override string ToString() => EpsgCode is int code ? $"{Name} (EPSG:{code})" : Name;

	private static string EsriGeographic(string datum, Ellipsoid ellipsoid)
	{
		(string gcs, string d) = datum switch
		{
			"WGS 84" => ("GCS_WGS_1984", "D_WGS_1984"),
			"NAD83(HARN)" => ("GCS_North_American_1983_HARN", "D_North_American_1983_HARN"),
			"NAD83(2011)" => ("GCS_NAD_1983_2011", "D_NAD_1983_2011"),
			"NAD83(NSRS2007)" => ("GCS_NAD_1983_NSRS2007", "D_NAD_1983_NSRS2007"),
			_ => ("GCS_North_American_1983", "D_North_American_1983"),
		};
		string spheroid = Math.Abs(ellipsoid.InverseFlattening - 298.257223563) < 1e-9 ? "WGS_1984" : "GRS_1980";
		return $"GEOGCS[\"{gcs}\",DATUM[\"{d}\",SPHEROID[\"{spheroid}\",{Num(ellipsoid.SemiMajorAxis)},{Num(ellipsoid.InverseFlattening)}]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
	}

	private static string EsriName(string name)
	{
		StringBuilder s = new StringBuilder();
		foreach (char c in name ?? "")
		{
			if (char.IsAsciiLetterOrDigit(c))
			{
				s.Append(c);
			}
			else if (s.Length > 0 && s[^1] != '_')
			{
				s.Append('_');
			}
		}
		return s.ToString().TrimEnd('_');
	}

	private static string Num(double v)
	{
		string s = v.ToString("R", CultureInfo.InvariantCulture);
		return s.Contains('.') || s.Contains('E') ? s : s + ".0";
	}
}
