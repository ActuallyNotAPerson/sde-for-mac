using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>One structure as a GIS feature: where it is and what the assessment found.</summary>
public sealed class GisRow
{
	public Guid StructureID { get; init; }
	public Guid AssessmentID { get; init; }
	public string Address { get; init; }
	public string Parcel { get; init; }
	public decimal? Latitude { get; init; }
	public decimal? Longitude { get; init; }
	public decimal? PercentDamage { get; init; }
	public DeterminationClass Class { get; init; }
	public decimal? BuildingValue { get; init; }
	public decimal? RepairCost { get; init; }
	public DateTime? InspectionDate { get; init; }
	public string FirmZone { get; init; }

	/// <summary>Only written when <see cref="GisExportOptions.IncludeOwnerNames"/> is on.</summary>
	public string OwnerName { get; init; }

	/// <summary>One row per assessment, in the order given.</summary>
	public static List<GisRow> From(
		IEnumerable<AssessmentFile> assessments,
		ReferenceData reference,
		decimal substantialPercent = Determinations.DefaultSubstantialPercent,
		decimal closePercent = Determinations.DefaultClosePercent)
	{
		List<GisRow> rows = new();
		foreach (AssessmentFile f in assessments)
		{
			PropertyRecord p = f.Property ?? new PropertyRecord();
			AssessmentRecord a = f.Assessment ?? new AssessmentRecord();
			rows.Add(new GisRow
			{
				StructureID = p.PropertyID,
				AssessmentID = a.AssessmentID,
				Address = Text.Join(p.StreetNumber, p.StreetName, reference?.LookupName(p.StreetSuffixID)),
				Parcel = Text.Clean(p.ParcelNumber),
				Latitude = p.Latitude,
				Longitude = p.Longitude,
				PercentDamage = a.PercentDamage,
				Class = Determinations.Classify(a, substantialPercent, closePercent),
				BuildingValue = AssessmentAmounts.BuildingValue(a),
				RepairCost = AssessmentAmounts.RepairCost(a),
				InspectionDate = a.DateofInspection,
				FirmZone = reference?.LookupName(p.FIRMZone),
				OwnerName = Text.Join(p.OwnerFirstName, p.OwnerLastName),
			});
		}
		return rows;
	}

	/// <summary>True when the row has usable WGS 84 coordinates (not missing, not 0,0, within range).</summary>
	public bool HasLocation => Latitude is decimal lat && Longitude is decimal lon
		&& !(lat == 0m && lon == 0m) && Math.Abs(lat) <= 90m && Math.Abs(lon) <= 180m;
}

/// <summary>Choices for the GIS files.</summary>
public sealed class GisExportOptions
{
	/// <summary>Owner names are personal information; they are left out unless this is turned on.</summary>
	public bool IncludeOwnerNames { get; init; }

	/// <summary>The feature table's name inside the GeoPackage (letters, digits and underscores).</summary>
	public string LayerName { get; init; } = "structures";

	/// <summary>Human-readable layer title stored in the GeoPackage.</summary>
	public string LayerTitle { get; init; } = "SDE structures";
}

/// <summary>What a GIS export wrote.</summary>
public sealed class GisExportResult
{
	public string Path { get; init; }

	/// <summary>Structures written.</summary>
	public int Features { get; set; }

	/// <summary>Structures left out because they have no usable latitude/longitude.</summary>
	public int SkippedNoCoordinates => Skipped.Count;

	public List<GeoFileSkipped> Skipped { get; } = new();
}

/// <summary>
/// GIS files for GIS staff and the state: a GeoPackage (OGC GPKG 1.3, opens in QGIS, ArcGIS Pro and GDAL), GeoJSON
/// (RFC 7946) and a QGIS style file. One point per structure in WGS 84, coloured by <see cref="DeterminationClass"/>.
/// Structures without coordinates are skipped and counted. Owner names are only written when asked for.
/// </summary>
public static class GisExport
{
	/// <summary>GeoPackage "application_id": the ASCII letters GPKG.</summary>
	public const int GeoPackageApplicationId = 0x47504B47;

