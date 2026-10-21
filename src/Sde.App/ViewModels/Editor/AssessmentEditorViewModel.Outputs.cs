using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.ViewModels.Editor;

/// <summary>
/// The editor's v2.0 outputs: the ⋯ menu's Improvements and SI, Appeal and Mark letter as sent, and the cumulative
/// damage card in the result panel. None of these change FEMA's percent damaged or determination on their own.
/// </summary>
public sealed partial class AssessmentEditorViewModel
{
	private CumulativeSettings _cumulativeSettings;
	private List<AssessmentRecord> _otherEvents;

	// ───────────── Cumulative damage ─────────────

	/// <summary>True when cumulative damage is turned on in Settings (the card is hidden otherwise).</summary>
	[ObservableProperty]
	public partial bool ShowCumulative { get; private set; }

	/// <summary>"Cumulative over 10 years: 82.5%".</summary>
	[ObservableProperty]
	public partial string CumulativeTitle { get; private set; }

	/// <summary>"Meets the 50% threshold" or "Doesn't meet the 50% threshold".</summary>
	[ObservableProperty]
	public partial string CumulativeVerdict { get; private set; }

	/// <summary>Colours the card like the main result: Substantial when it meets the threshold.</summary>
	[ObservableProperty]
	public partial ResultState CumulativeState { get; private set; }

	/// <summary>How the total was worked out, one line each (for the card's "How this was worked out").</summary>
	[ObservableProperty]
	public partial IReadOnlyList<string> CumulativeLines { get; private set; } = Array.Empty<string>();

	/// <summary>The result <see cref="UpdateCumulative"/> worked out last (null while the card is hidden).</summary>
	public CumulativeResult Cumulative { get; private set; }

	/// <summary>
	/// Works out cumulative damage over the structure's assessments, using this assessment as it is on screen. The
	/// settings and the other assessments are read once per editor; called after every recalculation.
	/// </summary>
	private void UpdateCumulative()
	{
		_cumulativeSettings ??= _app.Store.GetCumulativeSettings();
		if (!_cumulativeSettings.Enabled)
		{
			ShowCumulative = false;
			Cumulative = null;
			return;
		}
		_otherEvents ??= Property.PropertyID == Guid.Empty
			? new List<AssessmentRecord>()
			: _app.Store.GetAssessmentsForProperty(Property.PropertyID).Where(a => a.AssessmentID != Assessment.AssessmentID).ToList();
		List<AssessmentRecord> history = _otherEvents.Where(a => a.PropertyID == Assessment.PropertyID).Append(Assessment).ToList();
		CumulativeResult r;
		try
		{
			r = CumulativeDamage.Evaluate(history, _cumulativeSettings);
		}
		catch (ArgumentException)
		{
			ShowCumulative = false;
			Cumulative = null;
			return;
		}
		Cumulative = r;
		CultureInfo us = CultureInfo.GetCultureInfo("en-US");
		string period = r.LookbackYears == 0 ? "all events" : r.LookbackYears == 1 ? "1 year" : r.LookbackYears + " years";
		string threshold = r.ThresholdPercent.ToString("0.##", us) + "%";
		if (r.Status == CumulativeStatus.NoEvents)
		{
			CumulativeTitle = $"Cumulative over {period}: not worked out";
			CumulativeVerdict = "Enter the date damage occurred to count this event.";
			CumulativeState = ResultState.Incomplete;
		}
		else if (r.TotalPercent is decimal total)
		{
			CumulativeTitle = $"Cumulative over {period}: {total.ToString("#,##0.0", us)}%";
			CumulativeVerdict = r.MeetsThreshold ? $"Meets the {threshold} cumulative threshold" : $"Doesn't meet the {threshold} cumulative threshold";
			CumulativeState = r.MeetsThreshold ? ResultState.Substantial : ResultState.NotSubstantial;
		}
		else
		{
			CumulativeTitle = $"Cumulative over {period}: not worked out";
			CumulativeVerdict = "This assessment needs a building value.";
			CumulativeState = ResultState.Incomplete;
		}
		List<string> lines = r.Explanation.ToList();
		foreach (CumulativeEvent e in r.Events)
		{
			string pct = e.PercentDamage is decimal p ? p.ToString("0.0", us) + "%" : "no percent";
			string cost = e.RepairCost is decimal c ? ", repairs " + c.ToString("C0", us) : "";
			lines.Add($"{e.DateDamageOccurred.ToString("MM/dd/yyyy", us)}: {pct}{cost}. {e.Note}.");
		}
		lines.AddRange(r.Skipped);
		CumulativeLines = lines;
		ShowCumulative = true;
	}

	// ───────────── Improvements, appeals, letters ─────────────

	/// <summary>⋯, then Improvements and SI: the structure's improvement projects and substantial improvement determinations.</summary>
	[RelayCommand]
	private async Task ShowImprovements()
	{
		Save();
		if (Property.PropertyID == Guid.Empty || IsDraft && _app.Store.GetProperty(Property.PropertyID) == null)
		{
			_app.Dialogs.Notify("Not started yet", "Enter something in this assessment first, then add improvement projects.");
			return;
		}
		await _app.Sheets.ShowAsync(new ImprovementsSheetViewModel(_app, Property.PropertyID, Title));
	}

	/// <summary>⋯, then Appeal: the owner's appeal and contractor bids, with what each bid would make the percent damaged.</summary>
	[RelayCommand]
	private async Task ShowAppeal()
	{
		Save();
		if (IsDraft)
		{
			_app.Dialogs.Notify("Not started yet", "Enter something in this assessment first, then record an appeal.");
			return;
		}
		await _app.Sheets.ShowAsync(new AppealSheetViewModel(_app, Assessment.AssessmentID, Title));
	}

	/// <summary>
	/// ⋯, then Mark letter as sent: marks the letters written for this assessment as sent today and sets the status to
	/// Letter sent, which locks it.
	/// </summary>
	[RelayCommand]
	private async Task MarkLetterSent()
	{
		Save();
		if (IsDraft)
		{
			_app.Dialogs.Notify("Not started yet", "Enter something in this assessment first.");
			return;
		}
		Guid id = Assessment.AssessmentID;
		if (Status?.Value == AssessmentStatus.LetterSent && _app.Store.GetLetters(id).All(l => l.SentOn != null))
		{
			_app.Dialogs.Notify("Already sent", "This assessment is already marked Letter sent.");
			return;
		}
		List<LetterLogRecord> unsent = _app.Store.GetLetters(id).Where(l => l.SentOn == null).ToList();
		string message = unsent.Count > 0
			? $"Marks the {string.Join(" and ", unsent.Select(l => (l.Kind ?? "owner").ToLowerInvariant()).Distinct())} letter written on {unsent[0].GeneratedOn:MMM d, yyyy} as sent today, and sets the status to Letter sent. That locks this assessment until someone reopens it."
			: "No letter for this assessment has been written in SDE. Mark it as sent anyway? The status becomes Letter sent, which locks this assessment until someone reopens it.";
		if (!await _app.Dialogs.ConfirmAsync("Mark the letter as sent?", message, "Mark as sent"))
		{
			return;
		}
		try
		{
			_app.Writes.Write(new[] { id }, () => _app.Store.MarkLetterSent(id));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't mark the letter as sent", ex);
		}
	}
}
