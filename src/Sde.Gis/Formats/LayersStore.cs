using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Projections;

namespace Sde.Gis.Formats;

/// <summary>Changes to one feature table, applied together by <see cref="LayersStore.ApplyEdits"/>.</summary>
public sealed class FeatureEdits
{
	/// <summary>New features (their <see cref="Feature.Id"/> is ignored; the store assigns one).</summary>
	public List<Feature> Inserts { get; } = new List<Feature>();

	/// <summary>Changed features, matched by <see cref="Feature.Id"/>: the geometry and every field are written.</summary>
	public List<Feature> Updates { get; } = new List<Feature>();

	/// <summary>Ids of features to delete.</summary>
	public List<long> Deletes { get; } = new List<long>();

	public bool IsEmpty => Inserts.Count == 0 && Updates.Count == 0 && Deletes.Count == 0;
}

/// <summary>What <see cref="LayersStore.ApplyEdits"/> did.</summary>
public sealed class FeatureEditResult
{
	/// <summary>The ids given to the inserted features, in the order of <see cref="FeatureEdits.Inserts"/> (also set on those features).</summary>
	public List<long> InsertedIds { get; } = new List<long>();

	public int Updated { get; set; }

	public int Deleted { get; set; }
}

/// <summary>
/// The layers GeoPackage beside an SDE database ("&lt;db&gt;.layers.gpkg", <c>SdeDatabase.LayersPath</c>): a plain GeoPackage
/// that QGIS and ArcGIS open, holding the feature tables of layers copied into SDE. A metadata table records the SDE
/// DatabaseId; when it doesn't match the database it is opened with, <see cref="Warning"/> says so. Every method opens
/// its own connection, so one store can be used from several threads. Feature tables are always WGS 84 (EPSG 4326).
/// Writes must go through this class (or another connection with the ST_ functions registered), because the R-tree
/// triggers call those functions.
/// </summary>
public sealed class LayersStore
{
	/// <summary>
	/// SDE's own keys live in one row of the GeoPackage metadata extension (gpkg_metadata, as JSON), marked with this
	/// standard URI, so QGIS and GDAL see them as metadata rather than as a table.
	/// </summary>
	public const string MetadataStandardUri = "urn:sde-for-mac:layers";

	public const string DatabaseIdKey = "DatabaseId";

	private LayersStore(string path, Guid databaseId, Guid storedId)
	{
		Path = path;
		DatabaseId = databaseId;
		StoredDatabaseId = storedId;
	}

	/// <summary>The GeoPackage's path.</summary>
	public string Path { get; }

	/// <summary>The DatabaseId of the SDE database this store was opened for.</summary>
	public Guid DatabaseId { get; }

	/// <summary>The DatabaseId recorded inside the GeoPackage.</summary>
	public Guid StoredDatabaseId { get; private set; }

	/// <summary>True when the GeoPackage was made for a different SDE database (a copied or swapped file).</summary>
	public bool BelongsToAnotherDatabase => StoredDatabaseId != DatabaseId;

	/// <summary>A plain-English warning when <see cref="BelongsToAnotherDatabase"/>, otherwise null.</summary>
	public string Warning => BelongsToAnotherDatabase
		? "These map layers were saved with a different SDE database, so they may not match this database's structures. Check them before you edit."
		: null;

