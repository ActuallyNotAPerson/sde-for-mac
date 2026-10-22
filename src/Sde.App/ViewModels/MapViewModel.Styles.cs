using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using NetTopologySuite.Geometries;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Features;
using Sde.Gis.Styles;

namespace Sde.App.ViewModels;

/// <summary>A field a layer can be coloured, labelled or queried by: its key (the feature field's name), label and whether it holds numbers.</summary>
public sealed record MapFieldChoice(string Key, string Label, bool IsNumber)
{
	public override string ToString() => Label;
}

/// <summary>How the structures are coloured and labelled, and the structures as features (for styles, the table and queries).</summary>
public sealed partial class MapViewModel
{
	/// <summary>The style field that means "by result", in the theme's result colours (the structures' default).</summary>
	public const string ResultField = "Result";

	private FeatureLayer _structureFeatures;
	private Dictionary<long, MapPointViewModel> _structureById = new Dictionary<long, MapPointViewModel>();
	private readonly HashSet<string> _extraStructureFields = new HashSet<string>(StringComparer.Ordinal);

	/// <summary>
	/// The structures on the map (those the legend shows) as features: one point each with Address, City, Result,
	/// Percent damaged, Status, Team, Event, Latitude and Longitude, plus any assessment field a style, label or query
	/// asked for. Feature ids are this map's own (see <see cref="StructureOf"/>).
	/// </summary>
	public FeatureLayer StructureFeatures => _structureFeatures ??= BuildStructureFeatures();

	/// <summary>The structure a structure feature stands for, or null.</summary>
	public MapPointViewModel StructureOf(long featureId) => StructureFeatures != null && _structureById.TryGetValue(featureId, out MapPointViewModel p) ? p : null;

	/// <summary>The feature id of a structure on the map, or null.</summary>
	public long? FeatureIdOf(Guid propertyId)
	{
		_ = StructureFeatures;
		foreach (KeyValuePair<long, MapPointViewModel> kv in _structureById)
		{
			if (kv.Value.Id == propertyId)
			{
				return kv.Key;
			}
		}
		return null;
	}

	/// <summary>The fields the structures can be coloured, labelled or queried by.</summary>
	public static IReadOnlyList<MapFieldChoice> StructureFieldChoices { get; } = BuildStructureFieldChoices();

