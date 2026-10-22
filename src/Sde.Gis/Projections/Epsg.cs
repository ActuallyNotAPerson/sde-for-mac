using System.Globalization;
using System.Reflection;

namespace Sde.Gis.Projections;

/// <summary>
/// The EPSG codes SDE knows: geographic NAD83 and WGS 84, WGS 84 / Pseudo-Mercator, and (from EpsgUs.csv, made by
/// tools/gis/make_epsg_table.py from the EPSG Dataset) every State Plane (SPCS83) zone on NAD83, NAD83(HARN),
/// NAD83(NSRS2007) and NAD83(2011) in metres and feet, the UTM zones on those datums and WGS 84, and CONUS Albers.
/// GeoTIFFs name their coordinate system this way.
/// </summary>
public static class Epsg
{
	private static readonly Lazy<Dictionary<int, CoordinateSystem>> _table = new Lazy<Dictionary<int, CoordinateSystem>>(Load);

	/// <summary>The geographic systems by code and datum name.</summary>
	private static readonly Dictionary<int, (string Name, string Datum)> Geographic = new Dictionary<int, (string, string)>
	{
		[4326] = ("WGS 84", "WGS 84"),
		[4269] = ("NAD83", "NAD83"),
		[4152] = ("NAD83(HARN)", "NAD83(HARN)"),
		[4759] = ("NAD83(NSRS2007)", "NAD83(NSRS2007)"),
		[6318] = ("NAD83(2011)", "NAD83(2011)"),
		[4617] = ("NAD83(CSRS)", "NAD83(CSRS)"),
	};

	/// <summary>Every projected code in the table (for listing and tests).</summary>
	public static IReadOnlyCollection<int> ProjectedCodes => _table.Value.Keys;

	/// <summary>The system for an EPSG code; throws <see cref="UnsupportedCoordinateSystemException"/> when SDE doesn't know it.</summary>
	public static CoordinateSystem Get(int code)
	{
		if (TryGet(code, out CoordinateSystem cs))
		{
			return cs;
		}
		if (IsNad27(code))
		{
			throw new UnsupportedCoordinateSystemException("NAD27", $"EPSG:{code} is on NAD27.");
		}
		throw new UnsupportedCoordinateSystemException($"EPSG:{code}", $"EPSG:{code} isn't one of the State Plane, UTM, Albers, Web Mercator or geographic NAD83 and WGS 84 systems SDE knows.");
	}

	/// <summary>The system for an EPSG code, or false when SDE doesn't know it.</summary>
	public static bool TryGet(int code, out CoordinateSystem cs)
	{
		if (code == 4326)
		{
			cs = CoordinateSystem.Wgs84;
			return true;
		}
		if (code == 4269)
		{
			cs = CoordinateSystem.Nad83;
			return true;
		}
		if (Geographic.TryGetValue(code, out (string Name, string Datum) g))
		{
			cs = CoordinateSystem.Geographic(g.Name, g.Datum, Ellipsoid.Grs80, code);
			return true;
		}
		if (code is 3857 or 900913 or 102100 or 102113 or 3785)
		{
			cs = CoordinateSystem.WebMercator;
			return true;
		}
		return _table.Value.TryGetValue(code, out cs);
	}

	/// <summary>True for the NAD27 geographic, UTM and State Plane codes (4267, 267xx, 320xx).</summary>
	public static bool IsNad27(int code) => code == 4267 || (code >= 26701 && code <= 26799) || (code >= 32001 && code <= 32099);

	private static Dictionary<int, CoordinateSystem> Load()
	{
		Dictionary<int, CoordinateSystem> table = new Dictionary<int, CoordinateSystem>();
		using Stream stream = typeof(Epsg).Assembly.GetManifestResourceStream("Sde.Gis.Projections.EpsgUs.csv")
			?? throw new InvalidOperationException("The EPSG table resource is missing.");
		using StreamReader reader = new StreamReader(stream);
		reader.ReadLine();
		string line;
		while ((line = reader.ReadLine()) != null)
		{
			if (line.Length == 0)
			{
				continue;
			}
			// code,"name",datum,method,lat0,lon0,sp1,sp2,k0,fe_m,fn_m,unit (the name is quoted and has no quotes inside)
			int q1 = line.IndexOf('"');
			int q2 = line.IndexOf('"', q1 + 1);
			int code = int.Parse(line.AsSpan(0, q1 - 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
			string name = line.Substring(q1 + 1, q2 - q1 - 1);
			string[] f = line.Substring(q2 + 2).Split(',');
			string datum = f[0];
			double D(int i) => f[i].Length == 0 ? 0.0 : double.Parse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture);
			ProjectionMethod method = f[1] switch
			{
				"TM" => ProjectionMethod.TransverseMercator,
				"LCC2" => ProjectionMethod.LambertConformalConic2SP,
				"LCC1" => ProjectionMethod.LambertConformalConic1SP,
				"AEA" => ProjectionMethod.AlbersEqualArea,
				_ => ProjectionMethod.WebMercator,
			};
			ProjectionDefinition definition = new ProjectionDefinition
			{
				Method = method,
				LatitudeOfOrigin = D(2),
				CentralMeridian = D(3),
				StandardParallel1 = D(4),
				StandardParallel2 = D(5),
				ScaleFactor = D(6),
				FalseEasting = D(7),
				FalseNorthing = D(8),
			};
			LinearUnit unit = f[9] switch
			{
				"ftUS" => LinearUnit.UsSurveyFoot,
				"ft" => LinearUnit.InternationalFoot,
				_ => LinearUnit.Metre,
			};
			Ellipsoid ellipsoid = datum == "WGS 84" ? Ellipsoid.Wgs84 : Ellipsoid.Grs80;
			table[code] = CoordinateSystem.Projected(name, datum, ellipsoid, definition, unit, code);
		}
		return table;
	}
}
