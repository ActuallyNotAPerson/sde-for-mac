using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>The Map and GIS settings of a database: the background map's row, flood zones at start, owner names in GIS files.</summary>
public class MapSettingsStoreTests
{
	[Fact]
	public void ANewDatabase_HasNoBackgroundChosen_FloodZonesOff_AndLeavesOwnerNamesOut()
	{
		using TempDatabase t = new TempDatabase();
		Assert.Null(t.Store.GetMapBasemapId());
		Assert.False(t.Store.GetMapFloodZonesAtStart());
		Assert.True(t.Store.GetGisLeaveOutOwnerNames());
	}

	[Fact]
	public void TheBackground_IsOneBasemapRow_UnderEveryLayer()
	{
		using TempDatabase t = new TempDatabase();
		MapLayerRecord first = t.Store.SetMapBasemapId("usgs-imagery");
		MapLayerRecord again = t.Store.SetMapBasemapId("osm");
		Assert.Equal(first.LayerID, again.LayerID);
		Assert.Equal("osm", t.Store.GetMapBasemapId());
		MapLayerRecord row = Assert.Single(t.Store.GetMapLayers(), r => r.SourceKind == MapLayerKinds.Basemap);
		Assert.Equal(-1, row.SortOrder);
		Assert.Equal(again.UpdatedOn, t.Store.SetMapBasemapId("osm").UpdatedOn);
	}

	[Fact]
	public void TheSwitches_AreKeptInTheDatabase()
	{
		using TempDatabase t = new TempDatabase();
		t.Store.SetMapFloodZonesAtStart(true);
		t.Store.SetGisLeaveOutOwnerNames(false);
		SdeStore reopened = new SdeStore(SdeDatabase.Open(t.Database.FilePath));
		Assert.True(reopened.GetMapFloodZonesAtStart());
		Assert.False(reopened.GetGisLeaveOutOwnerNames());
	}
}