	/// <summary>GeoPackage 1.3 "user_version".</summary>
	public const int GeoPackageUserVersion = 10300;

	/// <summary>The field the QGIS style (and most GIS users) classify on.</summary>
	public const string ClassField = "determination";

	/// <summary>The name the QGIS style file's categories use for each class.</summary>
	public static string ClassName(DeterminationClass c) => Determinations.Label(c);

	// ───────── GeoPackage ─────────

	public static GisExportResult WriteGeoPackage(string path, IEnumerable<GisRow> rows, GisExportOptions options)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(rows);
		options ??= new GisExportOptions();
		string layer = options.LayerName;
		if (string.IsNullOrEmpty(layer) || !layer.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') || char.IsAsciiDigit(layer[0]))
		{
			throw new ArgumentException("The layer name may only contain letters, digits and underscores, and cannot start with a digit.", nameof(options));
		}

		GisExportResult result = new GisExportResult { Path = path };
		string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		// Written beside the target and moved into place, so a failure never leaves half a file.
		string temp = path + ".tmp";
		File.Delete(temp);
		try
		{
			using (SqliteConnection db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
			{
				db.Open();
				Execute(db, $"PRAGMA application_id = {GeoPackageApplicationId}");
				Execute(db, $"PRAGMA user_version = {GeoPackageUserVersion}");
				using SqliteTransaction tx = db.BeginTransaction();
				CreateSystemTables(db, tx);
				CreateFeatureTable(db, tx, layer, options);

				double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
				using (SqliteCommand insert = db.CreateCommand())
				{
					insert.Transaction = tx;
					List<string> columns = new() { "geom", "structure_id", "assessment_id", "address", "parcel", "damage_pct", ClassField, "building_value", "repair_cost", "inspection_date", "firm_zone" };
					if (options.IncludeOwnerNames)
					{
						columns.Add("owner_name");
					}
					insert.CommandText = $"INSERT INTO \"{layer}\" ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "$" + c))})";
					foreach (string c in columns)
					{
						insert.Parameters.Add("$" + c, c == "geom" ? SqliteType.Blob : SqliteType.Text);
					}
					foreach (GisRow row in rows)
					{
						if (!row.HasLocation)
						{
							result.Skipped.Add(Skip(row));
							continue;
						}
						double x = (double)row.Longitude.Value;
						double y = (double)row.Latitude.Value;
						minX = Math.Min(minX, x);
						maxX = Math.Max(maxX, x);
						minY = Math.Min(minY, y);
						maxY = Math.Max(maxY, y);
						insert.Parameters["$geom"].Value = PointBlob(x, y);
						insert.Parameters["$structure_id"].Value = row.StructureID.ToString();
						insert.Parameters["$assessment_id"].Value = row.AssessmentID.ToString();
						insert.Parameters["$address"].Value = Db(row.Address);
						insert.Parameters["$parcel"].Value = Db(row.Parcel);
						insert.Parameters["$damage_pct"].Value = Db(row.PercentDamage);
						insert.Parameters[$"${ClassField}"].Value = ClassName(row.Class);
						insert.Parameters["$building_value"].Value = Db(row.BuildingValue);
						insert.Parameters["$repair_cost"].Value = Db(row.RepairCost);
						insert.Parameters["$inspection_date"].Value = row.InspectionDate is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value;
						insert.Parameters["$firm_zone"].Value = Db(row.FirmZone);
						if (options.IncludeOwnerNames)
						{
							insert.Parameters["$owner_name"].Value = Db(row.OwnerName);
						}
						insert.ExecuteNonQuery();
						result.Features++;
					}
				}

				using (SqliteCommand contents = db.CreateCommand())
				{
					contents.Transaction = tx;
					contents.CommandText = "INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, min_x, min_y, max_x, max_y, srs_id) " +
						"VALUES ($t, 'features', $i, $d, $c, $x0, $y0, $x1, $y1, 4326)";
					contents.Parameters.AddWithValue("$t", layer);
					contents.Parameters.AddWithValue("$i", layer);
					contents.Parameters.AddWithValue("$d", options.LayerTitle ?? "");
					contents.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
					bool any = result.Features > 0;
					contents.Parameters.AddWithValue("$x0", any ? minX : DBNull.Value);
					contents.Parameters.AddWithValue("$y0", any ? minY : DBNull.Value);
					contents.Parameters.AddWithValue("$x1", any ? maxX : DBNull.Value);
					contents.Parameters.AddWithValue("$y1", any ? maxY : DBNull.Value);
					contents.ExecuteNonQuery();
				}
				Execute(db, tx, $"INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m) VALUES ('{layer}', 'geom', 'POINT', 4326, 0, 0)");

				using (SqliteCommand style = db.CreateCommand())
				{
					style.Transaction = tx;
					style.CommandText = "INSERT INTO layer_styles (f_table_catalog, f_table_schema, f_table_name, f_geometry_column, styleName, styleQML, styleSLD, useAsDefault, description, owner, ui, update_time) " +
						"VALUES ('', '', $t, 'geom', 'Determination', $qml, '', 1, 'Coloured by determination class', '', '', $u)";
					style.Parameters.AddWithValue("$t", layer);
					style.Parameters.AddWithValue("$qml", QgisStyleXml());
					style.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
					style.ExecuteNonQuery();
				}
				tx.Commit();
			}
			File.Move(temp, path, overwrite: true);
		}
		finally
		{
			File.Delete(temp);
		}
		return result;
	}

	/// <summary>A GeoPackage geometry blob for a WGS 84 point: "GP" header (version 0, little-endian, no envelope, srs 4326) then WKB.</summary>
	public static byte[] PointBlob(double x, double y)
	{
		byte[] blob = new byte[8 + 21];
		blob[0] = (byte)'G';
		blob[1] = (byte)'P';
		blob[2] = 0;       // version 0
		blob[3] = 0x01;    // flags: little-endian, envelope type 0 (none), not empty, standard binary
		BitConverter.TryWriteBytes(blob.AsSpan(4, 4), 4326);
		blob[8] = 0x01;    // WKB byte order: little-endian
		BitConverter.TryWriteBytes(blob.AsSpan(9, 4), 1u);   // wkbPoint
		BitConverter.TryWriteBytes(blob.AsSpan(13, 8), x);
		BitConverter.TryWriteBytes(blob.AsSpan(21, 8), y);
		return blob;
	}

	private static void CreateSystemTables(SqliteConnection db, SqliteTransaction tx)
	{
		Execute(db, tx, "CREATE TABLE gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER NOT NULL PRIMARY KEY, organization TEXT NOT NULL, organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT)");
		Execute(db, tx, "CREATE TABLE gpkg_contents (table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT UNIQUE, description TEXT DEFAULT '', last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER, CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id))");
		Execute(db, tx, "CREATE TABLE gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL, srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL, CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name), CONSTRAINT fk_gc_tn FOREIGN KEY (table_name) REFERENCES gpkg_contents(table_name), CONSTRAINT fk_gc_srs FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id))");
		Execute(db, tx, "CREATE TABLE layer_styles (id INTEGER PRIMARY KEY AUTOINCREMENT, f_table_catalog TEXT(256), f_table_schema TEXT(256), f_table_name TEXT(256), f_geometry_column TEXT(256), styleName TEXT(30), styleQML TEXT, styleSLD TEXT, useAsDefault BOOLEAN, description TEXT, owner TEXT(30), ui TEXT(30), update_time DATETIME DEFAULT CURRENT_TIMESTAMP)");

		const string wgs84 = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]],AUTHORITY[\"EPSG\",\"4326\"]]";
		using SqliteCommand srs = db.CreateCommand();
		srs.Transaction = tx;
		srs.CommandText = "INSERT INTO gpkg_spatial_ref_sys (srs_name, srs_id, organization, organization_coordsys_id, definition, description) VALUES ($n, $i, $o, $c, $d, $s)";
		SqliteParameter n = srs.Parameters.Add("$n", SqliteType.Text);
		SqliteParameter i = srs.Parameters.Add("$i", SqliteType.Integer);
		SqliteParameter o = srs.Parameters.Add("$o", SqliteType.Text);
		SqliteParameter c = srs.Parameters.Add("$c", SqliteType.Integer);
		SqliteParameter d = srs.Parameters.Add("$d", SqliteType.Text);
		SqliteParameter s = srs.Parameters.Add("$s", SqliteType.Text);
		foreach ((string name, int id, string org, int orgId, string def, string desc) in new[]
		{
			("Undefined Cartesian SRS", -1, "NONE", -1, "undefined", "undefined Cartesian coordinate reference system"),
			("Undefined geographic SRS", 0, "NONE", 0, "undefined", "undefined geographic coordinate reference system"),
			("WGS 84 geodetic", 4326, "EPSG", 4326, wgs84, "longitude/latitude coordinates in decimal degrees on the WGS 84 spheroid"),
		})
		{
			n.Value = name;
			i.Value = id;
			o.Value = org;
			c.Value = orgId;
			d.Value = def;
			s.Value = desc;
			srs.ExecuteNonQuery();
		}
	}

