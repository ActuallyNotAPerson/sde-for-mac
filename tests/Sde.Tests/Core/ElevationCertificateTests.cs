using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.ElevationCertificates;
using Sde.Core.Records;
using N = Sde.Core.ElevationCertificates.ElevationCertificateValues.Names;

namespace Sde.Tests.Core;

/// <summary>An elevation certificate's values read from its field names, and their mapping onto a structure's fields.</summary>
public class ElevationCertificateTests
{
	/// <summary>A filled certificate as FEMA's XFA form stores it (made-up values).</summary>
	private static Dictionary<string, string> Filled() => new Dictionary<string, string>
	{
		[N.StreetAddress] = "123 Example Road", [N.City] = "Exampleville", [N.State] = "35", [N.Zip] = "43702",
		[N.Latitude] = "39.712345", [N.Longitude] = "-84.178901", [N.Nad1927] = "0", [N.Nad1983] = "1", [N.Wgs84] = "0",
		[N.BuildingDiagram] = "1B",
		[N.CommunityName] = "Exampleville, City of", [N.CommunityNumber] = "399990", [N.County] = "Example County", [N.CommunityState] = "35",
		[N.PanelNumber] = "39113C0157", [N.PanelSuffix] = "E", [N.FirmIndexDate] = "2011-01-06", [N.PanelDate] = "2011-01-06",
		[N.FloodZone] = "AE", [N.Bfe] = "742.5", [N.BfeNavd88] = "1",
		[N.Benchmark] = "RM 12", [N.Navd88] = "1",
		[N.C2a] = "739.8", [N.C2aFeet] = "1", [N.C2f] = "738.4", [N.C2fFeet] = "1",
		["A1_BuildingOwnersName"] = "Pat Example", ["c2.e_measurement"] = " ",
	};

	[Fact]
	public void FromFields_ReadsTheFormsValues()
	{
		ElevationCertificateValues v = ElevationCertificateValues.FromFields(Filled());
		Assert.Equal(("123 Example Road", "Exampleville", "OH", "43702"), (v.StreetAddress, v.City, v.State, v.ZipCode));
		Assert.Equal((39.712345m, -84.178901m, "NAD 1983"), (v.Latitude.Value, v.Longitude.Value, v.HorizontalDatum));
		Assert.Equal("1B", v.BuildingDiagram);
		Assert.Equal(("Exampleville, City of", "399990", "Example County"), (v.CommunityName, v.CommunityNumber, v.County));
		Assert.Equal(("39113C0157", "E", new DateTime(2011, 1, 6)), (v.PanelNumber, v.PanelSuffix, v.PanelEffectiveDate.Value));
		Assert.Equal(("AE", 742.5m, "NAVD 1988", "NAVD 1988", "RM 12"), (v.FloodZone, v.Bfe.Value, v.BfeDatum, v.ElevationDatum, v.Benchmark));
		Assert.Equal(new EcElevation(739.8m, LengthUnit.Feet), v.TopOfBottomFloor);
		Assert.Equal(new EcElevation(738.4m, LengthUnit.Feet), v.LowestAdjacentGrade);
		Assert.Null(v.TopOfNextHigherFloor);
		Assert.True(v.HasAnyValue);
		Assert.Empty(v.Notes);
		Assert.False(v.Fields.ContainsKey("c2.e_measurement"));   // blank fields are left out
		Assert.Equal("Pat Example", v.Fields["A1_BuildingOwnersName"]);
	}

	[Fact]
	public void FromFields_BlankForm_HasNoValues()
	{
		ElevationCertificateValues v = ElevationCertificateValues.FromFields(new Dictionary<string, string> { [N.Nad1983] = "0", [N.BuildingDiagram] = " ", [N.State] = "51" });
		Assert.False(v.HasAnyValue);
		Assert.Null(v.State);      // "--", the list's blank choice
		Assert.Null(v.HorizontalDatum);
		Assert.False(ElevationCertificateValues.FromFields(null).HasAnyValue);
	}

