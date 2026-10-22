using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.Gis;
using Sde.Gis.Features;
using Sde.Gis.Online;
using Sde.Gis.Styles;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Add layer, then From an ArcGIS web address: paste a FeatureServer or MapServer layer's address, check it, and
/// download its features for the area in view (or the whole layer) into the layers GeoPackage.
/// </summary>
public sealed partial class OnlineLayerSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly MapExtent _extent;
	private readonly Func<FeatureLayer, string, string, Task<MapLayerViewModel>> _add;

	/// <param name="extent">The area the map shows now.</param>
	/// <param name="add">Saves and shows the downloaded layer (features, address, copyright).</param>
	public OnlineLayerSheetViewModel(AppServices app, MapExtent extent, Func<FeatureLayer, string, string, Task<MapLayerViewModel>> add)
		: base("Add a layer from ArcGIS")
	{
		_app = app;
		_extent = extent;
		_add = add;
	}

	public override SheetSize Size => SheetSize.Medium;

	/// <summary>The layer's address, such as https://…/FeatureServer/0.</summary>
	[ObservableProperty]
	public partial string Url { get; set; }

	partial void OnUrlChanged(string value)
	{
		Info = null;
		Problem = null;
	}

	/// <summary>What the service said about the layer, once checked.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasInfo), nameof(InfoText), nameof(CopyrightText), nameof(HasCopyright))]
	public partial ArcGisLayerInfo Info { get; set; }

	public bool HasInfo => Info != null;

	/// <summary>"Parcels: areas, 12 fields."</summary>
	public string InfoText => Info == null ? null : $"{Info.Name}: {Kind(Info.GeometryKind)}, {Info.Fields.Count} field{(Info.Fields.Count == 1 ? "" : "s")}.";

	public string CopyrightText => string.IsNullOrWhiteSpace(Info?.Copyright) ? null : "Credit: " + Info.Copyright.Trim();

	public bool HasCopyright => CopyrightText != null;

	/// <summary>True to download the whole layer; false (the default) for the area the map shows now.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAreaInView))]
	public partial bool WholeLayer { get; set; }

	public bool IsAreaInView
	{
		get => !WholeLayer;
		set => WholeLayer = !value;
	}

	/// <summary>Why the address couldn't be used, in plain English, or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	[ObservableProperty]
	public partial bool IsBusy { get; set; }

	/// <summary>"Downloading…" while busy.</summary>
	[ObservableProperty]
	public partial string Status { get; set; }

	public override bool CanCancel => !IsBusy;

	private ArcGisRestClient Client() => new ArcGisRestClient(_app.HttpClientFactory());

	/// <summary>Asks the service about the layer (name, kind, fields).</summary>
	[RelayCommand]
	private async Task Check()
	{
		Problem = null;
		IsBusy = true;
		Status = "Checking the address…";
		try
		{
			Info = await Client().GetLayerInfoAsync(Url, CancellationToken.None);
		}
		catch (GisException ex)
		{
			Problem = ex.Message;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			Problem = "The ArcGIS service didn't answer. Check the address and the internet connection.";
		}
		finally
		{
			IsBusy = false;
			Status = null;
		}
	}

	/// <summary>Downloads the features and adds the layer.</summary>
	[RelayCommand]
	private async Task Add()
	{
		if (Info == null)
		{
			await Check();
			if (Info == null)
			{
				return;
			}
		}
		Problem = null;
		IsBusy = true;
		Status = "Downloading the features…";
		string url = Info.Url;
		Envelope area = WholeLayer || _extent.IsEmpty ? null : new Envelope(_extent.MinLongitude, _extent.MaxLongitude, _extent.MinLatitude, _extent.MaxLatitude);
		try
		{
			ArcGisDownload download = await Client().DownloadAsync(url, area, new ArcGisQueryOptions(), CancellationToken.None);
			if (download.Layer.Features.Count == 0)
			{
				Problem = WholeLayer ? "The layer has no features." : "The layer has no features in the area the map shows. Move the map, or choose the whole layer.";
				return;
			}
			if (download.Truncated)
			{
				download.Layer.Warnings.Add("Only part of the layer was downloaded: the service stopped sending features. Zoom in and add it again for a smaller area.");
			}
			Status = "Saving…";
			MapLayerViewModel added = await _add(download.Layer, url, Info.Copyright);
			IsBusy = false;
			Close(added);
		}
		catch (GisException ex)
		{
			Problem = ex.Message;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			Problem = "The ArcGIS service didn't answer. Check the address and the internet connection.";
		}
		finally
		{
			IsBusy = false;
			Status = null;
		}
	}

	private static string Kind(GeometryKind kind) => kind switch
	{
		GeometryKind.Point => "points",
		GeometryKind.Line => "lines",
		GeometryKind.Polygon => "areas",
		_ => "features",
	};
}

