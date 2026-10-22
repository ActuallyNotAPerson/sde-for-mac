using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Projections;

namespace Sde.Gis.Formats;

/// <summary>A feature table in a GeoPackage: what gpkg_contents and gpkg_geometry_columns say about it.</summary>
public sealed class GeoPackageTableInfo
{
	/// <summary>The SQL table name.</summary>
	public string Name { get; init; }

	/// <summary>The layer title (gpkg_contents.identifier).</summary>
	public string Title { get; init; }

	public string Description { get; init; }

	/// <summary>The geometry column's name, usually "geom".</summary>
	public string GeometryColumn { get; init; }

	/// <summary>The declared geometry type: POINT, MULTIPOLYGON, GEOMETRY and so on.</summary>
	public string GeometryType { get; init; }

	public GeometryKind GeometryKind => GeoPackageDb.KindOfTypeName(GeometryType);

	public int SrsId { get; init; }

	/// <summary>The extent recorded in gpkg_contents (in the table's coordinates), or null.</summary>
	public Envelope Extent { get; init; }

	public long FeatureCount { get; init; }

	public DateTime? LastChange { get; init; }

	public IReadOnlyList<LayerField> Fields { get; init; }
}

/// <summary>
/// The SQL behind <see cref="GeoPackageFormat"/> and <see cref="LayersStore"/>: the GeoPackage 1.3 system tables, feature
/// tables, the R-tree spatial index with the triggers QGIS, GDAL and ArcGIS expect, and the ST_ functions those
/// triggers call (registered on every connection SDE opens, because plain SQLite doesn't have them).
/// </summary>
internal static class GeoPackageDb
{
	public const int ApplicationId = 0x47504B47;
	public const int UserVersion = 10300;
	public const string FidColumn = "fid";
	public const string GeometryColumn = "geom";

