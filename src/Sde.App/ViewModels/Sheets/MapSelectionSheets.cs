using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Analysis;
using Sde.Gis.Features;
using Sde.Interop.WebMap;

namespace Sde.App.ViewModels.Sheets;

/// <summary>An operator in the select-by-attribute sheet, with the words shown for it.</summary>
public sealed record OperatorChoice(AttributeOperator Operator, string Label)
{
	public override string ToString() => Label;

	public bool NeedsValue => Operator is not (AttributeOperator.IsEmpty or AttributeOperator.IsNotEmpty);

	public static IReadOnlyList<OperatorChoice> All { get; } = Enum.GetValues<AttributeOperator>()
		.Select(op => new OperatorChoice(op, AttributeCondition.Label(op)))
		.ToList();
}

/// <summary>One row of the select-by-attribute sheet: field, operator and value.</summary>
public sealed partial class AttributeConditionViewModel : ObservableObject
{
	private readonly Action<AttributeConditionViewModel> _remove;
	private readonly Action _changed;

	public AttributeConditionViewModel(IReadOnlyList<MapFieldChoice> fields, Action<AttributeConditionViewModel> remove, Action changed)
	{
		Fields = fields;
		_remove = remove;
		_changed = changed;
		Field = fields.FirstOrDefault();
		Operator = OperatorChoice.All[0];
	}

	public IReadOnlyList<MapFieldChoice> Fields { get; }

	public IReadOnlyList<OperatorChoice> Operators => OperatorChoice.All;

	[ObservableProperty]
	public partial MapFieldChoice Field { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(NeedsValue))]
	public partial OperatorChoice Operator { get; set; }

	[ObservableProperty]
	public partial string Value { get; set; }

	public bool NeedsValue => Operator?.NeedsValue ?? true;

	partial void OnFieldChanged(MapFieldChoice value) => _changed();

	partial void OnOperatorChanged(OperatorChoice value) => _changed();

	partial void OnValueChanged(string value) => _changed();

	[RelayCommand]
	private void Remove() => _remove(this);

	/// <summary>The condition as the query library reads it, or null when no field is chosen.</summary>
	public AttributeCondition ToCondition() => Field == null || Operator == null
		? null
		: new AttributeCondition(Field.Key, Operator.Operator, NeedsValue ? Value : null);
}

/// <summary>
/// Select, then By attribute: picks the features of a layer (or the structures) whose values meet one or more
/// conditions, all of them (AND) or any one (OR). The count of matches is shown as the conditions change.
/// </summary>
public sealed partial class SelectByAttributeSheetViewModel : SheetViewModel
{
	private readonly MapViewModel _map;

	public SelectByAttributeSheetViewModel(MapViewModel map, MapLayerViewModel layer) : base("Select by attribute")
	{
		_map = map;
		Layers = map.SelectableLayers;
		Layer = Layers.Contains(layer) ? layer : Layers.FirstOrDefault();
	}

	public override SheetSize Size => SheetSize.Large;

	public IReadOnlyList<MapLayerViewModel> Layers { get; }

	/// <summary>The layer to select in.</summary>
	[ObservableProperty]
	public partial MapLayerViewModel Layer { get; set; }

	public IReadOnlyList<MapFieldChoice> Fields { get; private set; } = Array.Empty<MapFieldChoice>();

	public ObservableCollection<AttributeConditionViewModel> Conditions { get; } = new();

	/// <summary>True: every condition must hold (AND). False: any one (OR).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(MatchAny))]
	public partial bool MatchAll { get; set; } = true;

	public bool MatchAny
	{
		get => !MatchAll;
		set => MatchAll = !value;
	}

	/// <summary>Adds the matches to what is already selected instead of replacing it.</summary>
	[ObservableProperty]
	public partial bool AddToSelection { get; set; }

	/// <summary>"12 of 30 match", or why the conditions can't be used.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	[ObservableProperty]
	public partial string CountText { get; set; }

	partial void OnLayerChanged(MapLayerViewModel value)
	{
		Fields = value == null ? Array.Empty<MapFieldChoice>() : _map.FieldChoices(value);
		OnPropertyChanged(nameof(Fields));
		Conditions.Clear();
		AddCondition();
	}

	partial void OnMatchAllChanged(bool value) => Preview();

	[RelayCommand]
	private void AddCondition()
	{
		Conditions.Add(new AttributeConditionViewModel(Fields, c =>
		{
			Conditions.Remove(c);
			Preview();
		}, Preview));
		Preview();
	}

	/// <summary>The conditions as a query.</summary>
	public AttributeQuery Query() => new AttributeQuery
	{
		Combine = MatchAll ? AttributeCombine.And : AttributeCombine.Or,
		Conditions = Conditions.Select(c => c.ToCondition()).Where(c => c != null).ToList(),
	};

	private FeatureLayer Data(AttributeQuery query) => Layer == null ? null : Layer.IsStructures ? _map.StructureFeaturesFor(query) : Layer.Data;

	/// <summary>Counts the matches for the conditions as they stand.</summary>
	public void Preview()
	{
		AttributeQuery query = Query();
		FeatureLayer data = Data(query);
		if (data == null)
		{
			CountText = null;
			return;
		}
		try
		{
			int n = query.Select(data).Count;
			Problem = null;
			CountText = $"{n.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} of {data.Features.Count.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} match.";
		}
		catch (GisException ex)
		{
			// A value still being typed, such as "1," for a number: say why, and wait.
			Problem = ex.Message;
			CountText = null;
		}
	}

