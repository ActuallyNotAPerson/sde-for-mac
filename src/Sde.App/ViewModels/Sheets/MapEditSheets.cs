using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.Gis.Features;

namespace Sde.App.ViewModels.Sheets;

/// <summary>What to do with unsaved map edits before leaving them.</summary>
public enum SaveEditsChoice
{
	Save,
	Discard,
}

/// <summary>
/// The question shown when leaving unsaved map edits (another mode, layer or page, or quitting): Save, Don't save, or
/// Cancel to stay. Closes with a <see cref="SaveEditsChoice"/>, or null for Cancel.
/// </summary>
public sealed partial class SaveEditsSheetViewModel(string layerName, int changes) : SheetViewModel($"Save your edits to {layerName}?")
{
	/// <summary>"3 changes to Parcels aren't saved yet."</summary>
	public string Message { get; } = (changes == 1 ? "1 change" : $"{changes} changes") + $" to {layerName} {(changes == 1 ? "isn't" : "aren't")} saved yet. Save them to the map layers file, or throw them away.";

	public override SheetSize Size => SheetSize.Small;

	[RelayCommand]
	private void Save() => Close(SaveEditsChoice.Save);

	[RelayCommand]
	private void Discard() => Close(SaveEditsChoice.Discard);
}

/// <summary>Add layer, then New layer to draw on: its name and whether it holds points, lines or areas.</summary>
public sealed partial class NewLayerSheetViewModel : SheetViewModel
{
	/// <summary>What the sheet gives back: the name and kind of shape.</summary>
	public sealed record Result(string Name, GeometryKind Kind);

	private readonly IReadOnlyCollection<string> _taken;

	/// <param name="taken">The names of the layers already on the map (the suggested name differs from them).</param>
	public NewLayerSheetViewModel(IReadOnlyCollection<string> taken) : base("New layer to draw on")
	{
		_taken = taken ?? Array.Empty<string>();
		string name = "Sketch";
		for (int n = 2; _taken.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
		{
			name = "Sketch " + n;
		}
		LayerName = name;
	}

	public override SheetSize Size => SheetSize.Small;

	[ObservableProperty]
	public partial string LayerName { get; set; }

	/// <summary>0 points, 1 lines, 2 areas (the segmented control).</summary>
	[ObservableProperty]
	public partial int KindIndex { get; set; } = 2;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	public GeometryKind Kind => KindIndex switch
	{
		0 => GeometryKind.Point,
		1 => GeometryKind.Line,
		_ => GeometryKind.Polygon,
	};

	[RelayCommand]
	private void Create()
	{
		if (string.IsNullOrWhiteSpace(LayerName))
		{
			Problem = "Give the layer a name.";
			return;
		}
		Close(new Result(LayerName.Trim(), Kind));
	}
}

/// <summary>
/// The question after a structure's dot is dragged in Set location: "Move 12 River Street about 40 ft north-east?",
/// with the old and new coordinates. Closes with true for Move.
/// </summary>
public sealed partial class MoveStructureSheetViewModel(string address, string distance, string direction, LatLon from, LatLon to) : SheetViewModel("Move the structure?")
{
	public string Address { get; } = address;

	/// <summary>"about 40 ft".</summary>
	public string Distance { get; } = distance;

	/// <summary>"north-east".</summary>
	public string Direction { get; } = direction;

	/// <summary>"Move 12 River Street about 40 ft north-east?"</summary>
	public string Question => $"Move {Address} {Distance} {Direction}?";

	public string FromText { get; } = Coordinates(from);

	public string ToText { get; } = Coordinates(to);

	public override SheetSize Size => SheetSize.Small;

	private static string Coordinates(LatLon p) =>
		p.Latitude.ToString("0.000000", CultureInfo.InvariantCulture) + ", " + p.Longitude.ToString("0.000000", CultureInfo.InvariantCulture);

	[RelayCommand]
	private void Move() => Close(true);
}
