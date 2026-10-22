using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.Gis.Features;

namespace Sde.App.ViewModels;

/// <summary>The Map page's selection: structures (the ones checked in the Structures list) and layers' features.</summary>
public sealed partial class MapViewModel
{
	/// <summary>The map should ask for its tiles again (after "Try again").</summary>
	public event Action RetryRequested;

	/// <summary>The selected structures changed (the map rings them).</summary>
	public event Action SelectionChanged;

	/// <summary>The structures selected on the map: those whose assessments are checked in the Structures list.</summary>
	public HashSet<Guid> SelectedStructures()
	{
		HashSet<Guid> checkedIds = _main.Structures.CheckedAssessmentIds().ToHashSet();
		return _all.Where(p => p.AssessmentID is Guid a && checkedIds.Contains(a)).Select(p => p.Id).ToHashSet();
	}

	/// <summary>
	/// Selects structures, which checks their assessments in the Structures list (replacing what was checked, unless
	/// <paramref name="add"/>). Structures without an assessment can't be checked there, so they aren't selected.
	/// </summary>
	public void SelectStructures(IReadOnlyCollection<Guid> propertyIds, bool add)
	{
		HashSet<Guid> ids = _all.Where(p => propertyIds.Contains(p.Id) && p.AssessmentID != null).Select(p => p.AssessmentID!.Value).ToHashSet();
		if (add)
		{
			ids.UnionWith(_main.Structures.CheckedAssessmentIds());
		}
		_main.Structures.CheckOnly(ids);
		OnPropertyChanged(nameof(CheckedCount));
		OnPropertyChanged(nameof(HasChecked));
		SelectionChanged?.Invoke();
		if (TableSelectedOnly && TableLayer?.IsStructures == true)
		{
			RefreshTable();
		}
	}

	/// <summary>A feature of a layer was clicked: in Open mode its values are shown; in Select mode it is selected.</summary>
	public void ClickFeature(string layerKey, Feature feature, bool add)
	{
		if (Mode == MapMode.Select)
		{
			if (LayerWithKey(layerKey) is MapLayerViewModel layer)
			{
				if (add && layer.Selected.Contains(feature.Id))
				{
					layer.Selected.Remove(feature.Id);
					layer.Select(Array.Empty<long>(), add: true);
				}
				else
				{
					layer.Select(new[] { feature.Id }, add);
				}
				Message = $"{layer.SelectedCount} selected in {layer.Name}.";
				RaiseLayersChanged();
				if (TableSelectedOnly && ReferenceEquals(TableLayer, layer))
				{
					RefreshTable();
				}
			}
			return;
		}
		if (Mode == MapMode.Browse)
		{
			Identify(layerKey, feature);
		}
	}

	/// <summary>A box was drawn over a layer's features (in Select mode, selecting in that layer).</summary>
	public void SelectFeatures(string layerKey, IReadOnlyList<Feature> features, bool add)
	{
		if (LayerWithKey(layerKey) is not MapLayerViewModel layer)
		{
			return;
		}
		layer.Select(features.Select(f => f.Id), add);
		Message = layer.SelectedCount == 0 ? $"Nothing selected in {layer.Name}." : $"{layer.SelectedCount} selected in {layer.Name}.";
		RaiseLayersChanged();
		if (TableSelectedOnly && ReferenceEquals(TableLayer, layer))
		{
			RefreshTable();
		}
	}

	/// <summary>A corner was added to the line or polygon being drawn, or the shape was finished.</summary>
	public void SketchChanged(IReadOnlyList<LatLon> points, bool finished)
	{
	}

	/// <summary>"Try again" after tiles couldn't be downloaded.</summary>
	[RelayCommand]
	private void RetryTiles()
	{
		BasemapProblem = null;
		Tiles.ResetFailures();
		RetryRequested?.Invoke();
	}
}