	[RelayCommand]
	private void Select()
	{
		if (Layer == null)
		{
			return;
		}
		try
		{
			int n = _map.SelectWhere(Layer, Query(), AddToSelection);
			Close(n);
		}
		catch (GisException ex)
		{
			Problem = ex.Message;
		}
	}
}

/// <summary>A team in the assign-to-team sheet; the last choice is "A new team".</summary>
public sealed record AssignTeamChoice(TeamRecord Team, string Name)
{
	public override string ToString() => Name;

	public bool IsNew => Team == null;
}

/// <summary>An event in the assign-to-team sheet (no event first).</summary>
public sealed record AssignEventChoice(Guid? Id, string Name)
{
	public override string ToString() => Name;
}

/// <summary>Selection bar, then Assign to team: gives the selected structures to a team, for an event or none.</summary>
public sealed partial class AssignTeamSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly IReadOnlyList<Guid> _propertyIds;

	public AssignTeamSheetViewModel(AppServices app, IReadOnlyList<Guid> propertyIds)
		: base(propertyIds.Count == 1 ? "Assign 1 structure to a team" : $"Assign {propertyIds.Count} structures to a team")
	{
		_app = app;
		_propertyIds = propertyIds;
		Teams = app.Store.GetTeams().Select(t => new AssignTeamChoice(t, t.Name)).Append(new AssignTeamChoice(null, "A new team…")).ToList();
		Team = Teams[0];
		Events = new[] { new AssignEventChoice(null, "No event") }.Concat(app.Store.GetEvents().Select(e => new AssignEventChoice(e.EventID, e.Name))).ToList();
		Event = Events[0];
	}

	public override SheetSize Size => SheetSize.Small;

	public IReadOnlyList<AssignTeamChoice> Teams { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNewTeam))]
	public partial AssignTeamChoice Team { get; set; }

	public bool IsNewTeam => Team?.IsNew == true;

	/// <summary>The new team's name, when "A new team" is chosen.</summary>
	[ObservableProperty]
	public partial string NewTeamName { get; set; }

	public IReadOnlyList<AssignEventChoice> Events { get; }

	[ObservableProperty]
	public partial AssignEventChoice Event { get; set; }

	/// <summary>Also sets FEMA's own Team name field on the structures (it is exported).</summary>
	[ObservableProperty]
	public partial bool AlsoSetTeamName { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	[RelayCommand]
	private void Assign()
	{
		try
		{
			TeamRecord team = IsNewTeam ? _app.Store.SaveTeam(new TeamRecord { Name = NewTeamName }) : Team?.Team;
			if (team == null)
			{
				Problem = "Choose a team.";
				return;
			}
			SaveContext ctx = SaveContext.Batch(SaveVia.Map, "Assign to " + team.Name);
			List<AssignmentRecord> done = _app.Writes.WriteForStructures(_propertyIds,
				() => _app.Store.AssignToTeam(_propertyIds, team.TeamID, Event?.Id, null, AlsoSetTeamName, ctx));
			Close(done.Count == 1 ? $"Assigned 1 structure to {team.Name}." : $"Assigned {done.Count} structures to {team.Name}.");
		}
		catch (ArgumentException ex)
		{
			Problem = ex.Message;
		}
	}
}

/// <summary>Selection bar, then Add to web map: a web map folder (index.html and the data) of the selected structures.</summary>
public sealed partial class WebMapSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly IReadOnlyList<Guid> _assessmentIds;

	public WebMapSheetViewModel(AppServices app, IReadOnlyList<Guid> assessmentIds)
		: base(assessmentIds.Count == 1 ? "Web map of 1 structure" : $"Web map of {assessmentIds.Count} structures")
	{
		_app = app;
		_assessmentIds = assessmentIds;
	}

	public override SheetSize Size => SheetSize.Small;

	/// <summary>The title at the top of the web map.</summary>
	[ObservableProperty]
	public partial string MapTitle { get; set; } = "Damage assessments";

	/// <summary>True: for the public (grid squares, no owners, addresses cut to the block). False: full detail.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsInternal))]
	public partial bool IsPublic { get; set; }

	public bool IsInternal
	{
		get => !IsPublic;
		set => IsPublic = !value;
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	[ObservableProperty]
	public partial bool IsBusy { get; set; }

	public override bool CanCancel => !IsBusy;

	[RelayCommand]
	private async Task Create()
	{
		string parent = await _app.Dialogs.PickFolderAsync("Choose where to put the web map folder");
		if (parent == null)
		{
			return;
		}
		string title = string.IsNullOrWhiteSpace(MapTitle) ? "Damage assessments" : MapTitle.Trim();
		string folder = FreeFolder(parent, title);
		WebMapOptions options = new WebMapOptions
		{
			Title = title,
			Privacy = IsPublic ? WebMapPrivacy.Public : WebMapPrivacy.Internal,
		};
		IsBusy = true;
		try
		{
			ReferenceData reference = _app.Store.Reference;
			List<AssessmentFile> files = _app.Store.LoadAssessments(_assessmentIds);
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
