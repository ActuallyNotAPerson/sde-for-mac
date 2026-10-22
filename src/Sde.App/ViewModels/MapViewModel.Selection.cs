using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.Gis.Features;

namespace Sde.App.ViewModels;

/// <summary>The Map page's clicks on layers' features, boxes drawn over them, and shapes drawn on the map.</summary>
public sealed partial class MapViewModel
{
	/// <summary>The map should ask for its tiles again (after "Try again").</summary>
	public event Action RetryRequested;

	/// <summary>A feature of a layer was clicked.</summary>
	public void ClickFeature(string layerKey, Feature feature, bool add)
	{
	}

	/// <summary>A box was drawn over a layer's features (in Select mode, selecting in that layer).</summary>
	public void SelectFeatures(string layerKey, IReadOnlyList<Feature> features, bool add)
	{
		if (LayerWithKey(layerKey) is not MapLayerViewModel layer)
		{
			return;
		}
		layer.Select(features.Select(f => f.Id), add);
		RaiseLayersChanged();
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
