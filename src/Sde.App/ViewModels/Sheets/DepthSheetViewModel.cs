using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Depth;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Raster;
using Sde.Interop.Online;

namespace Sde.App.ViewModels.Sheets;

/// <summary>Where the water surface comes from.</summary>
public enum DepthSource
{
	/// <summary>A file of high-water marks (CSV or GeoJSON, such as a USGS export).</summary>
	MarksFile,
	/// <summary>USGS's high-water marks for an event (the STN service).</summary>
	Usgs,
	/// <summary>A water-surface elevation grid (GeoTIFF).</summary>
	Grid,
}

/// <summary>One structure's proposed flood depth, to tick.</summary>
public sealed partial class FloodDepthRowViewModel : ObservableObject
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public FloodDepthRowViewModel(DepthRow row)
	{
		Row = row;
		IsChecked = row.CanApply;
	}

	public DepthRow Row { get; }

	public string Address => Row.Address;

	public string Now => Join(Row.CurrentFloodElevation, Row.CurrentAboveFloor) is { Length: > 0 } now ? now : "(blank)";

	public string Proposed => Row.CanApply ? Join(Ft(Row.Proposal.FloodElevationFt), Ft(Row.Proposal.DepthAboveLowestFloorFt)) : "";

	public string Confidence => Row.CanApply ? Row.Estimate?.Confidence.ToString() ?? "" : "";

	/// <summary>How the estimate was made, or why there is none.</summary>
	public string Note => Row.Skipped ?? string.Join(" ", new[] { Row.Estimate?.Note }.Concat(Row.Proposal?.Notes ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());

	public bool CanApply => Row.CanApply;

	[ObservableProperty]
	public partial bool IsChecked { get; set; }

	public string CheckName => "Use the depth for " + Address;

	private static string Ft(decimal? v) => v is decimal d ? d.ToString("0.0", Us) + " ft" : "";

	/// <summary>"742.5 ft · 2.5 ft above the floor".</summary>
	private static string Join(string flood, string above) =>
		string.Join(" · ", new[] { flood, string.IsNullOrEmpty(above) ? null : above + " above the floor" }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Flood depth from high-water marks or a water-surface grid (C4) for the chosen assessments: estimate the water
/// surface at each structure, compare it with what the assessment has, and save the ticked ones as "Depth tool" in
/// one edit history batch (the depth table's damage suggestions follow). Each event's datum offset converts marks in
/// another vertical datum. Closes with the <see cref="DepthApplyResult"/>.
/// </summary>
public sealed partial class DepthSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly IReadOnlyList<Guid> _assessmentIds;
	private List<HighWaterMark> _marks = new();

	public DepthSheetViewModel(AppServices app, IReadOnlyList<Guid> assessmentIds) : base("Flood depth from high-water marks")
	{
		_app = app;
		_assessmentIds = assessmentIds;
		Intro = $"For {assessmentIds.Count:N0} assessment{(assessmentIds.Count == 1 ? "" : "s")}. SDE estimates the water surface at each structure "
			+ "and proposes the flood elevation and the depth above the lowest floor. Nothing changes until you save.";
		UsgsStates = string.Join(", ", app.Store.LoadAssessments(assessmentIds)
			.Select(f => app.Store.Reference.StateAbbreviation(f.Property.StateID)).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s));
	}

	public override SheetSize Size => SheetSize.Large;

	public string Intro { get; }

	public IReadOnlyList<string> Datums { get; } = new[] { "NAVD88", "NGVD29", "Not given" };

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMarksFile), nameof(IsUsgs), nameof(IsGrid))]
	public partial DepthSource Source { get; set; }

	public bool IsMarksFile
	{
		get => Source == DepthSource.MarksFile;
		set { if (value) { Source = DepthSource.MarksFile; } }
	}

	public bool IsUsgs
	{
		get => Source == DepthSource.Usgs;
		set { if (value) { Source = DepthSource.Usgs; } }
	}

	public bool IsGrid
	{
		get => Source == DepthSource.Grid;
		set { if (value) { Source = DepthSource.Grid; } }
	}

	// ───────────── Marks ─────────────

	/// <summary>The datum of marks whose row doesn't name one.</summary>
	[ObservableProperty]
	public partial string MarksDatum { get; set; } = "Not given";

	[ObservableProperty]
	public partial string MarksSummary { get; set; }

	/// <summary>Search radius in metres, how many of the nearest marks to use, and the worst USGS quality (1 to 6) to use.</summary>
	[ObservableProperty]
	public partial decimal? RadiusMeters { get; set; } = 1000;

	[ObservableProperty]
	public partial decimal? NearestCount { get; set; } = 5;

	[ObservableProperty]
	public partial decimal? WorstQuality { get; set; } = 6;

	[RelayCommand]
	private async Task ChooseMarksFile()
	{
		IReadOnlyList<string> picked = await _app.Dialogs.PickFilesAsync("Choose a file of high-water marks", false, "High-water marks", "*.csv", "*.txt", "*.geojson", "*.json");
		if (picked == null || picked.Count == 0)
		{
			return;
		}
		try
		{
			HighWaterMarkFile file = HighWaterMarkFiles.Read(picked[0], MarksDatum == "Not given" ? null : MarksDatum);
			_marks = file.Marks;
			MarksSummary = $"{Path.GetFileName(picked[0])}: {file.Marks.Count:N0} mark{(file.Marks.Count == 1 ? "" : "s")}"
				+ (file.Skipped > 0 ? $", {file.Skipped:N0} row{(file.Skipped == 1 ? "" : "s")} skipped ({string.Join("; ", file.Problems.Take(3))})" : "") + ".";
			Estimate();
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
		{
			Error = "Couldn't read the file: " + ex.Message;
		}
	}

	// ───────────── USGS ─────────────

	public ObservableCollection<StnEvent> UsgsEvents { get; } = new();

	[ObservableProperty]
	public partial StnEvent SelectedUsgsEvent { get; set; }

	/// <summary>The states to ask USGS for, as two-letter codes ("OH, KY"); filled from the structures.</summary>
	[ObservableProperty]
	public partial string UsgsStates { get; set; }

	[ObservableProperty]
	public partial string UsgsNotice { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNotBusy))]
	public partial bool IsBusy { get; set; }

	public bool IsNotBusy => !IsBusy;

	private StnClient Stn() => new StnClient(_app.HttpClientFactory(), string.IsNullOrWhiteSpace(_app.Settings.UsgsStnUrl) ? StnClient.DefaultBaseUrl : _app.Settings.UsgsStnUrl.Trim());

	/// <summary>Asks USGS for its list of events (needs the internet).</summary>
	[RelayCommand]
	private async Task LoadUsgsEvents()
	{
		await Busy(async () =>
		{
			IReadOnlyList<StnEvent> events = await Stn().GetEventsAsync(CancellationToken.None);
			UsgsEvents.Clear();
			foreach (StnEvent e in events.OrderByDescending(e => e.StartDate ?? DateTime.MinValue))
			{
				UsgsEvents.Add(e);
			}
			SelectedUsgsEvent = UsgsEvents.FirstOrDefault();
		});
	}

	/// <summary>Downloads the chosen event's marks in the listed states, then estimates.</summary>
	[RelayCommand]
	private async Task DownloadUsgsMarks()
	{
		if (SelectedUsgsEvent == null)
		{
			Error = "Choose a USGS event first.";
			return;
		}
		string[] states = (UsgsStates ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()).ToArray();
		await Busy(async () =>
		{
			StnHighWaterMarks got = await Stn().GetHighWaterMarksAsync(SelectedUsgsEvent.Id, states, CancellationToken.None);
			_marks = got.Marks.ToList();
			MarksSummary = $"{SelectedUsgsEvent.Name}: {got.Marks.Count:N0} mark{(got.Marks.Count == 1 ? "" : "s")} from USGS" + (got.Skipped > 0 ? $", {got.Skipped:N0} without a usable position or elevation" : "") + ".";
			UsgsNotice = got.Notice;
			Estimate();
		});
	}

	private async Task Busy(Func<Task> work)
	{
		IsBusy = true;
		Error = null;
		try
		{
			await work();
		}
		catch (OnlineServiceException ex)
		{
			Error = ex.Message;
		}
		catch (HttpRequestException ex)
		{
			Error = "Couldn't reach USGS: " + ex.Message;
		}
		finally
		{
			IsBusy = false;
		}
	}

	// ───────────── Grid ─────────────

	[ObservableProperty]
	public partial string GridFile { get; set; }

	[ObservableProperty]
	public partial string GridDatum { get; set; } = "NAVD88";

	/// <summary>The grid's values are feet (else metres).</summary>
	[ObservableProperty]
	public partial bool GridInFeet { get; set; } = true;

	public bool GridInMeters
	{
		get => !GridInFeet;
		set => GridInFeet = !value;
	}

	[RelayCommand]
	private async Task ChooseGrid()
	{
		IReadOnlyList<string> picked = await _app.Dialogs.PickFilesAsync("Choose a water-surface elevation grid", false, "GeoTIFF", "*.tif", "*.tiff");
		if (picked == null || picked.Count == 0)
		{
			return;
		}
		GridFile = picked[0];
		Estimate();
	}

	// ───────────── Estimate and save ─────────────

	public ObservableCollection<FloodDepthRowViewModel> Rows { get; } = new();

	public bool HasRows => Rows.Count > 0;

	[ObservableProperty]
	public partial string Summary { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; }

	public bool HasError => !string.IsNullOrEmpty(Error);

	/// <summary>Estimates every structure again from the current source and options.</summary>
	[RelayCommand]
	private void Estimate()
	{
		Error = null;
		List<DepthRow> rows;
		try
		{
			rows = Source == DepthSource.Grid ? FromGrid() : FromMarks();
		}
		catch (GisException ex)
		{
			Error = ex.Message;
			return;
		}
		catch (IOException ex)
		{
			Error = "Couldn't read the grid: " + ex.Message;
			return;
		}
		if (rows == null)
		{
			return;
		}
		Rows.Clear();
		foreach (DepthRow r in rows)
		{
			Rows.Add(new FloodDepthRowViewModel(r));
		}
		int can = rows.Count(r => r.CanApply);
		Summary = $"{can:N0} of {rows.Count:N0} can be filled in." + (can < rows.Count ? " The others say why not." : "");
		OnPropertyChanged(nameof(HasRows));
	}

	private List<DepthRow> FromMarks()
	{
		if (_marks.Count == 0)
		{
			return null;
		}
		HighWaterMarkOptions options = new HighWaterMarkOptions
		{
			RadiusMeters = (double)(RadiusMeters ?? 1000),
			NearestCount = (int)(NearestCount ?? 5),
			WorstQuality = (int)(WorstQuality ?? 6),
		};
		return DepthFromMarks.Propose(_app.Store, _assessmentIds, _marks, options);
	}

	private List<DepthRow> FromGrid()
	{
		if (string.IsNullOrEmpty(GridFile))
		{
			return null;
		}
		using GeoTiffRaster grid = GeoTiffRaster.Open(GridFile);
		decimal factor = GridInFeet ? 1m : 1m / 0.3048m;
		return DepthFromMarks.ProposeFromGrid(_app.Store, _assessmentIds,
			(lat, lon) => grid.Sample(lon, lat) is double v && !double.IsNaN(v) ? (decimal)v * factor : null,
			GridDatum == "Not given" ? null : GridDatum, Path.GetFileName(GridFile));
	}

	/// <summary>Saves the ticked rows as one Depth tool batch.</summary>
	[RelayCommand]
	private void Save()
	{
		List<DepthRow> chosen = Rows.Where(r => r.IsChecked && r.CanApply).Select(r => r.Row).ToList();
		if (chosen.Count == 0)
		{
			Error = "Tick at least one structure to fill in.";
			return;
		}
		DepthApplyResult result = _app.Writes.Write(chosen.Select(r => r.AssessmentID).ToList(), () => DepthFromMarks.Apply(_app.Store, chosen));
		Close(result);
	}
}
