using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sde.App.ViewModels.Fields;

namespace Sde.App.ViewModels.Editor;

/// <summary>A titled group of fields inside a step (one card on screen).</summary>
public sealed partial class FieldGroupViewModel(string title, string note, IReadOnlyList<FieldViewModel> fields) : ObservableObject
{
	public string Title { get; } = title;

	public string Note { get; } = note;

	public IReadOnlyList<FieldViewModel> Fields { get; } = fields;

	public bool HasTitle => !string.IsNullOrEmpty(Title);

	public bool HasNote => !string.IsNullOrEmpty(Note);

	/// <summary>Optional link in the card header (e.g. "Same as structure").</summary>
	public string ActionText { get; init; }

	public System.Windows.Input.ICommand ActionCommand { get; init; }

	public bool HasAction => ActionCommand != null;

	[ObservableProperty]
	public partial bool IsVisible { get; set; } = true;

	public Func<bool> VisibleWhen { get; init; }

	public void Refresh()
	{
		foreach (FieldViewModel f in Fields)
		{
			f.Refresh();
		}
		IsVisible = (VisibleWhen?.Invoke() ?? true) && Fields.Any(f => f.IsVisible);
	}
}

/// <summary>One step of the assessment (left-hand list). Subclasses add custom content (damage table, result, photos…).</summary>
public partial class SectionViewModel : ObservableObject
{
	public SectionViewModel(AssessmentEditorViewModel editor, string title, string subtitle, string icon)
	{
		Editor = editor;
		Title = title;
		Subtitle = subtitle;
		Icon = icon;
	}

	public AssessmentEditorViewModel Editor { get; }

	public string Title { get; }

	public string Subtitle { get; }

	/// <summary>The step's icon in the step list: an icon kind from Styles/Icons.axaml, such as "MapPin" (for the Icon control).</summary>
	public string Icon { get; }

	public int Number { get; set; }

	public ObservableCollection<FieldGroupViewModel> Groups { get; } = new();

	/// <summary>"Look up online…" in the step's header (Location and Flood zone), or null.</summary>
	public System.Windows.Input.ICommand OnlineLookupCommand { get; set; }

	public bool HasOnlineLookup => OnlineLookupCommand != null;

	/// <summary>"Elevation certificate…" in the Flood zone step's header, or null.</summary>
	public System.Windows.Input.ICommand ElevationCertificateCommand { get; set; }

	public bool HasElevationCertificate => ElevationCertificateCommand != null;

	/// <summary>Every field shown in this step (used to place validation issues).</summary>
	public virtual IEnumerable<FieldViewModel> AllFields => Groups.SelectMany(g => g.Fields);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasRequiredIssues), nameof(NeedsCheck), nameof(StatusText), nameof(IsDone))]
	public partial int RequiredIssues { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(NeedsCheck), nameof(StatusText), nameof(IsDone))]
	public partial int OtherIssues { get; set; }

	public bool HasRequiredIssues => RequiredIssues > 0;

	/// <summary>Nothing required is missing, but something recommended is (the step list shows it in amber).</summary>
	public bool NeedsCheck => RequiredIssues == 0 && OtherIssues > 0;

	public bool IsDone => RequiredIssues == 0 && OtherIssues == 0 && EmptyStatus == null;

	public string StatusText => RequiredIssues > 0 ? $"{RequiredIssues} required"
		: OtherIssues > 0 ? $"{OtherIssues} to check"
		: EmptyStatus ?? "Complete";

	/// <summary>Shown instead of "Complete" when a step has no issues but nothing entered yet (e.g. "Nothing entered").</summary>
	protected virtual string EmptyStatus => null;

	/// <summary>Call when content that affects <see cref="EmptyStatus"/> changed.</summary>
	public void RefreshStatus()
	{
		OnPropertyChanged(nameof(StatusText));
		OnPropertyChanged(nameof(IsDone));
	}

	public virtual void Refresh()
	{
		foreach (FieldGroupViewModel g in Groups)
		{
			g.Refresh();
		}
	}
}

/// <summary>Step 5: replacement cost (square footage, base cost, adjustments) and depreciation.</summary>
public sealed class CostSectionViewModel(AssessmentEditorViewModel editor) : SectionViewModel(editor, "Cost & depreciation", "What would it cost to rebuild, and what was it worth?", "Cost")
{
	public FieldGroupViewModel Basics { get; set; }

	public FieldGroupViewModel Depreciation { get; set; }

	public override IEnumerable<FieldViewModel> AllFields => Basics.Fields.Concat(Depreciation.Fields).Concat(base.AllFields);

	public override void Refresh()
	{
		Basics?.Refresh();
		Depreciation?.Refresh();
		base.Refresh();
	}
}

/// <summary>Step 6: % damaged per building element.</summary>
public sealed class DamageSectionViewModel(AssessmentEditorViewModel editor) : SectionViewModel(editor, "Damage by element", "How badly is each part of the building damaged?", "Sliders")
{
	protected override string EmptyStatus => Editor.IsUndetermined ? null : Editor.Elements.All(e => e.UserDamage == 0) ? "No damage entered" : null;
}

/// <summary>Step 7: value / repair-cost bases and the determination.</summary>
public sealed class ResultSectionViewModel(AssessmentEditorViewModel editor) : SectionViewModel(editor, "Result", "Choose the value and repair-cost bases", "Result")
{
	public FieldGroupViewModel ValueInputs { get; set; }

	public FieldGroupViewModel RepairInputs { get; set; }

	public override IEnumerable<FieldViewModel> AllFields => ValueInputs.Fields.Concat(RepairInputs.Fields).Concat(base.AllFields);

	public override void Refresh()
	{
		ValueInputs?.Refresh();
		RepairInputs?.Refresh();
		base.Refresh();
	}
}

/// <summary>Step 8: photos and custom fields.</summary>
public sealed class PhotosSectionViewModel(AssessmentEditorViewModel editor) : SectionViewModel(editor, "Photos & notes", "Pictures of the damage and your own fields", "Camera")
{
	protected override string EmptyStatus => Editor.Photos.Count == 0 ? "Optional" : null;
}
