using Sde.Core;
using Sde.Core.Records;
using Sde.Interop.Online;

namespace Sde.Interop.Tests;

/// <summary>Mapping NFHL answers onto SDE's FIRM zone and floodway lookups, using the real seed data.</summary>
public class FloodInfoMapperTests
{
	private static FloodInfo Info(string zone, string subtype = null, bool floodway = false, decimal? bfe = null) =>
		new FloodInfo(zone, subtype, floodway, bfe, null, null, null, null, null, null, DateTime.UtcNow);

	private static ProposedValue Zone(ReferenceData data, FloodInfo info) =>
		FloodInfoMapper.Propose(info, data).FirstOrDefault(p => p.Member == nameof(PropertyRecord.FIRMZone));

	[Theory]
	[InlineData("A", 29)]
	[InlineData("AE", 30)]
	[InlineData("AH", 31)]
	[InlineData("AO", 32)]
	[InlineData("AR", 33)]
	[InlineData("A1", 34)]
	[InlineData("A30", 34)]
	[InlineData("A99", 34)]
	[InlineData("V", 35)]
	[InlineData("V1", 35)]
	[InlineData("V30", 35)]
	[InlineData("VE", 36)]
	[InlineData("X", 37)]
	[InlineData("B", 37)]
	[InlineData("C", 37)]
	[InlineData("x", 37)]
	[InlineData("D", 38)]
	public void Zone_MapsToSeedLookupId(string zone, int expectedId)
	{
		using TempDatabase t = new TempDatabase();
		ProposedValue p = Zone(t.Store.Reference, Info(zone));
		Assert.NotNull(p);
		Assert.Equal(expectedId, p.Value);
		Assert.Equal(t.Store.Reference.LookupName(expectedId), p.Display);
	}

	[Theory]
	[InlineData("AREA OF MINIMAL FLOOD HAZARD", null)]
	[InlineData("0.2 PCT ANNUAL CHANCE FLOOD HAZARD", null)]
	[InlineData("X", "0.2 PCT ANNUAL CHANCE FLOOD HAZARD")]
	[InlineData("X", "AREA OF MINIMAL FLOOD HAZARD")]
	public void Zone_MinimalHazardWordings_MapToX(string zone, string subtype)
	{
		using TempDatabase t = new TempDatabase();
		Assert.Equal(37, Zone(t.Store.Reference, Info(zone, subtype)).Value);
	}

	[Theory]
	[InlineData("OPEN WATER")]
	[InlineData("AREA NOT INCLUDED")]
	[InlineData("")]
	[InlineData(null)]
	public void UnknownZone_ProposesNothingForZoneOrFloodway(string zone)
	{
		using TempDatabase t = new TempDatabase();
		IReadOnlyList<ProposedValue> list = FloodInfoMapper.Propose(Info(zone, floodway: true), t.Store.Reference);
		Assert.DoesNotContain(list, p => p.Member == nameof(PropertyRecord.FIRMZone) || p.Member == nameof(PropertyRecord.RegulatoryFloodway));
	}

	[Fact]
	public void Floodway_MapsToYesOrNo()
	{
		using TempDatabase t = new TempDatabase();
		ProposedValue yes = FloodInfoMapper.Propose(Info("AE", "FLOODWAY", true), t.Store.Reference).Single(p => p.Member == nameof(PropertyRecord.RegulatoryFloodway));
		ProposedValue no = FloodInfoMapper.Propose(Info("AE"), t.Store.Reference).Single(p => p.Member == nameof(PropertyRecord.RegulatoryFloodway));
		Assert.Equal(40, yes.Value);
		Assert.Equal("Yes", yes.Display);
		Assert.Equal(41, no.Value);
	}

	[Fact]
	public void FullAnswer_ProposesEveryField_WithMatchingTypes()
	{
		using TempDatabase t = new TempDatabase();
		FloodInfo info = new FloodInfo("AE", null, false, 12.5m, "NAVD88", "11001C0359E", "E", new DateTime(2014, 2, 24), "110001", "DISTRICT OF COLUMBIA", DateTime.UtcNow);
		IReadOnlyList<ProposedValue> list = FloodInfoMapper.Propose(info, t.Store.Reference);

		Assert.Equal(new[] { "FIRMZone", "RegulatoryFloodway", "BFE", "FIRMPanelNumber", "PanelSuffix", "FIRMPanelDate", "NFIPCID", "NFIPCommunityName" },
			list.Select(p => p.Member));
		Assert.Equal(12.5m, list.Single(p => p.Member == "BFE").Value);
		Assert.Equal("12.5 ft (NAVD88)", list.Single(p => p.Member == "BFE").Display);
		Assert.Equal(new DateTime(2014, 2, 24), list.Single(p => p.Member == "FIRMPanelDate").Value);
		Assert.Equal("110001", list.Single(p => p.Member == "NFIPCID").Value);

		// Every member named exists on the record with a compatible type, so a caller can set it by reflection.
		foreach (ProposedValue p in list)
		{
			System.Reflection.PropertyInfo prop = typeof(PropertyRecord).GetProperty(p.Member);
			Assert.NotNull(prop);
			Type target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
			Assert.IsType(target, p.Value);
		}
	}

	[Fact]
	public void MissingParts_AreSkipped_AndNullInfoProposesNothing()
	{
		using TempDatabase t = new TempDatabase();
		IReadOnlyList<ProposedValue> list = FloodInfoMapper.Propose(Info("X"), t.Store.Reference);
		Assert.Equal(new[] { "FIRMZone", "RegulatoryFloodway" }, list.Select(p => p.Member));
		Assert.Empty(FloodInfoMapper.Propose((FloodInfo)null, t.Store.Reference));
		Assert.Empty(FloodInfoMapper.Propose((GeocodeMatch)null));
	}

	[Fact]
	public void Geocode_ProposesLatitudeAndLongitude_ToSixDecimals()
	{
		IReadOnlyList<ProposedValue> list = FloodInfoMapper.Propose(new GeocodeMatch(38.889912345, -77.009111234, "1 1ST ST NE"));
		Assert.Equal(new[] { "Latitude", "Longitude" }, list.Select(p => p.Member));
		Assert.Equal(38.889912m, list[0].Value);
		Assert.Equal(-77.009111m, list[1].Value);
		Assert.Equal("-77.009111", list[1].Display);
	}
}
