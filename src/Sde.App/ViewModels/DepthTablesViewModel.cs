using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Assessments;

namespace Sde.App.ViewModels;

/// <summary>One element's % damage at one depth.</summary>
public sealed partial class DepthCellViewModel(string element, Func<string> depthText, Action changed) : ObservableObject
{
	public string Element { get; } = element;

	/// <summary>What VoiceOver reads for the input.</summary>
	public string Label => $"{Element} percent damaged at {depthText()}";

	private decimal? _percent;

	public decimal? Percent
	{
		get => _percent;
		set
		{
			decimal? v = value is decimal d ? Math.Clamp(d, 0m, 100m) : null;
			if (SetProperty(ref _percent, v))
			{
				changed();
			}
		}
	}

	public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>One row of a depth table: a depth and a % for each element.</summary>
public sealed partial class DepthRowViewModel : ObservableObject
{
	private readonly Action _changed;

	public DepthRowViewModel(decimal? depth, IReadOnlyList<string> elements, IReadOnlyDictionary<string, decimal?> percents, Action changed, Action<DepthRowViewModel> remove)
	{
		_depth = depth;
		_changed = changed;
		Remove = new RelayCommand(() => remove(this));
		foreach (string e in elements)
		{
			DepthCellViewModel cell = new DepthCellViewModel(e, () => DepthText, changed);
			cell.Percent = percents != null && percents.TryGetValue(e, out decimal? p) ? p : null;
			Cells.Add(cell);
		}
	}

	private decimal? _depth;

	/// <summary>Feet of water above the lowest floor.</summary>
	public decimal? Depth
	{
		get => _depth;
		set
		{
			if (SetProperty(ref _depth, value))
			{
				OnPropertyChanged(nameof(DepthLabel));
				OnPropertyChanged(nameof(RemoveLabel));
				foreach (DepthCellViewModel c in Cells)
				{
					c.RefreshLabel();
				}
				_changed();
			}
		}
	}

	public string DepthText => Depth is decimal d ? d.ToString("0.##", CultureInfo.InvariantCulture) + " ft" : "a row with no depth";

	public string DepthLabel => "Depth in feet above the lowest floor, " + (Depth is decimal ? "row at " + DepthText : "new row");

	public string RemoveLabel => "Remove the row at " + DepthText;

	public ObservableCollection<DepthCellViewModel> Cells { get; } = new();

	public IRelayCommand Remove { get; }

	public Dictionary<string, decimal?> Percents() => Cells.Where(c => c.Percent != null).ToDictionary(c => c.Element, c => c.Percent, StringComparer.Ordinal);
}

/// <summary>
/// Settings, then Depth tables: the community's own depth-damage tables. SDE ships none. Each is a name, a building
/// type, and rows of depth (feet of water above the lowest floor) to % damage per building element. Saved as typed.
/// The editor's Damage step uses them to suggest values the inspector then confirms.
/// </summary>
public sealed partial class DepthTablesViewModel : ObservableObject
{
	private readonly AppServices _app;
	private bool _loading;
	private string _savedName;

	public DepthTablesViewModel(AppServices app)
	{
		_app = app;
		ReloadNames();
		SelectedName = Names.FirstOrDefault();
	}

	public ObservableCollection<string> Names { get; } = new();

