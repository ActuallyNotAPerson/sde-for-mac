using Microsoft.Data.Sqlite;
using Sde.Gis.Features;
using Sde.Gis.Projections;

namespace Sde.Gis.Formats;

/// <summary>
/// Reads feature tables from any GeoPackage (QGIS, ArcGIS Pro and GDAL write them), converting to WGS 84, and writes a
/// layer as a new GeoPackage (OGC GPKG 1.3, WGS 84, with an R-tree spatial index) that those programs open.
/// </summary>
public static class GeoPackageFormat
{
	/// <summary>The feature tables in a GeoPackage, with their geometry type, coordinate system id, count and extent.</summary>
	public static IReadOnlyList<GeoPackageTableInfo> ListLayers(string path)
	{
		using SqliteConnection c = OpenExisting(path);
		return GeoPackageDb.ListFeatureTables(c);
	}

	/// <summary>
	/// Reads one feature table (the first when <paramref name="table"/> is null), converted to WGS 84. Throws
	/// <see cref="GisException"/> when the file isn't a GeoPackage or has no such table, and
	/// <see cref="UnsupportedCoordinateSystemException"/> when its coordinate system can't be converted.
	/// </summary>
	public static FeatureLayer Read(string path, string table = null)
	{
		using SqliteConnection c = OpenExisting(path);
		List<GeoPackageTableInfo> tables = GeoPackageDb.ListFeatureTables(c);
		GeoPackageTableInfo info = table == null
			? tables.FirstOrDefault()
			: tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
		if (info == null)
		{
			throw new GisException(table == null ? "This GeoPackage has no map layers in it." : $"This GeoPackage has no layer called {table}.",
				$"{path}: feature tables {string.Join(", ", tables.Select(t => t.Name))}.");
		}
		CoordinateSystem cs = GeoPackageDb.CoordinateSystemOf(c, info.SrsId);
		FeatureLayer layer = GeoPackageDb.ReadFeatures(c, null, info, cs);
		if (info.SrsId == 0)
		{
			layer.Warnings.Add("This layer's coordinate system is marked undefined, so SDE has read it as WGS 84 longitude and latitude.");
		}
		return layer;
	}

	/// <summary>
	/// Writes a layer to a new GeoPackage file (replacing any file there), as one feature table in WGS 84 with a spatial
	/// index. The file is written beside the target and moved into place, so a failure never leaves half a file.
	/// </summary>
	public static void Write(string path, FeatureLayer layer, string tableName = null, string description = null)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(layer);
		string table = LayersStore.SafeTableName(tableName ?? layer.Name);
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		string temp = path + ".tmp";
		File.Delete(temp);
		try
		{
			using (SqliteConnection c = GeoPackageDb.Open(temp, create: true))
			{
				GeoPackageDb.StampGeoPackage(c);
				using SqliteTransaction tx = c.BeginTransaction();
				GeoPackageDb.CreateSystemTables(c, tx);
				WriteFeatureTable(c, tx, table, layer, description);
				tx.Commit();
			}
			File.Move(temp, path, overwrite: true);
		}
		finally
		{
			File.Delete(temp);
		}
	}

	/// <summary>Creates a feature table for a layer, inserts its features (keeping their ids when positive), indexes it and records its extent.</summary>
	internal static string WriteFeatureTable(SqliteConnection c, SqliteTransaction tx, string table, FeatureLayer layer, string description)
	{
		string geometryType = GeoPackageDb.TypeNameFor(layer);
		List<string> columns = GeoPackageDb.ColumnNames(layer.Fields);
		GeoPackageDb.CreateFeatureTable(c, tx, table, layer.Fields, columns, geometryType, UniqueIdentifier(c, tx, layer.Name ?? table), description);
		bool keepIds = layer.Features.Count > 0 && layer.Features.All(f => f.Id > 0) && layer.Features.Select(f => f.Id).Distinct().Count() == layer.Features.Count;
		GeoPackageDb.InsertFeatures(c, tx, table, layer.Fields, columns, geometryType, layer.Features, keepIds);
		GeoPackageDb.CreateSpatialIndex(c, tx, table);
		GeoPackageDb.UpdateContents(c, tx, table);
		return geometryType;
	}

	/// <summary>gpkg_contents.identifier must be unique: the name, or the name with " (2)" and so on.</summary>
	private static string UniqueIdentifier(SqliteConnection c, SqliteTransaction tx, string name)
	{
		string candidate = name;
		for (int n = 2; GeoPackageDb.Scalar(c, tx, "SELECT 1 FROM gpkg_contents WHERE identifier = $i", ("$i", candidate)) != null; n++)
		{
			candidate = $"{name} ({n})";
		}
		return candidate;
	}

	private static SqliteConnection OpenExisting(string path)
	{
		if (!File.Exists(path))
		{
			throw new GisException("The GeoPackage file couldn't be found.", path);
		}
		SqliteConnection c;
		try
		{
			c = GeoPackageDb.Open(path, create: false, readOnly: true);
			if (!GeoPackageDb.TableExists(c, null, "gpkg_contents") || !GeoPackageDb.TableExists(c, null, "gpkg_geometry_columns"))
			{
				c.Dispose();
				throw new GisException("This file isn't a GeoPackage SDE can read (it has no gpkg_contents table).", path);
			}
		}
		catch (SqliteException ex)
		{
			throw new GisException("This file isn't a GeoPackage SDE can read.", $"{path}: {ex.Message}", ex);
		}
		return c;
	}
}
