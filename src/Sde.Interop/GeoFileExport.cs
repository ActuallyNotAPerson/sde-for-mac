using System.IO.Compression;
using System.Security;
using System.Text;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>An assessment left out of a GeoFile.</summary>
public sealed record GeoFileSkipped(Guid AssessmentID, Guid PropertyID, string Address, string Reason);

/// <summary>What <see cref="GeoFileExport.WriteKmz"/> wrote.</summary>
public sealed class GeoFileResult
{
	public string Path { get; init; }
	public int Placemarks { get; set; }

	/// <summary>Assessments left out because their structure has no latitude/longitude.</summary>
	public List<GeoFileSkipped> Skipped { get; } = new();
}

/// <summary>
/// "Generate Geo Reference File": a KMZ (zipped KML) for Google Earth with a legend overlay and one pin per
/// assessment, coloured by structure type and % damaged.
///
/// Port of decomp/wpfSDE/wpfSDE.Tools/ucGeoFileCreator.xaml.cs (CreateGepFile, GetIcon) and
/// wpfSDE.VisualTools/Utilities.ZipThisFile (SharpZipLib; System.IO.Compression here). The KML keeps the original's
/// layout: a ScreenOverlay legend (legend-transparent.png) and per-Placemark IconStyle with the same icon files.
///
/// Original bugs fixed:
/// <list type="bullet">
/// <item>Names went into the XML unescaped, so an address with "&amp;" or "&lt;" produced an invalid file.</item>
/// <item>An assessment without coordinates was written with empty &lt;coordinates&gt;; it is now skipped and reported.</item>
/// <item>The address was built in SQL as StreetNumber + ' ' + StreetName, which is NULL when either part is missing, and
/// the grid's .ToString() then crashed (ucGeoFileCreator.xaml.cs:87); parts are joined null-safely.</item>
/// <item>Coordinates were written as "lon, lat,0"; KML separates tuples with whitespace, so the space after the
/// first comma is dropped ("lon,lat,0", invariant culture).</item>
/// <item>The KML went to a temporary folder beside the program (not writable on macOS); the archive is written directly.
/// The KML entry is named doc.kml, the KMZ convention, instead of KMZInfo.KML.</item>
/// </list>
/// </summary>
public static class GeoFileExport
{
	/// <summary>The icon files zipped with the KML, in the order the original copied them.</summary>
	public static readonly IReadOnlyList<string> IconFiles = new[]
	{
		"n_txt_green.png", "n_txt_yellow.png", "n_txt_red.png", "r_txt_green.png", "r_txt_red.png",
		"NonResProperty.png", "ResProperty.png", "legend-transparent.png",
	};

	public const string KmlEntryName = "doc.kml";

