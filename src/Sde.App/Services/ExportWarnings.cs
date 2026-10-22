using Sde.App.ViewModels.Sheets;
using Sde.Core.Quality;
using Sde.Core.Records;

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

	/// <summary>
	/// When the assessments (null = all) have open quality findings of error severity, says how many and offers to
	/// show them on the Review page (<paramref name="showOnReview"/> gets the assessments that have them) or to export
	/// anyway. True to go ahead (including when there are none).
	/// </summary>
	public static async Task<bool> ConfirmQaAsync(AppServices app, IReadOnlyCollection<Guid> assessmentIds, Action<IReadOnlyCollection<Guid>> showOnReview)
	{
		List<QaFinding> errors;
		try
		{
			errors = QualityChecks.OpenErrors(app.Store, assessmentIds);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// The checks are advice: an export is never blocked because they couldn't run.
			AppLog.Write("Couldn't run the quality checks before an export", ex);
			return true;
		}
		if (errors.Count == 0)
		{
			return true;
		}
		List<Guid> withErrors = errors.Where(f => f.AssessmentId != null).Select(f => f.AssessmentId!.Value).Distinct().ToList();
		List<string> examples = errors.Take(3).Select(f =>
		{
			PropertyRecord p = f.PropertyId is Guid id ? app.Store.GetProperty(id) : null;
			return (p == null ? "" : QaRules.Address(p, app.Store.Reference) + ": ") + f.Message;
		}).ToList();
		if (errors.Count > examples.Count)
		{
			examples.Add($"And {errors.Count - examples.Count} more.");
		}
		QaExportChoice choice = await app.Sheets.ShowAsync<QaExportChoice>(new QaBeforeExportSheetViewModel(errors.Count, Math.Max(1, withErrors.Count), examples));
		if (choice == QaExportChoice.Review)
		{
			showOnReview?.Invoke(withErrors);
		}
		return choice == QaExportChoice.ExportAnyway;
	}
}
