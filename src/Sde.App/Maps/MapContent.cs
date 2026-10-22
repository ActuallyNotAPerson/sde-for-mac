using Avalonia.Media;
using Sde.Gis.Features;
using Sde.Gis.Styles;

namespace Sde.App.Maps;

/// <summary>A dot on the map: which structure, where, what colour, and (optionally) its label.</summary>
public readonly record struct MapMarker(Guid Id, double Latitude, double Longitude, IBrush Fill, string Label = null);

/// <summary>What dragging does on the map.</summary>
public enum MapDrag
{
	/// <summary>Dragging moves the map. Holding Command and dragging draws a box instead.</summary>
	Pan,

	/// <summary>Dragging draws a box around the structures (or features) to select.</summary>
	Box,
}

/// <summary>What clicks on the map draw, when they draw a shape rather than pick something.</summary>
public enum MapSketch
{
	/// <summary>Clicks pick dots and features, or report the spot.</summary>
	None,

	/// <summary>Clicks add the corners of a line (measuring a distance).</summary>
	Line,

	/// <summary>Clicks add the corners of a polygon (measuring an area, or selecting what is inside).</summary>
	Polygon,
}

/// <summary>What clicks and drags do to the layer being edited.</summary>
public enum MapEditTool
{
	/// <summary>Click a shape to pick it; drag its corners, its midpoints (to add a corner) or the shape itself.</summary>
	Change,

	/// <summary>Each click adds a point.</summary>
	AddPoint,

	/// <summary>Clicks add the corners of a line; a double click or Return finishes it.</summary>
	AddLine,

	/// <summary>Clicks add the corners of an area; a double click or Return finishes it.</summary>
	AddArea,
}

/// <summary>
/// The unsaved edits to a layer, as the map draws them: the layer's own copies of changed and deleted features are
/// hidden, and the features as they are now are drawn over it. A full rebuild of a large layer only happens on save.
/// </summary>
public sealed class MapLayerEdits
{
	/// <summary>Ids of the layer's features not to draw from the layer itself (changed, deleted, or being changed).</summary>
	public IReadOnlyCollection<long> Hidden { get; init; } = Array.Empty<long>();

	/// <summary>The changed and new features as they are now (WGS 84), drawn over the layer in its style.</summary>
	public IReadOnlyList<Feature> Drawn { get; init; } = Array.Empty<Feature>();

	/// <summary>Changes whenever the edits change, so the map draws them again.</summary>
	public int Revision { get; init; }
}

/// <summary>
/// A layer of features for the map, independent of the map engine: the data (WGS 84), how to draw it and label it,
/// its opacity and scale range, and which features are selected. The map rebuilds what it draws when
/// <see cref="Revision"/> changes, and only the selection highlight when <see cref="SelectionRevision"/> does.
/// </summary>
public sealed class MapFeatureLayer
{
	/// <summary>The layer's id (MapLayer.LayerID).</summary>
	public string Key { get; init; }

	public string Name { get; init; }

	public FeatureLayer Data { get; init; }

	public LayerStyle Style { get; init; } = new LayerStyle();

	/// <summary>Labels, or null for none.</summary>
	public LabelStyle Labels { get; init; }

	/// <summary>Opacity and scale range.</summary>
	public LayerDisplay Display { get; init; } = new LayerDisplay();

	public bool Visible { get; init; } = true;

	/// <summary>Feature ids (<see cref="Feature.Id"/>) to highlight as selected.</summary>
	public IReadOnlyCollection<long> Selected { get; init; } = Array.Empty<long>();

	/// <summary>Changes whenever the data, style or labels change.</summary>
	public int Revision { get; init; }

	/// <summary>Changes whenever <see cref="Selected"/> changes.</summary>
	public int SelectionRevision { get; init; }

	/// <summary>Unsaved edits drawn over the layer while it is being edited, or null.</summary>
	public MapLayerEdits Edits { get; init; }
}

/// <summary>A tile overlay for the map (such as FEMA's flood zones), drawn over the basemap.</summary>
public sealed class MapTileLayer
{
	public string Key { get; init; }

	public Basemap Source { get; init; }

	public LayerDisplay Display { get; init; } = new LayerDisplay();

	public bool Visible { get; init; } = true;
}

/// <summary>A place on the map in longitude and latitude (WGS 84): a click, or a corner of a drawn shape.</summary>
public readonly record struct LatLon(double Latitude, double Longitude);

/// <summary>A rectangle of the map in longitude and latitude (WGS 84).</summary>
public readonly record struct MapExtent(double MinLongitude, double MinLatitude, double MaxLongitude, double MaxLatitude)
{
	public double CenterLatitude => (MinLatitude + MaxLatitude) / 2;

	public double CenterLongitude => (MinLongitude + MaxLongitude) / 2;

	public bool IsEmpty => !(MaxLongitude > MinLongitude) || !(MaxLatitude > MinLatitude);
}
