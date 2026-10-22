using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetTopologySuite.Geometries;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Analysis;
using Sde.Gis.Features;

namespace Sde.App.ViewModels.Sheets;

/// <summary>What select by location does with the features it finds.</summary>
public enum SelectionMethod
{
	/// <summary>They become the selection.</summary>
	New,

	/// <summary>They are added to what is selected.</summary>
	Add,

	/// <summary>Only the ones already selected stay selected.</summary>
	Within,
}

/// <summary>A spatial relation in the select-by-location sheet, with the words shown for it.</summary>
public sealed record RelationChoice(SpatialRelation Relation, string Label)
{
	public override string ToString() => Label;

	public static IReadOnlyList<RelationChoice> All { get; } = new[]
	{
		new RelationChoice(SpatialRelation.Intersects, "touch or overlap"),
		new RelationChoice(SpatialRelation.Within, "are completely inside"),
		new RelationChoice(SpatialRelation.Contains, "completely contain"),
		new RelationChoice(SpatialRelation.WithinDistance, "are within a distance of"),
	};
}

/// <summary>What the selected features are compared with: a layer's selection, or an area drawn on the map (Layer null).</summary>
public sealed record LocationSourceChoice(MapLayerViewModel Layer, string Label)
{
	public bool IsDrawnArea => Layer == null;

	public override string ToString() => Label;
}

/// <summary>A select-by-location query, as the sheet gives it back to the map.</summary>
public sealed record LocationQuery(MapLayerViewModel Target, SpatialRelation Relation, double Metres, MapLayerViewModel Source, SelectionMethod Method)
{
	/// <summary>True when the area to compare with is still to be drawn on the map.</summary>
	public bool FromDrawnArea => Source == null;
}