	public static GeoFileResult WriteKmz(IEnumerable<AssessmentFile> assessments, ReferenceData reference, string kmzPath)
	{
		GeoFileResult result = new GeoFileResult { Path = kmzPath };
		string kml = BuildKml(assessments, reference, result);

		using FileStream fs = new FileStream(kmzPath, FileMode.Create, FileAccess.Write);
		using ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create);
		ZipArchiveEntry entry = zip.CreateEntry(KmlEntryName, CompressionLevel.Optimal);
		using (Stream s = entry.Open())
		{
			byte[] bytes = new UTF8Encoding(false).GetBytes(kml);
			s.Write(bytes, 0, bytes.Length);
		}
		foreach (string icon in IconFiles)
		{
			using Stream source = Icon(icon);
			using Stream target = zip.CreateEntry(icon, CompressionLevel.Optimal).Open();
			source.CopyTo(target);
		}
		return result;
	}

	/// <summary>
	/// The pin for a structure, exactly as GetIcon (ucGeoFileCreator.xaml.cs:179-226): residential red at ≥ 50 %, green
	/// below; non-residential red at ≥ 50 %, yellow below 50 %, green at ≤ 40 %; the plain house icon when there is no
	/// % damaged. Anything that is not residential (1) uses the non-residential icons, as the original's grid did.
	/// </summary>
	public static string IconFor(int? structureTypeId, decimal? percentDamage)
	{
		if (structureTypeId == 1)
		{
			string result = "ResProperty.png";
			if (percentDamage >= 50m)
			{
				result = "r_txt_red.png";
			}
			if (percentDamage < 50m)
			{
				result = "r_txt_green.png";
			}
			return result;
		}
		string nonRes = "NonResProperty.png";
		if (percentDamage >= 50m)
		{
			nonRes = "n_txt_red.png";
		}
		if (percentDamage < 50m)
		{
			nonRes = "n_txt_yellow.png";
		}
		if (percentDamage <= 40m)
		{
			nonRes = "n_txt_green.png";
		}
		return nonRes;
	}

	/// <summary>An embedded icon (FEMA's PNGs from extracted/images).</summary>
	public static Stream Icon(string fileName) =>
		typeof(GeoFileExport).Assembly.GetManifestResourceStream("Sde.Interop.Assets." + fileName)
		?? throw new ArgumentException("No embedded icon " + fileName);

	internal static string BuildKml(IEnumerable<AssessmentFile> assessments, ReferenceData reference, GeoFileResult result)
	{
		const string nl = "\r\n";
		StringBuilder k = new StringBuilder();
		k.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(nl);
		k.Append("<kml xmlns=\"http://earth.google.com/kml/2.2\">").Append(nl);
		k.Append("<Document>").Append(nl);
		k.Append("<ScreenOverlay> ").Append(nl);
		k.Append(" <name>Legend name</name> ").Append(nl);
		k.Append("<color>ffffffff</color> ").Append(nl);
		k.Append(" <Icon> ").Append(nl);
		k.Append(" <href>legend-transparent.png</href> ").Append(nl);
		k.Append(" </Icon> ").Append(nl);
		k.Append(" <overlayXY x=\"0\" y=\"1\" xunits=\"fraction\" yunits=\"fraction\"/> ").Append(nl);
		k.Append(" <screenXY x = \"0\" y = \"1\" xunits = \"fraction\" yunits = \"fraction\"/> ").Append(nl);
		k.Append("<rotationXY x = \"0\" y = \"0\" xunits = \"fraction\" yunits = \"fraction\"/> ").Append(nl);
		k.Append("<size x = \"0\" y = \"0\" xunits = \"fraction\" yunits = \"fraction\"/> ").Append(nl);
		k.Append(" </ScreenOverlay> ").Append(nl);

		foreach (AssessmentFile f in assessments)
		{
			PropertyRecord p = f.Property ?? new PropertyRecord();
			AssessmentRecord a = f.Assessment ?? new AssessmentRecord();
			string address = Text.Join(p.StreetNumber, p.StreetName, reference.LookupName(p.StreetSuffixID));
			if (p.Latitude is not decimal lat || p.Longitude is not decimal lon || (lat == 0m && lon == 0m))
			{
				result.Skipped.Add(new GeoFileSkipped(a.AssessmentID, p.PropertyID, address, "No latitude/longitude"));
				continue;
			}

			k.Append(" <Placemark> ").Append(nl);
			k.Append(" <Style> ").Append(nl);
			k.Append(" <IconStyle> ").Append(nl);
			k.Append(" <Icon>").Append(nl);
			k.Append("  <href>").Append(IconFor(p.StructureTypeID, a.PercentDamage)).Append("</href> ").Append(nl);
			k.Append(" <x>64</x> ").Append(nl);
			k.Append(" <y>32</y> ").Append(nl);
			k.Append(" <w>55</w> ").Append(nl);
			k.Append(" <h>55</h> ").Append(nl);
			k.Append(" </Icon> ").Append(nl);
			k.Append(" </IconStyle> ").Append(nl);
			k.Append(" </Style> ").Append(nl);
			k.Append(" <name>").Append(Escape(address.Length > 0 ? address : "(no address)")).Append("</name> ").Append(nl);
			k.Append(" <description>").Append(Escape(Description(p, a))).Append("</description> ").Append(nl);
			k.Append(" <Point> ").Append(nl);
			k.Append("  <coordinates>").Append(Coordinate(lon)).Append(',').Append(Coordinate(lat)).Append(",0</coordinates> ").Append(nl);
			k.Append(" </Point> ").Append(nl);
			k.Append(" </Placemark>").Append(nl);
			result.Placemarks++;
		}
		k.Append("</Document>").Append(nl);
		k.Append("</kml>");
		return k.ToString();
	}

	/// <summary>The original left the description empty; it now carries the type, % damaged and inspection date.</summary>
	private static string Description(PropertyRecord p, AssessmentRecord a)
	{
		List<string> parts = new() { p.StructureTypeID == 1 ? "Residential" : "Non-Residential" };
		if (a.PercentDamage is decimal pct)
		{
			parts.Add(pct.ToString("0.0", Text.Us) + "% damaged" + (string.IsNullOrWhiteSpace(a.DamageMessage) ? "" : " (" + a.DamageMessage.Trim() + ")"));
		}
		if (a.DateofInspection is DateTime d)
		{
			parts.Add("inspected " + Text.ShortDate(d));
		}
		return string.Join("; ", parts);
	}

	private static string Coordinate(decimal v) => v.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);

	private static string Escape(string s) => SecurityElement.Escape(s ?? "");
}
