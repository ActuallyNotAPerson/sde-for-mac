using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Sde.App.Services;
using Sde.App.ViewModels.Fields;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>Asks for a name (a saved view, a snippet).</summary>
public sealed partial class NameSheetViewModel(string title, string label, string placeholder, string initial = null) : SheetViewModel(title)
{
	public string Label { get; } = label;

	public string Placeholder { get; } = placeholder;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(OkCommand))]
	public partial string Name { get; set; } = initial;

	[RelayCommand(CanExecute = nameof(HasName))]
	private void Ok() => Close(Name.Trim());

	private bool HasName() => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>A field to choose in bulk edit: its step and label.</summary>
public sealed record FieldChoice(FieldInfo Field)
{
	public override string ToString() => FieldCatalog.Steps[Field.Step - 1] + ": " + Field.Label;
}

/// <summary>
/// Set a field on all the checked assessments: pick the field, enter the value with the editor's own input, see how
/// many will change, apply in one go (one transaction, one batch in edit history), and revert it if it was a mistake.
/// Fields that identify one building (address, owner, parcel, coordinates) aren't offered. Final assessments are skipped.
/// </summary>
public sealed partial class BulkEditSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly IReadOnlyList<Guid> _ids;
	private readonly AssessmentFile _scratch;
	private readonly int _locked;

	public BulkEditSheetViewModel(AppServices app, IReadOnlyList<Guid> assessmentIds)
		: base("Set a field on " + Count(assessmentIds.Count))
	{
		_app = app;
		_ids = assessmentIds;
		// The value is entered on a copy of the first assessment, so choices that depend on it (counties, stories) make sense.
		_scratch = UndoService.Read(UndoService.Snapshot(app.Store.LoadAssessment(assessmentIds[0])));
		Dictionary<Guid, AssessmentStatusRecord> statuses = app.Store.GetStatuses();
		_locked = assessmentIds.Count(id => statuses.TryGetValue(id, out AssessmentStatusRecord s) && s.IsLocked);
		Fields = FieldCatalog.All.Where(f => !f.PerStructure).Select(f => new FieldChoice(f)).ToList();
	}

	private static string Count(int n) => n == 1 ? "1 assessment" : $"{n} assessments";

	public IReadOnlyList<FieldChoice> Fields { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Summary), nameof(HasField))]
	[NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
	public partial FieldChoice SelectedField { get; set; }

	/// <summary>The input for the chosen field (the same kind the editor shows).</summary>
	[ObservableProperty]
	public partial FieldViewModel Input { get; private set; }

	public bool HasField => SelectedField != null;

	partial void OnSelectedFieldChanged(FieldChoice value)
	{
		if (value == null)
		{
			Input = null;
			return;
		}
		FieldCatalog.SetValue(_scratch, value.Field, null);
		Input = FieldFactory.Create(value.Field, _scratch, _app.Store.Reference);
		Input.Changed += _ => OnPropertyChanged(nameof(Summary));
	}

	private object Value => SelectedField == null ? null : FieldCatalog.GetValue(_scratch, SelectedField.Field);

	public string Summary
	{
		get
		{
			if (SelectedField == null)
			{
				return "Choose the field to set.";
			}
			string shown = FieldCatalog.Display(SelectedField.Field, Value, _app.Store.Reference);
			string what = string.IsNullOrEmpty(shown) ? $"Clears {SelectedField.Field.Label}" : $"Sets {SelectedField.Field.Label} to {shown}";
			string skip = _locked == 0 ? "" : $" {_locked} {(_locked == 1 ? "is" : "are")} Final or have had a letter sent and won't change.";
			string shared = SelectedField.Field.Model == FieldModel.Property ? " It's a structure field, so every assessment of those structures shows it." : "";
			return $"{what} on {Count(_ids.Count)}.{skip}{shared}";
		}
	}

	[ObservableProperty]
	public partial string ResultText { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RevertCommand))]
	public partial string LastBatch { get; private set; }

	[RelayCommand(CanExecute = nameof(HasField))]
	private void Apply()
	{
		FieldInfo f = SelectedField.Field;
		SaveContext ctx = SaveContext.Batch(SaveVia.BulkEdit, $"Set {f.Label} on {Count(_ids.Count)}");
		try
		{
			BulkResult r = _app.Writes.Write(_ids, () => _app.Store.ApplyChanges(_ids, new[] { new FieldChange(f.Key, Value) }, ctx));
			LastBatch = r.Changed > 0 ? r.BatchId : null;
			ResultText = $"Changed {Count(r.Changed)}." + (r.Locked.Count > 0 ? $" {r.Locked.Count} Final {(r.Locked.Count == 1 ? "was" : "were")} left as {(r.Locked.Count == 1 ? "it was" : "they were")}." : "");
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't set " + f.Label, ex);
		}
	}

	/// <summary>Puts back what the last Apply changed.</summary>
	[RelayCommand(CanExecute = nameof(CanRevert))]
	private void Revert()
	{
		try
		{
			BulkResult r = _app.Writes.Write(_ids, () => _app.Store.RevertBatch(LastBatch));
			ResultText = $"Put back {Count(r.Changed)}." + (r.ChangedSince > 0 ? $" {r.ChangedSince} value{(r.ChangedSince == 1 ? " was" : "s were")} changed again since and left alone." : "");
			LastBatch = null;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't put it back", ex);
		}
	}

	private bool CanRevert() => LastBatch != null;
}

