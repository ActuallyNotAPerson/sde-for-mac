namespace Sde.Data;

/// <summary>
/// The Map and GIS settings of a database (Settings, then Map and GIS): the map's background, whether FEMA's flood zones
/// show when the map opens, and whether GIS files leave owner names out unless asked. All are Mac-only: the background
/// is the basemap row of the MapLayer table, the others are MacSetting values.
/// </summary>
public sealed partial class SdeStore
{
	/// <summary>The MacSetting key: true when the map turns FEMA's flood zones on each time it opens.</summary>
	public const string MapFloodZonesAtStartKey = "Map.FloodZonesAtStart";

	/// <summary>The MacSetting key: true (the default) when GIS files start without owners' names.</summary>
	public const string GisLeaveOutOwnerNamesKey = "Gis.LeaveOutOwnerNames";

	/// <summary>The id of the map's background ("usgs-topo"), or null when none has been chosen yet.</summary>
	public string GetMapBasemapId() => GetMapLayers().FirstOrDefault(r => r.SourceKind == MapLayerKinds.Basemap)?.Source;

	/// <summary>Saves the map's background in the basemap row (made the first time), and returns the row.</summary>
	public MapLayerRecord SetMapBasemapId(string basemapId)
	{
		ArgumentException.ThrowIfNullOrEmpty(basemapId);
		MapLayerRecord existing = GetMapLayers().FirstOrDefault(r => r.SourceKind == MapLayerKinds.Basemap);
		if (existing != null && existing.Source == basemapId)
		{
			return existing;
		}
		MapLayerRecord row = existing ?? new MapLayerRecord { Name = "Basemap", SourceKind = MapLayerKinds.Basemap, SortOrder = -1 };
		row.Source = basemapId;
		return SaveMapLayer(row);
	}

	/// <summary>True when the map turns FEMA's flood zones on each time it opens (off unless chosen).</summary>
	public bool GetMapFloodZonesAtStart() => GetSetting(MapFloodZonesAtStartKey, false);

	public void SetMapFloodZonesAtStart(bool on) => SetSetting(MapFloodZonesAtStartKey, on);

	/// <summary>True when GIS files start without owners' names (the default, because names are personal information).</summary>
	public bool GetGisLeaveOutOwnerNames() => GetSetting(GisLeaveOutOwnerNamesKey, true);

	public void SetGisLeaveOutOwnerNames(bool leaveOut) => SetSetting(GisLeaveOutOwnerNamesKey, leaveOut);
}
