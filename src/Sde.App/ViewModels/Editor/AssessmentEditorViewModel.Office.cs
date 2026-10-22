using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Fields;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.ViewModels.Editor;

/// <summary>
/// The editor's part in the office workflow: the Comments button and sheet, ⋯ Compliance case, and going to a field
/// named by a quality finding or a comment.
/// </summary>
public sealed partial class AssessmentEditorViewModel
{
	/// <summary>Open comment threads on this assessment (shown on the Comments button).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CommentsText), nameof(CommentsAccessibleName))]
	public partial int OpenCommentCount { get; private set; }

	/// <summary>"Comments" or "Comments (2)".</summary>
	public string CommentsText => OpenCommentCount == 0 ? "Comments" : $"Comments ({OpenCommentCount})";

	public string CommentsAccessibleName => OpenCommentCount switch
	{
		0 => "Comments",
		1 => "Comments: 1 open",
		_ => $"Comments: {OpenCommentCount} open",
	};

	/// <summary>Counts the open comment threads again.</summary>
	internal void RefreshComments() =>
		OpenCommentCount = IsDraft ? 0 : _app.Store.GetReviewComments(Assessment.AssessmentID).Count(c => c.ParentID == null && !c.IsResolved);

	/// <summary>The Comments button and ⋯ › Comments: the reviewers' comments on this assessment, with reply and resolve.</summary>
	[RelayCommand]
	private async Task ShowComments()
	{
		Save();
		if (IsDraft)
		{
			_app.Dialogs.Notify("Not started yet", "Enter something in this assessment first, then comment on it.");
			return;
		}
		CommentThreadsViewModel threads = new CommentThreadsViewModel(_app, Assessment.AssessmentID, key =>
		{
			_app.Sheets.CancelCurrentCommand.Execute(null);
			GoToField(key);
		});
		threads.Changed += RefreshComments;
		await _app.Sheets.ShowAsync(new CommentsSheetViewModel(threads, Title));
		threads.Changed -= RefreshComments;
		RefreshComments();
	}

	/// <summary>
	/// ⋯ › Compliance case: the compliance case for this substantially damaged assessment, opening one (after asking)
	/// when it has none yet.
	/// </summary>
	[RelayCommand]
	private async Task ComplianceCase()
	{
		Save();
		if (IsDraft)
		{
			_app.Dialogs.Notify("Not started yet", "Enter something in this assessment first.");
			return;
		}
		Guid id = Assessment.AssessmentID;
		ComplianceCaseRecord existing = _app.Store.GetComplianceCaseFor(id);
		if (existing == null)
		{
			if (!(Assessment.PercentDamage >= 50m) || Assessment.PhysicalDamage)
			{
				_app.Dialogs.Notify("No compliance case needed", "Only a substantially damaged building (50% or more) needs a compliance case.");
				return;
			}
			if (!await _app.Dialogs.ConfirmAsync("Open a compliance case?",
				"A compliance case follows this structure until it is brought into compliance: the way the owner chooses, the permit and deadline, the ICC claim, the elevation certificate and the final inspection.",
				"Open case"))
			{
				return;
			}
			try
			{
				existing = _app.Store.OpenComplianceCase(id);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_app.Errors.Show("Couldn't open a compliance case", ex);
				return;
			}
			_app.RaiseDataChanged();
		}
		await _app.Sheets.ShowAsync(new ComplianceCaseSheetViewModel(_app, existing.CaseID));
		_app.RaiseDataChanged();
	}

	/// <summary>
	/// Shows the step that holds a field and puts the cursor in it. <paramref name="fieldKey"/> is a FieldCatalog key
	/// ("Assessment.DateDamageOccurred") or "Element." and an element name (the damage step).
	/// </summary>
	public void GoToField(string fieldKey)
	{
		if (string.IsNullOrEmpty(fieldKey))
		{
			return;
		}
		if (fieldKey.StartsWith("Element.", StringComparison.Ordinal))
		{
			if (Sections.OfType<DamageSectionViewModel>().FirstOrDefault() is SectionViewModel damage)
			{
				SelectedSection = damage;
			}
			return;
		}
		FieldInfo info = FieldCatalog.Get(fieldKey);
		if (info == null)
		{
			return;
		}
		FieldViewModel field = _fields.FirstOrDefault(f => f.Member == info.Member && f.RecordType == info.RecordType);
		SectionViewModel section = field == null ? null : Sections.FirstOrDefault(s => s.AllFields.Contains(field));
		section ??= info.Step >= 1 && info.Step <= Sections.Count ? Sections[info.Step - 1] : null;
		if (section == null)
		{
			return;
		}
		SelectedSection = section;
		if (field != null)
		{
			// After the step is laid out (and, when the editor was just opened, after its view is there to listen).
			Dispatcher.UIThread.Post(() => FocusFieldRequested?.Invoke(field), DispatcherPriority.Background);
		}
	}
}
