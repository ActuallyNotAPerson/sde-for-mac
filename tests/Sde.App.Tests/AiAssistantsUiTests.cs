using Avalonia.Headless.XUnit;
using Sde.App.ViewModels;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Mcp;

namespace Sde.App.Tests;

/// <summary>
/// Settings › AI assistants (v2.5, M9): the switch that lets AI assistants edit, the setup to copy into Claude Code or
/// Claude Desktop, and the assistants' recent changes with Revert. Writes the panel's screenshot with a change listed.
/// </summary>
public class AiAssistantsUiTests
{
	[AvaloniaFact]
	public async Task Switch_Setup_AndRevertingAnAiChange()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Main.Settings.Show(SettingsCategory.AiAssistants);
		ui.Pump();
		AiAssistantsViewModel ai = ui.Main.Settings.AiAssistants;

		// Off by default; the switch is the database's setting the server reads on every edit.
		Assert.False(ai.AllowEdits);
		Assert.True(ai.HasNoChanges);
		ai.AllowEdits = true;
		Assert.True(AiAssistantSettings.AllowEdits(ui.Store));

		// The setup names this database and the installed program.
		await ai.CopyClaudeCodeCommand.ExecuteAsync(null);
		await ai.CopyClaudeDesktopCommand.ExecuteAsync(null);
		Assert.Equal(2, ui.Dialogs.Copied.Count);
		Assert.StartsWith("claude mcp add sde -- \"/Applications/SDE.app/Contents/MacOS/SDE\" mcp --db \"", ui.Dialogs.Copied[0]);
		Assert.Contains(ui.Store.Database.FilePath, ui.Dialogs.Copied[0]);
		Assert.Contains("\"mcpServers\"", ui.Dialogs.Copied[1]);
		Assert.False(ai.HasTranslocationWarning);

		// An assistant changes an inspector's name (as the server records it), and the panel lists it.
		AssessmentFile river = ui.Store.LoadAllAssessments().First(f => f.Property.StreetNumber == "123");
		Guid id = river.Assessment.AssessmentID;
		string before = river.Assessment.InspectorName;
		SaveContext ctx = new SaveContext { Via = SaveVia.AiAssistant, BatchId = Guid.NewGuid().ToString("N"), User = "claude-code (AI)" };
		ui.Store.Apply(id, f => f.Assessment.InspectorName = "Inspector Q", ctx);
		ui.Main.Settings.Show(SettingsCategory.General);
		ui.Main.Settings.Show(SettingsCategory.AiAssistants);
		ui.Pump();
		AiChangeRowViewModel row = Assert.Single(ai.Changes);
		Assert.Contains("Inspector", row.Summary);
		Assert.StartsWith("claude-code (AI)", row.Detail);
		Assert.True(row.CanRevert);
		Assert.StartsWith("Revert puts back the values", row.RevertNote);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("31f-settings-ai-changes");

		// Revert puts the value back, says so, and the row stays marked as reverted.
		await row.RevertCommand.ExecuteAsync(null);
		Assert.Equal(before, ui.Store.LoadAssessment(id).Assessment.InspectorName);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Change reverted: Put back 1 value."));
		Assert.True(row.IsReverted);
		ui.Main.Settings.Show(SettingsCategory.General);
		ui.Main.Settings.Show(SettingsCategory.AiAssistants);
		Assert.True(Assert.Single(ai.Changes).IsReverted);
	}
}