	public const string Wgs84Definition = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]],AUTHORITY[\"EPSG\",\"4326\"]]";

	/// <summary>Opens a GeoPackage (creating the file when asked) with the ST_ functions registered.</summary>
	public static SqliteConnection Open(string path, bool create, bool readOnly = false)
	{
		SqliteConnectionStringBuilder b = new SqliteConnectionStringBuilder
		{
			DataSource = path,
			Pooling = false,
			Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
		};
		SqliteConnection c = new SqliteConnection(b.ToString());
		c.Open();
		RegisterFunctions(c);
		return c;
	}

	/// <summary>The GeoPackage functions the R-tree triggers call: ST_IsEmpty, ST_MinX, ST_MaxX, ST_MinY, ST_MaxY.</summary>
	public static void RegisterFunctions(SqliteConnection c)
	{
		c.CreateFunction("ST_IsEmpty", (byte[] blob) => blob == null ? (long?)null : GeoPackageGeometry.IsEmpty(blob) ? 1L : 0L, isDeterministic: true);
		c.CreateFunction("ST_MinX", (byte[] blob) => Env(blob)?.MinX, isDeterministic: true);
		c.CreateFunction("ST_MaxX", (byte[] blob) => Env(blob)?.MaxX, isDeterministic: true);
		c.CreateFunction("ST_MinY", (byte[] blob) => Env(blob)?.MinY, isDeterministic: true);
		c.CreateFunction("ST_MaxY", (byte[] blob) => Env(blob)?.MaxY, isDeterministic: true);
	}

	private static Envelope Env(byte[] blob) => GeoPackageGeometry.TryGetEnvelope(blob, out Envelope e) ? e : null;

	public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

	public static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
	{
		using SqliteCommand cmd = c.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = sql;
		foreach ((string name, object value) in parameters)
		{
			cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
		}
		cmd.ExecuteNonQuery();
	}

	public static object Scalar(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
	{
		using SqliteCommand cmd = c.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = sql;
		foreach ((string name, object value) in parameters)
		{
			cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
		}
		object result = cmd.ExecuteScalar();
		return result is DBNull ? null : result;
	}

	/// <summary>Sets the GeoPackage application id and version. Runs outside any transaction.</summary>
	public static void StampGeoPackage(SqliteConnection c)
	{
		Execute(c, null, $"PRAGMA application_id = {ApplicationId}");
		Execute(c, null, $"PRAGMA user_version = {UserVersion}");
	}

	/// <summary>Creates the GeoPackage system tables when missing, with the three required spatial reference systems.</summary>
	public static void CreateSystemTables(SqliteConnection c, SqliteTransaction tx)
	{
		Execute(c, tx, "CREATE TABLE IF NOT EXISTS gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER NOT NULL PRIMARY KEY, organization TEXT NOT NULL, organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT)");
		Execute(c, tx, "CREATE TABLE IF NOT EXISTS gpkg_contents (table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT UNIQUE, description TEXT DEFAULT '', last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER, CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id))");
		Execute(c, tx, "CREATE TABLE IF NOT EXISTS gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL, srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL, CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name), CONSTRAINT uk_gc_table_name UNIQUE (table_name), CONSTRAINT fk_gc_tn FOREIGN KEY (table_name) REFERENCES gpkg_contents(table_name), CONSTRAINT fk_gc_srs FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id))");
		Execute(c, tx, "CREATE TABLE IF NOT EXISTS gpkg_extensions (table_name TEXT, column_name TEXT, extension_name TEXT NOT NULL, definition TEXT NOT NULL, scope TEXT NOT NULL, CONSTRAINT ge_tce UNIQUE (table_name, column_name, extension_name))");
		const string insert = "INSERT OR IGNORE INTO gpkg_spatial_ref_sys (srs_name, srs_id, organization, organization_coordsys_id, definition, description) VALUES ($n, $i, $o, $c, $d, $s)";
		Execute(c, tx, insert, ("$n", "Undefined Cartesian SRS"), ("$i", -1), ("$o", "NONE"), ("$c", -1), ("$d", "undefined"), ("$s", "undefined Cartesian coordinate reference system"));
		Execute(c, tx, insert, ("$n", "Undefined geographic SRS"), ("$i", 0), ("$o", "NONE"), ("$c", 0), ("$d", "undefined"), ("$s", "undefined geographic coordinate reference system"));
		Execute(c, tx, insert, ("$n", "WGS 84 geodetic"), ("$i", 4326), ("$o", "EPSG"), ("$c", 4326), ("$d", Wgs84Definition), ("$s", "longitude/latitude coordinates in decimal degrees on the WGS 84 spheroid"));
	}

	/// <summary>True when the table exists in sqlite_master.</summary>
	public static bool TableExists(SqliteConnection c, SqliteTransaction tx, string table) =>
		Convert.ToInt64(Scalar(c, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name = $t COLLATE NOCASE", ("$t", table)), CultureInfo.InvariantCulture) > 0;

	/// <summary>The SQL type a field is stored as.</summary>
	public static string SqlType(FieldType type) => type switch
	{
		FieldType.Integer => "INTEGER",
		FieldType.Real => "DOUBLE",
		FieldType.Boolean => "BOOLEAN",
		FieldType.Date => "DATE",
		FieldType.DateTime => "DATETIME",
		_ => "TEXT",
	};

	/// <summary>A field type for a GeoPackage column's declared type, or null for BLOBs (which SDE doesn't show).</summary>
	public static FieldType? FieldTypeOf(string declared)
	{
		string t = (declared ?? "").Trim().ToUpperInvariant();
		int paren = t.IndexOf('(');
		if (paren >= 0)
		{
			t = t.Substring(0, paren).Trim();
		}
		return t switch
		{
			"INTEGER" or "INT" or "MEDIUMINT" or "SMALLINT" or "TINYINT" or "BIGINT" => FieldType.Integer,
			"BOOLEAN" or "BOOL" => FieldType.Boolean,
			"DOUBLE" or "REAL" or "FLOAT" or "NUMERIC" or "DECIMAL" => FieldType.Real,
			"DATE" => FieldType.Date,
			"DATETIME" or "TIMESTAMP" => FieldType.DateTime,
			"BLOB" => null,
			_ => FieldType.Text,
		};
	}

	/// <summary>A field value as stored: bools as 0 and 1, dates as ISO 8601 text (date-times in UTC with a Z).</summary>
	public static object ToDb(FieldType type, object value)
	{
		if (value == null)
		{
			return DBNull.Value;
		}
		switch (type)
		{
			case FieldType.Boolean:
				return value is bool b ? (b ? 1L : 0L) : value;
			case FieldType.Date:
				return value is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : value;
			case FieldType.DateTime:
				if (value is DateTime t)
				{
					DateTime utc = t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
					return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
				}
				return value;
			default:
				return value;
		}
	}

	/// <summary>A stored value read back as the field's type; null when it can't be (counted by the caller).</summary>
	public static object FromDb(FieldType type, object raw, out bool unreadable)
	{
		unreadable = false;
		if (raw == null || raw is DBNull)
		{
			return null;
		}
		if (type == FieldType.DateTime && raw is string s
			&& DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
		{
			return parsed;
		}
		if (FieldValues.TryConvert(type, raw, out object value))
		{
			return value;
		}
		unreadable = true;
		return null;
	}

	/// <summary>The kind of features a declared geometry type holds.</summary>
	public static GeometryKind KindOfTypeName(string typeName) => (typeName ?? "").ToUpperInvariant() switch
	{
		"POINT" or "MULTIPOINT" => GeometryKind.Point,
		"LINESTRING" or "MULTILINESTRING" or "CURVE" or "MULTICURVE" or "COMPOUNDCURVE" or "CIRCULARSTRING" => GeometryKind.Line,
		"POLYGON" or "MULTIPOLYGON" or "SURFACE" or "MULTISURFACE" or "CURVEPOLYGON" => GeometryKind.Polygon,
		_ => GeometryKind.Unknown,
	};

	/// <summary>
	/// The geometry type to declare for a layer: the single type when every feature is single (POINT, LINESTRING,
	/// POLYGON), the multi type when any is multi, and GEOMETRY for a mix of kinds.
	/// </summary>
	public static string TypeNameFor(FeatureLayer layer)
	{
		GeometryKind kind = layer.GeometryKind;
		bool anyMulti = false;
		bool mixed = false;
		foreach (Feature f in layer.Features)
		{
			GeometryKind k = FeatureLayer.KindOf(f.Geometry);
			if (f.Geometry is GeometryCollection gc && k == GeometryKind.Unknown && !gc.IsEmpty)
			{
				mixed = true;
				break;
			}
			if (k == GeometryKind.Unknown)
			{
				continue;
			}
			if (kind == GeometryKind.Unknown)
			{
				kind = k;
			}
			else if (kind != k)
			{
				mixed = true;
				break;
			}
			anyMulti |= f.Geometry is GeometryCollection;
		}
		if (mixed)
		{
			return "GEOMETRY";
		}
		return kind switch
		{
			GeometryKind.Point => anyMulti ? "MULTIPOINT" : "POINT",
			GeometryKind.Line => anyMulti ? "MULTILINESTRING" : "LINESTRING",
			GeometryKind.Polygon => anyMulti ? "MULTIPOLYGON" : "POLYGON",
			_ => "GEOMETRY",
		};
	}

	/// <summary>
	/// A geometry made to fit a column's declared type: a single polygon is wrapped for a MULTIPOLYGON column, a
	/// one-part multi is unwrapped for a POLYGON column. Throws <see cref="GisException"/> when it can't fit.
	/// </summary>
	public static Geometry Coerce(Geometry g, string typeName)
	{
		if (g == null || g.IsEmpty)
		{
			return g;
		}
		string t = (typeName ?? "GEOMETRY").ToUpperInvariant();
		GeometryFactory f = g.Factory;
		switch (t)
		{
			case "POINT":
				if (g is Point)
				{
					return g;
				}
				if (g is MultiPoint mp && mp.NumGeometries == 1)
				{
					return mp.GetGeometryN(0);
				}
				break;
			case "MULTIPOINT":
				if (g is MultiPoint)
				{
					return g;
				}
				if (g is Point p)
				{
					return f.CreateMultiPoint(new[] { p });
				}
				break;
			case "LINESTRING":
				if (g is LineString and not LinearRing)
				{
					return g;
				}
				if (g is MultiLineString ml && ml.NumGeometries == 1)
				{
					return ml.GetGeometryN(0);
				}
				break;
			case "MULTILINESTRING":
				if (g is MultiLineString)
				{
					return g;
				}
				if (g is LineString l)
				{
					return f.CreateMultiLineString(new[] { f.CreateLineString(l.CoordinateSequence) });
				}
				break;
			case "POLYGON":
				if (g is Polygon)
				{
					return g;
				}
				if (g is MultiPolygon mpoly && mpoly.NumGeometries == 1)
				{
					return mpoly.GetGeometryN(0);
				}
				break;
			case "MULTIPOLYGON":
				if (g is MultiPolygon)
				{
					return g;
				}
				if (g is Polygon poly)
				{
					return f.CreateMultiPolygon(new[] { poly });
				}
				break;
			default:
				return g;
		}
		GeometryKind target = KindOfTypeName(t);
		GeometryKind actual = FeatureLayer.KindOf(g);
		string targetText = target switch { GeometryKind.Point => "points", GeometryKind.Line => "lines", _ => "polygons" };
		if (target == actual)
		{
			throw new GisException($"This layer holds single {targetText}, so a feature with several parts can't be saved in it.", $"{g.GeometryType} into {t}.");
		}
		throw new GisException($"This layer holds {targetText}, so a {Describe(actual)} can't be saved in it.", $"{g.GeometryType} into {t}.");
	}

	private static string Describe(GeometryKind kind) => kind switch
	{
		GeometryKind.Point => "point",
		GeometryKind.Line => "line",
		GeometryKind.Polygon => "polygon",
		_ => "mixed geometry",
	};

	/// <summary>
	/// Column names for a layer's fields in a GeoPackage table: the field name when it is free, with "_2", "_3" added
	/// when it clashes with fid, geom or another field (SQLite compares names without regard to case).
	/// </summary>
	public static List<string> ColumnNames(FieldList fields)
	{
		HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FidColumn, GeometryColumn };
		List<string> names = new List<string>(fields.Count);
		foreach (LayerField field in fields)
		{
			string name = field.Name;
			for (int n = 2; used.Contains(name); n++)
			{
				name = field.Name + "_" + n.ToString(CultureInfo.InvariantCulture);
			}
			used.Add(name);
			names.Add(name);
		}
		return names;
	}

	/// <summary>Creates a feature table and registers it in gpkg_contents and gpkg_geometry_columns (no index yet).</summary>
	public static void CreateFeatureTable(SqliteConnection c, SqliteTransaction tx, string table, FieldList fields, IReadOnlyList<string> columns,
		string geometryType, string identifier, string description, int srsId = 4326)
	{
		StringBuilder sql = new StringBuilder();
		sql.Append("CREATE TABLE ").Append(Quote(table)).Append(" (").Append(Quote(FidColumn)).Append(" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, ")
			.Append(Quote(GeometryColumn)).Append(' ').Append(geometryType);
		for (int i = 0; i < fields.Count; i++)
		{
			sql.Append(", ").Append(Quote(columns[i])).Append(' ').Append(SqlType(fields[i].Type));
		}
		sql.Append(')');
		Execute(c, tx, sql.ToString());
		Execute(c, tx, "INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, srs_id) VALUES ($t, 'features', $i, $d, strftime('%Y-%m-%dT%H:%M:%fZ','now'), $s)",
			("$t", table), ("$i", identifier ?? table), ("$d", description ?? ""), ("$s", srsId));
		Execute(c, tx, "INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m) VALUES ($t, $c, $g, $s, 0, 0)",
			("$t", table), ("$c", GeometryColumn), ("$g", geometryType), ("$s", srsId));
	}

	/// <summary>Inserts features; returns their new fids in order. Geometries are coerced to the declared type.</summary>
	public static List<long> InsertFeatures(SqliteConnection c, SqliteTransaction tx, string table, IReadOnlyList<LayerField> columnsFields,
		IReadOnlyList<string> columns, string geometryType, IEnumerable<Feature> features, bool keepIds = false)
	{
		List<long> ids = new List<long>();
		using SqliteCommand cmd = c.CreateCommand();
		cmd.Transaction = tx;
		StringBuilder sql = new StringBuilder("INSERT INTO ").Append(Quote(table)).Append(" (");
		if (keepIds)
		{
			sql.Append(Quote(FidColumn)).Append(", ");
		}
		sql.Append(Quote(GeometryColumn));
		foreach (string col in columns)
		{
			sql.Append(", ").Append(Quote(col));
		}
		sql.Append(") VALUES (");
		if (keepIds)
		{
			sql.Append("$fid, ");
		}
		sql.Append("$geom");
		for (int i = 0; i < columns.Count; i++)
		{
			sql.Append(", $c").Append(i.ToString(CultureInfo.InvariantCulture));
		}
		sql.Append(") RETURNING ").Append(Quote(FidColumn));
		cmd.CommandText = sql.ToString();
		SqliteParameter fid = keepIds ? cmd.Parameters.Add("$fid", SqliteType.Integer) : null;
		SqliteParameter geom = cmd.Parameters.Add("$geom", SqliteType.Blob);
		SqliteParameter[] values = new SqliteParameter[columns.Count];
		for (int i = 0; i < columns.Count; i++)
		{
			values[i] = cmd.Parameters.Add("$c" + i.ToString(CultureInfo.InvariantCulture), SqliteTypeFor(columnsFields[i].Type));
		}
		cmd.Prepare();
		foreach (Feature feature in features)
		{
			if (fid != null)
			{
				fid.Value = feature.Id > 0 ? feature.Id : DBNull.Value;
			}
			Geometry g = Coerce(feature.Geometry, geometryType);
			geom.Value = g == null ? DBNull.Value : GeoPackageGeometry.Write(g);
			for (int i = 0; i < columns.Count; i++)
			{
				values[i].Value = ToDb(columnsFields[i].Type, feature[columnsFields[i].Name]);
			}
			ids.Add(Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
		}
		return ids;
	}

	public static SqliteType SqliteTypeFor(FieldType type) => type switch
	{
		FieldType.Integer or FieldType.Boolean => SqliteType.Integer,
		FieldType.Real => SqliteType.Real,
		_ => SqliteType.Text,
	};

	/// <summary>The R-tree index table's name for a feature table (GeoPackage's naming rule).</summary>
	public static string RtreeName(string table, string geometryColumn = GeometryColumn) => "rtree_" + table + "_" + geometryColumn;

	/// <summary>
	/// Creates the GeoPackage R-tree spatial index (extension gpkg_rtree_index) for a table, fills it, and adds the
	/// triggers that keep it current (the GeoPackage 1.3 trigger set).
	/// </summary>
	public static void CreateSpatialIndex(SqliteConnection c, SqliteTransaction tx, string table)
	{
		string t = Quote(table);
		string r = RtreeName(table);
		string rq = Quote(r);
		string g = Quote(GeometryColumn);
		string i = Quote(FidColumn);
		Execute(c, tx, $"CREATE VIRTUAL TABLE {rq} USING rtree(id, minx, maxx, miny, maxy)");
		Execute(c, tx, $"INSERT OR REPLACE INTO {rq} SELECT {i}, ST_MinX({g}), ST_MaxX({g}), ST_MinY({g}), ST_MaxY({g}) FROM {t} WHERE {g} NOT NULL AND NOT ST_IsEmpty({g})");
		string values = $"NEW.{i}, ST_MinX(NEW.{g}), ST_MaxX(NEW.{g}), ST_MinY(NEW.{g}), ST_MaxY(NEW.{g})";
		Execute(c, tx, $"CREATE TRIGGER {Quote(r + "_insert")} AFTER INSERT ON {t} WHEN (new.{g} NOT NULL AND NOT ST_IsEmpty(NEW.{g})) BEGIN INSERT OR REPLACE INTO {rq} VALUES ({values}); END");
		Execute(c, tx, $"CREATE TRIGGER {Quote(r + "_update1")} AFTER UPDATE OF {g} ON {t} WHEN OLD.{i} = NEW.{i} AND (NEW.{g} NOTNULL AND NOT ST_IsEmpty(NEW.{g})) BEGIN INSERT OR REPLACE INTO {rq} VALUES ({values}); END");
		Execute(c, tx, $"CREATE TRIGGER {Quote(r + "_update2")} AFTER UPDATE OF {g} ON {t} WHEN OLD.{i} = NEW.{i} AND (NEW.{g} ISNULL OR ST_IsEmpty(NEW.{g})) BEGIN DELETE FROM {rq} WHERE id = OLD.{i}; END");
		Execute(c, tx, $"CREATE TRIGGER {Quote(r + "_update3")} AFTER UPDATE ON {t} WHEN OLD.{i} != NEW.{i} AND (NEW.{g} NOTNULL AND NOT ST_IsEmpty(NEW.{g})) BEGIN DELETE FROM {rq} WHERE id = OLD.{i}; INSERT OR REPLACE INTO {rq} VALUES ({values}); END");
		Execute(c, tx, $"CREATE TRIGGER {Quote(r + "_update4")} AFTER UPDATE ON {t} WHEN OLD.{i} != NEW.{i} AND (NEW.{g} ISNULL OR ST_IsEmpty(NEW.{g})) BEGIN DELETE FROM {rq} WHERE id IN (OLD.{i}, NEW.{i}); END");
		Execute(c, tx, $"CREATE TRIGGER {Quote(r + "_delete")} AFTER DELETE ON {t} WHEN old.{g} NOT NULL BEGIN DELETE FROM {rq} WHERE id = OLD.{i}; END");
		Execute(c, tx, "INSERT OR REPLACE INTO gpkg_extensions (table_name, column_name, extension_name, definition, scope) VALUES ($t, $c, 'gpkg_rtree_index', 'http://www.geopackage.org/spec120/#extension_rtree', 'write-only')",
			("$t", table), ("$c", GeometryColumn));
	}

	/// <summary>True when the table has an R-tree index.</summary>
	public static bool HasSpatialIndex(SqliteConnection c, SqliteTransaction tx, string table, string geometryColumn) =>
		TableExists(c, tx, RtreeName(table, geometryColumn));

	/// <summary>Sets gpkg_contents' extent (from the data) and last_change for a table.</summary>
	public static void UpdateContents(SqliteConnection c, SqliteTransaction tx, string table)
	{
		string rq = Quote(RtreeName(table));
		bool indexed = TableExists(c, tx, RtreeName(table));
		string source = indexed
			? $"SELECT MIN(minx), MIN(miny), MAX(maxx), MAX(maxy) FROM {rq}"
			: $"SELECT MIN(ST_MinX({Quote(GeometryColumn)})), MIN(ST_MinY({Quote(GeometryColumn)})), MAX(ST_MaxX({Quote(GeometryColumn)})), MAX(ST_MaxY({Quote(GeometryColumn)})) FROM {Quote(table)}";
		Execute(c, tx, $"UPDATE gpkg_contents SET (min_x, min_y, max_x, max_y) = ({source}), last_change = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE table_name = $t", ("$t", table));
	}

	/// <summary>Removes a feature table, its index and its registrations.</summary>
	public static void DropFeatureTable(SqliteConnection c, SqliteTransaction tx, string table)
	{
		string geometryColumn = Scalar(c, tx, "SELECT column_name FROM gpkg_geometry_columns WHERE table_name = $t", ("$t", table)) as string ?? GeometryColumn;
		Execute(c, tx, $"DROP TABLE IF EXISTS {Quote(RtreeName(table, geometryColumn))}");
		Execute(c, tx, "DELETE FROM gpkg_extensions WHERE table_name = $t", ("$t", table));
		Execute(c, tx, "DELETE FROM gpkg_geometry_columns WHERE table_name = $t", ("$t", table));
		Execute(c, tx, "DELETE FROM gpkg_contents WHERE table_name = $t", ("$t", table));
		Execute(c, tx, $"DROP TABLE IF EXISTS {Quote(table)}");
	}

	/// <summary>Every feature table in the GeoPackage, in gpkg_contents order.</summary>
	/// <param name="c">The connection.</param>
	/// <param name="tx">The transaction, if any.</param>
	/// <param name="onlyTable">Just this table (any case), or null for all.</param>
	/// <param name="counts">Whether to count each table's features (skipped for quick lookups; the count is then -1).</param>
	public static List<GeoPackageTableInfo> ListFeatureTables(SqliteConnection c, SqliteTransaction tx = null, string onlyTable = null, bool counts = true)
	{
		List<ContentsRow> rows = new List<ContentsRow>();
		using (SqliteCommand cmd = c.CreateCommand())
		{
			cmd.Transaction = tx;
			cmd.CommandText = "SELECT c.table_name, c.identifier, c.description, c.last_change, c.min_x, c.min_y, c.max_x, c.max_y, g.column_name, g.geometry_type_name, g.srs_id " +
				"FROM gpkg_contents c JOIN gpkg_geometry_columns g ON g.table_name = c.table_name WHERE c.data_type = 'features'" +
				(onlyTable == null ? "" : " AND c.table_name = $t COLLATE NOCASE") + " ORDER BY c.rowid";
			if (onlyTable != null)
			{
				cmd.Parameters.AddWithValue("$t", onlyTable);
			}
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				rows.Add(new ContentsRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
					r.IsDBNull(4) ? null : r.GetDouble(4), r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.IsDBNull(7) ? null : r.GetDouble(7),
					r.GetString(8), r.GetString(9), r.GetInt32(10)));
			}
		}
		List<GeoPackageTableInfo> tables = new List<GeoPackageTableInfo>();
		foreach (ContentsRow row in rows)
		{
			(FieldList fields, _, string fid) = ReadColumns(c, tx, row.Table, row.Column);
			long count = counts ? Convert.ToInt64(Scalar(c, tx, $"SELECT COUNT(*) FROM {Quote(row.Table)}"), CultureInfo.InvariantCulture) : -1;
			tables.Add(new GeoPackageTableInfo
			{
				Name = row.Table,
				Title = row.Identifier ?? row.Table,
				Description = row.Description,
				GeometryColumn = row.Column,
				GeometryType = row.Type,
				SrsId = row.Srs,
				Extent = row.MinX is double x0 && row.MinY is double y0 && row.MaxX is double x1 && row.MaxY is double y1 ? new Envelope(x0, x1, y0, y1) : null,
				FeatureCount = count,
				LastChange = DateTime.TryParse(row.LastChange, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime changed) ? changed : null,
				Fields = fields.ToList(),
			});
		}
		return tables;
	}

	/// <summary>One feature table's row of gpkg_contents joined with gpkg_geometry_columns.</summary>
	private readonly record struct ContentsRow(string Table, string Identifier, string Description, string LastChange, double? MinX, double? MinY,
		double? MaxX, double? MaxY, string Column, string Type, int Srs);

	/// <summary>
	/// A table's attribute columns as fields (in table order, leaving out the primary key, the geometry column and
	/// BLOBs), their SQL column names, and the primary key column's name.
	/// </summary>
	public static (FieldList Fields, List<string> Columns, string FidColumn) ReadColumns(SqliteConnection c, SqliteTransaction tx, string table, string geometryColumn)
	{
		FieldList fields = new FieldList();
		List<string> columns = new List<string>();
		string fid = null;
		using SqliteCommand cmd = c.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
		using SqliteDataReader r = cmd.ExecuteReader();
		while (r.Read())
		{
			string name = r.GetString(1);
			string declared = r.IsDBNull(2) ? "" : r.GetString(2);
			bool pk = r.GetInt32(5) > 0;
			if (pk && fid == null && FieldTypeOf(declared) == FieldType.Integer)
			{
				fid = name;
				continue;
			}
			if (string.Equals(name, geometryColumn, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			FieldType? type = FieldTypeOf(declared);
			if (type == null || fields.Contains(name))
			{
				continue;
			}
			fields.Add(new LayerField(name, type.Value));
			columns.Add(name);
		}
		return (fields, columns, fid);
	}

	/// <summary>The coordinate system of a GeoPackage SRS id: by EPSG code when it has one, otherwise from its WKT definition.</summary>
	public static CoordinateSystem CoordinateSystemOf(SqliteConnection c, int srsId)
	{
		if (srsId == 4326)
		{
			return CoordinateSystem.Wgs84;
		}
		string organization = null;
		long? orgId = null;
		string definition = null;
		using (SqliteCommand cmd = c.CreateCommand())
		{
			cmd.CommandText = "SELECT organization, organization_coordsys_id, definition FROM gpkg_spatial_ref_sys WHERE srs_id = $s";
			cmd.Parameters.AddWithValue("$s", srsId);
			using SqliteDataReader r = cmd.ExecuteReader();
			if (r.Read())
			{
				organization = r.IsDBNull(0) ? null : r.GetString(0);
				orgId = r.IsDBNull(1) ? null : r.GetInt64(1);
				definition = r.IsDBNull(2) ? null : r.GetString(2);
			}
		}
		if (srsId == 0)
		{
			return CoordinateSystem.Wgs84;
		}
		if (string.Equals(organization, "EPSG", StringComparison.OrdinalIgnoreCase) && orgId is long code && Epsg.TryGet((int)code, out CoordinateSystem known))
		{
			return known;
		}
		if (!string.IsNullOrWhiteSpace(definition) && !string.Equals(definition, "undefined", StringComparison.OrdinalIgnoreCase))
		{
			return CoordinateSystem.FromWkt(definition);
		}
		if (string.Equals(organization, "EPSG", StringComparison.OrdinalIgnoreCase) && orgId is long epsg)
		{
			return CoordinateSystem.FromEpsg((int)epsg);
		}
		throw new UnsupportedCoordinateSystemException("undefined", $"GeoPackage srs_id {srsId} has no definition.");
	}

	/// <summary>
	/// Reads features from a table into a layer. <paramref name="where"/> is an optional SQL condition on the table
	/// (aliased t) or its R-tree (aliased r, joined when <paramref name="joinRtree"/> is set).
	/// </summary>
	public static FeatureLayer ReadFeatures(SqliteConnection c, SqliteTransaction tx, GeoPackageTableInfo info, CoordinateSystem source,
		string where = null, bool joinRtree = false, params (string Name, object Value)[] parameters)
	{
		(FieldList fields, List<string> columns, string fidColumn) = ReadColumns(c, tx, info.Name, info.GeometryColumn);
		FeatureLayer layer = new FeatureLayer(info.Title ?? info.Name, info.GeometryKind);
		foreach (LayerField f in fields)
		{
			layer.Fields.Add(f);
		}
		layer.SourceCoordinateSystem = source == null || source.IsGeographic ? null : source;
		StringBuilder sql = new StringBuilder("SELECT ");
		sql.Append(fidColumn == null ? "t.rowid" : "t." + Quote(fidColumn)).Append(", t.").Append(Quote(info.GeometryColumn));
		foreach (string col in columns)
		{
			sql.Append(", t.").Append(Quote(col));
		}
		sql.Append(" FROM ").Append(Quote(info.Name)).Append(" t");
		if (joinRtree)
		{
			sql.Append(" JOIN ").Append(Quote(RtreeName(info.Name, info.GeometryColumn))).Append(" r ON r.id = t.").Append(Quote(fidColumn ?? "rowid"));
		}
		if (!string.IsNullOrEmpty(where))
		{
			sql.Append(" WHERE ").Append(where);
		}
		using SqliteCommand cmd = c.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = sql.ToString();
		foreach ((string name, object value) in parameters)
		{
			cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
		}
		int unreadable = 0;
		int badGeometry = 0;
		using SqliteDataReader r = cmd.ExecuteReader();
		while (r.Read())
		{
			Geometry g = null;
			if (!r.IsDBNull(1))
			{
				try
				{
					g = GeoPackageGeometry.Read((byte[])r.GetValue(1));
					g = Reproject.ToWgs84(g, source);
				}
				catch (FormatException)
				{
					badGeometry++;
				}
			}
			Feature feature = layer.NewFeature(g, r.GetInt64(0));
			for (int i = 0; i < columns.Count; i++)
			{
				feature.SetRaw(i, FromDb(fields[i].Type, r.GetValue(i + 2), out bool bad));
				if (bad)
				{
					unreadable++;
				}
			}
			layer.Features.Add(feature);
		}
		if (badGeometry > 0)
		{
			layer.Warnings.Add($"{badGeometry} features had a geometry SDE couldn't read and are shown without one.");
		}
		if (unreadable > 0)
		{
			layer.Warnings.Add($"{unreadable} values didn't match their column's type and are shown empty.");
		}
		return layer;
	}
}