/// <summary>
/// Select, then By location: picks the structures or a layer's features that touch, lie inside, contain or come within
/// a distance of the selected features of another layer (or of an area drawn on the map), as a new selection, added to
/// the selection, or within it. The count of matches is shown as the choices change.
/// </summary>
public sealed partial class SelectByLocationSheetViewModel : SheetViewModel
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
	private readonly MapViewModel _map;

	public SelectByLocationSheetViewModel(MapViewModel map, MapLayerViewModel target) : base("Select by location")
	{
		_map = map;
		Targets = map.SelectableLayers;
		Sources = BuildSources();
		Relation = RelationChoice.All[0];
		Source = Sources.FirstOrDefault(s => !s.IsDrawnArea && !ReferenceEquals(s.Layer, target)) ?? Sources[^1];
		Target = Targets.Contains(target) ? target : Targets.FirstOrDefault();
	}

	public override SheetSize Size => SheetSize.Medium;

	public IReadOnlyList<MapLayerViewModel> Targets { get; }

	/// <summary>The layer to select in (the structures or a layer of features).</summary>
	[ObservableProperty]
	public partial MapLayerViewModel Target { get; set; }

	public IReadOnlyList<RelationChoice> Relations => RelationChoice.All;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(NeedsDistance))]
	public partial RelationChoice Relation { get; set; }

	public bool NeedsDistance => Relation?.Relation == SpatialRelation.WithinDistance;

	/// <summary>The distance, as typed.</summary>
	[ObservableProperty]
	public partial string DistanceText { get; set; } = "100";

	/// <summary>0 feet, 1 miles, 2 metres.</summary>
	[ObservableProperty]
	public partial int UnitIndex { get; set; }

	/// <summary>Every layer with something selected, then "an area you draw on the map".</summary>
	public IReadOnlyList<LocationSourceChoice> Sources { get; }

	[ObservableProperty]
	public partial LocationSourceChoice Source { get; set; }

	/// <summary>0 a new selection, 1 add to it, 2 select within it.</summary>
	[ObservableProperty]
	public partial int MethodIndex { get; set; }

	public SelectionMethod Method => (SelectionMethod)Math.Clamp(MethodIndex, 0, 2);

	/// <summary>"12 of 30 parcels match.", or what happens next for a drawn area.</summary>
	[ObservableProperty]
	public partial string CountText { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	partial void OnTargetChanged(MapLayerViewModel value) => Preview();

	partial void OnRelationChanged(RelationChoice value) => Preview();

	partial void OnDistanceTextChanged(string value) => Preview();

	partial void OnUnitIndexChanged(int value) => Preview();

	partial void OnSourceChanged(LocationSourceChoice value) => Preview();

	partial void OnMethodIndexChanged(int value) => Preview();

	private List<LocationSourceChoice> BuildSources()
	{
		List<LocationSourceChoice> sources = new List<LocationSourceChoice>();
		foreach (MapLayerViewModel layer in _map.SelectableLayers)
		{
			int n = _map.SelectedIds(layer).Count;
			if (n == 0)
			{
				continue;
			}
			sources.Add(new LocationSourceChoice(layer, layer.IsStructures
				? n == 1 ? "the selected structure" : $"the {n.ToString("N0", Us)} selected structures"
				: n == 1 ? $"the selected feature of {layer.Name}" : $"the {n.ToString("N0", Us)} selected features of {layer.Name}"));
		}
		sources.Add(new LocationSourceChoice(null, "an area you draw on the map"));
		return sources;
	}

	/// <summary>The distance in metres; null (with the reason) when it can't be read.</summary>
	private double? Metres()
	{
		if (!NeedsDistance)
		{
			return 0;
		}
		if (!double.TryParse((DistanceText ?? "").Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d < 0)
		{
			Problem = "Type the distance as a number, such as 100.";
			return null;
		}
		return UnitIndex switch
		{
			1 => d * 1609.344,
			2 => d,
			_ => d * 0.3048,
		};
	}

	/// <summary>Counts the matches for the choices as they stand.</summary>
	public void Preview()
	{
		Problem = null;
		CountText = null;
		if (Target == null || Source == null || Relation == null || Metres() is not double metres)
		{
			return;
		}
		if (Source.IsDrawnArea)
		{
			CountText = "Next, draw the area on the map: click its corners, then double-click.";
			return;
		}
		int n = _map.RunLocationQuery(new LocationQuery(Target, Relation.Relation, metres, Source.Layer, Method), _map.SelectorsOf(Source.Layer), countOnly: true);
		int of = _map.FeaturesOf(Target)?.Features.Count ?? 0;
		CountText = $"{n.ToString("N0", Us)} of {of.ToString("N0", Us)} {(Target.IsStructures ? "structures" : Target.Name)} match.";
	}

	[RelayCommand]
	private void Select()
	{
		if (Target == null || Source == null || Relation == null)
		{
			return;
		}
		if (Metres() is not double metres)
		{
			return;
		}
		Close(new LocationQuery(Target, Relation.Relation, metres, Source.Layer, Method));
	}
}

/// <summary>A structure field values can be copied into, with the label shown for it.</summary>
public sealed record CopyTargetChoice(FieldInfo Field, string Label)
{
	public override string ToString() => Label;
}

/// <summary>One structure in the copy-values preview: its value now, the value it would get, and how it was matched.</summary>
public sealed partial class CopyValueRowViewModel(Guid propertyId, Guid? assessmentId, string address, string now, string newText, string input, string how, bool canCopy) : ObservableObject
{
	public Guid PropertyId { get; } = propertyId;

	public Guid? AssessmentId { get; } = assessmentId;

	public string Address { get; } = address;

	/// <summary>The structure's value now ("" when empty).</summary>
	public string Now { get; } = now;

	/// <summary>The polygon's value, as the field would show it ("" when there is none).</summary>
	public string New { get; } = newText ?? "";

	/// <summary>The value FieldInput gets.</summary>
	public string Input { get; } = input;

	/// <summary>"Inside one area", "Inside 2 areas: the smallest is used", or why nothing can be copied.</summary>
	public string How { get; } = how;

	/// <summary>False for structures nothing can be copied onto (no area, no value, locked, checked out).</summary>
	public bool CanCopy { get; } = canCopy;

	public bool IsSame => CanCopy && string.Equals(Now, New, StringComparison.Ordinal);

	[ObservableProperty]
	public partial bool IsTicked { get; set; }

	public string TickName => $"Copy the new value onto {Address}";
}

/// <summary>
/// Copy values onto structures: a polygon layer's field (parcel numbers, flood zones, community IDs) copied into a
/// structure field for the selected structures or every structure on the map. The preview lists each structure with
/// its value now, the new value and how it was matched, with ticks; locked and checked-out ones can't be ticked.
/// </summary>
public sealed partial class CopyValuesSheetViewModel : SheetViewModel
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
	private readonly AppServices _app;
	private readonly MapViewModel _map;
	private bool _ready;

	public CopyValuesSheetViewModel(AppServices app, MapViewModel map, MapLayerViewModel layer) : base("Copy values onto structures")
	{
		_app = app;
		_map = map;
		Layers = map.Layers.Where(l => l.IsAreaLayer).ToList();
		Targets = TargetChoices();
		SelectedCount = map.SelectedStructures().Count;
		AllCount = map.PlacedStructures.Count;
		OnlySelected = SelectedCount > 0;
		Layer = Layers.Contains(layer) ? layer : Layers.FirstOrDefault();
		_ready = true;
		Preview();
	}

	public override SheetSize Size => SheetSize.Large;

	/// <summary>The layers of areas values can come from.</summary>
	public IReadOnlyList<MapLayerViewModel> Layers { get; }

	[ObservableProperty]
	public partial MapLayerViewModel Layer { get; set; }

	public IReadOnlyList<MapFieldChoice> Fields { get; private set; } = Array.Empty<MapFieldChoice>();

	[ObservableProperty]
	public partial MapFieldChoice SourceField { get; set; }

	/// <summary>The structure fields: the usual ones first (parcel number, FIRM zone, NFIP community), then the rest by name.</summary>
	public IReadOnlyList<CopyTargetChoice> Targets { get; }

	[ObservableProperty]
	public partial CopyTargetChoice Target { get; set; }

	public int SelectedCount { get; }

	public int AllCount { get; }

	public bool HasSelection => SelectedCount > 0;

	/// <summary>True: the selected structures; false: every structure on the map.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AllStructures))]
	public partial bool OnlySelected { get; set; }

	public bool AllStructures
	{
		get => !OnlySelected;
		set => OnlySelected = !value;
	}

	public string SelectedText => SelectedCount switch
	{
		0 => "The selected structures (none are selected)",
		1 => "The selected structure",
		_ => $"The {SelectedCount.ToString("N0", Us)} selected structures",
	};

	public string AllText => AllCount == 1 ? "The structure on the map" : $"All {AllCount.ToString("N0", Us)} structures on the map";

	public ObservableCollection<CopyValueRowViewModel> Rows { get; } = new();

	public bool HasRows => Rows.Count > 0;

	/// <summary>"12 to change, 3 already the same, 2 not inside any area, 1 locked."</summary>
	[ObservableProperty]
	public partial string SummaryText { get; set; }

	public int TickedCount => Rows.Count(r => r.IsTicked);

	/// <summary>"Copy 12 values".</summary>
	public string CopyText => TickedCount == 1 ? "Copy 1 value" : $"Copy {TickedCount.ToString("N0", Us)} values";

	private IReadOnlyList<CopyTargetChoice> TargetChoices()
	{
		string[] first = { "Property.ParcelNumber", "Property.FIRMZone", "Property.NFIPCID", "Property.NFIPCommunityName" };
		List<CopyTargetChoice> choices = first.Select(FieldCatalog.Get).Where(f => f != null).Select(f => new CopyTargetChoice(f, f.Label)).ToList();
		foreach (FieldInfo f in ValueCopy.TargetFields.Where(f => !first.Contains(f.Key)).OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase))
		{
			choices.Add(new CopyTargetChoice(f, f.Label));
		}
		return choices;
	}

	partial void OnLayerChanged(MapLayerViewModel value)
	{
		Fields = value?.Data?.Fields.Select(f => new MapFieldChoice(f.Name, f.DisplayName, f.Type is FieldType.Integer or FieldType.Real)).ToList()
			?? new List<MapFieldChoice>();
		OnPropertyChanged(nameof(Fields));
		SourceField = Fields.FirstOrDefault(f => Suggest(f.Key) != null) ?? Fields.FirstOrDefault();
	}

	partial void OnSourceFieldChanged(MapFieldChoice value)
	{
		if (value != null && Suggest(value.Key) is string key)
		{
			Target = Targets.FirstOrDefault(t => t.Field.Key == key) ?? Target;
		}
		Target ??= Targets.FirstOrDefault();
		Preview();
	}

	partial void OnTargetChanged(CopyTargetChoice value) => Preview();

	partial void OnOnlySelectedChanged(bool value) => Preview();

	/// <summary>The structure field a layer's field name suggests: FLD_ZONE goes to FIRM zone, PIN or APN to parcel number, and so on.</summary>
	public static string Suggest(string field)
	{
		string f = (field ?? "").Trim().ToLowerInvariant().Replace(" ", "_", StringComparison.Ordinal);
		if (f is "fld_zone" or "flood_zone" or "firm_zone" or "zone_subty" || f.EndsWith("flood_zone", StringComparison.Ordinal))
		{
			return "Property.FIRMZone";
		}
		if (f is "pin" or "apn" or "parcel" or "parcel_id" or "parcelid" or "parcel_no" or "parcel_num" or "parcelnumber" or "parcel_number" or "parid")
		{
			return "Property.ParcelNumber";
		}
		if (f is "cid" or "nfip_cid" or "community_id" or "comm_id" or "dfirm_id" || f.EndsWith("_cid", StringComparison.Ordinal))
		{
			return "Property.NFIPCID";
		}
		if (f is "community" or "community_name" or "comm_name" or "cname" or "nfip_community")
		{
			return "Property.NFIPCommunityName";
		}
		return null;
	}

	/// <summary>Works out every structure's row for the choices as they stand.</summary>
	public void Preview()
	{
		if (!_ready)
		{
			return;
		}
		foreach (CopyValueRowViewModel old in Rows)
		{
			old.PropertyChanged -= RowChanged;
		}
		Rows.Clear();
		if (Layer?.Data != null && SourceField != null && Target != null)
		{
			foreach (CopyValueRowViewModel row in _map.PlanValueCopy(Layer, SourceField.Key, Target.Field, OnlySelected))
			{
				row.IsTicked = row.CanCopy && !row.IsSame;
				row.PropertyChanged += RowChanged;
				Rows.Add(row);
			}
		}
		int change = Rows.Count(r => r.CanCopy && !r.IsSame);
		int same = Rows.Count(r => r.IsSame);
		int cant = Rows.Count(r => !r.CanCopy);
		List<string> parts = new List<string> { $"{change.ToString("N0", Us)} to change" };
		if (same > 0)
		{
			parts.Add($"{same.ToString("N0", Us)} already the same");
		}
		if (cant > 0)
		{
			parts.Add($"{cant.ToString("N0", Us)} can't be changed");
		}
		SummaryText = Rows.Count == 0 ? "No structures to copy onto." : string.Join(", ", parts) + ".";
		OnPropertyChanged(nameof(HasRows));
		Counted();
	}

	private void RowChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(CopyValueRowViewModel.IsTicked))
		{
			Counted();
		}
	}

	private void Counted()
	{
		OnPropertyChanged(nameof(TickedCount));
		OnPropertyChanged(nameof(CopyText));
		CopyCommand.NotifyCanExecuteChanged();
	}

	private bool CanCopy() => TickedCount > 0;

	[RelayCommand(CanExecute = nameof(CanCopy))]
	private void Copy() => Close(this);
}
