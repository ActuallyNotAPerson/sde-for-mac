using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sde.App.Maps;
using Sde.Data;
using Sde.Gis.Features;
using Sde.Gis.Styles;

namespace Sde.App.ViewModels;

/// <summary>One line of a layer's legend in the Layers panel: a swatch and what it stands for.</summary>
public sealed class LegendLineViewModel(string label, string fill, string stroke, GeometryKind kind)
{
	public string Label { get; } = label;

	/// <summary>The style's fill colour (the layer's own data, not a theme colour).</summary>
	public IBrush Fill { get; } = Brush(fill);

	public IBrush Stroke { get; } = Brush(stroke);

	/// <summary>A brush for "#RRGGBB" or "#RRGGBBAA" (grey when the text isn't a colour).</summary>
	public static IBrush Brush(string hex)
	{
		Rgba c = Rgba.Parse(hex, new Rgba(128, 128, 128));
		return new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
	}

	public bool IsLine => kind == GeometryKind.Line;

	public bool IsPoint => kind == GeometryKind.Point;

	public bool IsArea => !IsLine && !IsPoint;
}

/// <summary>
/// A layer in the Map page's Layers panel: its MapLayer row, its features (read from the layers GeoPackage) and how it
/// is drawn. Ticking or unticking it, and its opacity, are saved at once.
/// </summary>
public sealed partial class MapLayerViewModel : ObservableObject
{
	private readonly Action<MapLayerViewModel> _changed;
	private bool _loading;

	public MapLayerViewModel(MapLayerRecord record, Action<MapLayerViewModel> changed)
	{
		Record = record;
		_changed = changed;
		_loading = true;
		IsVisible = record.Visible;
		OpacityPercent = Math.Round(Math.Clamp(record.Opacity, 0, 1) * 100);
		_loading = false;
		Style = LayerStyle.FromJson(record.Style);
		Labels = string.IsNullOrWhiteSpace(record.Labels) ? null : LabelStyle.FromJson(record.Labels);
	}

	public MapLayerRecord Record { get; }

	public Guid Id => Record.LayerID;

	/// <summary>The key the map knows the layer by.</summary>
	public string Key => Record.LayerID.ToString("N");

	public string Name => Record.Name;

	public string Kind => Record.SourceKind;

	public bool IsStructures => Kind == MapLayerKinds.Structures;

	/// <summary>A tile overlay (FEMA's flood zones) rather than features.</summary>
	public bool IsTiles => Kind == MapLayerKinds.Nfhl;

	/// <summary>A layer of features from a file or an ArcGIS service.</summary>
	public bool IsFeatures => Kind is MapLayerKinds.File or MapLayerKinds.ArcGisRest;

	/// <summary>Structures and the flood zones stay; other layers can be removed.</summary>
	public bool CanRemove => !IsStructures;

	/// <summary>
	/// True for a layer whose shapes can be drawn and changed on the map: one added from a file or made here. Layers
	/// from ArcGIS services and FEMA's flood zones are read only (their owners keep them up to date).
	/// </summary>
	public bool CanEdit => Kind == MapLayerKinds.File && Data != null;

	/// <summary>Unsaved edits the map draws over the layer while it is being edited, or null.</summary>
	public MapLayerEdits Edits { get; set; }

	[ObservableProperty]
	public partial bool IsVisible { get; set; }

	/// <summary>0 (invisible) to 100 (solid).</summary>
	[ObservableProperty]
	public partial double OpacityPercent { get; set; }

	partial void OnIsVisibleChanged(bool value)
	{
		Record.Visible = value;
		if (!_loading)
		{
			_changed(this);
		}
	}

	partial void OnOpacityPercentChanged(double value)
	{
		Record.Opacity = Math.Clamp(value / 100.0, 0, 1);
		OnPropertyChanged(nameof(OpacityText));
		if (!_loading)
		{
			_changed(this);
		}
	}

	public string OpacityText => $"{OpacityPercent:0}%";

	/// <summary>The features (WGS 84), once read; null for structures and tile overlays, or while reading.</summary>
	public FeatureLayer Data
	{
		get => _data;
		set
		{
			_data = value;
			Revision++;
			OnPropertyChanged();
			OnPropertyChanged(nameof(Detail));
			OnPropertyChanged(nameof(CanEdit));
			UpdateLegend();
		}
	}

	private FeatureLayer _data;

	public LayerStyle Style { get; private set; }

	/// <summary>Labels, or null for none.</summary>
	public LabelStyle Labels { get; private set; }

	/// <summary>Why the layer can't be drawn (its table is missing, say), or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem), nameof(Detail))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	/// <summary>The credit an online layer's owner asks for (shown in the map's attribution line), or null.</summary>
	public string Copyright { get; set; }