/// <summary>
/// FEMA's Search and Replace, over the same fields (FEMA's Lookup type 28), with a preview of every change. Unlike
/// FEMA's version it changes only the matching text and keeps the rest of the value's capitals. One batch in edit
/// history, so it can be reverted.
/// </summary>
public sealed partial class SearchReplaceSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;

	public SearchReplaceSheetViewModel(AppServices app)
		: base("Search and replace")
	{
		_app = app;
		Fields = app.Store.SearchFields();
		SelectedField = Fields.FirstOrDefault(f => f.Field.Member == nameof(PropertyRecord.StreetName)) ?? Fields.FirstOrDefault();
	}

	public IReadOnlyList<SearchField> Fields { get; }

	[ObservableProperty]
	public partial SearchField SelectedField { get; set; }

	[ObservableProperty]
	public partial string Find { get; set; }

	[ObservableProperty]
	public partial string ReplaceWith { get; set; }

	public ObservableCollection<SearchMatch> Matches { get; } = new();

	public string MatchText => string.IsNullOrEmpty(Find) ? "Type what to find." : Matches.Count == 0 ? "Nothing matches." :
		$"{Matches.Count} {(Matches.Count == 1 ? "value" : "values")} will change" + (Matches.Count(m => m.Locked) is int locked && locked > 0 ? $" ({locked} in Final assessments won't)" : "") + ".";

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RevertCommand))]
	public partial string LastBatch { get; private set; }

	[ObservableProperty]
	public partial string ResultText { get; private set; }

	partial void OnSelectedFieldChanged(SearchField value) => Preview();

	partial void OnFindChanged(string value) => Preview();

	partial void OnReplaceWithChanged(string value) => Preview();

	private void Preview()
	{
		Matches.Clear();
		if (SelectedField != null)
		{
			foreach (SearchMatch m in _app.Store.PreviewReplace(SelectedField, Find, ReplaceWith ?? ""))
			{
				Matches.Add(m);
			}
		}
		OnPropertyChanged(nameof(MatchText));
		ReplaceAllCommand.NotifyCanExecuteChanged();
	}

	[RelayCommand(CanExecute = nameof(HasMatches))]
	private void ReplaceAll()
	{
		try
		{
			BulkResult r = _app.Writes.Write(null, () => _app.Store.Replace(SelectedField, Find, ReplaceWith ?? ""));
			LastBatch = r.Changed > 0 ? r.BatchId : null;
			ResultText = $"Changed {r.Changed} assessment{(r.Changed == 1 ? "" : "s")}." + (r.Locked.Count > 0 ? $" {r.Locked.Count} Final left as they were." : "");
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't replace", ex);
		}
		Preview();
	}

	private bool HasMatches() => Matches.Any(m => !m.Locked);

	[RelayCommand(CanExecute = nameof(CanRevert))]
	private void Revert()
	{
		BulkResult r = _app.Writes.Write(null, () => _app.Store.RevertBatch(LastBatch));
		ResultText = $"Put back {r.Changed} assessment{(r.Changed == 1 ? "" : "s")}.";
		LastBatch = null;
		Preview();
	}

	private bool CanRevert() => LastBatch != null;
}

/// <summary>A step that copy-from can copy, with a tick box.</summary>
public sealed partial class CopyStepViewModel(int step, string title, bool isChecked) : ObservableObject
{
	public int Step { get; } = step;

	public string Title { get; } = title;

	[ObservableProperty]
	public partial bool IsChecked { get; set; } = isChecked;
}

