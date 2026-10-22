using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.Gis.Features;

namespace Sde.App.ViewModels;

/// <summary>A column of the attribute table: the field's position, its header, and whether it sorts as a number.</summary>
public sealed record AttributeColumn(int Index, string Header, bool IsNumber);

/// <summary>One row of the attribute table: a feature, with its values as text for the cells.</summary>
public sealed class AttributeRowViewModel(Feature feature, string[] texts)
{
	public Feature Feature { get; } = feature;

	public long Id => Feature.Id;

	/// <summary>The cell text of a column (the table's columns bind to this).</summary>
	public string this[int column] => column >= 0 && column < texts.Length ? texts[column] : "";

	/// <summary>Every cell's text, in column order.</summary>
	public IReadOnlyList<string> Texts => texts;

	/// <summary>True when any cell contains the text (ignoring case).</summary>
	public bool Contains(string text) => texts.Any(t => t != null && t.Contains(text, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Sorts table rows by one column's value: numbers as numbers, empty cells last, text without regard to case.</summary>
public sealed class AttributeComparer(int column) : IComparer
{
	public int Compare(object x, object y)
	{
		object a = (x as AttributeRowViewModel)?.Feature[column];
		object b = (y as AttributeRowViewModel)?.Feature[column];
		if (a == null || b == null)
		{
			return a == null ? (b == null ? 0 : 1) : -1;
		}
		if (a is long or double && b is long or double)
		{
			return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
		}
		if (a is DateTime da && b is DateTime db)
		{
			return da.CompareTo(db);
		}
		return string.Compare(FieldValues.Format(a), FieldValues.Format(b), StringComparison.OrdinalIgnoreCase);
	}
}

/// <summary>The identify card: a clicked feature's values, field by field.</summary>
public sealed class IdentifyViewModel(string layerName, string layerKey, Feature feature, IReadOnlyList<IdentifyLine> lines)
{
	public string LayerName { get; } = layerName;

	public string LayerKey { get; } = layerKey;

	public Feature Feature { get; } = feature;

	public IReadOnlyList<IdentifyLine> Lines { get; } = lines;

	/// <summary>The first value with text, as the card's title.</summary>
	public string Title => Lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Value))?.Value ?? LayerName;
}

/// <summary>One field of the identify card.</summary>
public sealed record IdentifyLine(string Field, string Value)
{
	public string SpokenName => $"{Field}: {(string.IsNullOrEmpty(Value) ? "empty" : Value)}";
}

/// <summary>The attribute table under the map (read-only in this version) and the identify card.</summary>
public sealed partial class MapViewModel
{
	/// <summary>True while the attribute table is shown under the map.</summary>
	[ObservableProperty]
	public partial bool IsTableOpen { get; set; }

	/// <summary>The layer the table shows (structures or a layer of features).</summary>
	[ObservableProperty]
	public partial MapLayerViewModel TableLayer { get; set; }

	/// <summary>Only rows containing this text (in any column).</summary>
	[ObservableProperty]
	public partial string TableFilter { get; set; }

	/// <summary>Only the selected features.</summary>
	[ObservableProperty]
	public partial bool TableSelectedOnly { get; set; }

	public IReadOnlyList<AttributeColumn> TableColumns { get; private set; } = Array.Empty<AttributeColumn>();

	public ObservableCollection<AttributeRowViewModel> TableRows { get; } = new();

	/// <summary>"12 of 30 rows".</summary>
	[ObservableProperty]
	public partial string TableCountText { get; set; }

	/// <summary>The table's columns changed (another layer, or new fields); the page makes them again.</summary>
	public event Action TableColumnsChanged;

	private FeatureLayer _tableData;

	partial void OnIsTableOpenChanged(bool value) => RefreshTable();

	partial void OnTableLayerChanged(MapLayerViewModel value) => RefreshTable();

	partial void OnTableFilterChanged(string value) => RefreshTable();

	partial void OnTableSelectedOnlyChanged(bool value) => RefreshTable();

	/// <summary>Opens the attribute table on a layer (the structures when none is given).</summary>
	[RelayCommand]
	private void ShowTable(MapLayerViewModel layer)
	{
		ReadLayers();
		TableLayer = layer ?? TableLayer ?? StructuresLayer;
		IsTableOpen = true;
		RefreshTable();
	}

	[RelayCommand]
	private void CloseTable() => IsTableOpen = false;

