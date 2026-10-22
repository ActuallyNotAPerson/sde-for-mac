using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Reports;

namespace Sde.App.ViewModels;

/// <summary>Printing the map: a Letter-size PDF of the area in view, with a legend, scale, north arrow, sources and the date.</summary>
public sealed partial class MapViewModel
{
	private static readonly CultureInfo Us2 = CultureInfo.GetCultureInfo("en-US");

	/// <summary>
	/// The map as it is on screen, in print colours, with its legend (the page sets this while the map is showing; it
	/// knows the view and the theme's colours).
	/// </summary>
	public Func<MapPrintScene> PrintScene { get; set; }

	/// <summary>The title the print sheet suggests: the event's name, or "Structures".</summary>
	public string DefaultPrintTitle => _app.Scope.IsAll ? "Structures" : _app.Scope.Event.Label;

	/// <summary>Print…: the sheet, where to save the PDF, then the PDF (opened when done).</summary>
	[RelayCommand]
	private async Task PrintMap()
	{
		ReadLayers();
		if (PrintScene == null)
		{
			return;
		}
		if (await _app.Sheets.ShowAsync(new PrintMapSheetViewModel(DefaultPrintTitle)) is not PrintMapSheetViewModel.Result choice)
		{
			return;
		}
		string file = string.Concat(choice.Title.Split(Path.GetInvalidFileNameChars())).Replace(':', '-').Trim().TrimStart('.');
		string path = await _app.Dialogs.SaveFileAsync("Save the map as a PDF", (file.Length == 0 ? "Map" : file) + ".pdf", "PDF", "pdf");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		await PrintToAsync(choice, path);
	}

	/// <summary>
	/// Prints the map to a PDF: the picture at print resolution (waiting up to <paramref name="tileTimeout"/> for map
	/// tiles), then the page. Opens the PDF and returns the picture's details; null when it couldn't be made.
	/// </summary>
	public async Task<MapPrintImage> PrintToAsync(PrintMapSheetViewModel.Result choice, string path, TimeSpan? tileTimeout = null)
	{
		MapPrintScene scene = PrintScene?.Invoke();
		if (scene == null)
		{
			return null;
		}
		(double width, double height) = MapPrintReport.MapFrame(choice.Landscape);
		string attribution = Attribution;
		string subtitle = _app.Scope.IsAll ? null : _app.Scope.Event.Label == choice.Title ? null : _app.Scope.Event.Label;
		JobOutcome<MapPrintImage> outcome = await _app.Jobs.RunTaskAsync("Printing the map", async (progress, cancel) =>
		{
			MapPrintImage image = await MapPrinter.RenderAsync(scene, Tiles, width, height, tileTimeout ?? MapPrinter.TileTimeout, cancel);
			MapPrintReport.RenderToFile(new MapPrintPage
			{
				Title = choice.Title,
				Subtitle = subtitle,
				Landscape = choice.Landscape,
				MapPng = image.Png,
				Legend = scene.Legend,
				ScaleText = ScaleText(image),
				Attribution = attribution,
				Note = TileNote(image),
				PrintedOn = DateTime.Now,
			}, path);
			return image;
		}, "Couldn't print the map");
		if (!outcome.Completed)
		{
			return null;
		}
		_app.Dialogs.Open(path);
		Message = outcome.Value.TilesMissing > 0
			? $"Saved the map as {Path.GetFileName(path)}. Some of the background map didn't arrive in time, so parts of it are blank."
			: $"Saved the map as {Path.GetFileName(path)}.";
		return outcome.Value;
	}

	/// <summary>"1:4,800 (1 inch = 400 feet)", or miles for small scales.</summary>
	public static string ScaleText(MapPrintImage image)
	{
		string ratio = "1:" + Math.Round(image.Scale).ToString("N0", Us2);
		double feet = image.FeetPerInch;
		string inch = feet < 2640
			? $"1 inch = {Math.Round(feet).ToString("N0", Us2)} feet"
			: $"1 inch = {(feet / 5280).ToString("0.##", Us2)} miles";
		return $"{ratio} ({inch})";
	}

	/// <summary>The warning printed under the map when some tiles never came, or null.</summary>
	public static string TileNote(MapPrintImage image) => image.TilesMissing == 0
		? null
		: $"Some of the background map couldn't be downloaded in time ({image.TilesMissing} of {image.TilesWanted} pieces), so parts of it are blank. Print again when the internet connection is better.";
}
