namespace Sde.Gis.Projections;

/// <summary>The projection methods SDE implements.</summary>
public enum ProjectionMethod
{
	TransverseMercator,
	LambertConformalConic2SP,
	LambertConformalConic1SP,
	AlbersEqualArea,
	WebMercator,
}

/// <summary>
/// The parameters of a projection, as read from a .prj file or the EPSG table. Angles are in degrees; the false
/// easting and northing are in metres whatever unit the coordinate system uses.
/// </summary>
public sealed class ProjectionDefinition
{
	public ProjectionMethod Method { get; init; }

	/// <summary>Latitude of the natural origin (Transverse Mercator, LCC 1SP) or of the false origin (LCC 2SP, Albers).</summary>
	public double LatitudeOfOrigin { get; init; }

	/// <summary>Longitude of the natural or false origin (the central meridian).</summary>
	public double CentralMeridian { get; init; }

	public double StandardParallel1 { get; init; }

	public double StandardParallel2 { get; init; }

	public double ScaleFactor { get; init; } = 1.0;

	/// <summary>Metres.</summary>
	public double FalseEasting { get; init; }

	/// <summary>Metres.</summary>
	public double FalseNorthing { get; init; }

	/// <summary>Builds the projection on the given ellipsoid.</summary>
	public MapProjection Create(Ellipsoid ellipsoid)
	{
		switch (Method)
		{
			case ProjectionMethod.TransverseMercator:
				return new TransverseMercator(ellipsoid, LatitudeOfOrigin, CentralMeridian, ScaleFactor, FalseEasting, FalseNorthing);
			case ProjectionMethod.LambertConformalConic2SP:
				return LambertConformalConic.TwoStandardParallels(ellipsoid, LatitudeOfOrigin, CentralMeridian, StandardParallel1, StandardParallel2,
					FalseEasting, FalseNorthing, ScaleFactor);
			case ProjectionMethod.LambertConformalConic1SP:
				return LambertConformalConic.OneStandardParallel(ellipsoid, LatitudeOfOrigin, CentralMeridian, ScaleFactor, FalseEasting, FalseNorthing);
			case ProjectionMethod.AlbersEqualArea:
				return new AlbersEqualArea(ellipsoid, LatitudeOfOrigin, CentralMeridian, StandardParallel1, StandardParallel2, FalseEasting, FalseNorthing);
			case ProjectionMethod.WebMercator:
				return new WebMercator(ellipsoid, CentralMeridian, FalseEasting, FalseNorthing);
			default:
				throw new NotSupportedException(Method.ToString());
		}
	}
}
