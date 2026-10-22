using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports;

namespace Sde.App.ViewModels.Sheets;

/// <summary>One improvement project in the sheet's list.</summary>
public sealed class ImprovementProjectRowViewModel(ImprovementProjectRecord project)
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public ImprovementProjectRecord Project { get; } = project;

	/// <summary>"BP-0002 · Second-story addition".</summary>
	public string Title
	{
		get
		{
			string t = string.Join(" · ", new[] { Project.PermitNumber, Project.Description }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
			return t.Length == 0 ? "(no permit or description)" : t;
		}
	}

	/// <summary>"Jun 1, 2026 · $42,000.00 · Excluded: routine maintenance".</summary>
	public string Detail => string.Join(" · ", new[]
	{
		Project.ProjectDate?.ToString("MMM d, yyyy", Us) ?? "No date",
		Project.Cost?.ToString("C2", Us) ?? "No cost",
		Project.Included ? null : "Excluded" + (string.IsNullOrWhiteSpace(Project.ExclusionReason) ? "" : ": " + Project.ExclusionReason.Trim()),
	}.Where(s => s != null));
}

/// <summary>One line of the result's project list: a project, and whether (and why) its cost was counted.</summary>
public sealed record SiLineViewModel(string Project, string Cost, string Reason, bool Counted);

/// <summary>An earlier determination, for the list at the bottom of the sheet.</summary>
public sealed record SiDeterminationRowViewModel(string When, string Result, string Detail)
{
	public Guid DeterminationId { get; init; }

	/// <summary>A substantial improvement: the building must be brought into compliance, so it can have a compliance case.</summary>
	public bool IsSubstantial { get; init; }

	/// <summary>"Open compliance case…", or "Compliance case…" when it has one.</summary>
	public string CaseText { get; init; }

	public string AccessibleCase => (CaseText ?? "").TrimEnd('…') + " for the determination of " + When;

	public IAsyncRelayCommand<SiDeterminationRowViewModel> OpenCase { get; init; }
}