	private static void CreateFeatureTable(SqliteConnection db, SqliteTransaction tx, string layer, GisExportOptions options)
	{
		string owner = options.IncludeOwnerNames ? ", owner_name TEXT" : "";
		Execute(db, tx, $"CREATE TABLE \"{layer}\" (fid INTEGER PRIMARY KEY AUTOINCREMENT, geom POINT, structure_id TEXT, assessment_id TEXT, address TEXT, parcel TEXT, damage_pct REAL, {ClassField} TEXT, building_value REAL, repair_cost REAL, inspection_date TEXT, firm_zone TEXT{owner})");
	}

	private static void Execute(SqliteConnection db, string sql) => Execute(db, null, sql);

	private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
	{
		using SqliteCommand cmd = db.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	private static object Db(string s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s;

	private static object Db(decimal? v) => v is decimal d ? (double)d : DBNull.Value;

	private static GeoFileSkipped Skip(GisRow row) => new GeoFileSkipped(row.AssessmentID, row.StructureID, row.Address ?? "", "No latitude/longitude");

	// ───────── GeoJSON ─────────

	/// <summary>RFC 7946 FeatureCollection of points, coordinates in [longitude, latitude] order.</summary>
	public static GisExportResult WriteGeoJson(string path, IEnumerable<GisRow> rows, GisExportOptions options)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(rows);
		options ??= new GisExportOptions();
		GisExportResult result = new GisExportResult { Path = path };
		string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		using MemoryStream buffer = new MemoryStream();
		using (Utf8JsonWriter w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
		{
			w.WriteStartObject();
			w.WriteString("type", "FeatureCollection");
			w.WriteStartArray("features");
			foreach (GisRow row in rows)
			{
				if (!row.HasLocation)
				{
					result.Skipped.Add(Skip(row));
					continue;
				}
				w.WriteStartObject();
				w.WriteString("type", "Feature");
				w.WriteStartObject("geometry");
				w.WriteString("type", "Point");
				w.WriteStartArray("coordinates");
				w.WriteNumberValue(row.Longitude.Value);
				w.WriteNumberValue(row.Latitude.Value);
				w.WriteEndArray();
				w.WriteEndObject();
				w.WriteStartObject("properties");
				w.WriteString("structure_id", row.StructureID.ToString());
				w.WriteString("assessment_id", row.AssessmentID.ToString());
				Nullable(w, "address", row.Address);
				Nullable(w, "parcel", row.Parcel);
				Nullable(w, "damage_pct", row.PercentDamage);
				w.WriteString(ClassField, ClassName(row.Class));
				Nullable(w, "building_value", row.BuildingValue);
				Nullable(w, "repair_cost", row.RepairCost);
				Nullable(w, "inspection_date", row.InspectionDate is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
				Nullable(w, "firm_zone", row.FirmZone);
				if (options.IncludeOwnerNames)
				{
					Nullable(w, "owner_name", row.OwnerName);
				}
				w.WriteEndObject();
				w.WriteEndObject();
				result.Features++;
			}
			w.WriteEndArray();
			w.WriteEndObject();
		}
		File.WriteAllBytes(path, buffer.ToArray());
		return result;
	}

	private static void Nullable(Utf8JsonWriter w, string name, string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			w.WriteNull(name);
		}
		else
		{
			w.WriteString(name, value);
		}
	}

	private static void Nullable(Utf8JsonWriter w, string name, decimal? value)
	{
		if (value is decimal d)
		{
			w.WriteNumber(name, d);
		}
		else
		{
			w.WriteNull(name);
		}
	}

	// ───────── QGIS style ─────────

	/// <summary>Writes a QGIS layer style (.qml) that colours points by determination class.</summary>
	public static void WriteQgisStyle(string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		File.WriteAllText(path, QgisStyleXml(), new UTF8Encoding(false));
	}

	/// <summary>The QML text: a categorized renderer on the <see cref="ClassField"/> field, one round marker per class.</summary>
	public static string QgisStyleXml()
	{
		(DeterminationClass Class, string Rgb)[] colours =
		{
			(DeterminationClass.Substantial, "198,40,40,255"),
			(DeterminationClass.Close, "240,140,0,255"),
			(DeterminationClass.NotSubstantial, "46,139,75,255"),
			(DeterminationClass.Undetermined, "138,148,163,255"),
			(DeterminationClass.Incomplete, "190,196,205,255"),
		};
		StringBuilder q = new StringBuilder();
		q.Append("<!DOCTYPE qgis PUBLIC 'http://mrcc.com/qgis.dtd' 'SYSTEM'>\n");
		q.Append("<qgis version=\"3.34.0\" styleCategories=\"Symbology\">\n");
		q.Append($"  <renderer-v2 type=\"categorizedSymbol\" attr=\"{ClassField}\" symbollevels=\"0\" enableorderby=\"0\" forceraster=\"0\">\n");
		q.Append("    <categories>\n");
		for (int i = 0; i < colours.Length; i++)
		{
			string label = ClassName(colours[i].Class);
			q.Append($"      <category value=\"{label}\" label=\"{label}\" symbol=\"{i}\" render=\"true\"/>\n");
		}
		q.Append("    </categories>\n");
		q.Append("    <symbols>\n");
		for (int i = 0; i < colours.Length; i++)
		{
			q.Append($"      <symbol type=\"marker\" name=\"{i}\" alpha=\"1\" force_rhr=\"0\" clip_to_extent=\"1\">\n");
			q.Append("        <layer class=\"SimpleMarker\" enabled=\"1\" locked=\"0\" pass=\"0\">\n");
			q.Append("          <Option type=\"Map\">\n");
			q.Append($"            <Option name=\"color\" value=\"{colours[i].Rgb}\" type=\"QString\"/>\n");
			q.Append("            <Option name=\"name\" value=\"circle\" type=\"QString\"/>\n");
			q.Append("            <Option name=\"size\" value=\"3\" type=\"QString\"/>\n");
			q.Append("            <Option name=\"size_unit\" value=\"MM\" type=\"QString\"/>\n");
			q.Append("            <Option name=\"outline_color\" value=\"255,255,255,255\" type=\"QString\"/>\n");
			q.Append("            <Option name=\"outline_style\" value=\"solid\" type=\"QString\"/>\n");
			q.Append("            <Option name=\"outline_width\" value=\"0.3\" type=\"QString\"/>\n");
			q.Append("            <Option name=\"outline_width_unit\" value=\"MM\" type=\"QString\"/>\n");
			q.Append("          </Option>\n");
			q.Append("        </layer>\n");
			q.Append("      </symbol>\n");
		}
		q.Append("    </symbols>\n");
		q.Append("  </renderer-v2>\n");
		q.Append("</qgis>\n");
		return q.ToString();
	}
}
