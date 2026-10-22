using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>One row in the structure list.</summary>
public sealed partial class StructureRowViewModel(StructureSummary s, ReferenceData reference = null) : ObservableObject
{
	public StructureSummary Summary { get; } = s;

	public Guid? AssessmentID => Summary.AssessmentID;

	public string Address => string.IsNullOrWhiteSpace(Summary.Address) ? "(no address)" : Summary.Address;

	public string Owner => Summary.Owner;

	/// <summary>"Permits BLD-2026-00123" (source and record ID) for an imported inspection or structure, otherwise null.</summary>
	public string Source => Summary.ExternalId == null ? null : $"{Summary.ExternalSource} {Summary.ExternalId}";

	/// <summary>The line under the address: the owner and, for imported inspections, the source record.</summary>
	public string Detail => string.Join(" · ", new[] { Summary.Owner, Source }.Where(x => !string.IsNullOrWhiteSpace(x)));

	/// <summary>The NFIP community when known (what reports group by), otherwise the city.</summary>
	public string Place => !string.IsNullOrWhiteSpace(Summary.NFIPCommunityName) ? Summary.NFIPCommunityName : Summary.City;

	public string CommunityId => Summary.NFIPCID;

	public string Type => Summary.StructureTypeID == 1 ? "Residential" : Summary.StructureTypeID == 2 ? "Non-residential" : "";

	public string Inspected => Summary.DateofInspection?.ToString("MMM d, yyyy") ?? "";

	public string Inspector => Summary.InspectorName;

	public string Zip => Summary.ZipCode;

	public string Parcel => Summary.ParcelNumber;

	public string FirmZone => reference?.LookupName(Summary.FIRMZone) ?? "";

	public string Status => Summary.AssessmentID == null ? "" : AssessmentStatuses.Label(AssessmentStatuses.Parse(Summary.Status));

	public string Updated => Summary.LastUpdate?.ToString("MMM d, yyyy") ?? "";

	/// <summary>The tile this row counts toward; the rule lives in <see cref="Determinations"/>.</summary>
	public ResultState State => Summary.AssessmentID == null ? ResultState.Incomplete
		: Determinations.Classify(Summary.DamageMessage, Summary.PercentDamage) switch
		{
			DeterminationClass.Substantial => ResultState.Substantial,
			DeterminationClass.Close => ResultState.Close,
			DeterminationClass.NotSubstantial => ResultState.NotSubstantial,
			DeterminationClass.Undetermined => ResultState.Undetermined,
			_ => ResultState.Incomplete,
		};

	public bool HasPercent => State is not (ResultState.Incomplete or ResultState.Undetermined);

	public string PercentText => HasPercent ? $"{Summary.PercentDamage:0.0}%" : "–";

	public string Result => Summary.AssessmentID == null ? "No assessment" : Summary.DamageMessage ?? "Data Entry Incomplete";

	[ObservableProperty]
	public partial bool IsChecked { get; set; }

	public string SearchText => string.Join(" | ", Summary.Address, Summary.Owner, Summary.City, Summary.NFIPCID, Summary.NFIPCommunityName, Summary.InspectorName, Summary.DamageMessage, Summary.ExternalId, Summary.ParcelNumber, Summary.ZipCode).ToLowerInvariant();
}

public enum StructureFilter
{
	All,
	Substantial,
	Close,
	Below,
	Incomplete,
}

/// <summary>An optional column of the structure list, shown or hidden with Columns ▾.</summary>
public sealed partial class ColumnChoice(string key, string header, bool visible) : ObservableObject
{
	public string Key { get; } = key;

	public string Header { get; } = header;

	[ObservableProperty]
	public partial bool IsVisible { get; set; } = visible;
}

/// <summary>
/// Everything about how the list is shown: search, filters, sort and columns. Remembered for each database, and what a
/// saved view stores.
/// </summary>
public sealed class ListViewState
{
	public string Search { get; set; }
	public StructureFilter Filter { get; set; }
	public int TypeFilter { get; set; }
	public int SourceFilter { get; set; }
	public DateTime? InspectedFrom { get; set; }
	public DateTime? InspectedTo { get; set; }
	public decimal? MinPercent { get; set; }
	public decimal? MaxPercent { get; set; }
	public string Street { get; set; }
	public string Zip { get; set; }
	public string Parcel { get; set; }
	/// <summary>A status name, or null for any.</summary>
	public string Status { get; set; }
	public string SortKey { get; set; }
	public bool SortDescending { get; set; }
	public List<string> Columns { get; set; }
}

