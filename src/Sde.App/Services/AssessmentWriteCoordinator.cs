using Avalonia.Threading;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.Services;

/// <summary>What the coordinator needs from the window: the assessment open in the editor, and a way to save or reload it.</summary>
public interface IEditorHost
{
	/// <summary>The assessment open in the editor, if any.</summary>
	Guid? OpenAssessmentId { get; }

	/// <summary>Saves what's on screen now.</summary>
	void FlushEditor();

	/// <summary>Reopens the editor from the database (after something else changed it), keeping the step that was showing.</summary>
	void ReloadEditor();
}

/// <summary>
/// Changes to assessments that come from outside the editor (bulk edit, copy from, search and replace, imports, the
/// phone form, auto-fill) go through here, so the editor never overwrites them with its older copy and shows them as
/// soon as they're saved. Everything runs on the UI thread, in order.
/// </summary>
public sealed class AssessmentWriteCoordinator(AppServices app)
{
	private IEditorHost _editor;

	public void Attach(IEditorHost editor) => _editor = editor;

	/// <summary>
	/// Runs a write that may touch <paramref name="assessmentIds"/> (null = any): saves the editor first if it has one
	/// of them open, runs the write, reloads the editor, and tells lists to refresh.
	/// </summary>
	public T Write<T>(IReadOnlyCollection<Guid> assessmentIds, Func<T> write)
	{
		bool touchesEditor = _editor?.OpenAssessmentId is Guid open && (assessmentIds == null || assessmentIds.Contains(open));
		if (touchesEditor)
		{
			_editor.FlushEditor();
		}
		try
		{
			return write();
		}
		finally
		{
			if (touchesEditor)
			{
				_editor.ReloadEditor();
			}
			app.RaiseDataChanged();
		}
	}

	/// <summary>
	/// The same for a write that runs off the UI thread (an import through the job runner): the editor is saved before
	/// it starts and reloaded after it ends, and lists refresh.
	/// </summary>
	public async Task<T> WriteAsync<T>(IReadOnlyCollection<Guid> assessmentIds, Func<Task<T>> write)
	{
		bool touchesEditor = _editor?.OpenAssessmentId is Guid open && (assessmentIds == null || assessmentIds.Contains(open));
		if (touchesEditor)
		{
			_editor.FlushEditor();
		}
		try
		{
			return await write();
		}
		finally
		{
			if (touchesEditor)
			{
				_editor.ReloadEditor();
			}
			app.RaiseDataChanged();
		}
	}

	public void Write(IReadOnlyCollection<Guid> assessmentIds, Action write) =>
		Write<bool>(assessmentIds, () =>
		{
			write();
			return true;
		});

	/// <summary>Loads one assessment, changes it, and saves it (recalculated, with history and lock checks).</summary>
	public AssessmentFile Apply(Guid assessmentId, Action<AssessmentFile> change, SaveContext context) =>
		Write(new[] { assessmentId }, () =>
		{
			AssessmentFile file = app.Store.LoadAssessment(assessmentId) ?? throw new ArgumentException("That assessment no longer exists.");
			change(file);
			app.Store.SaveAssessment(file, context);
			return file;
		});

	/// <summary>The same, from a background thread (the phone form's web server): runs on the UI thread and waits.</summary>
	public Task<AssessmentFile> ApplyFromBackgroundAsync(Guid assessmentId, Action<AssessmentFile> change, SaveContext context) =>
		Dispatcher.UIThread.InvokeAsync(() => Apply(assessmentId, change, context)).GetTask();
}
