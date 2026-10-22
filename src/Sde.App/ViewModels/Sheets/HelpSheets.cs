using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Help;
using Sde.App.Services;

namespace Sde.App.ViewModels.Sheets;

/// <summary>A sheet that can open FEMA's SDE User Manual, or say where to read it when the PDF is not here.</summary>
public abstract partial class ManualSheetViewModel : SheetViewModel
{
	protected ManualSheetViewModel(AppServices app, string title)
		: base(title)
	{
		Services = app;
		ManualAvailable = app.Manual.IsAvailable;
	}

	protected AppServices Services { get; }

	/// <summary>The manual PDF was found (see <see cref="ManualService.FindPdf"/>).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ManualMissing), nameof(MissingMessage))]
	public partial bool ManualAvailable { get; protected set; }

	public bool ManualMissing => !ManualAvailable;

	/// <summary>Shown instead of the open buttons when the manual is not here.</summary>
	public string MissingMessage => ManualAvailable ? "" : ManualService.MissingMessage;

	/// <summary>Opens the manual at a page, or notes that the manual is missing.</summary>
	protected void OpenManualPage(int page) => ManualAvailable = Services.Manual.OpenPage(page);

	[RelayCommand]
	private void OpenManual() => ManualAvailable = Services.Manual.OpenFull();

	/// <summary>Opens FEMA's web page for the Substantial Damage Estimator.</summary>
	[RelayCommand]
	private void OpenFemaPage() => Services.Manual.OpenPublishedPage();
}

/// <summary>Help, then FEMA SDE User Manual, when the manual PDF is not here: says so and offers FEMA's page.</summary>
public sealed class ManualMissingSheetViewModel(AppServices app) : ManualSheetViewModel(app, "FEMA SDE User Manual")
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Medium;
}

/// <summary>The info button beside a field: what to enter and why, the manual page, and a button to open the manual there.</summary>
public sealed partial class FieldHelpSheetViewModel : ManualSheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Medium;

	public FieldHelpSheetViewModel(AppServices app, FieldHelpEntry entry, string label = null)
		: base(app, "Help: " + (label ?? entry.Label))
	{
		Entry = entry;
	}

	public FieldHelpEntry Entry { get; }

	public string Text => Entry.Text;

	/// <summary>"On the Location &amp; owner step. See the FEMA SDE User Manual, page 3-28."</summary>
	public string Where => $"On the {Entry.Step} step. See the FEMA SDE User Manual, {Entry.PageText}.";

	[RelayCommand]
	private void OpenAtPage() => OpenManualPage(Entry.Page);
}

/// <summary>Help, then SDE Help: every field's help in one searchable list.</summary>
public sealed partial class HelpIndexSheetViewModel : ManualSheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Large;

	public HelpIndexSheetViewModel(AppServices app)
		: base(app, "SDE Help")
	{
		Entries = FieldHelp.All;
		Selected = Entries.FirstOrDefault();
	}

	/// <summary>The fields whose name, step or help matches the search.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<FieldHelpEntry> Entries { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection))]
	public partial FieldHelpEntry Selected { get; set; }

	[ObservableProperty]
	public partial string Search { get; set; }

	public bool HasSelection => Selected != null;

	/// <summary>"12 fields" or "No fields match".</summary>
	public string CountText => Entries.Count == 0 ? "No fields match." : Entries.Count == 1 ? "1 field" : $"{Entries.Count} fields";

	partial void OnSearchChanged(string value)
	{
		string[] words = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Entries = FieldHelp.All
			.Where(e => words.All(w => e.Label.Contains(w, StringComparison.OrdinalIgnoreCase)
				|| e.Step.Contains(w, StringComparison.OrdinalIgnoreCase)
				|| e.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))
			.ToList();
		if (Selected == null || !Entries.Contains(Selected))
		{
			Selected = Entries.FirstOrDefault();
		}
		OnPropertyChanged(nameof(CountText));
	}

	[RelayCommand]
	private void OpenAtPage()
	{
		if (Selected != null)
		{
			OpenManualPage(Selected.Page);
		}
	}
}

/// <summary>One page of the walkthrough.</summary>
public sealed record WalkthroughPage(string Heading, string Body);

/// <summary>The walkthrough shown on first launch and from Help: a few short pages with Back and Next.</summary>
public sealed partial class WalkthroughSheetViewModel : SheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Medium;

	/// <summary>The result of the sheet when "Open the sample database" was chosen.</summary>
	public const string OpenSampleResult = "sample";

	public WalkthroughSheetViewModel()
		: base("Welcome to the Substantial Damage Estimator")
	{
	}

	public IReadOnlyList<WalkthroughPage> Pages { get; } = new[]
	{
		new WalkthroughPage("What SDE does",
			"A building is substantially damaged when the cost to repair it is 50 percent or more of its value before the damage. SDE helps a community work that out for each building after a flood or other disaster. You enter what the inspector saw, SDE does the math with FEMA's own method, and you get reports and the files FEMA's tool reads."),
		new WalkthroughPage("The Structures list",
			"Structures is home. Each row is one building, with its percent damaged once it has been assessed. Search with ⌘F, sort and filter the list, and press Return to open a building. Add a house with ⌘N or a non-residential building with ⇧⌘N. Check several rows to change one field on all of them."),
		new WalkthroughPage("Eight steps and a live result",
			"Each assessment has eight steps: Location and owner, Building, Inspection, Flood zone, Cost and depreciation, Damage by element, Result, and Photos and notes. The result updates as you type, and anything missing is listed under Needs attention. Your work saves itself."),
		new WalkthroughPage("Import, export and FEMA packages",
			"Import SDE Data (⌘I) brings in a folder exported from FEMA's SDE. Export All to SDE (⌘E) writes a folder that FEMA's tool can open. Import Inspections or Parcels reads spreadsheets from your permit or assessor system. A submission folder gathers the reports and the FEMA package in one place to send on."),
		new WalkthroughPage("Where to find help",
			"The ? beside a field says what to enter and which page of FEMA's SDE User Manual covers it. Help, then SDE Help, lists all of them and can be searched. Help, then Open Sample Database, gives you made-up buildings to practice on without touching your own data. You can see this walkthrough again from the Help menu."),
	};

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Current), nameof(CanGoBack), nameof(IsLast), nameof(NextText), nameof(PositionText))]
	[NotifyCanExecuteChangedFor(nameof(BackCommand))]
	public partial int PageIndex { get; private set; }

	public WalkthroughPage Current => Pages[PageIndex];

	public bool CanGoBack => PageIndex > 0;

	public bool IsLast => PageIndex == Pages.Count - 1;

	public string NextText => IsLast ? "Done" : "Next";

	/// <summary>"Page 2 of 5".</summary>
	public string PositionText => $"Page {PageIndex + 1} of {Pages.Count}";

	[RelayCommand(CanExecute = nameof(CanGoBack))]
	private void Back() => PageIndex--;

	/// <summary>Next, or Done on the last page.</summary>
	[RelayCommand]
	private void Next()
	{
		if (IsLast)
		{
			Close(null);
		}
		else
		{
			PageIndex++;
		}
	}

	/// <summary>Closes the walkthrough and asks for the sample database to be opened.</summary>
	[RelayCommand]
	private void OpenSample() => Close(OpenSampleResult);
}
