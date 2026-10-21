namespace Sde.App.Services;

/// <summary>Checks made before data leaves SDE for FEMA or the state.</summary>
public static class ExportWarnings
{
	/// <summary>
	/// When some of the assessments (null = all) aren't Final or Letter sent yet, asks whether to export them anyway.
	/// True to go ahead (including when every one is Final).
	/// </summary>
	public static async Task<bool> ConfirmNotFinalAsync(AppServices app, IReadOnlyCollection<Guid> assessmentIds)
	{
		int notFinal = app.Store.CountNotFinal(assessmentIds);
		if (notFinal == 0)
		{
			return true;
		}
		string these = notFinal == 1 ? "1 of these isn't" : $"{notFinal} of these aren't";
		return await app.Dialogs.ConfirmAsync("Export assessments that aren't Final?",
			$"{these} Final yet. Export anyway? Assessments become Final through their status (Final locks them, so they can't change after they are sent).",
			"Export anyway");
	}
}