	private static IReadOnlyList<MapFieldChoice> BuildStructureFieldChoices()
	{
		List<MapFieldChoice> fields = new List<MapFieldChoice>
		{
			new MapFieldChoice("Address", "Address", false),
			new MapFieldChoice("City", "City", false),
			new MapFieldChoice(ResultField, "Result", false),
			new MapFieldChoice("Percent damaged", "Percent damaged", true),
			new MapFieldChoice("Status", "Status", false),
			new MapFieldChoice("Team", "Team", false),
			new MapFieldChoice("Event", "Event", false),
		};
		foreach (FieldInfo f in FieldCatalog.All.Where(f => f.Kind != FieldKind.MultilineText).OrderBy(f => f.Step).ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase))
		{
			if (fields.All(x => x.Label != f.Label))
			{
				fields.Add(new MapFieldChoice(f.Key, f.Label, f.Kind == FieldKind.Number));
			}
		}
		return fields;
	}

	private FeatureLayer BuildStructureFeatures()
	{
		FeatureLayer layer = new FeatureLayer("Structures", GeometryKind.Point);
		layer.AddField("Address", FieldType.Text);
		layer.AddField("City", FieldType.Text);
		layer.AddField(ResultField, FieldType.Text);
		layer.AddField("Percent damaged", FieldType.Real);
		layer.AddField("Status", FieldType.Text);
		layer.AddField("Team", FieldType.Text);
		layer.AddField("Event", FieldType.Text);
		layer.AddField("Latitude", FieldType.Real);
		layer.AddField("Longitude", FieldType.Real);
		List<FieldInfo> extra = _extraStructureFields.Select(FieldCatalog.Get).Where(f => f != null).ToList();
		foreach (FieldInfo f in extra)
		{
			layer.AddField(f.Key, f.Kind == FieldKind.Number ? FieldType.Real : FieldType.Text, f.Label);
		}
		Dictionary<Guid, AssessmentStatusRecord> statuses = Safe(() => _app.Store.GetStatuses(), new Dictionary<Guid, AssessmentStatusRecord>());
		Dictionary<Guid, Guid> events = Safe(() => _app.Store.GetEventMemberships(), new Dictionary<Guid, Guid>());
		Dictionary<Guid, string> eventNames = Safe(() => _app.Store.GetEvents().ToDictionary(e => e.EventID, e => e.Name), new Dictionary<Guid, string>());
		Dictionary<Guid, string> teamNames = Safe(() => _app.Store.GetTeams(activeOnly: false).ToDictionary(t => t.TeamID, t => t.Name), new Dictionary<Guid, string>());
		Dictionary<Guid, string> teamOf = Safe(() => _app.Store.GetAssignments()
			.GroupBy(a => a.PropertyID)
			.ToDictionary(g => g.Key, g => teamNames.TryGetValue(g.OrderByDescending(a => a.AssignedOn).First().TeamID, out string n) ? n : null),
			new Dictionary<Guid, string>());
		Dictionary<Guid, AssessmentFile> files = extra.Count == 0
			? new Dictionary<Guid, AssessmentFile>()
			: Safe(() => _app.Store.LoadAssessments(Visible.Where(p => p.AssessmentID != null).Select(p => p.AssessmentID!.Value)).ToDictionary(f => f.Assessment.AssessmentID), new Dictionary<Guid, AssessmentFile>());
		Dictionary<long, MapPointViewModel> byId = new Dictionary<long, MapPointViewModel>();
		long id = 0;
		foreach (MapPointViewModel p in Visible)
		{
			Feature f = layer.AddFeature(GisSetup.Factory.CreatePoint(new Coordinate(p.Longitude, p.Latitude)), ++id);
			byId[id] = p;
			f["Address"] = p.Address;
			f["City"] = p.Row.City;
			f[ResultField] = ResultName(p);
			f["Percent damaged"] = p.Row.PercentDamage is decimal pd ? (double)pd : null;
			f["Status"] = p.AssessmentID is Guid aid ? AssessmentStatuses.Label(statuses.TryGetValue(aid, out AssessmentStatusRecord s) ? s.Value : AssessmentStatus.Draft) : null;
			f["Team"] = teamOf.TryGetValue(p.Id, out string team) ? team : null;
			f["Event"] = p.AssessmentID is Guid a2 && events.TryGetValue(a2, out Guid ev) && eventNames.TryGetValue(ev, out string evName) ? evName : null;
			f["Latitude"] = p.Latitude;
			f["Longitude"] = p.Longitude;
			if (p.AssessmentID is Guid a3 && files.TryGetValue(a3, out AssessmentFile file))
			{
				foreach (FieldInfo fi in extra)
				{
					object value = FieldCatalog.GetValue(file, fi);
					f[fi.Key] = fi.Kind == FieldKind.Number
						? value == null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture)
						: FieldCatalog.Display(fi, value, _app.Store.Reference);
				}
			}
		}
		_structureById = byId;
		return layer;
	}

	private static T Safe<T>(Func<T> read, T fallback)
	{
		try
		{
			return read();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't read structure details for the map", ex);
			return fallback;
		}
	}

	/// <summary>The structures' result in words, as the legend names it.</summary>
	public string ResultName(MapPointViewModel p) => Classes.FirstOrDefault(c => c.Class == p.Class)?.Label ?? p.ResultText;

	/// <summary>
	/// Makes sure the structure features carry an assessment field (a FieldCatalog key) that a style, label or query
	/// needs; the features are rebuilt when it is new.
	/// </summary>
	public void UseStructureField(string key)
	{
		if (key != null && FieldCatalog.Get(key) != null && _extraStructureFields.Add(key))
		{
			_structureFeatures = null;
		}
	}

	/// <summary>The structure features with a field (an assessment field is read in when needed).</summary>
	public FeatureLayer StructureFeaturesWith(string key)
	{
		UseStructureField(key);
		return StructureFeatures;
	}

	/// <summary>The structures were read again or the legend changed what is shown: styles and the table follow.</summary>
	private void StructuresChanged()
	{
		if (StructuresLayer != null)
		{
			UseStructureField(StructuresLayer.Style.Field);
			UseStructureField(StructuresLayer.Labels?.Field);
			OnPropertyChanged(nameof(StructureLegend));
			OnPropertyChanged(nameof(HasStructureLegend));
			OnPropertyChanged(nameof(StructureLegendTitle));
		}
		RefreshTable();
	}

	/// <summary>True while the structures are coloured by their result (the theme's result colours).</summary>
	public bool ColoursByResult => StructuresLayer == null || StructuresLayer.Style.Kind == StyleKind.Single || StructuresLayer.Style.Field is null or ResultField;

	/// <summary>
	/// The fill of each structure's dot ("#RRGGBB") when the structures are coloured by something other than their
	/// result, by structure; empty when they are coloured by result (the page uses the theme's result colours), and a
	/// structure missing from it isn't drawn (its category is unticked).
	/// </summary>
	public IReadOnlyDictionary<Guid, string> StructureFills()
	{
		Dictionary<Guid, string> fills = new Dictionary<Guid, string>();
		if (ColoursByResult)
		{
			return fills;
		}
		FeatureLayer data = StructureFeaturesWith(StructuresLayer.Style.Field);
		foreach (Feature f in data.Features)
		{
			if (StructuresLayer.Style.SymbolFor(f) is Symbol s && StructureOf(f.Id) is MapPointViewModel p)
			{
				fills[p.Id] = s.Fill;
			}
		}
		return fills;
	}

	/// <summary>Each structure's label text (by structure), or null when labels are off.</summary>
	public IReadOnlyDictionary<Guid, string> StructureLabels()
	{
		LabelStyle labels = StructuresLayer?.Labels;
		if (labels == null || !labels.Enabled || string.IsNullOrEmpty(labels.Field))
		{
			return null;
		}
		FeatureLayer data = StructureFeaturesWith(labels.Field);
		Dictionary<Guid, string> text = new Dictionary<Guid, string>();
		foreach (Feature f in data.Features)
		{
			if (labels.TextFor(f) is string t && StructureOf(f.Id) is MapPointViewModel p)
			{
				text[p.Id] = t;
			}
		}
		return text;
	}

	/// <summary>Writes each structure's address beside its dot (when zoomed in to street level, 1:8,000 or closer).</summary>
	public bool ShowAddressLabels
	{
		get => StructuresLayer?.Labels?.Enabled == true;
		set
		{
			ReadLayers();
			if (value == ShowAddressLabels)
			{
				return;
			}
			StructuresLayer.SetStyle(StructuresLayer.Style, value ? new LabelStyle { Field = "Address", MinScale = 8000 } : null);
			LayerEdited(StructuresLayer);
			OnPropertyChanged();
		}
	}

	/// <summary>The legend when the structures are coloured by something other than their result.</summary>
	public IReadOnlyList<LegendLineViewModel> StructureLegend => ColoursByResult
		? Array.Empty<LegendLineViewModel>()
		: StructuresLayer.Legend;

	public bool HasStructureLegend => StructureLegend.Count > 0;

	/// <summary>"COLOURED BY STATUS".</summary>
	public string StructureLegendTitle => ColoursByResult ? null
		: "COLOURED BY " + (StructureFieldChoices.FirstOrDefault(f => f.Key == StructuresLayer.Style.Field)?.Label ?? StructuresLayer.Style.Field).ToUpperInvariant();

	/// <summary>The fields a layer can be styled, labelled or queried by.</summary>
	public IReadOnlyList<MapFieldChoice> FieldChoices(MapLayerViewModel layer) => layer.IsStructures
		? StructureFieldChoices
		: layer.Data?.Fields.Select(f => new MapFieldChoice(f.Name, f.DisplayName, f.Type is FieldType.Integer or FieldType.Real)).ToList() ?? new List<MapFieldChoice>();

	/// <summary>The features of a layer with a field in them (structures read an assessment field in when needed).</summary>
	public FeatureLayer DataWith(MapLayerViewModel layer, string field) => layer.IsStructures ? StructureFeaturesWith(field) : layer.Data;

	/// <summary>A layer's "Style…" menu item: colours, labels and the scale range.</summary>
	[RelayCommand]
	private async Task StyleLayer(MapLayerViewModel layer)
	{
		if (layer == null || layer.IsTiles || layer.IsFeatures && layer.Data == null)
		{
			return;
		}
		LayerStyleSheetViewModel sheet = new LayerStyleSheetViewModel(this, layer);
		if (await _app.Sheets.ShowAsync(sheet) is LayerStyleSheetViewModel.Result result)
		{
			ApplyStyle(layer, result);
		}
	}

	/// <summary>Saves and draws a layer's new style, labels and scale range.</summary>
	public void ApplyStyle(MapLayerViewModel layer, LayerStyleSheetViewModel.Result result)
	{
		layer.Record.MinScale = result.MinScale;
		layer.Record.MaxScale = result.MaxScale;
		layer.SetStyle(result.Style, result.Labels);
		LayerEdited(layer);
		if (layer.IsStructures)
		{
			StructuresChanged();
			OnPropertyChanged(nameof(ShowAddressLabels));
			OnPropertyChanged(nameof(ColoursByResult));
		}
	}
}
