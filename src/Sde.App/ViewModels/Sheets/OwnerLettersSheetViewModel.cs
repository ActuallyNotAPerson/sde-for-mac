using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Reports.Letters;

namespace Sde.App.ViewModels.Sheets;

/// <summary>Who the letters come from. Remembered per database (MacSetting "Letters.Sender").</summary>
public sealed class LetterSender
{
	public const string SettingKey = "Letters.Sender";

	public string CommunityName { get; set; }

	public string OfficeName { get; set; }

	public string OfficeAddress { get; set; }

	public string OfficePhone { get; set; }

	public string OfficeEmail { get; set; }

	public string OfficialName { get; set; }

	public string OfficialTitle { get; set; }
}

/// <summary>One kind of letter in the batch (substantial damage, or not), how many, and the template it uses.</summary>
public sealed partial class LetterKindRowViewModel : ObservableObject
{
	public LetterKindRowViewModel(string kind, int count, IReadOnlyList<string> templates)
	{
		Kind = kind;
		Count = count;
		Templates = templates;
		string starter = LetterTemplates.FileName(kind);
		Template = templates.FirstOrDefault(t => string.Equals(t, starter, StringComparison.OrdinalIgnoreCase));
	}

	public string Kind { get; }

	public int Count { get; }

	/// <summary>"Substantial damage (3 letters)".</summary>
	public string Label => $"{Kind} ({Count} letter{(Count == 1 ? "" : "s")})";

	public string AccessibleName => "Template for " + Kind.ToLowerInvariant() + " letters";

	public IReadOnlyList<string> Templates { get; }

	/// <summary>The template file name: the matching starter template unless someone chose another.</summary>
	[ObservableProperty]
	public partial string Template { get; set; }
}