	[Fact]
	public void FromFields_MetresDatumsAndAmbiguousNumbers()
	{
		Dictionary<string, string> f = new Dictionary<string, string>
		{
			[N.C2a] = "225.50", [N.C2aMeters] = "1", [N.Bfe] = "742, 743", [N.OtherDatum] = "1", [N.OtherDatumText] = "Local datum 3",
			[N.BfeNgvd29] = "1", [N.C2f] = "738.4 / 738.9", [N.PanelDate] = "01/06/2011", [N.Wgs84] = "Yes",
		};
		ElevationCertificateValues v = ElevationCertificateValues.FromFields(f);
		Assert.Equal(LengthUnit.Meters, v.TopOfBottomFloor.Unit);
		Assert.Equal(739.83m, v.TopOfBottomFloor.Feet);
		Assert.Null(v.Bfe);
		Assert.Equal("742, 743", v.BfeText);
		Assert.Equal(("Local datum 3", "NGVD 1929", "WGS 84"), (v.ElevationDatum, v.BfeDatum, v.HorizontalDatum));
		Assert.Null(v.LowestAdjacentGrade);
		Assert.Equal(new DateTime(2011, 1, 6), v.PanelEffectiveDate);
		Assert.Equal(new[] { "B9 holds more than one number (742, 743).", "C2.f holds more than one number (738.4 / 738.9)." }, v.Notes);
	}

	[Theory]
	[InlineData("39.712345", true, null, 39.712345)]
	[InlineData("39.712345° N", true, null, 39.712345)]
	[InlineData("39°42'44.4\"N", true, null, 39.712333)]
	[InlineData("N 39-42-44.4", true, null, 39.712333)]
	[InlineData("14.27 S", true, null, -14.27)]
	[InlineData("-84.178901", false, null, -84.178901)]
	[InlineData("84°10'44.0\" W", false, null, -84.178889)]
	[InlineData("Long. 84.178901 W", false, null, -84.178901)]
	[InlineData("144.75 E", false, null, 144.75)]
	[InlineData("144.75", false, "GU", 144.75)]
	[InlineData("91.5", true, null, null)]
	[InlineData("39 75 00", true, null, null)]
	[InlineData("N/A", true, null, null)]
	public void Coordinates_ReadDecimalOrDegreesMinutesSeconds(string text, bool latitude, string state, double? expected)
	{
		Assert.Equal(expected == null ? null : (decimal?)Math.Round((decimal)expected.Value, 6), ElevationCertificateValues.Coordinate(text, latitude, state));
	}

	[Fact]
	public void AnUnsignedUsLongitude_IsReadAsWest_WithANote()
	{
		List<string> notes = new List<string>();
		Assert.Equal(-84.178901m, ElevationCertificateValues.Coordinate("84.178901", false, "OH", notes));
		Assert.Equal("The longitude (84.178901) has no minus sign or W, so it was read as west.", Assert.Single(notes));
	}

	[Fact]
	public void Mapping_FillsTheStructureFields_ThroughFieldInput()
	{
		using TempDatabase t = new TempDatabase();
		ReferenceData reference = t.Store.Reference;
		EcFieldProposal p = ElevationCertificateMapping.ToFieldChanges(ElevationCertificateValues.FromFields(Filled()), reference);
		Assert.Empty(p.Notes);
		Assert.All(p.Changes, c => Assert.NotNull(FieldCatalog.Get(c.Key)));
		int ae = reference.OfType(LookupTypes.FirmZone).Single(l => l.LookupFieldName == "AE").LookupID;
		Assert.Equal(new[]
		{
			("Property.Latitude", "39.712345"), ("Property.Longitude", "-84.178901"),
			("Property.NFIPCommunityName", "Exampleville, City of"), ("Property.NFIPCID", "399990"),
			("Property.FIRMPanelNumber", "39113C0157"), ("Property.PanelSuffix", "E"), ("Property.FIRMPanelDate", "2011-01-06"),
			("Property.FIRMZone", ae.ToString()), ("Property.Datum", "NAVD88"), ("Property.BFE", "742.5"), ("Property.FirstFloorElevation", "739.8"),
		}, p.Changes.Select(c => (c.Key, c.Value)));

		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		FieldInput.Apply(f, p.Changes, reference, t.Store.Calculator);
		PropertyRecord s = f.Property;
		Assert.Equal((39.712345m, -84.178901m), (s.Latitude.Value, s.Longitude.Value));
		Assert.Equal(("Exampleville, City of", "399990"), (s.NFIPCommunityName, s.NFIPCID));
		Assert.Equal(("39113C0157", "E", new DateTime(2011, 1, 6)), (s.FIRMPanelNumber, s.PanelSuffix, s.FIRMPanelDate.Value));
		Assert.Equal((ae, 742.5m, 739.8m, "NAVD88"), (s.FIRMZone.Value, s.BFE.Value, s.FirstFloorElevation.Value, s.Datum));
		t.Store.SaveAssessment(f, new Sde.Data.SaveContext { Via = Sde.Data.SaveVia.EcImport });
		Assert.Equal(742.5m, t.Store.GetProperty(s.PropertyID).BFE);
	}

