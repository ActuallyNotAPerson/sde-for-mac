using Sde.Gis.Features;

namespace Sde.Gis.Formats;

/// <summary>Opens any layer file SDE reads, by its extension: .shp, .geojson or .json, .gpkg, .csv or .txt.</summary>
public static class GisFile
{
	/// <summary>The extensions <see cref="Read"/> accepts, for an Open dialog.</summary>
	public static IReadOnlyList<string> Extensions { get; } = new[] { ".shp", ".geojson", ".json", ".gpkg", ".csv", ".txt" };

	/// <summary>
	/// Reads a layer file in WGS 84. <paramref name="table"/> picks a GeoPackage table (the first when null);
	/// <paramref name="csv"/> says which CSV columns hold the location (found by name when null).
	/// </summary>
	public static FeatureLayer Read(string path, string table = null, CsvOptions csv = null)
	{
		string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
		return ext switch
		{
			".shp" => ShapefileFormat.Read(path),
			".geojson" or ".json" => GeoJsonFormat.Read(path),
			".gpkg" => GeoPackageFormat.Read(path, table),
			".csv" or ".txt" => CsvFormat.Read(path, csv),
			".dbf" or ".shx" or ".prj" or ".cpg" => ShapefileFormat.Read(Path.ChangeExtension(path, ".shp")),
			_ => throw new GisException("SDE can open Shapefiles, GeoJSON, GeoPackages and CSV files. Save this file as one of those in QGIS first.", path),
		};
	}
}
