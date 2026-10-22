namespace Sde.App.ViewModels;

/// <summary>Settings, then AI assistants (see <see cref="AiAssistantsViewModel"/>).</summary>
public sealed partial class SettingsViewModel
{
	private AiAssistantsViewModel _aiAssistants;

	/// <summary>Whether AI assistants may change this database, how to connect one, and what they changed.</summary>
	public AiAssistantsViewModel AiAssistants => _aiAssistants ??= new AiAssistantsViewModel(_app);
}