	/// <summary>The selected features (by <see cref="Feature.Id"/>).</summary>
	public HashSet<long> Selected { get; } = new HashSet<long>();

	/// <summary>Changes when the features or their style change, so the map rebuilds the layer.</summary>
	public int Revision { get; private set; }

	/// <summary>Changes when the selection changes.</summary>
	public int SelectionRevision { get; private set; }

	public LayerDisplay Display => new LayerDisplay { Opacity = Record.Opacity, MinScale = Record.MinScale, MaxScale = Record.MaxScale };

	public GeometryKind GeometryKind => Data?.GeometryKind is GeometryKind k and not GeometryKind.Unknown
		? k
		: Enum.TryParse(Record.GeometryType, out GeometryKind g) ? g : GeometryKind.Unknown;

	/// <summary>The line under the name: how many features and where from, or why it can't be drawn.</summary>
	public string Detail
	{
		get
		{
			if (Problem != null)
			{
				return Problem;
			}
			if (IsTiles)
			{
				return "From FEMA's map service (needs the internet)";
			}
			if (IsStructures)
			{
				string by = Style.Kind == StyleKind.Single || Style.Field is null or MapViewModel.ResultField
					? "result"
					: (MapViewModel.StructureFieldChoices.FirstOrDefault(f => f.Key == Style.Field)?.Label ?? Style.Field).ToLowerInvariant();
				return "Coloured by " + by;
			}
			if (Data == null)
			{
				return "Reading…";
			}
			string count = Data.Features.Count.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
			string what = GeometryKind switch
			{
				GeometryKind.Point => Data.Features.Count == 1 ? "point" : "points",
				GeometryKind.Line => Data.Features.Count == 1 ? "line" : "lines",
				GeometryKind.Polygon => Data.Features.Count == 1 ? "area" : "areas",
				_ => Data.Features.Count == 1 ? "feature" : "features",
			};
			string from = Kind == MapLayerKinds.ArcGisRest ? "from ArcGIS" : string.IsNullOrEmpty(Record.Source) ? "drawn in SDE" : "from " + Path.GetFileName(Record.Source);
			return $"{count} {what}, {from}";
		}
	}

	/// <summary>The legend lines shown under the layer in the panel.</summary>
	public IReadOnlyList<LegendLineViewModel> Legend { get; private set; } = Array.Empty<LegendLineViewModel>();

	public bool HasLegend => Legend.Count > 1;

	/// <summary>The first symbol's fill (for the swatch beside the name).</summary>
	public IBrush SwatchFill => Legend.Count > 0 ? Legend[0].Fill : Brushes.Transparent;

	public IBrush SwatchStroke => Legend.Count > 0 ? Legend[0].Stroke : Brushes.Transparent;

	/// <summary>Sets a new style and labels (and saves them in the row); the map redraws the layer.</summary>
	public void SetStyle(LayerStyle style, LabelStyle labels)
	{
		Style = style ?? new LayerStyle();
		Labels = labels;
		Record.Style = Style.ToJson();
		Record.Labels = labels?.ToJson();
		Revision++;
		UpdateLegend();
		OnPropertyChanged(nameof(Detail));
	}

	/// <summary>Replaces the selection (or adds to it); the map redraws the highlight.</summary>
	public void Select(IEnumerable<long> ids, bool add)
	{
		if (!add)
		{
			Selected.Clear();
		}
		Selected.UnionWith(ids);
		SelectionRevision++;
		OnPropertyChanged(nameof(SelectedCount));
	}

	public int SelectedCount => Selected.Count;

	private void UpdateLegend()
	{
		GeometryKind kind = GeometryKind;
		Legend = Style.Legend(Name).Select(e => new LegendLineViewModel(e.Label, e.Symbol?.Fill ?? "#808080", e.Symbol?.Stroke ?? "#404040", kind)).ToList();
		OnPropertyChanged(nameof(Legend));
		OnPropertyChanged(nameof(HasLegend));
		OnPropertyChanged(nameof(SwatchFill));
		OnPropertyChanged(nameof(SwatchStroke));
	}

	/// <summary>What the map draws for this layer: a <see cref="MapFeatureLayer"/>, a <see cref="MapTileLayer"/>, or null.</summary>
	public object ToMapLayer()
	{
		if (IsTiles)
		{
			return new MapTileLayer { Key = Key, Source = TileOverlays.FloodZones, Display = Display, Visible = IsVisible };
		}
		if (!IsFeatures || Data == null)
		{
			return null;
		}
		return new MapFeatureLayer
		{
			Key = Key,
			Name = Name,
			Data = Data,
			Style = Style,
			Labels = Labels,
			Display = Display,
			Visible = IsVisible,
			Selected = Selected,
			Revision = Revision,
			SelectionRevision = SelectionRevision,
			Edits = Edits,
		};
	}

	public override string ToString() => Name;
}