/// <summary>A colour in the style sheet's palette: its colour and its name (for VoiceOver).</summary>
public sealed class SwatchViewModel(string hex, string name)
{
	public string Hex { get; } = hex;

	public string Name { get; } = name;

	public Avalonia.Media.IBrush Brush { get; } = LegendLineViewModel.Brush(hex);
}

/// <summary>One category of a "by category" style, with its swatch and a tick to show or hide it.</summary>
public sealed partial class StyleCategoryViewModel(StyleCategory category) : ObservableObject
{
	public StyleCategory Category { get; } = category;

	public string Label => Category.Label ?? Category.Value;

	public Avalonia.Media.IBrush Fill { get; } = LegendLineViewModel.Brush(category.Symbol?.Fill);

	public Avalonia.Media.IBrush Stroke { get; } = LegendLineViewModel.Brush(category.Symbol?.Stroke);

	[ObservableProperty]
	public partial bool IsShown { get; set; } = category.Visible;

	partial void OnIsShownChanged(bool value) => Category.Visible = value;

	public string SpokenName => $"Show {Label}";
}

/// <summary>
/// A layer's "Style…" sheet: one colour, a colour for each category of a field, or colours for ranges of a number field
/// (equal intervals along a ramp); labels from a field with a halo and a scale range; the layer's own scale range. For
/// the structures, "By result" (the theme's result colours) is the first choice.
/// </summary>
public sealed partial class LayerStyleSheetViewModel : SheetViewModel
{
	/// <summary>What the sheet returns when Apply is pressed.</summary>
	public sealed record Result(LayerStyle Style, Sde.Gis.Styles.LabelStyle Labels, double? MinScale, double? MaxScale);

	private readonly MapViewModel _map;
	private readonly MapLayerViewModel _layer;
	private bool _loading;

	public LayerStyleSheetViewModel(MapViewModel map, MapLayerViewModel layer) : base("Style " + layer.Name)
	{
		_map = map;
		_layer = layer;
		Fields = map.FieldChoices(layer).Where(f => f.Key != MapViewModel.ResultField).ToList();
		NumberFields = Fields.Where(f => f.IsNumber).ToList();
		Kinds = layer.IsStructures
			? new[] { "By result", "By category", "By number range" }
			: new[] { "One colour", "By category", "By number range" };
		_loading = true;
		LayerStyle style = layer.Style ?? new LayerStyle();
		KindIndex = layer.IsStructures && map.ColoursByResult ? 0 : style.Kind switch
		{
			StyleKind.Categorized => 1,
			StyleKind.Graduated => 2,
			_ => 0,
		};
		Fill = Rgba.Parse(style.Symbol?.Fill, new Rgba(0x2F, 0x6F, 0xB3)).ToHex();
		PointSize = (decimal)(style.Symbol?.PointSize ?? 8);
		CategoryField = KindIndex == 1 ? Fields.FirstOrDefault(f => f.Key == style.Field) : null;
		RangeField = KindIndex == 2 ? NumberFields.FirstOrDefault(f => f.Key == style.Field) : null;
		ClassCount = style.Kind == StyleKind.Graduated && style.Classes.Count > 0 ? style.Classes.Count : 5;
		Ramp = ColorRamp.Named(style.Ramp).Name;
		Sde.Gis.Styles.LabelStyle labels = layer.Labels;
		ShowLabels = labels?.Enabled == true;
		LabelField = Fields.FirstOrDefault(f => f.Key == labels?.Field) ?? Fields.FirstOrDefault();
		LabelSize = (decimal)(labels?.Size ?? 11);
		LabelHalo = labels?.Halo ?? true;
		LabelMinScale = ToDecimal(labels?.MinScale);
		MinScale = ToDecimal(layer.Record.MinScale);
		MaxScale = ToDecimal(layer.Record.MaxScale);
		_loading = false;
		Rebuild(style);
	}