	[Fact]
	public void Mapping_LeavesOutWhatItCantTrust_AndSaysWhy()
	{
		using TempDatabase t = new TempDatabase();
		ReferenceData reference = t.Store.Reference;
		EcFieldProposal Map(Action<Dictionary<string, string>> change)
		{
			Dictionary<string, string> f = Filled();
			change(f);
			return ElevationCertificateMapping.ToFieldChanges(ElevationCertificateValues.FromFields(f), reference);
		}
		bool Has(EcFieldProposal p, string member) => p.Changes.Any(c => c.Key == "Property." + member);

		EcFieldProposal nad27 = Map(f => { f[N.Nad1983] = "0"; f[N.Nad1927] = "1"; });
		Assert.False(Has(nad27, nameof(PropertyRecord.Latitude)) || Has(nad27, nameof(PropertyRecord.Longitude)));
		Assert.Contains("A5 is in NAD 1927, which SDE can't convert. Enter the latitude and longitude by hand.", nad27.Notes);

		EcFieldProposal ao = Map(f => { f[N.FloodZone] = "AO"; f[N.Bfe] = "2"; });
		Assert.True(Has(ao, nameof(PropertyRecord.FIRMZone)));
		Assert.False(Has(ao, nameof(PropertyRecord.BFE)));
		Assert.Contains("In zone AO, B9 is a base flood depth (2), not an elevation, so the BFE was left blank.", ao.Notes);

		EcFieldProposal two = Map(f => f[N.FloodZone] = "AE, X");
		Assert.False(Has(two, nameof(PropertyRecord.FIRMZone)));
		Assert.Contains("B8 lists more than one zone (AE, X). Choose the FIRM zone by hand.", two.Notes);
		Assert.Contains("B8's zone (Q) isn't one of SDE's FIRM zones. Choose the FIRM zone by hand.", Map(f => f[N.FloodZone] = "Q").Notes);

		EcFieldProposal mixed = Map(f => { f[N.BfeNavd88] = "0"; f[N.BfeNgvd29] = "1"; });
		Assert.False(Has(mixed, nameof(PropertyRecord.BFE)));
		Assert.True(Has(mixed, nameof(PropertyRecord.FirstFloorElevation)));
		Assert.Contains("B9's BFE is in NGVD29 but the C2 elevations are in NAVD88. Check the conversion, then enter the BFE by hand.", mixed.Notes);

		EcFieldProposal metres = Map(f => { f[N.C2a] = "225.5"; f[N.C2aFeet] = "0"; f[N.C2aMeters] = "1"; });
		Assert.Contains(("Property.FirstFloorElevation", "739.83"), metres.Changes.Select(c => (c.Key, c.Value)));
		Assert.Contains("C2.a is in metres (225.5 m), converted to 739.83 ft.", metres.Notes);

		EcFieldProposal noC2Datum = Map(f => f[N.Navd88] = "0");
		Assert.Contains(("Property.Datum", "NAVD88"), noC2Datum.Changes.Select(c => (c.Key, c.Value)));
		Assert.Contains("C2 names no datum, so the datum is B11's.", noC2Datum.Notes);

		EcFieldProposal unnamed = Map(f => { f[N.Navd88] = "0"; f[N.OtherDatum] = "1"; f[N.BfeNavd88] = "0"; });
		Assert.False(Has(unnamed, nameof(PropertyRecord.Datum)));
		Assert.Contains("The certificate says its datum is another one but doesn't name it. Enter the datum by hand.", unnamed.Notes);

		EcFieldProposal na = Map(f => f[N.Bfe] = "N/A");
		Assert.False(Has(na, nameof(PropertyRecord.BFE)));
		Assert.Contains("B9 (N/A) isn't a number, so the BFE was left blank.", na.Notes);

		EcFieldProposal halfPosition = Map(f => f.Remove(N.Longitude));
		Assert.Contains("A5 has only one of latitude and longitude, so neither was used.", halfPosition.Notes);
		Assert.Empty(ElevationCertificateMapping.ToFieldChanges(null, reference).Changes);
	}

	[Fact]
	public void FirmZones_MapCodesToFemasChoices()
	{
		Assert.Equal("AE", FirmZones.ChoiceName(" ae "));
		Assert.Equal("A1 thru A99", FirmZones.ChoiceName("A12"));
		Assert.Equal("A1 thru A99", FirmZones.ChoiceName("A99"));
		Assert.Equal("V", FirmZones.ChoiceName("V7"));
		Assert.Equal("X", FirmZones.ChoiceName("B"));
		Assert.Equal("X", FirmZones.ChoiceName("X", "0.2 PCT ANNUAL CHANCE FLOOD HAZARD"));
		Assert.Null(FirmZones.ChoiceName("AREA NOT INCLUDED"));
		Assert.Null(FirmZones.ChoiceName(null));
	}
}