/// <summary>
/// Copy from another structure: fill this assessment's building, inspection, flood zone, cost or result details from
/// one already entered (a row of identical houses, say). Location and owner and the damage percentages are never
/// copied: they belong to each building. One batch in edit history.
/// </summary>
public sealed partial class CopyFromSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly Guid _target;
	private readonly List<StructureSummary> _all;

	public CopyFromSheetViewModel(AppServices app, Guid targetAssessmentId)
		: base("Copy from another structure")
	{
		_app = app;
		_target = targetAssessmentId;
		_all = app.Store.ListStructures().Where(s => s.AssessmentID != null && s.AssessmentID != targetAssessmentId).ToList();
		Steps = new[]
		{
			new CopyStepViewModel(FieldCatalog.BuildingStep, "Building", true),
			new CopyStepViewModel(FieldCatalog.InspectionStep, "Inspection", false),
			new CopyStepViewModel(FieldCatalog.FloodZoneStep, "Flood zone", true),
			new CopyStepViewModel(FieldCatalog.CostStep, "Cost & depreciation (including cost adjustments)", true),
			new CopyStepViewModel(FieldCatalog.ResultStep, "Result (value and repair-cost bases)", false),
			new CopyStepViewModel(FieldCatalog.NotesStep, "Your fields", false),
		};
		foreach (CopyStepViewModel s in Steps)
		{
			s.PropertyChanged += (_, _) => CopyCommand.NotifyCanExecuteChanged();
		}
		Filter();
	}

	public IReadOnlyList<CopyStepViewModel> Steps { get; }

	public ObservableCollection<StructureSummary> Sources { get; } = new();

	[ObservableProperty]
	public partial string Search { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CopyCommand))]
	public partial StructureSummary Source { get; set; }

	partial void OnSearchChanged(string value) => Filter();

	private void Filter()
	{
		string q = (Search ?? "").Trim();
		Sources.Clear();
		foreach (StructureSummary s in _all.Where(s => q.Length == 0 || (s.Address ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
			|| (s.Owner ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).Take(200))
		{
			Sources.Add(s);
		}
	}

	[RelayCommand(CanExecute = nameof(CanCopy))]
	private void Copy()
	{
		AssessmentFile source = _app.Store.LoadAssessment(Source.AssessmentID!.Value);
		HashSet<int> steps = Steps.Where(s => s.IsChecked).Select(s => s.Step).ToHashSet();
		string from = Source.Address;
		SaveContext ctx = SaveContext.Batch(SaveVia.CopyFrom, "Copied from " + from);
		try
		{
			_app.Writes.Write(new[] { _target }, () =>
			{
				AssessmentFile target = _app.Store.LoadAssessment(_target);
				CopyInto(source, target, steps, _app.Store.Calculator);
				BulkResult r = _app.Store.SaveAssessments(new[] { target }, ctx);
				if (r.Locked.Count > 0)
				{
					throw new AssessmentLockedException(_target, AssessmentStatus.Final, new List<FieldDiff>());
				}
			});
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't copy", ex);
			return;
		}
		Close(true);
		_app.Dialogs.Notify("Copied", $"Copied from {from}. Check the result, then adjust what differs.");
	}

	private bool CanCopy() => Source != null && Steps.Any(s => s.IsChecked);

	/// <summary>Copies the ticked steps' fields (the building step first, since the others depend on it), then the rows.</summary>
	public static void CopyInto(AssessmentFile source, AssessmentFile target, ISet<int> steps, AssessmentCalculator calculator)
	{
		List<FieldChange> changes = FieldCatalog.All
			.Where(f => steps.Contains(f.Step) && !f.PerStructure && f.Step != FieldCatalog.DamageStep)
			.OrderBy(f => f.Step)
			.Select(f => new FieldChange(f.Key, FieldCatalog.GetValue(source, f)))
			.ToList();
		FieldRules.Apply(target, changes, calculator);
		if (steps.Contains(FieldCatalog.CostStep))
		{
			calculator.NormalizeRows(target);
			foreach (CostAdjustmentResultRecord t in target.CostAdjustments)
			{
				CostAdjustmentResultRecord s = source.CostAdjustments.FirstOrDefault(x => x.CostAdjustmentLookupID == t.CostAdjustmentLookupID);
				t.Quantity = s?.Quantity;
				t.UnitCost = s?.UnitCost;
			}
			foreach (AdditionalAdjustmentRecord t in target.AdditionalAdjustments)
			{
				AdditionalAdjustmentRecord s = source.AdditionalAdjustments.FirstOrDefault(x => x.zOrder == t.zOrder);
				t.AdjustmentName = s?.AdjustmentName;
				t.Quantity = s?.Quantity;
				t.UnitCost = s?.UnitCost;
			}
		}
		if (steps.Contains(FieldCatalog.NotesStep))
		{
			foreach (CustomFieldResultRecord s in source.CustomFields)
			{
				CustomFieldResultRecord t = target.CustomFields.FirstOrDefault(x => x.CustomFieldID == s.CustomFieldID);
				if (t == null)
				{
					target.CustomFields.Add(new CustomFieldResultRecord { CustomFieldID = s.CustomFieldID, zOrder = s.zOrder, Result = s.Result, ResultType = "assessment" });
				}
				else
				{
					t.Result = s.Result;
				}
			}
		}
		calculator.Recalculate(target);
	}
}
