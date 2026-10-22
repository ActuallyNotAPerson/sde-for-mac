using Sde.Core.Assessments;

namespace Sde.App.ViewModels.Editor;

/// <summary>
/// Values someone else changed while an assessment was being edited, which the editor had changed too. The editor's
/// changes were saved onto their copy, keeping their values for these; <see cref="MineSnapshot"/> holds the editor's
/// copy so "Keep mine" can put its values back.
/// </summary>
public sealed class EditConflict(IReadOnlyList<FieldDiff> changes, string mineSnapshot)
{
	/// <summary>The editor's own changes to the fields both sides changed.</summary>
	public IReadOnlyList<FieldDiff> Changes { get; } = changes;

	public string MineSnapshot { get; } = mineSnapshot;

	/// <summary>The banner's text, naming the fields.</summary>
	public string Text => "Someone else changed " + string.Join(", ", Changes.Select(c => c.Label).Distinct()) + " while you were editing. Their values are showing.";
}