/// <summary>
/// Structures, Export, then Owner letters: one Word letter per checked (or shown) assessment, from the template for
/// its kind (chosen automatically from FEMA's result, and changeable here). The letters go into a new folder; each is
/// recorded in the letter log. Afterwards, "Mark as sent" sets the assessments to Letter sent, which locks them.
/// </summary>
public sealed partial class OwnerLettersSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly List<(AssessmentFile File, string Kind)> _letters = new();
	private List<Guid> _written = new();

	public OwnerLettersSheetViewModel(AppServices app, IReadOnlyList<Guid> assessmentIds)
		: base("Owner letters")
	{
		_app = app;
		Dictionary<Guid, int> order = assessmentIds.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
		List<string> leftOut = new();
		foreach (AssessmentFile f in app.Store.LoadAssessments(assessmentIds).OrderBy(f => order[f.Assessment.AssessmentID]))
		{
			string kind = LetterKinds.For(f.Assessment);
			if (kind == null)
			{
				leftOut.Add(AssessmentEditorViewModel.Address(f.Property, app.Store.Reference, "(no address)"));
			}
			else
			{
				_letters.Add((f, kind));
			}
		}
		LeftOutText = leftOut.Count == 0 ? null
			: $"Left out because there's no result yet (or damage is undetermined): {string.Join(", ", leftOut.Take(8))}{(leftOut.Count > 8 ? $" and {leftOut.Count - 8} more" : "")}.";

		LetterSender s = app.Store.GetSetting<LetterSender>(LetterSender.SettingKey) ?? new LetterSender();
		CommunityName = s.CommunityName;
		OfficeName = s.OfficeName;
		OfficeAddress = s.OfficeAddress;
		OfficePhone = s.OfficePhone;
		OfficeEmail = s.OfficeEmail;
		OfficialName = s.OfficialName;
		OfficialTitle = s.OfficialTitle;
		LoadKinds();
	}

	public string TemplatesFolder => LetterTemplateLocation.Folder(_app);

	public int LetterCount => _letters.Count;

	/// <summary>"4 letters for the checked structures".</summary>
	public string CountText => _letters.Count == 0 ? "None of these assessments has a result to write to the owner about." : $"{_letters.Count} letter{(_letters.Count == 1 ? "" : "s")}, one per assessment:";

	public string LeftOutText { get; }

	public ObservableCollection<LetterKindRowViewModel> Kinds { get; } = new();

	public bool HasTemplates => LetterTemplateLocation.Templates(TemplatesFolder).Count > 0;

	private void LoadKinds()
	{
		IReadOnlyList<string> templates = LetterTemplateLocation.Templates(TemplatesFolder);
		Kinds.Clear();
		foreach (IGrouping<string, (AssessmentFile File, string Kind)> g in _letters.GroupBy(l => l.Kind))
		{
			Kinds.Add(new LetterKindRowViewModel(g.Key, g.Count(), templates));
		}
		OnPropertyChanged(nameof(HasTemplates));
	}

	/// <summary>Writes the starter templates (never over ones already there) and picks them.</summary>
	[RelayCommand]
	private void CreateStarterTemplates()
	{
		try
		{
			LetterTemplates.WriteStarterTemplates(TemplatesFolder);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't create the starter templates", ex);
			return;
		}
		LoadKinds();
	}

	[RelayCommand]
	private void ShowTemplatesFolder()
	{
		Directory.CreateDirectory(TemplatesFolder);
		_app.Dialogs.Reveal(TemplatesFolder);
	}

	// ───────────── Sender ─────────────

	[ObservableProperty]
	public partial string CommunityName { get; set; }

	[ObservableProperty]
	public partial string OfficeName { get; set; }

	[ObservableProperty]
	public partial string OfficeAddress { get; set; }

	[ObservableProperty]
	public partial string OfficePhone { get; set; }

	[ObservableProperty]
	public partial string OfficeEmail { get; set; }

	[ObservableProperty]
	public partial string OfficialName { get; set; }

	[ObservableProperty]
	public partial string OfficialTitle { get; set; }

	// ───────────── Writing ─────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasWritten))]
	public partial string ResultText { get; private set; }

	/// <summary>Placeholders in the templates that aren't fields (left in the letters as typed).</summary>
	[ObservableProperty]
	public partial string WarningText { get; private set; }

	public bool HasWritten => _written.Count > 0;

	[ObservableProperty]
	public partial bool IsMarkedSent { get; private set; }

	/// <summary>Chooses a folder and writes the letters into a new "Letters yyyy-MM-dd HHmm" folder there, then shows it.</summary>
	[RelayCommand]
	public async Task Write()
	{
		if (_letters.Count == 0)
		{
			_app.Dialogs.Notify("No letters to write", "Check structures that have a result (substantially damaged or not), then try again.");
			return;
		}
		LetterKindRowViewModel missing = Kinds.FirstOrDefault(k => string.IsNullOrEmpty(k.Template));
		if (missing != null)
		{
			_app.Dialogs.Notify("Choose a template", $"Choose a template for the {missing.Kind.ToLowerInvariant()} letters, or create the starter templates.");
			return;
		}
		LetterSender sender = new LetterSender
		{
			CommunityName = Blank(CommunityName), OfficeName = Blank(OfficeName), OfficeAddress = Blank(OfficeAddress), OfficePhone = Blank(OfficePhone),
			OfficeEmail = Blank(OfficeEmail), OfficialName = Blank(OfficialName), OfficialTitle = Blank(OfficialTitle),
		};
		_app.Store.SetSetting(LetterSender.SettingKey, sender);

		string parent = await _app.Dialogs.PickFolderAsync("Choose where to put the letters");
		if (parent == null)
		{
			return;
		}
		Dictionary<string, string> templateFor = Kinds.ToDictionary(k => k.Kind, k => Path.Combine(TemplatesFolder, k.Template));
		Dictionary<Guid, AssessmentStatusRecord> statuses = _app.Store.GetStatuses();
		List<LetterItem> items = _letters.Select(l => new LetterItem
		{
			Address = AssessmentEditorViewModel.Address(l.File.Property, _app.Store.Reference, "No address"),
			Kind = l.Kind,
			AssessmentID = l.File.Assessment.AssessmentID,
			PropertyID = l.File.Property.PropertyID,
			Fields = LetterFields.For(l.File, _app.Store.Reference, new LetterContext
			{
				CommunityName = sender.CommunityName, OfficeName = sender.OfficeName, OfficeAddress = sender.OfficeAddress, OfficePhone = sender.OfficePhone,
				OfficeEmail = sender.OfficeEmail, OfficialName = sender.OfficialName, OfficialTitle = sender.OfficialTitle,
				Status = AssessmentStatuses.Label(statuses.TryGetValue(l.File.Assessment.AssessmentID, out AssessmentStatusRecord st) ? st.Value : AssessmentStatus.Draft),
			}),
		}).ToList();

		JobOutcome<LetterBatchResult> outcome = await _app.Jobs.RunAsync("Writing owner letters", (progress, ct) =>
			LetterBatch.Write(items, item => templateFor[item.Kind], parent, new LetterProgressAdapter(progress), ct), "Couldn't write the letters");
		if (!outcome.Completed)
		{
			if (outcome.Kind == JobOutcomeKind.Stopped)
			{
				ResultText = "Stopped. The partly written folder was removed.";
			}
			return;
		}
		LetterBatchResult result = outcome.Value;
		try
		{
			_app.Store.AddLetters(result.Files.Select(f => new LetterLogRecord
			{
				AssessmentID = f.Item.AssessmentID!.Value,
				Kind = f.Item.Kind,
				Template = Path.GetFileName(f.Template),
				FileName = f.Path,
			}).ToList());
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("The letters were written, but couldn't be recorded in the letter log", ex);
		}
		_written = result.Files.Select(f => f.Item.AssessmentID!.Value).Distinct().ToList();
		ResultText = $"Wrote {result.Files.Count} letter{(result.Files.Count == 1 ? "" : "s")} to \"{Path.GetFileName(result.Folder)}\". Check them, print and send them, then mark them as sent.";
		WarningText = result.UnknownPlaceholders.Count == 0 ? null
			: "These placeholders aren't fields, so they were left in the letters as typed: " + string.Join(", ", result.UnknownPlaceholders.Select(p => "«" + p + "»")) + ".";
		MarkSentCommand.NotifyCanExecuteChanged();
		_app.Dialogs.Reveal(result.Folder);
	}

	/// <summary>Marks the letters just written as sent today and sets their assessments to Letter sent (locked).</summary>
	[RelayCommand(CanExecute = nameof(CanMarkSent))]
	public async Task MarkSent()
	{
		int n = _written.Count;
		if (!await _app.Dialogs.ConfirmAsync("Mark the letters as sent?",
			$"Marks {n} letter{(n == 1 ? "" : "s")} as sent today and sets {(n == 1 ? "its assessment" : "their assessments")} to Letter sent. That locks them until someone reopens them.",
			"Mark as sent"))
		{
			return;
		}
		List<string> problems = new();
		int marked = 0;
		_app.Writes.Write(_written, () =>
		{
			foreach (Guid id in _written)
			{
				try
				{
					_app.Store.MarkLetterSent(id);
					marked++;
				}
				catch (Exception ex) when (ex is not OutOfMemoryException)
				{
					string address = _letters.Where(l => l.File.Assessment.AssessmentID == id).Select(l => AssessmentEditorViewModel.Address(l.File.Property, _app.Store.Reference, "(no address)")).FirstOrDefault();
					problems.Add($"{address}: {ErrorPresenter.Describe(null, ex).Message}");
				}
			}
		});
		IsMarkedSent = marked > 0;
		ResultText = $"Marked {marked} letter{(marked == 1 ? "" : "s")} as sent." + (problems.Count == 0 ? "" : " Not marked: " + string.Join(" ", problems));
		MarkSentCommand.NotifyCanExecuteChanged();
	}

	private bool CanMarkSent() => _written.Count > 0 && !IsMarkedSent;

	private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

	/// <summary>Passes the letter batch's progress on to the job's progress bar.</summary>
	private sealed class LetterProgressAdapter(IProgress<JobProgress> job) : IProgress<LetterProgress>
	{
		public void Report(LetterProgress value) => job.Report(new JobProgress(value.Done, value.Total, value.Step));
	}
}