	/// <summary>Reads the table's rows again (after the layer, its features, the filter or the selection changed).</summary>
	public void RefreshTable()
	{
		if (!IsTableOpen || TableLayer == null)
		{
			if (TableRows.Count > 0)
			{
				TableRows.Clear();
			}
			return;
		}
		FeatureLayer data = TableLayer.IsStructures ? StructureFeatures : TableLayer.Data;
		if (data == null)
		{
			TableRows.Clear();
			TableCountText = "Reading…";
			return;
		}
		if (!ReferenceEquals(data, _tableData) || TableColumns.Count != data.Fields.Count)
		{
			_tableData = data;
			TableColumns = data.Fields.Select((f, i) => new AttributeColumn(i, f.DisplayName, f.Type is FieldType.Integer or FieldType.Real)).ToList();
			TableColumnsChanged?.Invoke();
		}
		HashSet<long> selected = SelectedIds(TableLayer);
		string filter = string.IsNullOrWhiteSpace(TableFilter) ? null : TableFilter.Trim();
		List<AttributeRowViewModel> rows = new List<AttributeRowViewModel>();
		foreach (Feature f in data.Features)
		{
			if (TableSelectedOnly && !selected.Contains(f.Id))
			{
				continue;
			}
			string[] texts = new string[data.Fields.Count];
			for (int i = 0; i < texts.Length; i++)
			{
				texts[i] = FieldValues.Format(f[i]);
			}
			AttributeRowViewModel row = new AttributeRowViewModel(f, texts);
			if (filter == null || row.Contains(filter))
			{
				rows.Add(row);
			}
		}
		TableRows.Clear();
		foreach (AttributeRowViewModel row in rows)
		{
			TableRows.Add(row);
		}
		string total = data.Features.Count.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
		TableCountText = rows.Count == data.Features.Count
			? $"{total} row{(data.Features.Count == 1 ? "" : "s")}"
			: $"{rows.Count.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} of {total} rows";
	}

	/// <summary>Rows picked in the table become the selection on the map.</summary>
	public void SelectTableRows(IEnumerable<AttributeRowViewModel> rows)
	{
		if (TableLayer == null)
		{
			return;
		}
		List<long> ids = rows.Select(r => r.Id).ToList();
		if (TableLayer.IsStructures)
		{
			SelectStructures(ids.Select(StructureOf).Where(p => p != null).Select(p => p.Id).ToList(), add: false);
		}
		else
		{
			TableLayer.Select(ids, add: false);
			NotifyLayerSelection();
		}
	}

	/// <summary>A row was double-clicked: the map shows that feature.</summary>
	[RelayCommand]
	private void ZoomToRow(AttributeRowViewModel row)
	{
		if (row?.Feature.Geometry is NetTopologySuite.Geometries.Geometry g && !g.IsEmpty)
		{
			NetTopologySuite.Geometries.Envelope e = g.EnvelopeInternal;
			ZoomToRequested?.Invoke(new MapExtent(e.MinX, e.MinY, e.MaxX, e.MaxY));
		}
	}

	/// <summary>The ids of a layer's selected features (for structures, the features of the checked structures).</summary>
	public HashSet<long> SelectedIds(MapLayerViewModel layer)
	{
		if (layer == null)
		{
			return new HashSet<long>();
		}
		if (!layer.IsStructures)
		{
			return new HashSet<long>(layer.Selected);
		}
		HashSet<Guid> structures = SelectedStructures();
		_ = StructureFeatures;
		return _structureById.Where(kv => structures.Contains(kv.Value.Id)).Select(kv => kv.Key).ToHashSet();
	}

	// ───────────── Identify ─────────────

	/// <summary>The feature whose values the identify card shows, or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasIdentified))]
	public partial IdentifyViewModel Identified { get; set; }

	public bool HasIdentified => Identified != null;

	/// <summary>Shows a layer's feature in the identify card.</summary>
	public void Identify(string layerKey, Feature feature)
	{
		MapLayerViewModel layer = LayerWithKey(layerKey);
		if (layer == null || feature == null)
		{
			Identified = null;
			return;
		}
		List<IdentifyLine> lines = feature.Fields.Select((f, i) => new IdentifyLine(f.DisplayName, FieldValues.Format(feature[i]))).ToList();
		Identified = new IdentifyViewModel(layer.Name, layerKey, feature, lines);
	}

	[RelayCommand]
	private void CloseIdentify() => Identified = null;

	/// <summary>The identify card's "Show in table".</summary>
	[RelayCommand]
	private void ShowIdentifiedInTable()
	{
		if (Identified == null || LayerWithKey(Identified.LayerKey) is not MapLayerViewModel layer)
		{
			return;
		}
		layer.Select(new[] { Identified.Feature.Id }, add: false);
		NotifyLayerSelection();
		TableLayer = layer;
		IsTableOpen = true;
		RefreshTable();
	}
}