	/// <summary>
	/// Opens the layers GeoPackage, creating it (with the GeoPackage system tables and <paramref name="databaseId"/>)
	/// when it doesn't exist. A GeoPackage without SDE's metadata table is claimed for this database.
	/// </summary>
	public static LayersStore Open(string path, Guid databaseId)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		bool exists = File.Exists(path);
		if (!exists)
		{
			string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
		}
		Guid stored;
		try
		{
			using SqliteConnection c = GeoPackageDb.Open(path, create: true);
			if (!exists)
			{
				GeoPackageDb.StampGeoPackage(c);
			}
			using SqliteTransaction tx = c.BeginTransaction();
			GeoPackageDb.CreateSystemTables(c, tx);
			CreateMetadataTables(c, tx);
			ReadMetadata(c, tx).TryGetValue(DatabaseIdKey, out string text);
			if (!Guid.TryParse(text, out stored))
			{
				stored = databaseId;
				SetMetadata(c, tx, DatabaseIdKey, databaseId.ToString("D"));
			}
			tx.Commit();
		}
		catch (SqliteException ex)
		{
			throw new GisException("The map layers file couldn't be opened. It may be damaged or in use.", $"{path}: {ex.Message}", ex);
		}
		return new LayersStore(path, databaseId, stored);
	}

	/// <summary>Records this database's DatabaseId in the GeoPackage (after the person confirms the layers belong to it).</summary>
	public void ClaimForDatabase()
	{
		using SqliteConnection c = Connect();
		using SqliteTransaction tx = c.BeginTransaction();
		SetMetadata(c, tx, DatabaseIdKey, DatabaseId.ToString("D"));
		tx.Commit();
		StoredDatabaseId = DatabaseId;
	}

	/// <summary>Every feature table, with its fields, count and extent.</summary>
	public IReadOnlyList<GeoPackageTableInfo> Tables()
	{
		using SqliteConnection c = Connect();
		return GeoPackageDb.ListFeatureTables(c);
	}

	/// <summary>One feature table's details, or null when there is no such table.</summary>
	public GeoPackageTableInfo Table(string table)
	{
		using SqliteConnection c = Connect();
		return Find(c, null, table);
	}

	/// <summary>True when a feature table of that name exists.</summary>
	public bool Exists(string table) => Table(table) != null;

	/// <summary>
	/// A table name made from a layer name: lower-case letters, digits and underscores, starting with a letter, not
	/// one of GeoPackage's reserved prefixes, and not already used in this store.
	/// </summary>
	public string NewTableName(string layerName)
	{
		string baseName = SafeTableName(layerName);
		HashSet<string> taken;
		using (SqliteConnection c = Connect())
		{
			taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT name FROM sqlite_master";
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				taken.Add(r.GetString(0));
			}
		}
		string name = baseName;
		for (int n = 2; taken.Contains(name); n++)
		{
			name = baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
		}
		return name;
	}

	/// <summary>A safe table name for a layer name (see <see cref="NewTableName"/>), without checking what's taken.</summary>
	public static string SafeTableName(string layerName)
	{
		StringBuilder s = new StringBuilder();
		foreach (char ch in (layerName ?? "").Trim().ToLowerInvariant())
		{
			if (char.IsAsciiLetterOrDigit(ch))
			{
				s.Append(ch);
			}
			else if (s.Length > 0 && s[^1] != '_')
			{
				s.Append('_');
			}
		}
		string name = s.ToString().Trim('_');
		if (name.Length == 0 || !char.IsAsciiLetter(name[0]))
		{
			name = "layer_" + name;
		}
		if (name.StartsWith("gpkg_", StringComparison.Ordinal) || name.StartsWith("rtree_", StringComparison.Ordinal) || name.StartsWith("sqlite_", StringComparison.Ordinal)
			|| name.StartsWith("sde_", StringComparison.Ordinal))
		{
			name = "layer_" + name;
		}
		return name.Length > 60 ? name.Substring(0, 60).TrimEnd('_') : name;
	}

	/// <summary>
	/// Writes a layer as a feature table (WGS 84, with a spatial index), in one transaction. With
	/// <paramref name="replace"/> an existing table of that name is replaced; without it, an existing table is an error.
	/// </summary>
	public GeoPackageTableInfo WriteTable(string table, FeatureLayer layer, bool replace = false, string description = null)
	{
		ArgumentNullException.ThrowIfNull(layer);
		if (table != SafeTableName(table))
		{
			throw new ArgumentException("Use NewTableName or SafeTableName to make the table name.", nameof(table));
		}
		using SqliteConnection c = Connect();
		using SqliteTransaction tx = c.BeginTransaction();
		if (Find(c, tx, table, counts: false) != null)
		{
			if (!replace)
			{
				throw new GisException($"There's already a layer table called {table}.", table);
			}
			GeoPackageDb.DropFeatureTable(c, tx, table);
		}
		else if (GeoPackageDb.TableExists(c, tx, table))
		{
			throw new GisException($"The name {table} is already used inside the map layers file.", table);
		}
		GeoPackageFormat.WriteFeatureTable(c, tx, table, layer, description);
		tx.Commit();
		return Find(c, null, table);
	}

	/// <summary>Deletes a feature table and its index. False when there was no such table.</summary>
	public bool DeleteTable(string table)
	{
		using SqliteConnection c = Connect();
		using SqliteTransaction tx = c.BeginTransaction();
		if (Find(c, tx, table, counts: false) == null)
		{
			return false;
		}
		GeoPackageDb.DropFeatureTable(c, tx, table);
		tx.Commit();
		return true;
	}

	/// <summary>Every feature of a table.</summary>
	public FeatureLayer ReadTable(string table)
	{
		using SqliteConnection c = Connect();
		GeoPackageTableInfo info = Require(c, table);
		return GeoPackageDb.ReadFeatures(c, null, info, CoordinateSystem.Wgs84);
	}

	/// <summary>
	/// The features whose bounding box meets <paramref name="bbox"/> (WGS 84), found through the R-tree index. The
	/// boxes are compared, not the shapes; run a spatial predicate afterwards when the exact shape matters.
	/// </summary>
	public FeatureLayer ReadFeatures(string table, Envelope bbox)
	{
		ArgumentNullException.ThrowIfNull(bbox);
		using SqliteConnection c = Connect();
		GeoPackageTableInfo info = Require(c, table);
		FeatureLayer layer;
		if (GeoPackageDb.HasSpatialIndex(c, null, info.Name, info.GeometryColumn))
		{
			layer = GeoPackageDb.ReadFeatures(c, null, info, CoordinateSystem.Wgs84,
				"r.minx <= $maxx AND r.maxx >= $minx AND r.miny <= $maxy AND r.maxy >= $miny", joinRtree: true,
				("$minx", bbox.MinX), ("$maxx", bbox.MaxX), ("$miny", bbox.MinY), ("$maxy", bbox.MaxY));
			// The index stores 32-bit floats rounded outward, so drop the near misses.
			layer.Features.RemoveAll(f => f.Geometry == null || !f.Geometry.EnvelopeInternal.Intersects(bbox));
		}
		else
		{
			layer = GeoPackageDb.ReadFeatures(c, null, info, CoordinateSystem.Wgs84);
			layer.Features.RemoveAll(f => f.Geometry == null || !f.Geometry.EnvelopeInternal.Intersects(bbox));
		}
		return layer;
	}

	/// <summary>One feature by id, or null.</summary>
	public Feature ReadFeature(string table, long id)
	{
		using SqliteConnection c = Connect();
		GeoPackageTableInfo info = Require(c, table);
		FeatureLayer layer = GeoPackageDb.ReadFeatures(c, null, info, CoordinateSystem.Wgs84, $"t.{GeoPackageDb.Quote(GeoPackageDb.FidColumn)} = $id", false, ("$id", id));
		return layer.Features.FirstOrDefault();
	}

	/// <summary>
	/// Inserts, updates and deletes features in one transaction: either every change is saved or none is. Geometries
	/// (WGS 84) are fitted to the table's geometry type; values are matched to columns by field name. Throws
	/// <see cref="GisException"/> when an update or delete names a feature that isn't there or a geometry doesn't fit.
	/// </summary>
	public FeatureEditResult ApplyEdits(string table, FeatureEdits edits)
	{
		ArgumentNullException.ThrowIfNull(edits);
		FeatureEditResult result = new FeatureEditResult();
		if (edits.IsEmpty)
		{
			return result;
		}
		using SqliteConnection c = Connect();
		using SqliteTransaction tx = c.BeginTransaction();
		GeoPackageTableInfo info = Require(c, table, tx);
		(FieldList fields, List<string> columns, string fidColumn) = GeoPackageDb.ReadColumns(c, tx, info.Name, info.GeometryColumn);
		string fid = GeoPackageDb.Quote(fidColumn ?? GeoPackageDb.FidColumn);
		string t = GeoPackageDb.Quote(info.Name);

		if (edits.Deletes.Count > 0)
		{
			using SqliteCommand delete = c.CreateCommand();
			delete.Transaction = tx;
			delete.CommandText = $"DELETE FROM {t} WHERE {fid} = $id";
			SqliteParameter id = delete.Parameters.Add("$id", SqliteType.Integer);
			foreach (long featureId in edits.Deletes)
			{
				id.Value = featureId;
				if (delete.ExecuteNonQuery() == 0)
				{
					throw new GisException("A feature to delete is no longer in the layer. Reload the layer and try again.", $"{info.Name} fid {featureId}");
				}
				result.Deleted++;
			}
		}

		if (edits.Updates.Count > 0)
		{
			using SqliteCommand update = c.CreateCommand();
			update.Transaction = tx;
			StringBuilder sql = new StringBuilder($"UPDATE {t} SET {GeoPackageDb.Quote(info.GeometryColumn)} = $geom");
			for (int i = 0; i < columns.Count; i++)
			{
				sql.Append(", ").Append(GeoPackageDb.Quote(columns[i])).Append(" = $c").Append(i.ToString(CultureInfo.InvariantCulture));
			}
			sql.Append($" WHERE {fid} = $id");
			update.CommandText = sql.ToString();
			SqliteParameter id = update.Parameters.Add("$id", SqliteType.Integer);
			SqliteParameter geom = update.Parameters.Add("$geom", SqliteType.Blob);
			SqliteParameter[] values = new SqliteParameter[columns.Count];
			for (int i = 0; i < columns.Count; i++)
			{
				values[i] = update.Parameters.Add("$c" + i.ToString(CultureInfo.InvariantCulture), GeoPackageDb.SqliteTypeFor(fields[i].Type));
			}
			foreach (Feature feature in edits.Updates)
			{
				id.Value = feature.Id;
				Geometry g = GeoPackageDb.Coerce(feature.Geometry, info.GeometryType);
				geom.Value = g == null ? DBNull.Value : GeoPackageGeometry.Write(g);
				for (int i = 0; i < columns.Count; i++)
				{
					values[i].Value = GeoPackageDb.ToDb(fields[i].Type, fields[i].Convert(feature[fields[i].Name]));
				}
				if (update.ExecuteNonQuery() == 0)
				{
					throw new GisException("A feature to change is no longer in the layer. Reload the layer and try again.", $"{info.Name} fid {feature.Id}");
				}
				result.Updated++;
			}
		}

		if (edits.Inserts.Count > 0)
		{
			List<Feature> converted = edits.Inserts.Select(f =>
			{
				Feature copy = new Feature(fields, f.Geometry);
				foreach (LayerField field in fields)
				{
					copy[field.Name] = f[field.Name];
				}
				return copy;
			}).ToList();
			List<long> ids = GeoPackageDb.InsertFeatures(c, tx, info.Name, fields, columns, info.GeometryType, converted);
			result.InsertedIds.AddRange(ids);
			for (int i = 0; i < ids.Count; i++)
			{
				edits.Inserts[i].Id = ids[i];
			}
		}

		GeoPackageDb.UpdateContents(c, tx, info.Name);
		tx.Commit();
		return result;
	}

	/// <summary>A value from SDE's metadata, or null.</summary>
	public string GetMetadata(string key)
	{
		using SqliteConnection c = Connect();
		return ReadMetadata(c, null).TryGetValue(key, out string value) ? value : null;
	}

	/// <summary>Sets a value in SDE's metadata (null removes it).</summary>
	public void SetMetadata(string key, string value)
	{
		using SqliteConnection c = Connect();
		using SqliteTransaction tx = c.BeginTransaction();
		SetMetadata(c, tx, key, value);
		tx.Commit();
	}

	/// <summary>The GeoPackage metadata extension's tables (OGC GPKG 1.3 annex F.8), when missing.</summary>
	private static void CreateMetadataTables(SqliteConnection c, SqliteTransaction tx)
	{
		GeoPackageDb.Execute(c, tx, "CREATE TABLE IF NOT EXISTS gpkg_metadata (id INTEGER CONSTRAINT m_pk PRIMARY KEY ASC NOT NULL, md_scope TEXT NOT NULL DEFAULT 'dataset', md_standard_uri TEXT NOT NULL, mime_type TEXT NOT NULL DEFAULT 'text/xml', metadata TEXT NOT NULL DEFAULT '')");
		GeoPackageDb.Execute(c, tx, "CREATE TABLE IF NOT EXISTS gpkg_metadata_reference (reference_scope TEXT NOT NULL, table_name TEXT, column_name TEXT, row_id_value INTEGER, timestamp DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), md_file_id INTEGER NOT NULL, md_parent_id INTEGER, CONSTRAINT crmr_mfi_fk FOREIGN KEY (md_file_id) REFERENCES gpkg_metadata(id), CONSTRAINT crmr_mpi_fk FOREIGN KEY (md_parent_id) REFERENCES gpkg_metadata(id))");
		foreach (string table in new[] { "gpkg_metadata", "gpkg_metadata_reference" })
		{
			GeoPackageDb.Execute(c, tx, "INSERT OR IGNORE INTO gpkg_extensions (table_name, column_name, extension_name, definition, scope) VALUES ($t, NULL, 'gpkg_metadata', 'http://www.geopackage.org/spec120/#extension_metadata', 'read-write')",
				("$t", table));
		}
	}

	private static Dictionary<string, string> ReadMetadata(SqliteConnection c, SqliteTransaction tx)
	{
		string json = GeoPackageDb.TableExists(c, tx, "gpkg_metadata")
			? GeoPackageDb.Scalar(c, tx, "SELECT metadata FROM gpkg_metadata WHERE md_standard_uri = $u ORDER BY id LIMIT 1", ("$u", MetadataStandardUri)) as string
			: null;
		if (string.IsNullOrWhiteSpace(json))
		{
			return new Dictionary<string, string>(StringComparer.Ordinal);
		}
		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
		}
		catch (JsonException)
		{
			return new Dictionary<string, string>(StringComparer.Ordinal);
		}
	}

	private static void SetMetadata(SqliteConnection c, SqliteTransaction tx, string key, string value)
	{
		CreateMetadataTables(c, tx);
		Dictionary<string, string> all = ReadMetadata(c, tx);
		if (value == null)
		{
			all.Remove(key);
		}
		else
		{
			all[key] = value;
		}
		string json = JsonSerializer.Serialize(all);
		object id = GeoPackageDb.Scalar(c, tx, "SELECT id FROM gpkg_metadata WHERE md_standard_uri = $u ORDER BY id LIMIT 1", ("$u", MetadataStandardUri));
		if (id != null)
		{
			GeoPackageDb.Execute(c, tx, "UPDATE gpkg_metadata SET metadata = $m WHERE id = $i", ("$m", json), ("$i", id));
			GeoPackageDb.Execute(c, tx, "UPDATE gpkg_metadata_reference SET timestamp = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE md_file_id = $i", ("$i", id));
			return;
		}
		long newId = Convert.ToInt64(GeoPackageDb.Scalar(c, tx, "INSERT INTO gpkg_metadata (md_scope, md_standard_uri, mime_type, metadata) VALUES ('dataset', $u, 'application/json', $m) RETURNING id",
			("$u", MetadataStandardUri), ("$m", json)), CultureInfo.InvariantCulture);
		GeoPackageDb.Execute(c, tx, "INSERT INTO gpkg_metadata_reference (reference_scope, table_name, column_name, row_id_value, md_file_id) VALUES ('geopackage', NULL, NULL, NULL, $i)", ("$i", newId));
	}

	private SqliteConnection Connect()
	{
		try
		{
			return GeoPackageDb.Open(Path, create: false);
		}
		catch (SqliteException ex)
		{
			throw new GisException("The map layers file couldn't be opened. It may have been moved or deleted.", $"{Path}: {ex.Message}", ex);
		}
	}

	private static GeoPackageTableInfo Find(SqliteConnection c, SqliteTransaction tx, string table, bool counts = true) =>
		table == null ? null : GeoPackageDb.ListFeatureTables(c, tx, table, counts).FirstOrDefault();

	private static GeoPackageTableInfo Require(SqliteConnection c, string table, SqliteTransaction tx = null) =>
		Find(c, tx, table, counts: false) ?? throw new GisException($"The layer table {table} isn't in the map layers file.", table);
}
