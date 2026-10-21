namespace Sde.App.Services;

/// <summary>
/// Asks before anything built from the sample database leaves the app, so made-up structures are not sent to FEMA or a
/// state by mistake.
/// </summary>
public static class SampleWarning
{
	/// <summary>
	/// True when it is fine to carry on: the open database is a real one, or the person confirmed. <paramref name="action"/>
	/// finishes "Writing …": for example "an export for FEMA SDE".
	/// </summary>
	public static async Task<bool> ConfirmAsync(AppServices app, string action)
	{
		if (!app.IsSample)
		{
			return true;
		}
		return await app.Dialogs.ConfirmAsync(
			"This is the sample database",
			$"Its structures and people are made up. Writing {action} from it is only useful for practice, and it should not be sent to FEMA or anyone else as real data.",
			"Write It Anyway");
	}
}
