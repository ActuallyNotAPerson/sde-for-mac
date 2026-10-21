using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Interop.Tests;

/// <summary>GeoPackage, GeoJSON and QGIS style output.</summary>
public class GisExportTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "sde-gis-" + Guid.NewGuid().ToString("N"));

	public GisExportTests() => Directory.CreateDirectory(_folder);

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_folder, recursive: true);
	}

	private static GisRow Row(string street, decimal? percent, string message, decimal? lat, decimal? lon, string owner = "Pat Example") => new GisRow
	{
		StructureID = Guid.NewGuid(), AssessmentID = Guid.NewGuid(), Address = "12 " + street, Parcel = "P-" + street,
		Latitude = lat, Longitude = lon, PercentDamage = percent,
		Class = Determinations.Classify(message, percent),
		BuildingValue = 150000m, RepairCost = percent * 1500m, InspectionDate = new DateTime(2026, 5, 4), FirmZone = "AE", OwnerName = owner,
	};

	private static List<GisRow> Sample() => new()
	{
		Row("Oak Street", 75m, Determinations.SubstantialMessage, 39.1031m, -84.5120m),
		Row("Elm Street", 45m, Determinations.NotSubstantialMessage, 39.1040m, -84.5130m),
		Row("Pine Street", null, null, 39.1050m, -84.5140m),
		Row("Nowhere Street", 80m, Determinations.SubstantialMessage, null, null),
		Row("Zero Street", 80m, Determinations.SubstantialMessage, 0m, 0m),
	};

	private static object Scalar(SqliteConnection db, string sql)
	{
		using SqliteCommand cmd = db.CreateCommand();
		cmd.CommandText = sql;
		return cmd.ExecuteScalar();
	}

	private SqliteConnection Open(string path)
	{
		SqliteConnection db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
		db.Open();
		return db;
	}

	[Fact]
	public void GeoPackage_HasRequiredStructure()
	{
		string path = Path.Combine(_folder, "out.gpkg");
		GisExportResult result = GisExport.WriteGeoPackage(path, Sample(), new GisExportOptions());

		Assert.Equal(3, result.Features);
		Assert.Equal(2, result.SkippedNoCoordinates);

		using SqliteConnection db = Open(path);
		Assert.Equal(1196444487L, (long)Scalar(db, "PRAGMA application_id"));
		Assert.Equal(10300L, (long)Scalar(db, "PRAGMA user_version"));
		Assert.Equal("ok", (string)Scalar(db, "PRAGMA integrity_check"));
		Assert.Equal(3L, (long)Scalar(db, "SELECT COUNT(*) FROM gpkg_spatial_ref_sys WHERE srs_id IN (-1, 0, 4326)"));
		Assert.Equal("EPSG", (string)Scalar(db, "SELECT organization FROM gpkg_spatial_ref_sys WHERE srs_id = 4326"));
		Assert.Equal("features", (string)Scalar(db, "SELECT data_type FROM gpkg_contents WHERE table_name = 'structures'"));
		Assert.Equal(-84.514, (double)Scalar(db, "SELECT min_x FROM gpkg_contents"), 6);
		Assert.Equal(-84.512, (double)Scalar(db, "SELECT max_x FROM gpkg_contents"), 6);
		Assert.Equal("POINT", (string)Scalar(db, "SELECT geometry_type_name FROM gpkg_geometry_columns WHERE table_name = 'structures' AND column_name = 'geom'"));
		Assert.Equal(4326L, (long)Scalar(db, "SELECT srs_id FROM gpkg_geometry_columns"));
		Assert.Equal(3L, (long)Scalar(db, "SELECT COUNT(*) FROM structures"));

		// The geometry blob: GP, version 0, flags 1 (little-endian, no envelope), srs 4326, WKB point.
		byte[] blob = (byte[])Scalar(db, "SELECT geom FROM structures ORDER BY fid LIMIT 1");
		Assert.Equal(29, blob.Length);
		Assert.Equal((byte)'G', blob[0]);
		Assert.Equal((byte)'P', blob[1]);
		Assert.Equal(0, blob[2]);
		Assert.Equal(1, blob[3]);
		Assert.Equal(4326, BitConverter.ToInt32(blob, 4));
		Assert.Equal(1, blob[8]);
		Assert.Equal(1u, BitConverter.ToUInt32(blob, 9));
		Assert.Equal(-84.512, BitConverter.ToDouble(blob, 13), 9);
		Assert.Equal(39.1031, BitConverter.ToDouble(blob, 21), 9);

		Assert.Equal("Substantial", (string)Scalar(db, "SELECT determination FROM structures ORDER BY fid LIMIT 1"));
		Assert.Equal("Incomplete", (string)Scalar(db, "SELECT determination FROM structures WHERE address = '12 Pine Street'"));
		Assert.Equal("2026-05-04", (string)Scalar(db, "SELECT inspection_date FROM structures ORDER BY fid LIMIT 1"));

		// The style QGIS loads by default.
		Assert.Equal("structures", (string)Scalar(db, "SELECT f_table_name FROM layer_styles"));
		Assert.Equal("geom", (string)Scalar(db, "SELECT f_geometry_column FROM layer_styles"));
		Assert.Equal(1L, (long)Scalar(db, "SELECT useAsDefault FROM layer_styles"));
		Assert.Contains("categorizedSymbol", (string)Scalar(db, "SELECT styleQML FROM layer_styles"));
	}

	[Fact]
	public void GeoPackage_LeavesOwnerNamesOutByDefault_AndIncludesThemWhenAsked()
	{
		string plain = Path.Combine(_folder, "plain.gpkg");
		GisExport.WriteGeoPackage(plain, Sample(), new GisExportOptions());
		using (SqliteConnection db = Open(plain))
		{
			Assert.Equal(0L, (long)Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('structures') WHERE name = 'owner_name'"));
		}
		Assert.DoesNotContain("Pat Example", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(plain)));

		string named = Path.Combine(_folder, "named.gpkg");
		GisExport.WriteGeoPackage(named, Sample(), new GisExportOptions { IncludeOwnerNames = true });
		using SqliteConnection db2 = Open(named);
		Assert.Equal("Pat Example", (string)Scalar(db2, "SELECT owner_name FROM structures ORDER BY fid LIMIT 1"));
	}

	[Fact]
	public void GeoPackage_Overwrites_AndAllowsEmpty()
	{
		string path = Path.Combine(_folder, "again.gpkg");
		GisExport.WriteGeoPackage(path, Sample(), new GisExportOptions());
		GisExportResult empty = GisExport.WriteGeoPackage(path, new List<GisRow>(), new GisExportOptions());
		Assert.Equal(0, empty.Features);
		using SqliteConnection db = Open(path);
		Assert.Equal(0L, (long)Scalar(db, "SELECT COUNT(*) FROM structures"));
		Assert.False(File.Exists(path + ".tmp"));
	}

	[Fact]
	public void GeoPackage_RejectsUnsafeLayerNames()
	{
		Assert.Throws<ArgumentException>(() => GisExport.WriteGeoPackage(Path.Combine(_folder, "x.gpkg"), Sample(), new GisExportOptions { LayerName = "bad\"; DROP TABLE x" }));
	}

	[Fact]
	public void GeoPackage_ReadsBackWithOgrinfo()
	{
		string ogr = new[] { "/usr/bin/ogrinfo", "/usr/local/bin/ogrinfo", "/opt/homebrew/bin/ogrinfo" }.FirstOrDefault(File.Exists);
		if (ogr == null)
		{
			return; // GDAL is not installed here.
		}
		string path = Path.Combine(_folder, "ogr.gpkg");
		GisExport.WriteGeoPackage(path, Sample(), new GisExportOptions());
		ProcessStartInfo psi = new ProcessStartInfo(ogr) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (string a in new[] { "-al", "-so", path })
		{
			psi.ArgumentList.Add(a);
		}
		using Process p = Process.Start(psi)!;
		string output = p.StandardOutput.ReadToEnd();
		p.WaitForExit();
		Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
		Assert.Contains("Feature Count: 3", output);
		Assert.Contains("Geometry: Point", output);
		Assert.Contains("4326", output);
		foreach (string field in new[] { "structure_id", "assessment_id", "address", "parcel", "damage_pct", "determination", "building_value", "repair_cost", "inspection_date", "firm_zone" })
		{
			Assert.Contains(field + ":", output);
		}
		Assert.DoesNotContain("owner_name", output);
	}

	[Fact]
	public void GeoJson_IsRfc7946_LonLatOrder()
	{
		string path = Path.Combine(_folder, "out.geojson");
		GisExportResult result = GisExport.WriteGeoJson(path, Sample(), new GisExportOptions());
		Assert.Equal(3, result.Features);
		Assert.Equal(2, result.SkippedNoCoordinates);

		using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement root = doc.RootElement;
		Assert.Equal("FeatureCollection", root.GetProperty("type").GetString());
		JsonElement first = root.GetProperty("features")[0];
		Assert.Equal("Point", first.GetProperty("geometry").GetProperty("type").GetString());
		JsonElement coords = first.GetProperty("geometry").GetProperty("coordinates");
		Assert.Equal(-84.512, coords[0].GetDouble(), 6);
		Assert.Equal(39.1031, coords[1].GetDouble(), 6);
		JsonElement props = first.GetProperty("properties");
		Assert.Equal("Substantial", props.GetProperty("determination").GetString());
		Assert.Equal(75.0, props.GetProperty("damage_pct").GetDouble());
		Assert.False(props.TryGetProperty("owner_name", out _));
		Assert.Equal(JsonValueKind.Null, root.GetProperty("features")[2].GetProperty("properties").GetProperty("damage_pct").ValueKind);
	}

	[Fact]
	public void GeoJson_OwnerNamesOnlyWhenAsked()
	{
		string path = Path.Combine(_folder, "owners.geojson");
		GisExport.WriteGeoJson(path, Sample(), new GisExportOptions { IncludeOwnerNames = true });
		using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
		Assert.Equal("Pat Example", doc.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("owner_name").GetString());
	}

	[Fact]
	public void QgisStyle_IsCategorizedByDeterminationClass()
	{
		string path = Path.Combine(_folder, "style.qml");
		GisExport.WriteQgisStyle(path);
		System.Xml.Linq.XDocument xml = System.Xml.Linq.XDocument.Load(path);
		System.Xml.Linq.XElement renderer = xml.Root!.Element("renderer-v2")!;
		Assert.Equal("categorizedSymbol", (string)renderer.Attribute("type"));
		Assert.Equal("determination", (string)renderer.Attribute("attr"));
		string[] values = renderer.Element("categories")!.Elements("category").Select(c => (string)c.Attribute("value")).ToArray();
		Assert.Equal(Enum.GetValues<DeterminationClass>().Select(Determinations.Label).OrderBy(x => x), values.OrderBy(x => x));
		Assert.Equal(values.Length, renderer.Element("symbols")!.Elements("symbol").Count());
	}

	[Fact]
	public void GisRow_From_UsesAssessmentsAndTheSharedRule()
	{
		PropertyRecord p = new PropertyRecord { PropertyID = Guid.NewGuid(), StreetNumber = "5", StreetName = "Oak", OwnerFirstName = "Pat", OwnerLastName = "Example", Latitude = 39m, Longitude = -84m, ParcelNumber = " 123 " };
		AssessmentRecord a = new AssessmentRecord
		{
			AssessmentID = Guid.NewGuid(), PropertyID = p.PropertyID, PercentDamage = 42m, DamageMessage = Determinations.NotSubstantialMessage,
			ValueOfBuildingID = LookupIds.ValueMarketAppraisal, MarketAppraisal = 90000m, CostOfRepairsID = LookupIds.RepairContractorEstimate, ContractorEstimate = 37800m,
		};
		GisRow row = GisRow.From(new[] { new AssessmentFile { Property = p, Assessment = a } }, null).Single();
		Assert.Equal(DeterminationClass.Close, row.Class);
		Assert.Equal("5 Oak", row.Address);
		Assert.Equal("123", row.Parcel);
		Assert.Equal(90000m, row.BuildingValue);
		Assert.Equal(37800m, row.RepairCost);
		Assert.True(row.HasLocation);
	}
}
