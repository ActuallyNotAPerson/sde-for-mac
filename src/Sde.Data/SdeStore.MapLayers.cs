using Dapper;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>Where a map layer comes from (MapLayer.SourceKind).</summary>
public static class MapLayerKinds
{
	/// <summary>The structures themselves (one row holds their style and labels).</summary>
	public const string Structures = "Structures";

	/// <summary>A Shapefile, GeoJSON, GeoPackage or CSV copied into the layers GeoPackage.</summary>
	public const string File = "File";

	/// <summary>An ArcGIS REST FeatureServer or MapServer layer, downloaded into the layers GeoPackage.</summary>
	public const string ArcGisRest = "ArcGisRest";

	/// <summary>FEMA's National Flood Hazard Layer, drawn as tiles by FEMA's map service.</summary>
	public const string Nfhl = "Nfhl";

	/// <summary>The basemap (Source holds its id).</summary>
	public const string Basemap = "Basemap";
}

/// <summary>
/// A layer on the Map page (Mac-only MapLayer table, never exported): where it comes from, its feature table in the
/// layers GeoPackage, its place in the drawing order, and how it is drawn (style and label JSON, opacity, scale range).
/// </summary>
[Table("MapLayer")]
public sealed class MapLayerRecord
{
	[Key] public Guid LayerID { get; set; }

	public string Name { get; set; }

	/// <summary>See <see cref="MapLayerKinds"/>.</summary>
	public string SourceKind { get; set; }

	/// <summary>The original file path or web address (or the basemap's id).</summary>
	public string Source { get; set; }

	/// <summary>Its feature table in the layers GeoPackage, when copied in.</summary>
	public string TableName { get; set; }

	/// <summary>Point, Line or Polygon (empty when mixed or not features).</summary>
	public string GeometryType { get; set; }

	/// <summary>Higher is drawn on top.</summary>
	public int SortOrder { get; set; }

	public bool Visible { get; set; } = true;

	public double Opacity { get; set; } = 1;

	/// <summary>Shown only zoomed in past 1:MinScale (null for no limit).</summary>
	public double? MinScale { get; set; }

	/// <summary>Hidden zoomed in past 1:MaxScale (null for no limit).</summary>
	public double? MaxScale { get; set; }

	/// <summary>The style as JSON (Sde.Gis LayerStyle).</summary>
	public string Style { get; set; }

	/// <summary>The labels as JSON (Sde.Gis LabelStyle).</summary>
	public string Labels { get; set; }

	public bool Editable { get; set; }

	public DateTime? AddedOn { get; set; }

	public DateTime? UpdatedOn { get; set; }
}

/// <summary>A saved view of the map (Mac-only MapBookmark table): a name and an area in longitude and latitude.</summary>
[Table("MapBookmark")]
public sealed class MapBookmarkRecord
{
	[Key] public Guid BookmarkID { get; set; }

	public string Name { get; set; }

	public double MinX { get; set; }

	public double MinY { get; set; }

	public double MaxX { get; set; }

	public double MaxY { get; set; }

	public int SortOrder { get; set; }

	public DateTime? CreatedOn { get; set; }
}

public sealed partial class SdeStore
{
	/// <summary>Every map layer, bottom of the drawing order first.</summary>
	public List<MapLayerRecord> GetMapLayers() =>
		Database.Read(c => TableMap<MapLayerRecord>.Select(c, orderBy: "SortOrder, AddedOn").ToList());

	/// <summary>
	/// Adds (LayerID empty: it gets an id, and goes on top unless it has a place) or saves a map layer.
	/// </summary>
	public MapLayerRecord SaveMapLayer(MapLayerRecord layer)
	{
		ArgumentNullException.ThrowIfNull(layer);
		if (string.IsNullOrWhiteSpace(layer.Name))
		{
			throw new ArgumentException("Give the layer a name.");
		}
		Database.InTransaction((c, tx) =>
		{
			if (layer.LayerID == Guid.Empty)
			{
				layer.LayerID = Guid.NewGuid();
				layer.AddedOn = Clock();
				if (layer.SortOrder == 0)
				{
					layer.SortOrder = (c.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM MapLayer", transaction: tx) ?? 0) + 1;
				}
			}
			layer.UpdatedOn = Clock();
			TableMap<MapLayerRecord>.Upsert(c, layer, tx);
		});
		return layer;
	}

	/// <summary>Removes a map layer's row (its feature table in the layers GeoPackage is the caller's to delete).</summary>
	public void DeleteMapLayer(Guid layerId) =>
		Database.InTransaction((c, tx) => c.Execute("DELETE FROM MapLayer WHERE LayerID = @layerId", new { layerId }, tx));

	/// <summary>Puts the layers in this drawing order (bottom first); layers not named keep their place below.</summary>
	public void SetMapLayerOrder(IReadOnlyList<Guid> bottomFirst) => Database.InTransaction((c, tx) =>
	{
		for (int i = 0; i < bottomFirst.Count; i++)
		{
			c.Execute("UPDATE MapLayer SET SortOrder = @order WHERE LayerID = @id", new { order = i + 1, id = bottomFirst[i] }, tx);
		}
	});

	/// <summary>The saved map views, in the order they were made.</summary>
	public List<MapBookmarkRecord> GetMapBookmarks() =>
		Database.Read(c => TableMap<MapBookmarkRecord>.Select(c, orderBy: "SortOrder, CreatedOn").ToList());

	/// <summary>Adds (BookmarkID empty) or saves a map view. Names need not be unique.</summary>
	public MapBookmarkRecord SaveMapBookmark(MapBookmarkRecord bookmark)
	{
		ArgumentNullException.ThrowIfNull(bookmark);
		if (string.IsNullOrWhiteSpace(bookmark.Name))
		{
			throw new ArgumentException("Give the bookmark a name.");
		}
		bookmark.Name = bookmark.Name.Trim();
		if (!(bookmark.MaxX > bookmark.MinX) || !(bookmark.MaxY > bookmark.MinY))
		{
			throw new ArgumentException("A bookmark needs an area of the map.");
		}
		Database.InTransaction((c, tx) =>
		{
			if (bookmark.BookmarkID == Guid.Empty)
			{
				bookmark.BookmarkID = Guid.NewGuid();
				bookmark.CreatedOn = Clock();
				bookmark.SortOrder = (c.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM MapBookmark", transaction: tx) ?? 0) + 1;
			}
			TableMap<MapBookmarkRecord>.Upsert(c, bookmark, tx);
		});
		return bookmark;
	}

	public void DeleteMapBookmark(Guid bookmarkId) =>
		Database.InTransaction((c, tx) => c.Execute("DELETE FROM MapBookmark WHERE BookmarkID = @bookmarkId", new { bookmarkId }, tx));
}
