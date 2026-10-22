using Sde.App.Help;
using Sde.App.ViewModels.Fields;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.ViewModels.Editor;

/// <summary>The info button beside each field.</summary>
public sealed partial class AssessmentEditorViewModel
{
	/// <summary>Shows what to enter in a field, with the manual page, in a sheet.</summary>
	private void ShowFieldHelp(FieldViewModel field)
	{
		FieldHelpEntry entry = FieldHelp.Get(field.HelpKey);
		if (entry != null)
		{
			_ = _app.Sheets.ShowAsync(new FieldHelpSheetViewModel(_app, entry, field.Label));
		}
	}
}
