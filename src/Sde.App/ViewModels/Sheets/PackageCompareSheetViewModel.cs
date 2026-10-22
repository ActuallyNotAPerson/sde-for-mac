using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>One value that differs between the assessment here and the work package's copy.</summary>
public sealed partial class PackageDiffRowViewModel(FieldDiff diff) : ObservableObject
{
	public FieldDiff Diff { get; } = diff;

	public string Label => Diff.Label;

	public string Here => string.IsNullOrEmpty(Diff.OldValue) ? "(blank)" : Diff.OldValue;

	public string Field => string.IsNullOrEmpty(Diff.NewValue) ? "(blank)" : Diff.NewValue;

	public string CheckName => "Use the field's " + Label;

	[ObservableProperty]
	public partial bool IsChecked { get; set; } = true;
}

/// <summary>
/// After an assessment was taken back from a work package early: lists every value the field's copy has that differs
/// from the one here, to tick and copy over (one entry in History). Closes with how many values were copied.
/// </summary>
public sealed partial class PackageCompareSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly string _packagePath;
	private readonly Guid _assessmentId;

	public PackageCompareSheetViewModel(AppServices app, string packagePath, Guid assessmentId) : base("Copy from a work package")
	{
		_app = app;
		_packagePath = packagePath;
		_assessmentId = assessmentId;
		Rows = WorkPackages.Compare(app.Store, packagePath, assessmentId).Select(d => new PackageDiffRowViewModel(d)).ToList();
		Message = Rows.Count == 0
			? "The package's copy has the same values as this one. Nothing to copy."
			: $"{Path.GetFileName(packagePath)} has {(Rows.Count == 1 ? "1 value" : $"{Rows.Count} values")} that differ from this assessment. Tick the ones to use.";
	}

	public override SheetSize Size => SheetSize.Large;

	public string Message { get; }

	public IReadOnlyList<PackageDiffRowViewModel> Rows { get; }

	public bool HasRows => Rows.Count > 0;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; }

	public bool HasError => !string.IsNullOrEmpty(Error);

	[RelayCommand]
	private void Copy()
	{
		List<string> keys = Rows.Where(r => r.IsChecked).Select(r => r.Diff.Key).ToList();
		if (keys.Count == 0)
		{
			Close(0);
			return;
		}
		try
		{
			_app.Writes.Write(new[] { _assessmentId }, () => WorkPackages.CopyFromPackage(_app.Store, _packagePath, _assessmentId, keys));
			Close(keys.Count);
		}
		catch (Exception ex) when (ex is AssessmentLockedException or AssessmentCheckedOutException or ArgumentException or IOException)
		{
			Error = ex.Message;
		}
	}
}