	public override SheetSize Size => SheetSize.Medium;

	public bool IsStructures => _layer.IsStructures;

	/// <summary>Points have a size; lines and areas don't.</summary>
	public bool IsPoints => _layer.GeometryKind == GeometryKind.Point;

	public IReadOnlyList<string> Kinds { get; }

	/// <summary>0 one colour (or by result for structures), 1 by category, 2 by number range.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSingle), nameof(IsByResult), nameof(IsCategories), nameof(IsRanges))]
	public partial int KindIndex { get; set; }

	public bool IsSingle => KindIndex == 0 && !IsStructures;

	public bool IsByResult => KindIndex == 0 && IsStructures;

	public bool IsCategories => KindIndex == 1;

	public bool IsRanges => KindIndex == 2;

	public IReadOnlyList<MapFieldChoice> Fields { get; }

	public IReadOnlyList<MapFieldChoice> NumberFields { get; }

	public bool HasNumberFields => NumberFields.Count > 0;

	/// <summary>The palette for one colour.</summary>
	public IReadOnlyList<SwatchViewModel> Swatches { get; } = ColorRamp.Categories.Zip(new[]
	{
		"Blue", "Orange", "Green", "Red", "Purple", "Brown", "Pink", "Grey", "Olive", "Teal", "Yellow", "Indigo",
	}).Select(p => new SwatchViewModel(p.First, p.Second)).ToList();

	/// <summary>The one colour ("#RRGGBB").</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(FillBrush), nameof(FillName))]
	public partial string Fill { get; set; }

	public Avalonia.Media.IBrush FillBrush => LegendLineViewModel.Brush(Fill);

	public string FillName => Swatches.FirstOrDefault(s => string.Equals(s.Hex, Fill, StringComparison.OrdinalIgnoreCase))?.Name ?? "Custom";

	[RelayCommand]
	private void PickColour(SwatchViewModel swatch) => Fill = swatch.Hex;

	/// <summary>Point size in points.</summary>
	[ObservableProperty]
	public partial decimal? PointSize { get; set; }

	[ObservableProperty]
	public partial MapFieldChoice CategoryField { get; set; }

	[ObservableProperty]
	public partial MapFieldChoice RangeField { get; set; }

	/// <summary>How many ranges (2 to 9).</summary>
	[ObservableProperty]
	public partial decimal? ClassCount { get; set; }

	public IReadOnlyList<string> Ramps { get; } = ColorRamp.Presets.Select(r => r.Name).ToList();

	[ObservableProperty]
	public partial string Ramp { get; set; }

	/// <summary>The categories of the chosen field, each with a tick.</summary>
	public System.Collections.ObjectModel.ObservableCollection<StyleCategoryViewModel> Categories { get; } = new();

	/// <summary>The legend of the ranges.</summary>
	public System.Collections.ObjectModel.ObservableCollection<LegendLineViewModel> RangeLegend { get; } = new();

	/// <summary>Why the chosen field can't be used (no values, say), or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	[ObservableProperty]
	public partial bool ShowLabels { get; set; }

	[ObservableProperty]
	public partial MapFieldChoice LabelField { get; set; }

	/// <summary>Label text size in points.</summary>
	[ObservableProperty]
	public partial decimal? LabelSize { get; set; }

	[ObservableProperty]
	public partial bool LabelHalo { get; set; }

	/// <summary>Labels show only zoomed in past 1:this (null for always).</summary>
	[ObservableProperty]
	public partial decimal? LabelMinScale { get; set; }

	/// <summary>The layer shows only zoomed in past 1:this (null for always).</summary>
	[ObservableProperty]
	public partial decimal? MinScale { get; set; }

	/// <summary>The layer hides zoomed in past 1:this (null for never).</summary>
	[ObservableProperty]
	public partial decimal? MaxScale { get; set; }

	private LayerStyle _built;

	partial void OnKindIndexChanged(int value) => Rebuild();

	partial void OnFillChanged(string value) => Rebuild();

	partial void OnPointSizeChanged(decimal? value) => Rebuild();

	partial void OnCategoryFieldChanged(MapFieldChoice value) => Rebuild();

	partial void OnRangeFieldChanged(MapFieldChoice value) => Rebuild();

