using Newtonsoft.Json;
using Sde.Core.Assessments;

namespace Sde.App.Services;

/// <summary>
/// Undo and redo for assessments: a snapshot of the whole assessment before each burst of edits (everything typed
/// between two autosaves counts as one step). Kept per assessment for as long as the app is open, so undo still works
/// after the editor reloads or after switching to another assessment and back. Square-footage shapes aren't included.
/// </summary>
public sealed class UndoService
{
	public const int MaxSteps = 50;

	private readonly Dictionary<Guid, (List<string> Undo, List<string> Redo)> _stacks = new();

	public static string Snapshot(AssessmentFile file) => JsonConvert.SerializeObject(file);

	public static AssessmentFile Read(string snapshot) => JsonConvert.DeserializeObject<AssessmentFile>(snapshot);

	private (List<string> Undo, List<string> Redo) For(Guid id)
	{
		if (!_stacks.TryGetValue(id, out (List<string> Undo, List<string> Redo) s))
		{
			s = (new List<string>(), new List<string>());
			_stacks[id] = s;
		}
		return s;
	}

	/// <summary>Remembers how an assessment was before an edit; a new edit clears what could be redone.</summary>
	public void Push(Guid id, string before)
	{
		(List<string> undo, List<string> redo) = For(id);
		undo.Add(before);
		if (undo.Count > MaxSteps)
		{
			undo.RemoveAt(0);
		}
		redo.Clear();
	}

	public bool CanUndo(Guid id) => _stacks.TryGetValue(id, out (List<string> Undo, List<string> Redo) s) && s.Undo.Count > 0;

	public bool CanRedo(Guid id) => _stacks.TryGetValue(id, out (List<string> Undo, List<string> Redo) s) && s.Redo.Count > 0;

	/// <summary>The state to go back to; <paramref name="current"/> becomes redoable.</summary>
	public string Undo(Guid id, string current)
	{
		(List<string> undo, List<string> redo) = For(id);
		string back = undo[^1];
		undo.RemoveAt(undo.Count - 1);
		redo.Add(current);
		return back;
	}

	/// <summary>The state to go forward to; <paramref name="current"/> becomes undoable again.</summary>
	public string Redo(Guid id, string current)
	{
		(List<string> undo, List<string> redo) = For(id);
		string forward = redo[^1];
		redo.RemoveAt(redo.Count - 1);
		undo.Add(current);
		return forward;
	}

	/// <summary>Forgets everything (another database was opened).</summary>
	public void Clear() => _stacks.Clear();
}
