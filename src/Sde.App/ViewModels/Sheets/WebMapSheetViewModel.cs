using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Records;
using Sde.Interop.WebMap;

namespace Sde.App.ViewModels.Sheets;

/// <summary>A field that can be shown in each structure's popup on an internal web map.</summary>
public sealed partial class PopupFieldOption(string key, string label, bool isChecked) : ObservableObject
{
	public string Key { get; } = key;

	public string Label { get; } = label;

	[ObservableProperty]
	public partial bool IsChecked { get; set; } = isChecked;
}

/// <summary>
/// Makes a web map folder (C9) of some structures: index.html with Leaflet inlined, the data as GeoJSON, a legend,
/// popups and, for an event, its summary. For the office it shows a dot per structure with chosen fields; for the public
/// it counts structures in grid squares, never shows owners, and hides addresses or cuts them to the block. Opened from
/// Structures (Export ▾) and the map's selection bar. Closes with a line saying what was made.
/// </summary>
public sealed partial class WebMapSheetViewModel : SheetViewModel
{
	/// <summary>The fields offered for internal popups (address, result and percent damaged are always shown).</summary>
	private static readonly (FieldModel Model, string Member, bool Checked)[] Offered =
	{
		(FieldModel.Assessment, nameof(AssessmentRecord.DateofInspection), true),
		(FieldModel.Assessment, nameof(AssessmentRecord.DepthOfFloodAboveFFE), true),
		(FieldModel.Property, nameof(PropertyRecord.FIRMZone), false),
		(FieldModel.Property, nameof(PropertyRecord.StructureTypeID), false),
		(FieldModel.Property, nameof(PropertyRecord.ParcelNumber), false),
		(FieldModel.Assessment, nameof(AssessmentRecord.InspectorName), false),
		(FieldModel.Property, nameof(PropertyRecord.OwnerLastName), false),
	};

	/// <summary>Grid square sizes for a public map, in metres.</summary>
	public static readonly IReadOnlyList<int> CellSizes = new[] { 100, 200, 500 };

	public static readonly IReadOnlyList<string> CellSizeNames = new[] { "100 m", "200 m", "500 m" };

	private readonly AppServices _app;
	private readonly IReadOnlyList<Guid> _assessmentIds;

	public WebMapSheetViewModel(AppServices app, IReadOnlyList<Guid> assessmentIds)
		: base(assessmentIds.Count == 1 ? "Web map of 1 structure" : $"Web map of {assessmentIds.Count:N0} structures")
	{
		_app = app;
		_assessmentIds = assessmentIds;
		EventLabel = app.Scope.Event?.Label;
		MapTitle = EventLabel ?? "Damage assessments";
		IncludeSummary = EventLabel != null;
		foreach ((FieldModel model, string member, bool isChecked) in Offered)
		{
			if (FieldCatalog.Find(model, member) is FieldInfo f)
			{
				PopupFields.Add(new PopupFieldOption(f.Key, f.Label, isChecked));
			}
		}
	}

	public override SheetSize Size => SheetSize.Medium;

	/// <summary>The event chosen in the sidebar, or null for all events.</summary>
	public string EventLabel { get; }

	/// <summary>The title at the top of the web map.</summary>
	[ObservableProperty]
	public partial string MapTitle { get; set; }

	/// <summary>True: for the public (grid squares, no owners, addresses hidden or cut to the block). False: full detail.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsInternal))]
	public partial bool IsPublic { get; set; }

	public bool IsInternal
	{
		get => !IsPublic;
		set => IsPublic = !value;
	}

	/// <summary>Public maps: false hides addresses, true shows "100 block of River St".</summary>
	[ObservableProperty]
	public partial bool ShowBlocks { get; set; } = true;

	/// <summary>Public maps: the grid square size, as an index into <see cref="CellSizes"/>.</summary>
	[ObservableProperty]
	public partial int CellSizeIndex { get; set; } = 1;

	/// <summary>Internal maps: the fields shown in each structure's popup.</summary>
	public List<PopupFieldOption> PopupFields { get; } = new();

	/// <summary>0 = USGS topographic, 1 = OpenStreetMap, 2 = no background map.</summary>
	[ObservableProperty]
	public partial int BasemapIndex { get; set; }

	/// <summary>Shows the summary (counts, value, cost to repair) beside the map.</summary>
	[ObservableProperty]
	public partial bool IncludeSummary { get; set; }

	public string SummaryLabel => EventLabel == null ? "Show a summary beside the map" : $"Show the {EventLabel} summary beside the map";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	[ObservableProperty]
	public partial bool IsBusy { get; set; }

	public override bool CanCancel => !IsBusy;

	/// <summary>The choices as the export takes them.</summary>
	public WebMapOptions Options(EventSummary summary) => new WebMapOptions
	{
		Title = string.IsNullOrWhiteSpace(MapTitle) ? "Damage assessments" : MapTitle.Trim(),
		Privacy = IsPublic ? WebMapPrivacy.Public : WebMapPrivacy.Internal,
		AddressDetail = ShowBlocks ? PublicAddressDetail.Block : PublicAddressDetail.Hidden,
		CellMeters = CellSizes[Math.Clamp(CellSizeIndex, 0, CellSizes.Count - 1)],
		PopupFields = PopupFields.Where(p => p.IsChecked).Select(p => p.Key).ToList(),
		Basemap = BasemapIndex switch { 1 => WebMapBasemap.OpenStreetMap, 2 => WebMapBasemap.None, _ => WebMapBasemap.UsgsTopo },
		Summary = summary,
	};

	[RelayCommand]
	private async Task Create()
	{
		string parent = await _app.Dialogs.PickFolderAsync("Choose where to put the web map folder");
		if (parent == null)
		{
			return;
		}
		IsBusy = true;
		try
		{
			ReferenceData reference = _app.Store.Reference;
			List<AssessmentFile> files = _app.Store.LoadAssessments(_assessmentIds);
			EventSummary summary = IncludeSummary
				? EventSummary.Build(files, null, EventLabel ?? MapTitle?.Trim(), reference, details: _app.Scope.Event?.ToDetails())
				: null;
			WebMapOptions options = Options(summary);
			string folder = FreeFolder(parent, options.Title);
			WebMapResult result = await Task.Run(() => WebMapExport.Write(files, reference, options, folder));
			_app.Dialogs.Open(result.IndexPath);
			string skipped = result.SkippedNoCoordinates == 0 ? "" : $" {result.SkippedNoCoordinates} without coordinates {(result.SkippedNoCoordinates == 1 ? "was" : "were")} left out.";
			Close($"Made the web map in {Path.GetFileName(folder)}.{skipped}");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Problem = "Couldn't write the web map there. " + ex.Message;
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>"Damage assessments web map", or "… 2" when that folder is already there.</summary>
	public static string FreeFolder(string parent, string title)
	{
		string safe = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '-' : c).ToArray()).Trim();
		string name = (safe.Length == 0 ? "Web map" : safe) + " web map";
		string path = Path.Combine(parent, name);
		for (int i = 2; Directory.Exists(path) || File.Exists(path); i++)
		{
			path = Path.Combine(parent, $"{name} {i}");
		}
		return path;
	}
}