/// <summary>The home screen: every structure with its latest result, search, filters, sorting, columns and quick totals.</summary>
public sealed partial class StructuresViewModel : ViewModelBase
{
	/// <summary>Columns that can be shown or hidden, in order; true = shown by default.</summary>
	private static readonly (string Key, string Header, bool Visible)[] OptionalColumns =
	{
		("Owner", "Owner", true),
		("Community", "Community", true),
		("Type", "Type", true),
		("Inspected", "Inspected", true),
		("Inspector", "Inspector", false),
		("Status", "Status", true),
		("Zip", "ZIP", false),
		("Parcel", "Parcel", false),
		("FirmZone", "FIRM zone", false),
		("Source", "Source record", false),
		("Updated", "Last changed", false),
	};

	public const string ViewSettingKey = "Structures.View";

	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private List<StructureRowViewModel> _all = new();
	private bool _restoring;

	public StructuresViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
		Submission = new SubmissionViewModel(app, main);
		foreach ((string key, string header, bool visible) in OptionalColumns)
		{
			ColumnChoice c = new ColumnChoice(key, header, visible);
			c.PropertyChanged += (_, _) => ColumnsEdited();
			Columns.Add(c);
		}
		RestoreState(_app.Store.GetSetting<ListViewState>(ViewSettingKey));
		ReloadSavedViews();
		Reload();
	}

	/// <summary>The Submission folder panel (Export ▾).</summary>
	public SubmissionViewModel Submission { get; }

	public ObservableCollection<StructureRowViewModel> Rows { get; } = new();

	public ObservableCollection<ColumnChoice> Columns { get; } = new();

	/// <summary>Raised when a column is shown or hidden (the view applies it to the grid).</summary>
	public event Action ColumnsChanged;

	public bool IsColumnVisible(string key) => Columns.FirstOrDefault(c => c.Key == key)?.IsVisible ?? true;

	[ObservableProperty]
	public partial string Search { get; set; }

	[ObservableProperty]
	public partial StructureFilter Filter { get; set; }

	[ObservableProperty]
	public partial int TypeFilter { get; set; }

	/// <summary>0 = all, 1 = imported from another system, 2 = entered in SDE.</summary>
	[ObservableProperty]
	public partial int SourceFilter { get; set; }

	[ObservableProperty]
	public partial DateTime? InspectedFrom { get; set; }

	[ObservableProperty]
	public partial DateTime? InspectedTo { get; set; }

	[ObservableProperty]
	public partial decimal? MinPercent { get; set; }

	[ObservableProperty]
	public partial decimal? MaxPercent { get; set; }

	[ObservableProperty]
	public partial string StreetFilter { get; set; }

	[ObservableProperty]
	public partial string ZipFilter { get; set; }

	[ObservableProperty]
	public partial string ParcelFilter { get; set; }

	/// <summary>0 = any status, then Draft, Reviewed, Final, Letter sent, Appeal.</summary>
	[ObservableProperty]
	public partial int StatusFilter { get; set; }

	public static IReadOnlyList<string> StatusChoices { get; } = new[] { "Any status" }.Concat(Enum.GetValues<AssessmentStatus>().Select(AssessmentStatuses.Label)).ToList();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SortText))]
	public partial string SortKey { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SortText))]
	public partial bool SortDescending { get; private set; }

	public string SortText => SortKey == null ? "Most recently changed first" : $"Sorted by {SortHeader(SortKey)}, {(SortDescending ? "highest or latest first" : "lowest or earliest first")}";

	/// <summary>Whether any structure came from another system (the source filter is shown only then).</summary>
	[ObservableProperty]
	public partial bool HasImported { get; set; }

	/// <summary>When set, only these assessments (or structures) are listed (e.g. what the last import brought in).</summary>
	private HashSet<Guid> _only;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasOnly))]
	public partial string OnlyLabel { get; set; }

	public bool HasOnly => OnlyLabel != null;

	[ObservableProperty]
	public partial StructureRowViewModel Selected { get; set; }

	[ObservableProperty]
	public partial int TotalCount { get; set; }

	[ObservableProperty]
	public partial int SubstantialCount { get; set; }

	[ObservableProperty]
	public partial int CloseCount { get; set; }

	[ObservableProperty]
	public partial int BelowCount { get; set; }

	[ObservableProperty]
	public partial int IncompleteCount { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasChecked), nameof(CheckedText))]
	public partial int CheckedCount { get; set; }

	/// <summary>True while any structure is checked (the selection bar shows).</summary>
	public bool HasChecked => CheckedCount > 0;

	/// <summary>"3 selected", for the selection bar.</summary>
	public string CheckedText => $"{CheckedCount:N0} selected";

	/// <summary>"3 structures", or "2 of 3 structures" while a search or filter hides some.</summary>
	public string CountText => Rows.Count == _all.Count
		? $"{_all.Count:N0} structure{(_all.Count == 1 ? "" : "s")}"
		: $"{Rows.Count:N0} of {_all.Count:N0} structure{(_all.Count == 1 ? "" : "s")}";

	/// <summary>The selection bar's Clear: unchecks every structure, shown or not.</summary>
	[RelayCommand]
	private void ClearChecked()
	{
		foreach (StructureRowViewModel r in _all)
		{
			r.IsChecked = false;
		}
	}

	public bool IsEmpty => _all.Count == 0;

	/// <summary>How many structures the list holds (a structure with two assessments is one).</summary>
	public int StructureCount => _all.Select(r => r.Summary.PropertyID).Distinct().Count();

	/// <summary>The empty state's title: there are no structures at all, or none in the chosen event.</summary>
	public string EmptyTitle => _app.Scope.IsAll ? "No structures yet" : $"Nothing in {_app.Scope.Event.Name} yet";

	public string EmptyText => _app.Scope.IsAll
		? "Start a new residential or non-residential assessment, or bring in data from FEMA's SDE or another system on the Import & export page."
		: "A new assessment started while this event is chosen joins it. To add existing ones, choose All events, tick them, then use Change, Set event.";

	public bool HasRows => Rows.Count > 0;

	public bool NoMatches => !IsEmpty && Rows.Count == 0;

	/// <summary>How many of the extra filters (Filters ▾) are in use.</summary>
	public int ExtraFilterCount => new object[] { InspectedFrom, InspectedTo, MinPercent, MaxPercent, Blank(StreetFilter), Blank(ZipFilter), Blank(ParcelFilter), StatusFilter == 0 ? null : (object)StatusFilter }.Count(v => v != null);

	public string FiltersButtonText => ExtraFilterCount == 0 ? "Filters" : $"Filters ({ExtraFilterCount})";

	private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

	partial void OnSearchChanged(string value) => Changed();

	partial void OnFilterChanged(StructureFilter value) => Changed();

	partial void OnTypeFilterChanged(int value) => Changed();

	partial void OnSourceFilterChanged(int value) => Changed();

	partial void OnInspectedFromChanged(DateTime? value) => Changed();

	partial void OnInspectedToChanged(DateTime? value) => Changed();

	partial void OnMinPercentChanged(decimal? value) => Changed();

	partial void OnMaxPercentChanged(decimal? value) => Changed();

	partial void OnStreetFilterChanged(string value) => Changed();

	partial void OnZipFilterChanged(string value) => Changed();

	partial void OnParcelFilterChanged(string value) => Changed();

	partial void OnStatusFilterChanged(int value) => Changed();

	private void Changed()
	{
		if (_restoring)
		{
			return;
		}
		Apply();
		Remember();
	}

	private void ColumnsEdited()
	{
		ColumnsChanged?.Invoke();
		if (!_restoring)
		{
			Remember();
		}
	}

	/// <summary>Lists only the given assessments, with a chip showing <paramref name="label"/>; null shows everything again.</summary>
	public void ShowOnly(IEnumerable<Guid> assessmentIds, string label)
	{
		_only = assessmentIds?.ToHashSet();
		OnlyLabel = _only == null ? null : label;
		Apply();
	}

	[RelayCommand]
	private void ClearOnly() => ShowOnly(null, null);

	public void Reload()
	{
		foreach (StructureRowViewModel r in _all)
		{
			r.PropertyChanged -= RowChanged;
		}
		HashSet<Guid> checkedIds = _all.Where(r => r.IsChecked && r.AssessmentID != null).Select(r => r.AssessmentID!.Value).ToHashSet();
		ReferenceData reference = _app.Store.Reference;
		// The event chosen in the sidebar limits the list to its assessments.
		_all = _app.Store.ListStructures().Where(s => _app.Scope.Includes(s.AssessmentID))
			.Select(s => new StructureRowViewModel(s, reference) { IsChecked = s.AssessmentID is Guid id && checkedIds.Contains(id) }).ToList();
		foreach (StructureRowViewModel r in _all)
		{
			r.PropertyChanged += RowChanged;
		}
		TotalCount = _all.Count;
		SubstantialCount = _all.Count(r => r.State == ResultState.Substantial);
		CloseCount = _all.Count(r => r.State == ResultState.Close);
		BelowCount = _all.Count(r => r.State == ResultState.NotSubstantial);
		IncompleteCount = _all.Count(r => r.State is ResultState.Incomplete or ResultState.Undetermined);
		CheckedCount = _all.Count(r => r.IsChecked);
		HasImported = _all.Any(r => r.Summary.ExternalId != null);
		Apply();
	}

	/// <summary>Another database was opened: its own remembered view and saved views.</summary>
	public void DatabaseChanged()
	{
		RestoreState(_app.Store.GetSetting<ListViewState>(ViewSettingKey));
		ReloadSavedViews();
		Reload();
	}

	private void RowChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(StructureRowViewModel.IsChecked))
		{
			CheckedCount = _all.Count(r => r.IsChecked);
			OnPropertyChanged(nameof(AllChecked));
		}
	}

	/// <summary>
	/// The header check box: ticked when every row shown is checked, empty when none is, and a dash when some are.
	/// Ticking it checks every row shown; unticking unchecks them.
	/// </summary>
	public bool? AllChecked
	{
		get
		{
			int shown = Rows.Count(r => r.IsChecked);
			return shown == 0 ? false : shown == Rows.Count ? true : null;
		}
		set
		{
			bool check = value == true;
			foreach (StructureRowViewModel r in Rows)
			{
				r.IsChecked = check;
			}
			OnPropertyChanged();
		}
	}

	/// <summary>Checks exactly these assessments (e.g. what was selected on the map).</summary>
	public void CheckOnly(IEnumerable<Guid> assessmentIds)
	{
		HashSet<Guid> ids = assessmentIds.ToHashSet();
		foreach (StructureRowViewModel r in _all)
		{
			r.IsChecked = r.AssessmentID is Guid id && ids.Contains(id);
		}
	}

	/// <summary>Raised by Edit, then Find (Cmd+F): the view puts the cursor in the search box.</summary>
	public event Action FocusSearchRequested;

	public void FocusSearch() => FocusSearchRequested?.Invoke();

	/// <summary>
	/// Filters and sorts the rows. What's shown, in this order, is also the order reports and exports use, so a
	/// sorted list prints sorted.
	/// </summary>
	private void Apply()
	{
		string q = (Search ?? "").Trim().ToLowerInvariant();
		IEnumerable<StructureRowViewModel> rows = _all;
		if (_only != null)
		{
			rows = rows.Where(r => (r.AssessmentID is Guid id && _only.Contains(id)) || _only.Contains(r.Summary.PropertyID));
		}
		if (SourceFilter is 1 or 2)
		{
			rows = rows.Where(r => (r.Summary.ExternalId != null) == (SourceFilter == 1));
		}
		if (q.Length > 0)
		{
			rows = rows.Where(r => r.SearchText.Contains(q));
		}
		rows = Filter switch
		{
			StructureFilter.Substantial => rows.Where(r => r.State == ResultState.Substantial),
			StructureFilter.Close => rows.Where(r => r.State == ResultState.Close),
			StructureFilter.Below => rows.Where(r => r.State == ResultState.NotSubstantial),
			StructureFilter.Incomplete => rows.Where(r => r.State is ResultState.Incomplete or ResultState.Undetermined),
			_ => rows,
		};
		if (TypeFilter is 1 or 2)
		{
			rows = rows.Where(r => r.Summary.StructureTypeID == TypeFilter);
		}
		if (InspectedFrom is DateTime from)
		{
			rows = rows.Where(r => r.Summary.DateofInspection >= from.Date);
		}
		if (InspectedTo is DateTime to)
		{
			rows = rows.Where(r => r.Summary.DateofInspection < to.Date.AddDays(1));
		}
		if (MinPercent is decimal min)
		{
			rows = rows.Where(r => r.HasPercent && r.Summary.PercentDamage >= min);
		}
		if (MaxPercent is decimal max)
		{
			rows = rows.Where(r => r.HasPercent && r.Summary.PercentDamage <= max);
		}
		if (Blank(StreetFilter) is string street)
		{
			rows = rows.Where(r => (r.Summary.StreetName ?? "").Contains(street.Trim(), StringComparison.OrdinalIgnoreCase));
		}
		if (Blank(ZipFilter) is string zip)
		{
			rows = rows.Where(r => (r.Summary.ZipCode ?? "").StartsWith(zip.Trim(), StringComparison.OrdinalIgnoreCase));
		}
		if (Blank(ParcelFilter) is string parcel)
		{
			string wanted = Normalize(parcel);
			rows = rows.Where(r => Normalize(r.Summary.ParcelNumber).Contains(wanted));
		}
		if (StatusFilter > 0)
		{
			AssessmentStatus status = Enum.GetValues<AssessmentStatus>()[StatusFilter - 1];
			rows = rows.Where(r => r.AssessmentID != null && AssessmentStatuses.Parse(r.Summary.Status) == status);
		}
		rows = Sort(rows);
		Rows.Clear();
		foreach (StructureRowViewModel r in rows)
		{
			Rows.Add(r);
		}
		OnPropertyChanged(nameof(IsEmpty));
		OnPropertyChanged(nameof(EmptyTitle));
		OnPropertyChanged(nameof(EmptyText));
		OnPropertyChanged(nameof(HasRows));
		OnPropertyChanged(nameof(NoMatches));
		OnPropertyChanged(nameof(CountText));
		OnPropertyChanged(nameof(AllChecked));
		OnPropertyChanged(nameof(ExtraFilterCount));
		OnPropertyChanged(nameof(FiltersButtonText));
	}

	private static string Normalize(string parcel) => new string((parcel ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

	private IEnumerable<StructureRowViewModel> Sort(IEnumerable<StructureRowViewModel> rows)
	{
		if (SortKey == null)
		{
			return rows;    // the store's order: most recently changed first
		}
		IComparer<StructureRowViewModel> comparer = SortKey switch
		{
			"Address" => Comparer<StructureRowViewModel>.Create((a, b) =>
			{
				int byStreet = string.Compare(a.Summary.StreetName ?? "", b.Summary.StreetName ?? "", StringComparison.CurrentCultureIgnoreCase);
				return byStreet != 0 ? byStreet : ParseNumber(a.Summary.Address).CompareTo(ParseNumber(b.Summary.Address));
			}),
			"Inspected" => By(r => r.Summary.DateofInspection ?? DateTime.MinValue),
			"Damaged" => By(r => r.HasPercent ? r.Summary.PercentDamage ?? -1m : -1m),
			"Status" => By(r => r.AssessmentID == null ? -1 : (int)AssessmentStatuses.Parse(r.Summary.Status)),
			"Updated" => By(r => r.Summary.LastUpdate ?? DateTime.MinValue),
			_ => Comparer<StructureRowViewModel>.Create((a, b) => string.Compare(Text(a), Text(b), StringComparison.CurrentCultureIgnoreCase)),
		};
		return SortDescending ? rows.OrderByDescending(r => r, comparer) : rows.OrderBy(r => r, comparer);
	}

	private static IComparer<StructureRowViewModel> By<T>(Func<StructureRowViewModel, T> key)
		where T : IComparable<T> =>
		Comparer<StructureRowViewModel>.Create((a, b) => key(a).CompareTo(key(b)));

	private string Text(StructureRowViewModel r) => SortKey switch
	{
		"Owner" => r.Owner ?? "",
		"Inspector" => r.Inspector ?? "",
		"Community" => r.Place ?? "",
		"Type" => r.Type,
		"Result" => r.Result,
		"Zip" => r.Zip ?? "",
		"Parcel" => r.Parcel ?? "",
		"FirmZone" => r.FirmZone ?? "",
		"Source" => r.Source ?? "",
		_ => r.Address,
	};

	/// <summary>The house number at the start of an address, so 9 Elm sorts before 10 Elm.</summary>
	private static int ParseNumber(string address) =>
		int.TryParse(new string((address ?? "").TakeWhile(char.IsDigit).ToArray()), out int n) ? n : int.MaxValue;

	private static string SortHeader(string key) => key switch
	{
		"Address" => "structure",
		"Damaged" => "percent damaged",
		"Result" => "result",
		_ => OptionalColumns.FirstOrDefault(c => c.Key == key).Header?.ToLowerInvariant() ?? key.ToLowerInvariant(),
	};

	private static bool NewestFirst(string key) => key is "Damaged" or "Inspected" or "Updated";

	/// <summary>A column header was clicked: sort by it, or reverse the sort; a third click goes back to the default order.</summary>
	[RelayCommand]
	public void SortBy(string key)
	{
		if (SortKey != key)
		{
			SortKey = key;
			SortDescending = NewestFirst(key);
		}
		else if (SortDescending == NewestFirst(key))
		{
			SortDescending = !SortDescending;
		}
		else
		{
			SortKey = null;
			SortDescending = false;
		}
		Changed();
	}

	[RelayCommand]
	private void ClearFilters()
	{
		_restoring = true;
		InspectedFrom = null;
		InspectedTo = null;
		MinPercent = null;
		MaxPercent = null;
		StreetFilter = null;
		ZipFilter = null;
		ParcelFilter = null;
		StatusFilter = 0;
		_restoring = false;
		Changed();
	}

	/// <summary>"Nothing matches", then Show everything: clears the search, the tiles' filter, every filter and the import list.</summary>
	[RelayCommand]
	private void ShowEverything()
	{
		_restoring = true;
		Search = null;
		Filter = StructureFilter.All;
		TypeFilter = 0;
		SourceFilter = 0;
		_restoring = false;
		ShowOnly(null, null);
		ClearFilters();
	}

	// ───────────── Remembered state and saved views ─────────────

	public ListViewState CurrentState() => new ListViewState
	{
		Search = Search,
		Filter = Filter,
		TypeFilter = TypeFilter,
		SourceFilter = SourceFilter,
		InspectedFrom = InspectedFrom,
		InspectedTo = InspectedTo,
		MinPercent = MinPercent,
		MaxPercent = MaxPercent,
		Street = StreetFilter,
		Zip = ZipFilter,
		Parcel = ParcelFilter,
		Status = StatusFilter == 0 ? null : Enum.GetValues<AssessmentStatus>()[StatusFilter - 1].ToString(),
		SortKey = SortKey,
		SortDescending = SortDescending,
		Columns = Columns.Where(c => c.IsVisible).Select(c => c.Key).ToList(),
	};

	private void RestoreState(ListViewState state)
	{
		_restoring = true;
		try
		{
			state ??= new ListViewState();
			Search = state.Search;
			Filter = state.Filter;
			TypeFilter = state.TypeFilter;
			SourceFilter = state.SourceFilter;
			InspectedFrom = state.InspectedFrom;
			InspectedTo = state.InspectedTo;
			MinPercent = state.MinPercent;
			MaxPercent = state.MaxPercent;
			StreetFilter = state.Street;
			ZipFilter = state.Zip;
			ParcelFilter = state.Parcel;
			StatusFilter = state.Status == null ? 0 : (int)AssessmentStatuses.Parse(state.Status) + 1;
			SortKey = state.SortKey;
			SortDescending = state.SortDescending;
			foreach (ColumnChoice c in Columns)
			{
				c.IsVisible = state.Columns == null ? OptionalColumns.First(o => o.Key == c.Key).Visible : state.Columns.Contains(c.Key);
			}
		}
		finally
		{
			_restoring = false;
		}
		ColumnsChanged?.Invoke();
	}

	private void Remember()
	{
		try
		{
			_app.Store.SetSetting(ViewSettingKey, CurrentState());
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't remember the list's filters", ex);
		}
	}

	public ObservableCollection<string> SavedViews { get; } = new();

	public bool HasSavedViews => SavedViews.Count > 0;

	private void ReloadSavedViews()
	{
		SavedViews.Clear();
		foreach (SavedViewRecord v in _app.Store.GetSavedViews())
		{
			SavedViews.Add(v.Name);
		}
		OnPropertyChanged(nameof(HasSavedViews));
	}

	/// <summary>Views ▾, then a saved view: its filters, sort and columns.</summary>
	[RelayCommand]
	private void ApplyView(string name)
	{
		SavedViewRecord v = _app.Store.GetSavedViews().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
		if (v == null)
		{
			return;
		}
		RestoreState(JsonConvert.DeserializeObject<ListViewState>(v.State));
		Apply();
		Remember();
	}

	/// <summary>Views ▾, then Save this view: asks for a name.</summary>
	[RelayCommand]
	private async Task SaveView()
	{
		string name = await _app.Sheets.ShowAsync<string>(new NameSheetViewModel("Save this view", "Name", "For example: Zone AE, not yet reviewed"));
		if (string.IsNullOrWhiteSpace(name))
		{
			return;
		}
		_app.Store.SaveView(name.Trim(), JsonConvert.SerializeObject(CurrentState()));
		ReloadSavedViews();
	}

	[RelayCommand]
	private void DeleteView(string name)
	{
		_app.Store.DeleteView(name);
		ReloadSavedViews();
	}

	// ───────────── Commands ─────────────

	[RelayCommand]
	private void SetFilter(StructureFilter filter) => Filter = Filter == filter ? StructureFilter.All : filter;

	[RelayCommand]
	private void Open(StructureRowViewModel row)
	{
		if (row == null)
		{
			return;
		}
		if (row.AssessmentID is Guid id)
		{
			_main.OpenAssessment(id);
		}
		else if (_app.Store.GetProperty(row.Summary.PropertyID) is Sde.Core.Records.PropertyRecord p)
		{
			// A structure without an assessment (e.g. from Enterprise Import): start its first assessment, saved on the first edit.
			_main.OpenDraft(_app.Store.NewDraft(p, _app.Store.GetPreferences().UseDefaults));
		}
	}

	/// <summary>A new structure and its first assessment; nothing is written until the first edit.</summary>
	[RelayCommand]
	private void NewStructure(string type)
	{
		Sde.Core.Records.PropertyRecord p = new Sde.Core.Records.PropertyRecord { StructureTypeID = int.Parse(type) };
		_main.OpenDraft(_app.Store.NewDraft(p, _app.Store.GetPreferences().UseDefaults));
	}

	/// <summary>The checked assessments: those shown first, in the list's order, then any checked but filtered out.</summary>
	public IReadOnlyList<Guid> CheckedAssessmentIds() =>
		Rows.Where(r => r.IsChecked && r.AssessmentID != null).Select(r => r.AssessmentID!.Value)
			.Concat(_all.Where(r => r.IsChecked && r.AssessmentID != null && !Rows.Contains(r)).Select(r => r.AssessmentID!.Value)).ToList();

	public IReadOnlyList<Guid> VisibleAssessmentIds() =>
		Rows.Where(r => r.AssessmentID != null).Select(r => r.AssessmentID!.Value).ToList();

	/// <summary>The checked assessments, or every one shown when none is checked, in the list's order.</summary>
	public IReadOnlyList<Guid> TargetIds() => CheckedCount > 0 ? CheckedAssessmentIds() : VisibleAssessmentIds();

	[RelayCommand]
	private void CheckAllVisible() => AllChecked = Rows.Any(r => !r.IsChecked);

	/// <summary>Reports for the checked structures (or, when none are checked, everything in the current list).</summary>
	[RelayCommand]
	private async Task Print(string kind) => await _main.PrintAsync(kind, TargetIds());

	[RelayCommand]
	private async Task ExportChecked() => await _main.ImportExport.ExportAsync(TargetIds());

	/// <summary>Export ▾, then Submission folder: reports, the FEMA package and results.csv for the checked structures (or the list).</summary>
	[RelayCommand]
	private void OpenSubmission() => Submission.Open(TargetIds(), _all);

	[RelayCommand]
	private async Task ExportCheckedToExcel() => await _main.ImportExport.ExportExcelAsync(TargetIds());

	/// <summary>Change ▾, then Assign to team: the checked structures (or every one shown) go to a team for the field.</summary>
	[RelayCommand]
	private async Task AssignToTeam()
	{
		IEnumerable<StructureRowViewModel> rows = CheckedCount > 0 ? _all.Where(r => r.IsChecked) : Rows;
		List<Guid> structures = rows.Select(r => r.Summary.PropertyID).Distinct().ToList();
		if (structures.Count == 0)
		{
			return;
		}
		if (await _app.Sheets.ShowAsync(new AssignTeamSheetViewModel(_app, structures)) is string done)
		{
			_app.Dialogs.Notify("Assigned", done);
		}
	}

	/// <summary>Export ▾, then Web map: a web map folder of the checked structures (or the list), for the office or the public.</summary>
	[RelayCommand]
	private async Task WebMap()
	{
		IReadOnlyList<Guid> ids = TargetIds();
		if (ids.Count == 0)
		{
			return;
		}
		if (await _app.Sheets.ShowAsync(new WebMapSheetViewModel(_app, ids)) is string done)
		{
			_app.Dialogs.Notify("Web map made", done);
		}
	}

	// ───────────── Changing many at once ─────────────

	/// <summary>Set a field on all the checked assessments (or all shown).</summary>
	[RelayCommand]
	private async Task BulkEdit()
	{
		IReadOnlyList<Guid> ids = TargetIds();
		if (ids.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to change", "Check some structures first, or clear the search.");
			return;
		}
		await _app.Sheets.ShowAsync(new BulkEditSheetViewModel(_app, ids));
	}

	/// <summary>Change, then Set event: puts the checked assessments (or all shown) in an event, or takes them out.</summary>
	[RelayCommand]
	private async Task SetEvent()
	{
		IReadOnlyList<Guid> ids = TargetIds();
		if (ids.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to change", "Check some structures first, or clear the search.");
			return;
		}
		if (await _app.Sheets.ShowAsync(new SetEventSheetViewModel(_app, ids)) is int changed)
		{
			_app.RaiseDataChanged();
			_app.Dialogs.Notify("Event set", changed == 0 ? "They were already there." : $"{changed:N0} assessment{(changed == 1 ? "" : "s")} changed.");
		}
	}

	/// <summary>Change, then Flood depth from high-water marks: for the checked assessments (or all shown).</summary>
	[RelayCommand]
	private async Task FloodDepth()
	{
		IReadOnlyList<Guid> ids = TargetIds();
		if (ids.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to change", "Check some structures first, or clear the search.");
			return;
		}
		if (await _app.Sheets.ShowAsync(new DepthSheetViewModel(_app, ids)) is DepthApplyResult result)
		{
			_app.Dialogs.Notify("Flood depths saved", $"{result.Changed:N0} assessment{(result.Changed == 1 ? "" : "s")} changed."
				+ (result.Refused.Count > 0 ? $" {result.Refused.Count:N0} Final, Letter sent or checked out {(result.Refused.Count == 1 ? "was" : "were")} left alone." : ""));
		}
	}

	/// <summary>Edit, then Search and Replace.</summary>
	[RelayCommand]
	private async Task SearchReplace() => await _app.Sheets.ShowAsync(new SearchReplaceSheetViewModel(_app));

	/// <summary>Row menu, then Copy from another structure: fills this row's assessment from another one.</summary>
	[RelayCommand]
	private async Task CopyFrom(StructureRowViewModel row)
	{
		if (row?.AssessmentID is Guid id)
		{
			await _app.Sheets.ShowAsync(new CopyFromSheetViewModel(_app, id));
		}
	}

	/// <summary>Row menu, then Move to Recently deleted.</summary>
	[RelayCommand]
	private void Trash(StructureRowViewModel row)
	{
		if (row == null)
		{
			return;
		}
		try
		{
			if (row.AssessmentID is Guid id && _app.Store.GetAssessmentsForProperty(row.Summary.PropertyID).Count > 1)
			{
				_app.Writes.Write(new[] { id }, () => _app.Store.TrashAssessment(id));
			}
			else
			{
				_app.Writes.Write(row.AssessmentID is Guid only ? new[] { only } : null, () => _app.Store.TrashProperty(row.Summary.PropertyID));
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't delete " + row.Address, ex);
			return;
		}
		DeletedItemRecord deleted = _app.Store.GetDeletedItems().FirstOrDefault(d => row.AssessmentID is Guid aid && d.AssessmentID == aid && d.Value == DeletedKind.Assessment
			|| d.PropertyID == row.Summary.PropertyID && d.Value == DeletedKind.Structure);
		_app.Dialogs.Notify("Moved to Recently deleted", $"{row.Address} can be restored from Recently deleted for {SdeStore.KeepDays} days.",
			"Undo", () => UndoTrash(deleted, row.Address));
	}

	/// <summary>The toast's Undo after moving a structure or assessment to Recently deleted: puts it back.</summary>
	private void UndoTrash(DeletedItemRecord deleted, string address)
	{
		if (deleted == null)
		{
			return;
		}
		try
		{
			_app.Writes.Write(deleted.AssessmentID is Guid id ? new[] { id } : null, () => _app.Store.Restore(deleted.ItemKey));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't put back " + address, ex);
		}
	}
}
