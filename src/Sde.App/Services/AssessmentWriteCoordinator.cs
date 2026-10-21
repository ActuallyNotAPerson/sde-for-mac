using Avalonia.Threading;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.Services;

/// <summary>What the coordinator needs from the window: the assessment open in the editor, and a way to save or reload it.</summary>
public interface IEditorHost
{
	/// <summary>The assessment open in the editor, if any.</summary>
	Guid? OpenAssessmentId { get; }

	/// <summary>The structure of the assessment open in the editor, if any.</summary>
	Guid? OpenPropertyId { get; }

	/// <summary>Saves what's on screen now.</summary>
	void FlushEditor();

	/// <summary>
	/// Catches the editor up with the database after something else may have changed its assessment: nothing happens
	/// when it didn't change; otherwise a clean editor reloads (keeping the step that was showing) and one with unsaved
	/// changes saves them onto the new copy, asking only about values both sides changed.
	/// </summary>
	void ReloadEditor();
}

/// <summary>
/// Changes to assessments that come from outside the editor (bulk edit, copy from, search and replace, imports, the
/// phone form, auto-fill, the map) go through here, so the editor never overwrites them with its older copy and shows
/// them as soon as they're saved. Everything runs on the UI thread, in order. Writes are matched to the open editor by
/// assessment and by structure, and the database's versions catch anything the matching misses: the editor's own
/// save is refused if its copy is out of date, and it then merges.
/// </summary>
public sealed class AssessmentWriteCoordinator(AppServices app)
{
	private IEditorHost _editor;

	public void Attach(IEditorHost editor) => _editor = editor;

	/// <summary>
	/// Runs a write that may touch <paramref name="assessmentIds"/> (null = any): saves the editor first if it has one
	/// of them open, runs the write, catches the editor up, and tells lists to refresh.
	/// </summary>
	public T Write<T>(IReadOnlyCollection<Guid> assessmentIds, Func<T> write) => Write(assessmentIds, null, write);

	/// <summary>
	/// The same for a write to structures (their own fields, such as a location): the editor is caught up when it has
	/// an assessment of one of <paramref name="propertyIds"/> open.
	/// </summary>
	public T WriteForStructures<T>(IReadOnlyCollection<Guid> propertyIds, Func<T> write) => Write(Array.Empty<Guid>(), propertyIds, write);

	private T Write<T>(IReadOnlyCollection<Guid> assessmentIds, IReadOnlyCollection<Guid> propertyIds, Func<T> write)
	{
		if (Touches(assessmentIds, propertyIds))
		{
			_editor.FlushEditor();
		}
		try
		{
			return write();
		}
		finally
		{
			if (Touches(assessmentIds, propertyIds))
			{
				_editor.ReloadEditor();
			}
			app.RaiseDataChanged();
		}
	}

	/// <summary>
	/// The same for a write that runs off the UI thread (an import through the job runner): the editor is saved before
	/// it starts, and whatever editor is open when it ends (it may be another one, opened meanwhile) is caught up.
	/// </summary>
	public async Task<T> WriteAsync<T>(IReadOnlyCollection<Guid> assessmentIds, Func<Task<T>> write)
	{
		if (Touches(assessmentIds, null))
		{
			_editor.FlushEditor();
		}
		try
		{
			return await write();
		}
		finally
		{
			if (Touches(assessmentIds, null))
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

	/// <summary>True when the editor has one of these assessments, or an assessment of one of these structures, open (null assessments = any).</summary>
	private bool Touches(IReadOnlyCollection<Guid> assessmentIds, IReadOnlyCollection<Guid> propertyIds)
	{
		if (_editor?.OpenAssessmentId is not Guid open)
		{
			return false;
		}
		return assessmentIds == null || assessmentIds.Contains(open)
			|| (propertyIds != null && _editor.OpenPropertyId is Guid property && propertyIds.Contains(property));
	}

	/// <summary>Loads one assessment, changes it, and saves it in one transaction (recalculated, with history and lock checks).</summary>
	public AssessmentFile Apply(Guid assessmentId, Action<AssessmentFile> change, SaveContext context) =>
		Write(new[] { assessmentId }, () => app.Store.Apply(assessmentId, change, context));

	/// <summary>The same, from a background thread (the phone form's web server): runs on the UI thread and waits.</summary>
	public Task<AssessmentFile> ApplyFromBackgroundAsync(Guid assessmentId, Action<AssessmentFile> change, SaveContext context) =>
		Dispatcher.UIThread.InvokeAsync(() => Apply(assessmentId, change, context)).GetTask();
}
