using System.IO.Compression;
using System.Xml.Linq;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Interop.Tests;

/// <summary>KMZ GeoFile: icons by type and % damaged, valid KML, the 8 FEMA PNGs, skipped structures.</summary>
public class GeoFileExportTests
{
	[Theory]
	[InlineData(1, null, "ResProperty.png")]
	[InlineData(1, 50.0, "r_txt_red.png")]
	[InlineData(1, 49.9, "r_txt_green.png")]
	[InlineData(1, 0.0, "r_txt_green.png")]
	[InlineData(2, null, "NonResProperty.png")]
	[InlineData(2, 50.0, "n_txt_red.png")]
	[InlineData(2, 49.9, "n_txt_yellow.png")]
	[InlineData(2, 40.1, "n_txt_yellow.png")]
	[InlineData(2, 40.0, "n_txt_green.png")]
	[InlineData(null, 75.0, "n_txt_red.png")]
	public void IconFor_MatchesOriginalThresholds(int? structureType, double? percent, string icon) =>
		Assert.Equal(icon, GeoFileExport.IconFor(structureType, (decimal?)percent));

	[Fact]
	public void WriteKmz_ZipsKmlAndIcons_SkipsStructuresWithoutCoordinates()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		ranch.Property.Latitude = 39.103100m;
		ranch.Property.Longitude = -84.512000m;
		ranch.Property.StreetName = "River & <Main>";

		PropertyRecord shop = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 2, StreetNumber = null, StreetName = "Elm" });
		AssessmentFile noCoordinates = t.Store.CreateAssessment(shop.PropertyID, applyDefaults: false);

		string path = Path.Combine(t.Folder, "geo.kmz");
		GeoFileResult result = GeoFileExport.WriteKmz(new[] { ranch, noCoordinates }, t.Store.Reference, path);

		Assert.Equal(1, result.Placemarks);
		GeoFileSkipped skipped = Assert.Single(result.Skipped);
		Assert.Equal(noCoordinates.Assessment.AssessmentID, skipped.AssessmentID);
		Assert.Equal("Elm", skipped.Address);

		using ZipArchive zip = ZipFile.OpenRead(path);
		Assert.Equal(new[] { GeoFileExport.KmlEntryName }.Concat(GeoFileExport.IconFiles), zip.Entries.Select(e => e.FullName));
		foreach (string icon in GeoFileExport.IconFiles)
		{
			using Stream s = zip.GetEntry(icon)!.Open();
			byte[] head = new byte[8];
			s.ReadExactly(head);
			Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, head);   // PNG signature
		}
		long legendSize = zip.GetEntry("legend-transparent.png")!.Length;
		Assert.Equal(new FileInfo(Path.Combine(TestPaths.Root, "src", "Sde.Interop", "Assets", "legend-transparent.png")).Length, legendSize);

		using StreamReader reader = new StreamReader(zip.GetEntry(GeoFileExport.KmlEntryName)!.Open());
		XDocument kml = XDocument.Parse(reader.ReadToEnd());
		XNamespace ns = "http://earth.google.com/kml/2.2";
		Assert.Equal("legend-transparent.png", kml.Descendants(ns + "ScreenOverlay").Single().Element(ns + "Icon")!.Element(ns + "href")!.Value.Trim());
		XElement placemark = kml.Descendants(ns + "Placemark").Single();
		Assert.Equal("123 River & <Main> Street", placemark.Element(ns + "name")!.Value.Trim());
		Assert.Equal("r_txt_red.png", placemark.Descendants(ns + "href").Single().Value.Trim());
		Assert.Equal("-84.512,39.1031,0", placemark.Descendants(ns + "coordinates").Single().Value.Trim());
		Assert.Contains("75.0% damaged", placemark.Element(ns + "description")!.Value);
	}
}