	partial void OnClassCountChanged(decimal? value) => Rebuild();

	partial void OnRampChanged(string value) => Rebuild();

	/// <summary>Makes the style the choices describe (and the categories or ranges to show).</summary>
	private void Rebuild(LayerStyle existing = null)
	{
		if (_loading)
		{
			return;
		}
		Problem = null;
		Symbol baseSymbol = BaseSymbol();
		try
		{
			switch (KindIndex)
			{
				case 1 when CategoryField != null:
					FeatureLayer data = _map.DataWith(_layer, CategoryField.Key);
					LayerStyle categories = LayerStyle.Categorized(data, CategoryField.Key, baseSymbol);
					// Keep the ticks of categories that were already there.
					if (existing?.Kind == StyleKind.Categorized && existing.Field == CategoryField.Key)
					{
						foreach (StyleCategory c in categories.Categories)
						{
							c.Visible = existing.Categories.FirstOrDefault(x => x.Value == c.Value)?.Visible ?? true;
						}
					}
					_built = categories;
					break;
				case 2 when RangeField != null:
					_built = LayerStyle.Graduated(_map.DataWith(_layer, RangeField.Key), RangeField.Key, (int)Math.Clamp(ClassCount ?? 5, 2, 9), ColorRamp.Named(Ramp), baseSymbol);
					break;
				case 1 or 2:
					_built = null;
					break;
				default:
					_built = IsStructures ? new LayerStyle { Kind = StyleKind.Single, Field = MapViewModel.ResultField, Symbol = baseSymbol } : LayerStyle.SingleSymbol(baseSymbol);
					break;
			}
		}
		catch (GisException ex)
		{
			_built = null;
			Problem = ex.Message;
		}
		Categories.Clear();
		RangeLegend.Clear();
		if (_built?.Kind == StyleKind.Categorized)
		{
			foreach (StyleCategory c in _built.Categories)
			{
				Categories.Add(new StyleCategoryViewModel(c));
			}
		}
		else if (_built?.Kind == StyleKind.Graduated)
		{
			foreach (LegendEntry e in _built.Legend())
			{
				RangeLegend.Add(new LegendLineViewModel(e.Label, e.Symbol.Fill, e.Symbol.Stroke, _layer.GeometryKind));
			}
		}
	}

	/// <summary>The symbol every class starts from: the chosen colour, a darker outline, and the size for points.</summary>
	private Symbol BaseSymbol()
	{
		Rgba colour = Rgba.Parse(Fill, new Rgba(0x2F, 0x6F, 0xB3));
		Symbol s = new Symbol().WithFill(colour);
		s.PointSize = (double)Math.Clamp(PointSize ?? 8, 3, 40);
		switch (_layer.GeometryKind)
		{
			case GeometryKind.Polygon:
				s.Fill = new Rgba(colour.R, colour.G, colour.B, 90).ToHex();
				s.Stroke = colour.ToHex();
				s.StrokeWidth = 1.2;
				break;
			case GeometryKind.Line:
				s.Stroke = colour.ToHex();
				s.StrokeWidth = 2;
				break;
		}
		return s;
	}

	/// <summary>Saves the style, labels and scale range.</summary>
	[RelayCommand]
	private void Apply()
	{
		if (_built == null)
		{
			Problem ??= KindIndex == 2 ? "Choose a field that holds numbers." : "Choose a field.";
			return;
		}
		if (_built.Kind == StyleKind.Categorized && _built.Categories.Count > 0 && _built.Categories.All(c => !c.Visible))
		{
			Problem = "Tick at least one category, or nothing will show.";
			return;
		}
		Sde.Gis.Styles.LabelStyle labels = ShowLabels && LabelField != null
			? new Sde.Gis.Styles.LabelStyle
			{
				Field = LabelField.Key,
				Size = (double)Math.Clamp(LabelSize ?? 11, 7, 30),
				Halo = LabelHalo,
				MinScale = LabelMinScale is decimal l && l > 0 ? (double)l : null,
			}
			: null;
		Close(new Result(_built, labels, MinScale is decimal min && min > 0 ? (double)min : null, MaxScale is decimal max && max > 0 ? (double)max : null));
	}

	private static decimal? ToDecimal(double? value) => value is double v && v > 0 && v < 1e12 ? (decimal)v : null;
}