/// <summary>
/// ⋯, then Improvements and SI: a structure's improvement projects (permits), the live substantial improvement (SI)
/// result for the chosen project, and the determinations made so far. SI is a separate finding: nothing here changes
/// an assessment, its percent damaged or its determination.
/// </summary>
public sealed partial class ImprovementsSheetViewModel : SheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Large;

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
	private readonly AppServices _app;
	private readonly Guid _propertyId;
	private readonly string _address;
	private bool _loading;

	public ImprovementsSheetViewModel(AppServices app, Guid propertyId, string address)
		: base("Improvements and substantial improvement")
	{
		_app = app;
		_propertyId = propertyId;
		_address = address;
		Settings = app.Store.GetSiSettings();

		// The market value starts as the latest assessment's building value, on the basis it uses.
		AssessmentRecord latest = app.Store.GetAssessmentsForProperty(propertyId).FirstOrDefault();
		if (latest != null && CumulativeDamage.BuildingValue(latest) is decimal value)
		{
			MarketValue = Math.Round(value, 2);
			MarketValueBasis = app.Store.Reference.LookupName(latest.ValueOfBuildingID) ?? "Building value from the damage assessment";
		}
		else
		{
			MarketValueBasis = "";
		}
		LoadProjects(null);
		LoadDeterminations();
		Evaluate();
	}

	public string Address => _address;

	public SiSettings Settings { get; }

	/// <summary>"Threshold 50% of market value, each project counted on its own. Change these in Settings."</summary>
	public string SettingsText
	{
		get
		{
			string threshold = Settings.ThresholdPercent.ToString("0.##", Us) + "%";
			string period = Settings.LookbackYears == 0 || !Settings.IncludePriorProjects
				? "each project counted on its own"
				: $"projects in the {(Settings.LookbackYears == 1 ? "1 year" : Settings.LookbackYears + " years")} before it counted together";
			return $"Threshold {threshold} of market value, {period}. Change these in Settings.";
		}
	}

	// ───────────── Projects ─────────────

	public ObservableCollection<ImprovementProjectRowViewModel> Projects { get; } = new();

	public bool HasProjects => Projects.Count > 0;

	/// <summary>The project being determined (and edited below the list); null while a new one is being entered.</summary>
	[ObservableProperty]
	public partial ImprovementProjectRowViewModel SelectedProject { get; set; }

	[ObservableProperty]
	public partial string PermitNumber { get; set; }

	[ObservableProperty]
	public partial string Description { get; set; }

	[ObservableProperty]
	public partial DateTime? ProjectDate { get; set; }

	[ObservableProperty]
	public partial decimal? Cost { get; set; }

	[ObservableProperty]
	public partial string CostBasis { get; set; }

	[ObservableProperty]
	public partial bool Included { get; set; } = true;

	[ObservableProperty]
	public partial string ExclusionReason { get; set; }

	/// <summary>True when the form has changes that aren't saved yet.</summary>
	[ObservableProperty]
	public partial bool IsProjectDirty { get; private set; }

	public bool IsNewProject => SelectedProject == null;

	public string FormTitle => SelectedProject == null ? "New project" : "Project";

	public static IReadOnlyList<string> CostBases { get; } = new[]
	{
		"Contractor's estimate", "Owner's itemized estimate", "Building official's estimate", "Professional cost estimate", "Cost data (for example RSMeans)", "Actual cost (receipts)",
	};

	partial void OnSelectedProjectChanged(ImprovementProjectRowViewModel value)
	{
		_loading = true;
		ImprovementProjectRecord p = value?.Project;
		PermitNumber = p?.PermitNumber;
		Description = p?.Description;
		ProjectDate = p?.ProjectDate;
		Cost = p?.Cost;
		CostBasis = p?.CostBasis;
		Included = p?.Included ?? true;
		ExclusionReason = p?.ExclusionReason;
		_loading = false;
		IsProjectDirty = false;
		OnPropertyChanged(nameof(IsNewProject));
		OnPropertyChanged(nameof(FormTitle));
		DeleteProjectCommand.NotifyCanExecuteChanged();
		Evaluate();
	}

	partial void OnPermitNumberChanged(string value) => FormEdited();

	partial void OnDescriptionChanged(string value) => FormEdited();

	partial void OnProjectDateChanged(DateTime? value) => FormEdited();

	partial void OnCostChanged(decimal? value) => FormEdited();

	partial void OnCostBasisChanged(string value) => FormEdited();

	partial void OnIncludedChanged(bool value) => FormEdited();

	partial void OnExclusionReasonChanged(string value) => FormEdited();

	private void FormEdited()
	{
		if (_loading)
		{
			return;
		}
		IsProjectDirty = true;
		Evaluate();
	}

	private void LoadProjects(Guid? select)
	{
		Projects.Clear();
		foreach (ImprovementProjectRecord p in _app.Store.GetImprovementProjects(_propertyId))
		{
			Projects.Add(new ImprovementProjectRowViewModel(p));
		}
		OnPropertyChanged(nameof(HasProjects));
		SelectedProject = select is Guid id ? Projects.FirstOrDefault(r => r.Project.ProjectID == id) : Projects.FirstOrDefault();
	}

	/// <summary>The project as it is in the form (a copy; the stored one changes only on Save project).</summary>
	private ImprovementProjectRecord FormProject() => new ImprovementProjectRecord
	{
		ProjectID = SelectedProject?.Project.ProjectID ?? Guid.Empty,
		PropertyID = _propertyId,
		PermitNumber = Blank(PermitNumber),
		Description = Blank(Description),
		ProjectDate = ProjectDate?.Date,
		Cost = Cost,
		CostBasis = Blank(CostBasis),
		Included = Included,
		ExclusionReason = Included ? null : Blank(ExclusionReason),
		CreatedOn = SelectedProject?.Project.CreatedOn,
	};

	[RelayCommand]
	private void NewProject()
	{
		SelectedProject = null;
		ProjectDate = DateTime.Today;
		IsProjectDirty = false;
	}

	/// <summary>Saves the form: a new project is added, an existing one changed (both recorded in the structure's history).</summary>
	[RelayCommand]
	public void SaveProject()
	{
		ImprovementProjectRecord p = FormProject();
		if (p.PermitNumber == null && p.Description == null)
		{
			_app.Dialogs.Notify("Describe the project", "Enter a permit number or a description, so the project can be told apart from others.");
			return;
		}
		try
		{
			_app.Store.SaveImprovementProject(p);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the project", ex);
			return;
		}
		LoadProjects(p.ProjectID);
	}

	[RelayCommand(CanExecute = nameof(CanDeleteProject))]
	private async Task DeleteProject()
	{
		ImprovementProjectRowViewModel row = SelectedProject;
		if (row == null || !await _app.Dialogs.ConfirmAsync("Delete this project?", $"\"{row.Title}\" will be deleted for good. To keep it on record but leave its cost out, untick \"Counts toward SI\" instead.", "Delete", destructive: true))
		{
			return;
		}
		try
		{
			_app.Store.DeleteImprovementProject(row.Project.ProjectID);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't delete the project", ex);
			return;
		}
		LoadProjects(null);
	}

	private bool CanDeleteProject() => SelectedProject != null;

	// ───────────── Market value and the result ─────────────

	/// <summary>Market value of the structure only (not the land), before the improvement.</summary>
	[ObservableProperty]
	public partial decimal? MarketValue { get; set; }

	[ObservableProperty]
	public partial string MarketValueBasis { get; set; }

	/// <summary>Kept with the determination and printed on its PDF.</summary>
	[ObservableProperty]
	public partial string Notes { get; set; }

	partial void OnMarketValueChanged(decimal? value) => Evaluate();

	[ObservableProperty]
	public partial SiResult Result { get; private set; }

	[ObservableProperty]
	public partial string PercentText { get; private set; }

	[ObservableProperty]
	public partial string ResultText { get; private set; }

	/// <summary>Colours the result card: Substantial (red) for SI, Not substantial (green), Incomplete (grey) when it can't be determined.</summary>
	[ObservableProperty]
	public partial ResultState State { get; private set; }

	[ObservableProperty]
	public partial IReadOnlyList<string> Explanation { get; private set; } = Array.Empty<string>();

	[ObservableProperty]
	public partial IReadOnlyList<SiLineViewModel> Lines { get; private set; } = Array.Empty<SiLineViewModel>();

	/// <summary>Works the result out again from the form, the saved projects and the market value.</summary>
	private void Evaluate()
	{
		if (_app == null)
		{
			return;
		}
		ImprovementProjectRecord current = FormProject();
		List<ImprovementProjectRecord> others = Projects.Select(r => r.Project).Where(p => SelectedProject == null || p.ProjectID != SelectedProject.Project.ProjectID).ToList();
		SiResult r = ImprovementCalculator.Evaluate(current, others, MarketValue, Settings);
		Result = r;
		PercentText = r.Percent is decimal pct ? pct.ToString("#,##0.0", Us) + "%" : "–";
		ResultText = r.ResultText;
		State = !r.CanDetermine ? ResultState.Incomplete : r.IsSubstantialImprovement ? ResultState.Substantial : ResultState.NotSubstantial;
		Explanation = r.Explanation;
		Lines = r.Projects.Select(l => new SiLineViewModel(
			l.IsCurrent ? "This project" + (new ImprovementProjectRowViewModel(l.Project).Title is string t && t != "(no permit or description)" ? ": " + t : "") : new ImprovementProjectRowViewModel(l.Project).Title,
			l.Counted ? l.CostCounted.ToString("C2", Us) : "",
			l.Reason,
			l.Counted)).ToList();
	}

	// ───────────── Determinations ─────────────

	public ObservableCollection<SiDeterminationRowViewModel> Determinations { get; } = new();

	public bool HasDeterminations => Determinations.Count > 0;

	[ObservableProperty]
	public partial string SavedText { get; private set; }

	private void LoadDeterminations()
	{
		Determinations.Clear();
		foreach (SIDeterminationRecord d in _app.Store.GetSiDeterminations(_propertyId))
		{
			string pct = d.Percent is decimal p ? p.ToString("0.0", Us) + "% of " + (d.MarketValue?.ToString("C0", Us) ?? "?") : "no market value";
			Determinations.Add(new SiDeterminationRowViewModel(d.DeterminedOn.ToString("MMM d, yyyy", Us), d.Result,
				$"{d.CumulativeCost.ToString("C0", Us)} ({pct})" + (string.IsNullOrEmpty(d.DeterminedBy) ? "" : " · " + d.DeterminedBy))
			{
				DeterminationId = d.DeterminationID,
				IsSubstantial = d.Result == SiResult.SubstantialImprovement,
				CaseText = _app.Store.GetComplianceCaseForImprovement(d.DeterminationID) == null ? "Open compliance case…" : "Compliance case…",
				OpenCase = OpenComplianceCaseCommand,
			});
		}
		OnPropertyChanged(nameof(HasDeterminations));
	}

	/// <summary>Saves the project if it has changes, then records the determination as worked out now.</summary>
	[RelayCommand]
	public void SaveDetermination()
	{
		if (!EnsureProjectSaved())
		{
			return;
		}
		try
		{
			SIDeterminationRecord d = Result.ToRecord(_propertyId, _app.Store.Clock(), Blank(MarketValueBasis), _app.UserName, Blank(Notes));
			_app.Store.SaveSiDetermination(d);
			SavedText = $"Determination saved: {d.Result}" + (d.Percent is decimal p ? $" ({p.ToString("0.0", Us)}%)." : ".");
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the determination", ex);
			return;
		}
		LoadDeterminations();
	}

	/// <summary>Writes the SI determination PDF (modelled on FEMA P-758) and opens it in Preview.</summary>
	[RelayCommand]
	private async Task SavePdf()
	{
		if (!EnsureProjectSaved())
		{
			return;
		}
		string suggested = string.Concat($"SI determination – {_address}.pdf".Split(Path.GetInvalidFileNameChars())).Replace(':', '-');
		string path = await _app.Dialogs.SaveFileAsync("Save the SI determination", suggested, "PDF document", "pdf");
		if (path == null)
		{
			return;
		}
		SiDeterminationRequest request = new SiDeterminationRequest
		{
			Property = _app.Store.GetProperty(_propertyId),
			Project = SelectedProject.Project,
			Result = Result,
			MarketValueBasis = Blank(MarketValueBasis),
			DeterminedBy = _app.UserName,
			DeterminedOn = DateTime.Today,
			Notes = Blank(Notes),
		};
		try
		{
			ReferenceData reference = _app.Store.Reference;
			await Task.Run(() => SiDeterminationReport.RenderToFile(request, reference, path));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't create the SI determination", ex);
			return;
		}
		_app.Dialogs.Open(path);
	}

	/// <summary>
	/// The compliance case for a substantial improvement determination: opens one (after asking) when it has none, then
	/// shows it on top of this sheet.
	/// </summary>
	[RelayCommand]
	private async Task OpenComplianceCase(SiDeterminationRowViewModel row)
	{
		if (row == null || !row.IsSubstantial)
		{
			return;
		}
		ComplianceCaseRecord existing = _app.Store.GetComplianceCaseForImprovement(row.DeterminationId);
		if (existing == null)
		{
			if (!await _app.Dialogs.ConfirmAsync("Open a compliance case?",
				"A compliance case follows the improved building until it is brought into compliance: the permit and deadline, the elevation certificate and the final inspection.",
				"Open case"))
			{
				return;
			}
			try
			{
				existing = _app.Store.OpenComplianceCaseForImprovement(row.DeterminationId);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_app.Errors.Show("Couldn't open a compliance case", ex);
				return;
			}
		}
		await _app.Sheets.ShowAsync(new ComplianceCaseSheetViewModel(_app, existing.CaseID));
		LoadDeterminations();
		_app.RaiseDataChanged();
	}

	/// <summary>A determination is about a saved project: saves the form first when it has changes.</summary>
	private bool EnsureProjectSaved()
	{
		if (SelectedProject != null && !IsProjectDirty)
		{
			return true;
		}
		SaveProject();
		return SelectedProject != null && !IsProjectDirty;
	}

	private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