	public bool HasTables => Names.Count > 0;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasTable))]
	[NotifyCanExecuteChangedFor(nameof(DeleteTableCommand), nameof(AddRowCommand))]
	public partial string SelectedName { get; set; }

	public bool HasTable => _savedName != null;

	/// <summary>The table's name (renames its file as it is typed).</summary>
	[ObservableProperty]
	public partial string TableName { get; set; }

	/// <summary>0 = residential, 1 = non-residential.</summary>
	[ObservableProperty]
	public partial int BuildingTypeIndex { get; set; }

	/// <summary>A problem with the name, e.g. another table has it.</summary>
	[ObservableProperty]
	public partial string NameProblem { get; private set; }

	public IReadOnlyList<string> ElementNames { get; private set; } = Array.Empty<string>();

	public ObservableCollection<DepthRowViewModel> Rows { get; } = new();

	private DepthTableBuildingType BuildingType => BuildingTypeIndex == 1 ? DepthTableBuildingType.NonResidential : DepthTableBuildingType.Residential;

	partial void OnSelectedNameChanged(string value)
	{
		if (_loading)
		{
			return;
		}
		Load(value);
	}

	partial void OnTableNameChanged(string value)
	{
		if (!_loading)
		{
			Save();
		}
	}

	partial void OnBuildingTypeIndexChanged(int value)
	{
		if (_loading)
		{
			return;
		}
		// Keep the values of elements both building types have.
		List<(decimal? Depth, Dictionary<string, decimal?> Percents)> kept = Rows.Select(r => (r.Depth, r.Percents())).ToList();
		ElementNames = DepthDamageTable.ElementNames(BuildingType, _app.Store.Reference);
		OnPropertyChanged(nameof(ElementNames));
		Rows.Clear();
		foreach ((decimal? depth, Dictionary<string, decimal?> percents) in kept)
		{
			Rows.Add(NewRow(depth, percents));
		}
		Save();
	}

	private void ReloadNames()
	{
		Names.Clear();
		foreach (string n in _app.DepthTables.Names())
		{
			Names.Add(n);
		}
		OnPropertyChanged(nameof(HasTables));
	}

	private void Load(string name)
	{
		_loading = true;
		try
		{
			Rows.Clear();
			NameProblem = null;
			DepthDamageTable table = null;
			if (name != null && _app.DepthTables.Exists(name))
			{
				try
				{
					table = _app.DepthTables.Load(name);
				}
				catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
				{
					_app.Errors.Show("Couldn't open the depth table " + name, ex);
				}
			}
			_savedName = table?.Name;
			TableName = table?.Name;
			BuildingTypeIndex = table?.BuildingType == DepthTableBuildingType.NonResidential ? 1 : 0;
			ElementNames = DepthDamageTable.ElementNames(BuildingType, _app.Store.Reference);
			OnPropertyChanged(nameof(ElementNames));
			foreach (DepthDamageRow r in table?.Rows ?? new List<DepthDamageRow>())
			{
				Rows.Add(NewRow(r.DepthFt, r.Percents));
			}
		}
		finally
		{
			_loading = false;
		}
		OnPropertyChanged(nameof(HasTable));
		DeleteTableCommand.NotifyCanExecuteChanged();
		AddRowCommand.NotifyCanExecuteChanged();
	}

	private DepthRowViewModel NewRow(decimal? depth, IReadOnlyDictionary<string, decimal?> percents) =>
		new DepthRowViewModel(depth, ElementNames, percents, Save, RemoveRow);

	/// <summary>Writes the table as it is now (rows without a depth are left out of the file until they have one).</summary>
	private void Save()
	{
		if (_loading || _savedName == null)
		{
			return;
		}
		string wanted = string.IsNullOrWhiteSpace(TableName) ? _savedName : TableName.Trim();
		bool renaming = !string.Equals(ImportProfiles.SafeName(wanted), _savedName, StringComparison.Ordinal);
		if (renaming && _app.DepthTables.Exists(wanted) && !string.Equals(ImportProfiles.SafeName(wanted), _savedName, StringComparison.OrdinalIgnoreCase))
		{
			NameProblem = "Another depth table already has that name.";
			wanted = _savedName;
			renaming = false;
		}
		else
		{
			NameProblem = null;
		}
		DepthDamageTable table = new DepthDamageTable
		{
			Name = wanted,
			BuildingType = BuildingType,
			Rows = Rows.Where(r => r.Depth != null).Select(r => new DepthDamageRow { DepthFt = r.Depth.Value, Percents = r.Percents() }).ToList(),
		};
		try
		{
			string saved = _app.DepthTables.Save(table);
			if (renaming && !string.Equals(saved, _savedName, StringComparison.Ordinal))
			{
				if (!string.Equals(saved, _savedName, StringComparison.OrdinalIgnoreCase))
				{
					_app.DepthTables.Delete(_savedName);
				}
				_savedName = saved;
				_loading = true;
				try
				{
					ReloadNames();
					SelectedName = saved;
				}
				finally
				{
					_loading = false;
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_app.Errors.Show("Couldn't save the depth table", ex);
		}
	}

	/// <summary>Starts a new, empty table (residential, one row at 0 ft).</summary>
	[RelayCommand]
	private void NewTable()
	{
		string name = _app.DepthTables.UnusedName("New depth table");
		_app.DepthTables.Save(new DepthDamageTable { Name = name, Rows = new List<DepthDamageRow> { new DepthDamageRow { DepthFt = 0 } } });
		_loading = true;
		try
		{
			ReloadNames();
		}
		finally
		{
			_loading = false;
		}
		SelectedName = name;
	}

	[RelayCommand(CanExecute = nameof(HasTable))]
	private async Task DeleteTable()
	{
		string name = _savedName;
		if (name == null || !await _app.Dialogs.ConfirmAsync("Delete this depth table?", $"\"{name}\" will be deleted. Damage already suggested from it stays as it is.", "Delete", destructive: true))
		{
			return;
		}
		_app.DepthTables.Delete(name);
		_loading = true;
		try
		{
			ReloadNames();
		}
		finally
		{
			_loading = false;
		}
		SelectedName = Names.FirstOrDefault();
		if (SelectedName == null)
		{
			Load(null);
		}
	}

	/// <summary>Adds a row 1 ft deeper than the deepest so far.</summary>
	[RelayCommand(CanExecute = nameof(HasTable))]
	private void AddRow()
	{
		decimal next = Rows.Where(r => r.Depth != null).Select(r => r.Depth.Value).DefaultIfEmpty(-1m).Max() + 1m;
		Rows.Add(NewRow(next, null));
		Save();
	}

	private void RemoveRow(DepthRowViewModel row)
	{
		Rows.Remove(row);
		Save();
	}
}
