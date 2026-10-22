using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Data;
using Sde.Interop;
using Sde.Reports;

namespace Sde.App.ViewModels;

/// <summary>A piece of a bar: how much of the full bar it fills (0 to 1) and which colour of the app's resources it uses.</summary>
public readonly record struct BarSegment(double Fraction, string ColourKey);

/// <summary>One bar on the dashboard with its label and the numbers in words, so the bar is never the only way to read it.</summary>
public sealed class BarRowViewModel(string label, int count, string detail, IReadOnlyList<BarSegment> segments)
{
	public string Label { get; } = label;

	public int Count { get; } = count;

	/// <summary>"12 assessments, 4 substantial".</summary>
	public string Detail { get; } = detail;

	public IReadOnlyList<BarSegment> Segments { get; } = segments;

	public string SpokenName => $"{Label}: {Detail}";
}

/// <summary>
/// The Event dashboard: the headline numbers for the whole database (or the event it holds), simple bars by result,
/// street, community and inspector, and the buttons that produce the one-page summary PDF and the GIS files. The
/// numbers come from <see cref="EventSummary"/>, the same ones the PDF prints.
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
	private const int TopCount = 10;
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private readonly AppServices _app;
	private List<AssessmentFile> _files = new();

	public DashboardViewModel(AppServices app)
	{
		_app = app;
		app.DataChanged += () =>
		{
			if (IsActive)
			{
				Reload();
			}
		};
	}

	/// <summary>Set by the window while this is the page showing.</summary>
	public bool IsActive { get; set; }

	/// <summary>The numbers behind everything on the page; null until the first <see cref="Reload"/>.</summary>
	public EventSummary Summary { get; private set; }

	public ObservableCollection<BarRowViewModel> ByResult { get; } = new();

	public ObservableCollection<BarRowViewModel> Progress { get; } = new();

	public ObservableCollection<BarRowViewModel> ByStreet { get; } = new();

	public ObservableCollection<BarRowViewModel> ByCommunity { get; } = new();

	public ObservableCollection<BarRowViewModel> ByInspector { get; } = new();

	/// <summary>An optional name printed on the summary and kept in the GIS files' layer title.</summary>
	[ObservableProperty]
	public partial string EventName { get; set; }

	[ObservableProperty]
	public partial string Structures { get; set; }

	[ObservableProperty]
	public partial string StructuresNote { get; set; }

	[ObservableProperty]
	public partial string SubstantialText { get; set; }

	[ObservableProperty]
	public partial string SubstantialNote { get; set; }

	[ObservableProperty]
	public partial string BuildingValue { get; set; }

	[ObservableProperty]
	public partial string RepairCost { get; set; }

	/// <summary>"Damage occurred Jun 1 to Jun 3, 2026. Inspected ..." or null when no dates were entered.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasDates))]
	public partial string Dates { get; set; }

	public bool HasDates => Dates != null;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasAnything))]
	public partial int TotalStructures { get; set; }

	public bool HasAnything => TotalStructures > 0;

	/// <summary>Whether the GIS files carry the owners' names. Off unless ticked, because names are personal information.</summary>
	[ObservableProperty]
	public partial bool IncludeOwnerNames { get; set; }

	/// <summary>What the last export wrote.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasResult))]
	public partial string LastResult { get; set; }

	public bool HasResult => LastResult != null;

	public string OwnerWarning => "The file will contain owners' names, which are personal information. Share it only with people who are allowed to see them.";

	/// <summary>True when no event is chosen in the sidebar: the page covers the whole database and takes a typed name.</summary>
	public bool IsAllEvents => _app.Scope.IsAll;

	public string Subtitle => _app.Scope.IsAll
		? "The numbers for every structure in this database, in one place."
		: $"The numbers for {_app.Scope.Event.Label}, in one place.";

	/// <summary>The second chart: assessed and not yet assessed for the whole database; review status for one event.</summary>
	public string ProgressTitle => _app.Scope.IsAll ? "Assessed and not yet assessed" : "Review status";

	/// <summary>The name printed on the summary and used for file names: the chosen event, else the typed name.</summary>
	private string Title => _app.Scope.Event?.Label ?? (string.IsNullOrWhiteSpace(EventName) ? null : EventName.Trim());

	/// <summary>Reads the database again and rebuilds every number and bar.</summary>
	public void Reload()
	{
		List<Guid> ids;
		List<StructureSummary> inEvent = null;
		if (_app.Scope.IsAll)
		{
			ids = _app.Store.ListMapPoints().Where(r => r.AssessmentID != null).Select(r => r.AssessmentID!.Value).ToList();
		}
		else
		{
			// Each structure's latest assessment in the event.
			inEvent = _app.Store.ListStructures().Where(r => r.AssessmentID != null && _app.Scope.Includes(r.AssessmentID))
				.GroupBy(r => r.PropertyID)
				.Select(g => g.OrderByDescending(r => r.DateofInspection).ThenByDescending(r => r.LastUpdate).First())
				.ToList();
			ids = inEvent.Select(r => r.AssessmentID!.Value).ToList();
		}
		_files = _app.Store.LoadAssessments(ids);
		Summary = EventSummary.Build(_files, _app.Scope.IsAll ? _app.Store.GetProperties() : null, Title, _app.Store.Reference,
			details: _app.Scope.Event?.ToDetails());
		Show(Summary);
		if (inEvent != null)
		{
			// "Not yet assessed" means nothing inside one event: every structure in it has an assessment there.
			StructuresNote = "With an assessment in this event";
			ShowStatuses(inEvent);
		}
		LoadCompliance();
		LoadTeams();
		OnPropertyChanged(nameof(IsAllEvents));
		OnPropertyChanged(nameof(Subtitle));
		OnPropertyChanged(nameof(ProgressTitle));
	}

	/// <summary>How far the event's assessments have got through review.</summary>
	private void ShowStatuses(List<StructureSummary> rows)
	{
		Dictionary<AssessmentStatus, int> counts = Enum.GetValues<AssessmentStatus>().ToDictionary(v => v, v => 0);
		foreach (StructureSummary r in rows)
		{
			counts[Enum.TryParse(r.Status, out AssessmentStatus st) ? st : AssessmentStatus.Draft]++;
		}
		Fill(Progress, new[] { AssessmentStatus.Draft, AssessmentStatus.Reviewed, AssessmentStatus.Final, AssessmentStatus.LetterSent, AssessmentStatus.Appeal }
			.Select(v => (AssessmentStatuses.Label(v), counts[v], $"{counts[v]:N0}", new[] { (counts[v], v is AssessmentStatus.Final or AssessmentStatus.LetterSent ? "Accent" : "StatusIncompleteFg") })));
	}

	private void Show(EventSummary s)
	{
		TotalStructures = s.TotalStructures;
		Structures = s.TotalStructures.ToString("N0", Us);
		StructuresNote = $"{s.AssessedStructures:N0} assessed, {s.NotYetAssessed:N0} not yet";
		int substantial = s.Count(DeterminationClass.Substantial);
		SubstantialText = substantial.ToString("N0", Us);
		SubstantialNote = s.AssessmentCount == 0 ? "No assessments yet" : $"{100.0 * substantial / s.AssessmentCount:0.#}% of assessments";
		BuildingValue = s.TotalBuildingValue.ToString("C0", Us);
		RepairCost = s.TotalRepairCost.ToString("C0", Us);
		Dates = DateText(s);

		Fill(ByResult, new[]
		{
			DeterminationClass.Substantial, DeterminationClass.Close, DeterminationClass.NotSubstantial, DeterminationClass.Undetermined, DeterminationClass.Incomplete,
		}.Select(c => (Label(c), s.Count(c), $"{s.Count(c):N0}", new[] { (s.Count(c), "Status" + c + "Fg") })));
		Fill(Progress, new[]
		{
			("Assessed", s.AssessedStructures, $"{s.AssessedStructures:N0} structures", new[] { (s.AssessedStructures, "Accent") }),
			("Not yet assessed", s.NotYetAssessed, $"{s.NotYetAssessed:N0} structures", new[] { (s.NotYetAssessed, "StatusIncompleteFg") }),
		});
		Fill(ByStreet, s.ByStreet.Take(TopCount).Select(Group));
		Fill(ByCommunity, s.ByCommunity.Take(TopCount).Select(Group));
		Fill(ByInspector, s.ByInspector.Take(TopCount).Select(Group));
	}

	private static string Label(DeterminationClass c) => c switch
	{
		DeterminationClass.Substantial => "Substantially damaged",
		DeterminationClass.Close => "Close to the threshold",
		DeterminationClass.NotSubstantial => "Not substantially damaged",
		DeterminationClass.Undetermined => "Undetermined",
		_ => "Incomplete",
	};

	private static (string Label, int Count, string Detail, (int Part, string Colour)[] Parts) Group(EventGroup g) => (
		g.Label, g.Count,
		$"{g.Count:N0} assessment{(g.Count == 1 ? "" : "s")}" + (g.Substantial > 0 ? $", {g.Substantial:N0} substantial" : ""),
		new[] { (g.Substantial, "StatusSubstantialFg"), (g.Close, "StatusCloseFg"), (g.NotSubstantial, "StatusNotSubstantialFg"), (g.Other, "StatusIncompleteFg") });

	/// <summary>Replaces the rows of one chart. Every bar is drawn against the longest in its own chart.</summary>
	private static void Fill(ObservableCollection<BarRowViewModel> rows, IEnumerable<(string Label, int Count, string Detail, (int Part, string Colour)[] Parts)> items)
	{
		List<(string Label, int Count, string Detail, (int Part, string Colour)[] Parts)> list = items.ToList();
		double max = Math.Max(1, list.Count == 0 ? 1 : list.Max(i => i.Count));
		rows.Clear();
		foreach ((string label, int count, string detail, (int Part, string Colour)[] parts) in list)
		{
			rows.Add(new BarRowViewModel(label, count, detail, parts.Where(p => p.Part > 0).Select(p => new BarSegment(p.Part / max, p.Colour)).ToList()));
		}
	}

	private static string DateText(EventSummary s)
	{
		List<string> parts = new();
		if (Range(s.DamageStart, s.DamageEnd) is string damage)
		{
			parts.Add("Damage occurred " + damage);
		}
		if (Range(s.InspectionStart, s.InspectionEnd) is string inspected)
		{
			parts.Add("inspected " + inspected);
		}
		if (parts.Count == 0)
		{
			return null;
		}
		string text = string.Join(", ", parts);
		return char.ToUpperInvariant(text[0]) + text[1..];
	}

	private static string Range(DateTime? from, DateTime? to)
	{
		if (from == null || to == null)
		{
			return null;
		}
		string a = from.Value.ToString("MMM d, yyyy", Us);
		string b = to.Value.ToString("MMM d, yyyy", Us);
		return a == b ? a : $"{a} to {b}";
	}

	partial void OnEventNameChanged(string value)
	{
		if (Summary != null && IsActive)
		{
			Reload();
		}
	}

	// ───────────── Exports ─────────────

	/// <summary>The one-page PDF for officials, then opened in Preview.</summary>
	[RelayCommand]
	private async Task SummaryPdf()
	{
		Reload();
		string name = Title ?? "Event summary";
		string path = await _app.Dialogs.SaveFileAsync("Save the event summary", name + ".pdf", "PDF document", "pdf");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		EventSummary summary = Summary;
		EventSummaryOptions options = new EventSummaryOptions { Title = Title };
		JobOutcome<bool> result = await _app.Jobs.RunAsync("Writing the event summary", (progress, ct) =>
		{
			EventSummaryReport.RenderToFile(summary, options, path);
			return true;
		}, "Couldn't write the event summary");
		if (result.Completed)
		{
			LastResult = "Saved " + Path.GetFileName(path) + ".";
			_app.Dialogs.Open(path);
		}
	}

	[RelayCommand]
	private Task GeoPackage() => ExportGis("GeoPackage for GIS", ".gpkg", "GeoPackage", (path, rows, options) => GisExport.WriteGeoPackage(path, rows, options));

	[RelayCommand]
	private Task GeoJson() => ExportGis("GeoJSON", ".geojson", "GeoJSON", (path, rows, options) => GisExport.WriteGeoJson(path, rows, options));

	private async Task ExportGis(string title, string extension, string filterName, Func<string, List<GisRow>, GisExportOptions, GisExportResult> write)
	{
		Reload();
		string baseName = Title ?? "SDE structures";
		string path = await _app.Dialogs.SaveFileAsync("Save " + title, baseName + extension, filterName, extension.TrimStart('.'));
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		List<GisRow> rows = GisRow.From(_files, _app.Store.Reference, Summary.SubstantialPercent, Summary.ClosePercent);
		GisExportOptions options = new GisExportOptions
		{
			IncludeOwnerNames = IncludeOwnerNames,
			LayerTitle = Title ?? "SDE structures",
		};
		string stylePath = Path.ChangeExtension(path, ".qml");
		JobOutcome<GisExportResult> result = await _app.Jobs.RunAsync("Writing " + title, (progress, ct) =>
		{
			GisExportResult r = write(path, rows, options);
			GisExport.WriteQgisStyle(stylePath);
			return r;
		}, "Couldn't write the file");
		if (!result.Completed)
		{
			return;
		}
		GisExportResult done = result.Value;
		string skipped = done.SkippedNoCoordinates == 0 ? "" : $" {done.SkippedNoCoordinates} had no coordinates and {(done.SkippedNoCoordinates == 1 ? "was" : "were")} left out.";
		LastResult = $"Saved {done.Features} structure{(done.Features == 1 ? "" : "s")} to {Path.GetFileName(path)}, with a QGIS style file ({Path.GetFileName(stylePath)}) beside it.{skipped}";
		_app.Dialogs.Reveal(path);
	}
}
