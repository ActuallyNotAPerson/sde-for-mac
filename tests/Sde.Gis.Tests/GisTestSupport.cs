using System.Diagnostics;
using NetTopologySuite.Algorithm.Distance;
using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Formats;

namespace Sde.Gis.Tests;

/// <summary>Paths to the synthetic fixtures (tools/gis/make_fixtures.sh) and small helpers shared by the GIS tests.</summary>
internal static class Fixtures
{
	public static string Path(params string[] parts) => System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures" }.Concat(parts).ToArray());

	public static string Town(string name) => Path("town", name);

	/// <summary>The WGS 84 source parcels every converted fixture is compared with.</summary>
	public static FeatureLayer SourceParcels() => GeoJsonFormat.Read(Town("parcels.geojson"));

	/// <summary>The largest distance between two shapes (discrete Hausdorff), in their units.</summary>
	public static double Distance(Geometry a, Geometry b) => DiscreteHausdorffDistance.Distance(a, b);

	/// <summary>Asserts that every parcel in <paramref name="layer"/> has the source parcel's shape to within <paramref name="degrees"/>.</summary>
	public static void AssertSameParcels(FeatureLayer layer, double degrees = 1e-7, string idField = "parcel_id")
	{
		FeatureLayer source = SourceParcels();
		Assert.Equal(source.Features.Count, layer.Features.Count);
		Dictionary<string, Feature> byId = source.Features.ToDictionary(f => (string)f["parcel_id"]);
		foreach (Feature f in layer.Features)
		{
			Feature expected = byId[(string)f[idField]];
			Assert.NotNull(f.Geometry);
			double d = Distance(expected.Geometry, f.Geometry);
			Assert.True(d <= degrees, $"{f[idField]} is {d} degrees from the source.");
			Assert.Equal(expected.Geometry.NumGeometries, f.Geometry.NumGeometries);
			Assert.Equal(((Polygon)expected.Geometry.GetGeometryN(0)).NumInteriorRings, ((Polygon)f.Geometry.GetGeometryN(0)).NumInteriorRings);
		}
	}
}

/// <summary>A temporary folder, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
	public TempFolder()
	{
		Folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sde-gis-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Folder);
	}

	public string Folder { get; }

	public string Path(string name) => System.IO.Path.Combine(Folder, name);

	/// <summary>Copies fixture files (all parts of a Shapefile, for example) into the folder.</summary>
	public void CopyTown(string stem, params string[] extensions)
	{
		foreach (string ext in extensions)
		{
			File.Copy(Fixtures.Town(stem + ext), Path(stem + ext));
		}
	}

	public void Dispose()
	{
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try
		{
			Directory.Delete(Folder, recursive: true);
		}
		catch (IOException)
		{
		}
	}
}

/// <summary>GDAL's command-line tools, from a system install or inside QGIS.app, for checking files other programs must open.</summary>
internal static class Gdal
{
	private static readonly Lazy<string> _ogrinfo = new Lazy<string>(() =>
		new[] { "/usr/bin/ogrinfo", "/usr/local/bin/ogrinfo", "/opt/homebrew/bin/ogrinfo" }
			.Concat(QgisApps().Select(app => System.IO.Path.Combine(app, "Contents", "MacOS", "ogrinfo")))
			.FirstOrDefault(File.Exists));

	/// <summary>ogrinfo, or null when GDAL isn't installed.</summary>
	public static string OgrInfo => _ogrinfo.Value;

	private static IEnumerable<string> QgisApps() =>
		Directory.Exists("/Applications") ? Directory.EnumerateDirectories("/Applications", "QGIS*.app").OrderByDescending(d => d) : Enumerable.Empty<string>();

	/// <summary>Runs ogrinfo with arguments; returns its exit code and output.</summary>
	public static (int ExitCode, string Output) RunOgrInfo(params string[] args)
	{
		ProcessStartInfo psi = new ProcessStartInfo(OgrInfo) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		SetQgisEnvironment(psi, OgrInfo);
		foreach (string a in args)
		{
			psi.ArgumentList.Add(a);
		}
		using Process p = Process.Start(psi);
		string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
		p.WaitForExit();
		return (p.ExitCode, output);
	}

	/// <summary>
	/// GDAL's GeoPackage validator (validate_gpkg.py) run by QGIS's Python, or null when QGIS isn't installed here.
	/// Returns the exit code and output.
	/// </summary>
	public static (int ExitCode, string Output)? ValidateGeoPackage(string path)
	{
		foreach (string app in QgisApps())
		{
			string python = System.IO.Path.Combine(app, "Contents", "MacOS", "python3.12");
			string script = System.IO.Path.Combine(app, "Contents", "Resources", "python3.12", "site-packages", "osgeo_utils", "samples", "validate_gpkg.py");
			if (!File.Exists(python) || !File.Exists(script))
			{
				continue;
			}
			ProcessStartInfo psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
			SetQgisEnvironment(psi, python);
			psi.Environment["PYTHONHOME"] = System.IO.Path.Combine(app, "Contents", "Frameworks");
			psi.Environment["PYTHONPATH"] = System.IO.Path.Combine(app, "Contents", "Resources", "python3.12", "site-packages") + ":" + System.IO.Path.Combine(app, "Contents", "Resources", "python3.12");
			psi.ArgumentList.Add(script);
			psi.ArgumentList.Add(path);
			using Process p = Process.Start(psi);
			string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
			p.WaitForExit();
			return (p.ExitCode, output);
		}
		return null;
	}

	private static void SetQgisEnvironment(ProcessStartInfo psi, string tool)
	{
		int i = tool.IndexOf(".app/", StringComparison.Ordinal);
		if (i < 0)
		{
			return;
		}
		string resources = System.IO.Path.Combine(tool.Substring(0, i + 4), "Contents", "Resources", "qgis");
		psi.Environment["PROJ_DATA"] = System.IO.Path.Combine(resources, "proj");
		psi.Environment["GDAL_DATA"] = System.IO.Path.Combine(resources, "gdal");
	}
}
