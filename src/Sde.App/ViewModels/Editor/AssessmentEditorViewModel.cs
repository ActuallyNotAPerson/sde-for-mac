using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Fields;
using Sde.App.ViewModels.Sheets;
using Sde.Calculation.Models;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.ViewModels.Editor;

/// <summary>How the result panel colours the determination.</summary>
public enum ResultState
{
	Incomplete,
	Undetermined,
	NotSubstantial,
	Close,
	Substantial,
}

/// <summary>A custom field (3 slots, named in Settings) on this assessment.</summary>
public sealed partial class CustomFieldEntryViewModel(CustomFieldResultRecord row, string label, Action edited) : ObservableObject
{
	public CustomFieldResultRecord Row { get; } = row;

	public string Label { get; } = label;

	public string Text
	{
		get => Row.Result;
		set
		{
			if (Row.Result != value)
			{
				Row.Result = string.IsNullOrEmpty(value) ? null : value;
				OnPropertyChanged();
				edited();
			}
		}
	}
}

/// <summary>Another assessment of the same structure (shown in the header switcher).</summary>
public sealed record AssessmentChoice(Guid Id, string Label, IRelayCommand<AssessmentChoice> Open);

/// <summary>A validation issue in the "Needs attention" list; clicking it opens the step that has the field.</summary>
public sealed record IssueItem(ValidationIssue Issue, IRelayCommand<ValidationIssue> Go)
{
	public string FieldName => Issue.FieldName;
	public string Message => Issue.Message?.Trim();
	public IssueSeverity Severity => Issue.Severity;
}

/// <summary>
/// One assessment, edited as 8 steps with a live result panel. Every edit recalculates with FEMA's engine
/// immediately, re-runs SDE's validation rules, and saves automatically shortly after typing stops — replacing
/// the original's six tabs, explicit Save, modal validation dialog and "must save before printing" rule.
/// </summary>
public sealed partial class AssessmentEditorViewModel : ViewModelBase
{
	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private readonly AssessmentCalculator _calculator;
	private readonly AssessmentValidator _validator;
	private readonly DispatcherTimer _saveTimer;
	private readonly List<FieldViewModel> _fields = new();
	private bool _applying;
	private bool _shapesChanged;
	private readonly string _source;
	private bool _dirty;
	private bool _saveErrorShown;
	private bool _inBurst;
	private string _lastSaved;

	/// <param name="isDraft">A new assessment that isn't in the database yet; it is saved on the first edit.</param>
	public AssessmentEditorViewModel(AppServices app, MainViewModel main, AssessmentFile file, bool isDraft = false)
	{
		_app = app;
		_main = main;
		File = file;
		IsDraft = isDraft;
		Reference = app.Store.Reference;
		// "Permits BLD-2026-00123 / 4471" (source, record and inspection) for an imported inspection.
		_source = app.Store.GetExternalLink(file.Assessment.AssessmentID) is Sde.Data.ExternalLinkRecord link
			? $"{link.Source} {link.RecordId}" + (string.IsNullOrEmpty(link.InspectionId) ? "" : " / " + link.InspectionId)
			: null;
		_calculator = new AssessmentCalculator(Reference);
		_validator = new AssessmentValidator(Reference);
		_saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
		_saveTimer.Tick += (_, _) =>
		{
			_saveTimer.Stop();
			Save();
		};

		BuildSections();
		foreach (ShapeRecord s in app.Store.GetShapes(file.Property.PropertyID))
		{
			Shapes.Add(new ShapeViewModel(s, ShapesEdited, RemoveShape));
		}
		if (Shapes.Count == 0 && file.Property.SquareFootage is > 0)
		{
			// Square footage with no calculator shapes (e.g. from an import): show it as a typed-in total.
			Shapes.Add(new ShapeViewModel(new ShapeRecord { PropertyID = file.Property.PropertyID, ShapeType = "c", TotalSqFt = file.Property.SquareFootage }, ShapesEdited, RemoveShape));
		}
		foreach (CustomFieldRecord cf in Reference.CustomFields.OrderBy(c => c.zOrder))
		{
			CustomFieldResultRecord row = File.CustomFields.FirstOrDefault(r => r.CustomFieldID == cf.CustomFieldID);
			if (row == null)
			{
				row = new CustomFieldResultRecord { CustomFieldID = cf.CustomFieldID, zOrder = cf.zOrder, ResultType = "assessment" };
				File.CustomFields.Add(row);
			}
			CustomFields.Add(new CustomFieldEntryViewModel(row, cf.CustomField, Edited));
		}
		LoadPhotos();
		LoadOtherAssessments();

		List<NoteRecord> snippets = app.Store.GetNotes();
		foreach (FieldViewModel f in _fields)
		{
			f.Load();
			f.Changed += OnFieldChanged;
			f.HelpHandler = ShowFieldHelp;
			if (f is TextFieldViewModel { IsMultiline: true } text)
			{
				text.Snippets = snippets;
			}
		}
		Recalculate();
		SelectedSection = Sections[0];
		SaveState = "Saved";
		_lastSaved = UndoService.Snapshot(File);
		LoadStatus();
		InitFieldData();
	}

	public AssessmentFile File { get; }

	/// <summary>True until a new assessment's first edit is saved: closing it before then leaves nothing behind.</summary>
	[ObservableProperty]
	public partial bool IsDraft { get; private set; }

	/// <summary>Raised to move keyboard focus to a field (after "Needs attention" is clicked); the view scrolls to it.</summary>
	public event Action<FieldViewModel> FocusFieldRequested;

	public ReferenceData Reference { get; }

	public PropertyRecord Property => File.Property;

	public AssessmentRecord Assessment => File.Assessment;

	public ObservableCollection<SectionViewModel> Sections { get; } = new();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoNext), nameof(NextTitle))]
	public partial SectionViewModel SelectedSection { get; set; }

	public bool CanGoBack => SelectedSection != null && Sections.IndexOf(SelectedSection) > 0;

	public bool CanGoNext => SelectedSection != null && Sections.IndexOf(SelectedSection) < Sections.Count - 1;

	public string NextTitle => CanGoNext ? Sections[Sections.IndexOf(SelectedSection) + 1].Title : null;

	public ObservableCollection<ElementRowViewModel> Elements { get; } = new();

	public ObservableCollection<CostAdjustmentRowViewModel> CostAdjustments { get; } = new();

	public ObservableCollection<AdditionalAdjustmentRowViewModel> AdditionalAdjustments { get; } = new();

	public ObservableCollection<ShapeViewModel> Shapes { get; } = new();

	public ObservableCollection<PhotoViewModel> Photos { get; } = new();

	public ObservableCollection<CustomFieldEntryViewModel> CustomFields { get; } = new();

	public ObservableCollection<IssueItem> Issues { get; } = new();

	public ObservableCollection<AssessmentChoice> OtherAssessments { get; } = new();

	[ObservableProperty]
	public partial string SaveState { get; set; }

	[ObservableProperty]
	public partial string Title { get; set; }

	[ObservableProperty]
	public partial string Subtitle { get; set; }

	// ───────────── Result panel ─────────────

	[ObservableProperty]
	public partial ResultState State { get; set; }

	[ObservableProperty]
	public partial string PercentText { get; set; }

	[ObservableProperty]
	public partial string Determination { get; set; }

	[ObservableProperty]
	public partial string StateHint { get; set; }

	[ObservableProperty]
	public partial string ValueBasisName { get; set; }

	[ObservableProperty]
	public partial decimal? ValueBasisAmount { get; set; }

	[ObservableProperty]
	public partial string RepairBasisName { get; set; }

	[ObservableProperty]
	public partial decimal? RepairBasisAmount { get; set; }

	[ObservableProperty]
	public partial decimal? AdjustedTaxValue { get; set; }

	[ObservableProperty]
	public partial int RequiredIssueCount { get; set; }

	[ObservableProperty]
	public partial int OtherIssueCount { get; set; }

	[ObservableProperty]
	public partial int SuggestionCount { get; set; }

	public decimal? Cost => Raw?.Cost is decimal c ? AssessmentCalculator.Money(c) : null;

	/// <summary>Square footage from the calculator shapes (or typed directly).</summary>
	public decimal? SquareFootage => Property.SquareFootage;

	public bool IsResidential => Property.StructureTypeID == 1;

	public bool IsUndetermined => Assessment.PhysicalDamage;

	public DataInputOutputModel Raw { get; private set; }

	public bool IsValueAcv => Assessment.ValueOfBuildingID == LookupIds.ValueComputedAcv;
	public bool IsValueAppraisal => Assessment.ValueOfBuildingID == LookupIds.ValueMarketAppraisal;
	public bool IsValueTax => Assessment.ValueOfBuildingID == LookupIds.ValueAdjustedTax;
	public bool IsRepairComputed => Assessment.CostOfRepairsID == LookupIds.RepairComputedDamages;
	public bool IsRepairContractor => Assessment.CostOfRepairsID == LookupIds.RepairContractorEstimate;
	public bool IsRepairCommunity => Assessment.CostOfRepairsID == LookupIds.RepairCommunityEstimate;

	[RelayCommand]
	private void SetValueBasis(string id) => SetBasis(() => Assessment.ValueOfBuildingID = int.Parse(id));

	[RelayCommand]
	private void SetRepairBasis(string id) => SetBasis(() => Assessment.CostOfRepairsID = int.Parse(id));

	private void SetBasis(Action apply)
	{
		apply();
		Edited();
	}

	// ───────────── Building the steps ─────────────

	private void BuildSections()
	{
		ReferenceData r = Reference;
		Func<object> P = () => File.Property;
		Func<object> A = () => File.Assessment;
		Type pt = typeof(PropertyRecord);
		Type at = typeof(AssessmentRecord);

		IReadOnlyList<ChoiceOption> Lookup(int type) => r.OfType(type).Select(l => new ChoiceOption(l.LookupID, l.LookupFieldName)).ToList();
		IReadOnlyList<ChoiceOption> States() => r.States.OrderBy(s => s.State).Select(s => new ChoiceOption(s.StateID, s.State)).ToList();
		T Track<T>(T f)
			where T : FieldViewModel
		{
			_fields.Add(f);
			return f;
		}
		TextFieldViewModel PText(string label, string member, bool wide = false) => Track(new TextFieldViewModel(label, P, pt, member) { IsWide = wide });
		TextFieldViewModel AText(string label, string member, bool wide = false) => Track(new TextFieldViewModel(label, A, at, member) { IsWide = wide });
		ChoiceFieldViewModel PChoice(string label, string member, Func<IReadOnlyList<ChoiceOption>> options) => Track(new ChoiceFieldViewModel(label, P, pt, member, options));
		ChoiceFieldViewModel AChoice(string label, string member, Func<IReadOnlyList<ChoiceOption>> options) => Track(new ChoiceFieldViewModel(label, A, at, member, options));

		bool Flood() => Assessment.CauseofDamageID is LookupIds.CauseFlood or LookupIds.CauseFloodAndWind;

		// 1. Location & owner
		SectionViewModel location = AddSection(new SectionViewModel(this, "Location & owner", "Where is the building and who owns it?", "⌂"));
		location.Groups.Add(new FieldGroupViewModel("Structure address", "Shared by every assessment of this building.", new FieldViewModel[]
		{
			PText("Street number", nameof(PropertyRecord.StreetNumber)),
			PText("Street name", nameof(PropertyRecord.StreetName)),
			PChoice("Street suffix", nameof(PropertyRecord.StreetSuffixID), () => Lookup(LookupTypes.StreetSuffix)),
			PText("City", nameof(PropertyRecord.City)),
			PChoice("State", nameof(PropertyRecord.StateID), States),
			PChoice("County / parish", nameof(PropertyRecord.CountyID), () => r.CountiesIn(Property.StateID).Select(c => new ChoiceOption(c.CountyID, c.County)).ToList()),
			PText("ZIP code", nameof(PropertyRecord.ZipCode)),
		}));
		location.Groups.Add(new FieldGroupViewModel("Owner", null, new FieldViewModel[]
		{
			PText("First name", nameof(PropertyRecord.OwnerFirstName)),
			PText("Last name", nameof(PropertyRecord.OwnerLastName)),
			PText("Phone", nameof(PropertyRecord.PhoneNumber)),
			PText("Cell phone", nameof(PropertyRecord.CellPhone)),
		}));
		location.Groups.Add(new FieldGroupViewModel("Mailing address", "Where letters about this determination should go.", new FieldViewModel[]
		{
			PText("First name", nameof(PropertyRecord.MailingOwnerFirstName)),
			PText("Last name", nameof(PropertyRecord.MailingOwnerLastName)),
			PText("Care of", nameof(PropertyRecord.MailingCareOf)),
			PText("Street number", nameof(PropertyRecord.MailingStreetNumber)),
			PText("Street name", nameof(PropertyRecord.MailingStreetName)),
			PChoice("Street suffix", nameof(PropertyRecord.MailingStreetSuffixID), () => Lookup(LookupTypes.StreetSuffix)),
			PText("City", nameof(PropertyRecord.MailingCity)),
			PChoice("State", nameof(PropertyRecord.MailingStateID), States),
			PChoice("County / parish", nameof(PropertyRecord.MailingCountyID), () => r.CountiesIn(Property.MailingStateID).Select(c => new ChoiceOption(c.CountyID, c.County)).ToList()),
			PText("ZIP code", nameof(PropertyRecord.MailingZipCode)),
			PText("Phone", nameof(PropertyRecord.MailingPhoneNumber)),
		}) { ActionText = "Same as structure", ActionCommand = CopyAddressToMailingCommand });
		location.Groups.Add(new FieldGroupViewModel("Parcel & coordinates", null, new FieldViewModel[]
		{
			PText("Parcel number", nameof(PropertyRecord.ParcelNumber)),
			PText("Lot number", nameof(PropertyRecord.LotNumber)),
			PText("Subdivision", nameof(PropertyRecord.Subdivision)),
			Track(new NumberFieldViewModel("Latitude", P, pt, nameof(PropertyRecord.Latitude)) { Format = "0.######", Minimum = -90, Maximum = 90, Placeholder = "39.103118" }),
			Track(new NumberFieldViewModel("Longitude", P, pt, nameof(PropertyRecord.Longitude)) { Format = "0.######", Minimum = -180, Maximum = 180, Placeholder = "-84.512020" }),
		}));

		// 2. Building
		SectionViewModel building = AddSection(new SectionViewModel(this, "Building", "What kind of building is it?", "▤"));
		bool Res() => Property.StructureTypeID == 1;
		bool NonRes() => Property.StructureTypeID == 2;
		building.Groups.Add(new FieldGroupViewModel("Type", "These choices pick FEMA's cost breakdown for the building.", new FieldViewModel[]
		{
			PChoice("Structure type", nameof(PropertyRecord.StructureTypeID), () => new[] { new ChoiceOption(1, "Residential"), new ChoiceOption(2, "Non-residential") }),
			PChoice("Number of stories", nameof(PropertyRecord.StoryTypeID), () => r.OfType(LookupTypes.Stories)
				.Where(l => (l.LookupValue1 ?? "").Contains("non", StringComparison.OrdinalIgnoreCase) == !Res())
				.Select(l => new ChoiceOption(l.LookupID, l.LookupFieldName)).ToList()),
			Track(new ChoiceFieldViewModel("Residence type", P, pt, nameof(PropertyRecord.ResidenceTypeID), () => Lookup(LookupTypes.ResidenceType)) { VisibleWhen = Res }),
			Track(new ChoiceFieldViewModel("Foundation", P, pt, nameof(PropertyRecord.FoundationTypeID), () => Lookup(LookupTypes.Foundation)) { VisibleWhen = Res }),
			Track(new ChoiceFieldViewModel("Superstructure", P, pt, nameof(PropertyRecord.SuperStructureTypeID), () => Lookup(LookupTypes.Superstructure)) { VisibleWhen = Res }),
			Track(new ChoiceFieldViewModel("Roof covering", P, pt, nameof(PropertyRecord.RoofCoveringTypeID), () => Lookup(LookupTypes.RoofCovering)) { VisibleWhen = Res }),
			Track(new ChoiceFieldViewModel("Exterior finish", P, pt, nameof(PropertyRecord.ExteriorFinishTypeID), () => Lookup(LookupTypes.ExteriorFinish)) { VisibleWhen = Res }),
			Track(new ChoiceFieldViewModel("Heating & cooling", P, pt, nameof(PropertyRecord.HVACTypeID), () => Lookup(LookupTypes.Hvac)) { VisibleWhen = Res }),
			Track(new ChoiceFieldViewModel("Structure use", P, pt, nameof(PropertyRecord.StructureUseID), () => r.OfType(LookupTypes.StructureUse)
				.Where(l => (l.LookupValue1 ?? "").Contains(Property.StoryTypeID?.ToString() ?? "—"))
				.Select(l => new ChoiceOption(l.LookupID, l.LookupFieldName)).ToList())
			{ VisibleWhen = NonRes, Help = "Pick the number of stories first." }),
			Track(new ChoiceFieldViewModel("Sprinkler system", P, pt, nameof(PropertyRecord.SprinklerSystem), () => Lookup(LookupTypes.SprinklerSystem))
			{ VisibleWhen = NonRes, EnabledWhen = () => Property.StoryTypeID is 101 or 102, Help = "For 1–4 story buildings." }),
			Track(new ChoiceFieldViewModel("Conveyance (elevators)", P, pt, nameof(PropertyRecord.Conveyance), () => Lookup(LookupTypes.YesNo))
			{ VisibleWhen = NonRes, EnabledWhen = () => Property.StoryTypeID == 102, Help = "For 2–4 story buildings." }),
		}));
		building.Groups.Add(new FieldGroupViewModel("Details", null, new FieldViewModel[]
		{
			Track(new NumberFieldViewModel("Year built", P, pt, nameof(PropertyRecord.YearOfConstruction)) { Format = "0", Minimum = 0, Maximum = 2200, Placeholder = "1962" }),
			PChoice("Construction quality", nameof(PropertyRecord.StructureQualityID), () => Lookup(LookupTypes.StructureQuality)),
			Track(new TextFieldViewModel("Notes about the structure", P, pt, nameof(PropertyRecord.StructureInformation)) { IsWide = true, IsMultiline = true }),
		}));

		// 3. Inspection & damage event
		SectionViewModel inspection = AddSection(new SectionViewModel(this, "Inspection", "Who inspected, when, and what caused the damage", "✎"));
		inspection.Groups.Add(new FieldGroupViewModel("Inspection", null, new FieldViewModel[]
		{
			AText("Inspector", nameof(AssessmentRecord.InspectorName)),
			AText("Inspector phone", nameof(AssessmentRecord.InspectorPhoneNumber)),
			Track(new DateFieldViewModel("Inspection date", A, at, nameof(AssessmentRecord.DateofInspection))),
		}));
		inspection.Groups.Add(new FieldGroupViewModel("Damage event", null, new FieldViewModel[]
		{
			Track(new DateFieldViewModel("Date damage occurred", A, at, nameof(AssessmentRecord.DateDamageOccurred))),
			AChoice("Cause of damage", nameof(AssessmentRecord.CauseofDamageID), () => Lookup(LookupTypes.CauseOfDamage)),
			Track(new TextFieldViewModel("Describe the cause", A, at, nameof(AssessmentRecord.CauseOfDamageDescription)) { IsWide = true, VisibleWhen = () => Assessment.CauseofDamageID == LookupIds.CauseOther }),
			Track(new NumberFieldViewModel("Flood duration", A, at, nameof(AssessmentRecord.DurationOfFlood)) { Format = "0", Minimum = 0, VisibleWhen = Flood }),
			Track(new ChoiceFieldViewModel("Duration units", A, at, nameof(AssessmentRecord.DurationOfFloodUnitsID), () => Lookup(LookupTypes.DurationUnits)) { VisibleWhen = Flood }),
			Track(new NumberFieldViewModel("Flood elevation", A, at, nameof(AssessmentRecord.DepthOfFlood)) { Format = "0.##", Suffix = "ft", VisibleWhen = Flood }),
			Track(new NumberFieldViewModel("Depth above lowest floor", A, at, nameof(AssessmentRecord.DepthOfFloodAboveFFE)) { Format = "0.##", Suffix = "ft", VisibleWhen = Flood }),
		}));
		inspection.Groups.Add(new FieldGroupViewModel("Damage could not be assessed", "Use this when there is no inspection result (no damage, vacant lot, access refused…). The structure is reported with 0% and the reason.", new FieldViewModel[]
		{
			Track(new BoolFieldViewModel("Damage undetermined", A, at, nameof(AssessmentRecord.PhysicalDamage))),
			Track(new ChoiceFieldViewModel("Reason", A, at, nameof(AssessmentRecord.UndeterminedDamageTypeID), () => Lookup(LookupTypes.UndeterminedDamageType)) { VisibleWhen = () => Assessment.PhysicalDamage }),
		}));

		// 4. Flood zone
		SectionViewModel nfip = AddSection(new SectionViewModel(this, "Flood zone", "NFIP community and FIRM information", "≈"));
		nfip.Groups.Add(new FieldGroupViewModel("NFIP community", null, new FieldViewModel[]
		{
			Track(new TextFieldViewModel("NFIP community ID", P, pt, nameof(PropertyRecord.NFIPCID)) { MaxLength = 6, Placeholder = "6 digits" }),
			PText("Community name", nameof(PropertyRecord.NFIPCommunityName)),
			Track(new TextFieldViewModel("Community information", P, pt, nameof(PropertyRecord.CommunityInformation)) { IsWide = true, IsMultiline = true }),
		}));
		nfip.Groups.Add(new FieldGroupViewModel("Flood Insurance Rate Map", null, new FieldViewModel[]
		{
			PText("FIRM panel number", nameof(PropertyRecord.FIRMPanelNumber)),
			Track(new TextFieldViewModel("Panel suffix", P, pt, nameof(PropertyRecord.PanelSuffix)) { MaxLength = 1 }),
			Track(new DateFieldViewModel("FIRM panel date", P, pt, nameof(PropertyRecord.FIRMPanelDate))),
			PChoice("FIRM zone", nameof(PropertyRecord.FIRMZone), () => Lookup(LookupTypes.FirmZone)),
			Track(new NumberFieldViewModel("Base flood elevation (BFE)", P, pt, nameof(PropertyRecord.BFE)) { Format = "0.##", Suffix = "ft" }),
			PChoice("Regulatory floodway", nameof(PropertyRecord.RegulatoryFloodway), () => Lookup(LookupTypes.RegulatoryFloodway)),
			Track(new NumberFieldViewModel("Elevation of lowest floor", P, pt, nameof(PropertyRecord.FirstFloorElevation)) { Format = "0.##", Suffix = "ft" }),
			PText("Datum", nameof(PropertyRecord.Datum)),
		}));

		// 5. Cost
		CostSectionViewModel cost = (CostSectionViewModel)AddSection(new CostSectionViewModel(this));
		cost.Basics = new FieldGroupViewModel("Base cost", "Use a cost estimating guide (e.g. RSMeans) for the base cost per square foot.", new FieldViewModel[]
		{
			Track(new NumberFieldViewModel("Base cost per sq ft", A, at, nameof(AssessmentRecord.BaseCost)) { Format = "N2", Prefix = "$", Minimum = 0 }),
			Track(new NumberFieldViewModel("Geographic adjustment", A, at, nameof(AssessmentRecord.GeographicAdjustment)) { Format = "0.###", Minimum = 0, Help = "Local cost multiplier, e.g. 0.93", Placeholder = "1.00" }),
			AText("Cost data source", nameof(AssessmentRecord.CostData)),
			Track(new DateFieldViewModel("Cost data date", A, at, nameof(AssessmentRecord.CostDataDate))),
		});
		cost.Depreciation = new FieldGroupViewModel("Depreciation", "Condition of the building before the damage.", new FieldViewModel[]
		{
			AChoice("Condition rating", nameof(AssessmentRecord.DepreciationRatingID), () => Lookup(LookupTypes.DepreciationRating)),
			Track(new NumberFieldViewModel("Depreciation", A, at, nameof(AssessmentRecord.DepreciationValue)) { Format = "0.0", Suffix = "%", Minimum = 0, Maximum = 100, EnabledWhen = () => Assessment.DepreciationRatingID == LookupIds.DepreciationOther, Help = "Filled in from FEMA's table; editable for “Other”." }),
			Track(new TextFieldViewModel("Why “Other”?", A, at, nameof(AssessmentRecord.DepreciationExplanation)) { IsWide = true, IsMultiline = true, VisibleWhen = () => Assessment.DepreciationRatingID == LookupIds.DepreciationOther }),
		});

		// 6. Damage
		AddSection(new DamageSectionViewModel(this));

		// 7. Result
		ResultSectionViewModel result = (ResultSectionViewModel)AddSection(new ResultSectionViewModel(this));
		result.ValueInputs = new FieldGroupViewModel(null, null, new FieldViewModel[]
		{
			Track(new NumberFieldViewModel("Professional market appraisal", A, at, nameof(AssessmentRecord.MarketAppraisal)) { Format = "N2", Prefix = "$", Minimum = 0 }),
			Track(new NumberFieldViewModel("Tax assessed value", A, at, nameof(AssessmentRecord.TaxAssessedValue)) { Format = "N2", Prefix = "$", Minimum = 0 }),
			Track(new NumberFieldViewModel("Tax factor adjustment", A, at, nameof(AssessmentRecord.FactorAdjustment)) { Format = "0.##", Minimum = 0, Help = "Multiplier that turns assessed value into market value, e.g. 1.43" }),
		});
		result.RepairInputs = new FieldGroupViewModel(null, null, new FieldViewModel[]
		{
			Track(new NumberFieldViewModel("Contractor's estimate", A, at, nameof(AssessmentRecord.ContractorEstimate)) { Format = "N2", Prefix = "$", Minimum = 0 }),
			Track(new NumberFieldViewModel("Community's estimate", A, at, nameof(AssessmentRecord.CommunityEstimate)) { Format = "N2", Prefix = "$", Minimum = 0 }),
			Track(new NumberFieldViewModel("Pre-existing improvements", A, at, nameof(AssessmentRecord.ExistingImprovementPercent)) { Format = "0.##", Suffix = "%", Minimum = 0, Help = "Recorded for the report; FEMA's calculation does not use it." }),
		});

		// 8. Photos & notes
		AddSection(new PhotosSectionViewModel(this));

		for (int i = 0; i < Sections.Count; i++)
		{
			Sections[i].Number = i + 1;
		}
	}

	private SectionViewModel AddSection(SectionViewModel s)
	{
		Sections.Add(s);
		return s;
	}

	// ───────────── Editing ─────────────

	private void OnFieldChanged(FieldViewModel field)
	{
		if (_applying)
		{
			return;
		}
		_applying = true;
		try
		{
			FieldRules.AfterChange(File, field.Member, _calculator);
			foreach (FieldViewModel f in _fields)
			{
				if (f is ChoiceFieldViewModel c)
				{
					c.RefreshOptions();
				}
				if (!ReferenceEquals(f, field))
				{
					f.Load();
				}
			}
		}
		finally
		{
			_applying = false;
		}
		Edited();
	}

	/// <summary>Called after any edit: recalculate now, save shortly.</summary>
	public void Edited()
	{
		if (!_inBurst && !IsDraft)
		{
			_app.Undo.Push(Assessment.AssessmentID, _lastSaved);
			_inBurst = true;
			RefreshUndo();
		}
		_dirty = true;
		Recalculate();
		SaveState = "Editing…";
		_saveTimer.Stop();
		_saveTimer.Start();
	}

	private void ShapesEdited()
	{
		_shapesChanged = true;
		Property.SquareFootage = Shapes.Count == 0 ? null : Shapes.Sum(s => s.Shape.TotalSqFt ?? 0m);
		OnPropertyChanged(nameof(SquareFootage));
		Edited();
	}

	/// <summary>Runs FEMA's calculation and SDE's validation on the in-memory assessment and refreshes everything shown.</summary>
	private void Recalculate()
	{
		Raw = _calculator.Recalculate(File);
		SyncRows();
		foreach (SectionViewModel s in Sections)
		{
			s.Refresh();
		}
		UpdateResultPanel();
		UpdateCumulative();
		Validate();
		Title = Address(Property, _app.Store.Reference);
		Subtitle = string.Join(" · ", new[]
		{
			Property.StructureTypeID == 1 ? Reference.LookupName(Property.ResidenceTypeID) ?? "Residential" : Reference.LookupName(Property.StructureUseID) ?? "Non-residential",
			Property.City,
			Assessment.DateofInspection is DateTime d ? "inspected " + d.ToString("MMM d, yyyy") : null,
			_source,
		}.Where(s => !string.IsNullOrEmpty(s)));
		OnPropertyChanged(nameof(Cost));
		OnPropertyChanged(nameof(IsResidential));
		OnPropertyChanged(nameof(IsUndetermined));
		OnPropertyChanged(nameof(IsValueAcv));
		OnPropertyChanged(nameof(IsValueAppraisal));
		OnPropertyChanged(nameof(IsValueTax));
		OnPropertyChanged(nameof(IsRepairComputed));
		OnPropertyChanged(nameof(IsRepairContractor));
		OnPropertyChanged(nameof(IsRepairCommunity));
		OnPropertyChanged(nameof(Assessment));
		OnPropertyChanged(nameof(Property));
		OnPropertyChanged(nameof(SquareFootage));
	}

	/// <summary>"123 River Street" (number, name and suffix, as the Structures list shows it), or <paramref name="empty"/>.</summary>
	public static string Address(PropertyRecord p, ReferenceData reference, string empty = "New structure (no address yet)")
	{
		string street = string.Join(" ", new[] { p.StreetNumber, p.StreetName, reference.LookupName(p.StreetSuffixID) }.Where(s => !string.IsNullOrWhiteSpace(s)));
		return string.IsNullOrWhiteSpace(street) ? empty : street;
	}

	/// <summary>Keeps the row lists in step with the records (rows change when the building type changes).</summary>
	private void SyncRows()
	{
		if (!Elements.Select(e => e.Row).SequenceEqual(File.ElementPercentages))
		{
			Elements.Clear();
			foreach (ElementPercentageRecord e in File.ElementPercentages)
			{
				Elements.Add(new ElementRowViewModel(e, Edited));
			}
		}
		if (!CostAdjustments.Select(c => c.Row).SequenceEqual(File.CostAdjustments))
		{
			CostAdjustments.Clear();
			foreach (CostAdjustmentResultRecord c in File.CostAdjustments)
			{
				CostAdjustments.Add(new CostAdjustmentRowViewModel(c, Reference.LookupName(c.CostAdjustmentLookupID), Edited));
			}
		}
		if (!AdditionalAdjustments.Select(c => c.Row).SequenceEqual(File.AdditionalAdjustments))
		{
			AdditionalAdjustments.Clear();
			foreach (AdditionalAdjustmentRecord a in File.AdditionalAdjustments)
			{
				AdditionalAdjustments.Add(new AdditionalAdjustmentRowViewModel(a, Edited));
			}
		}
		foreach (ElementRowViewModel e in Elements)
		{
			e.RefreshComputed();
		}
		foreach (CostAdjustmentRowViewModel c in CostAdjustments)
		{
			c.RefreshComputed();
		}
		foreach (AdditionalAdjustmentRowViewModel a in AdditionalAdjustments)
		{
			a.RefreshComputed();
		}
	}

	private void UpdateResultPanel()
	{
		AssessmentRecord a = Assessment;
		ValueBasisName = Reference.LookupName(a.ValueOfBuildingID) ?? "No value basis chosen";
		RepairBasisName = Reference.LookupName(a.CostOfRepairsID) ?? "No repair-cost basis chosen";
		AdjustedTaxValue = AssessmentCalculator.Money(Raw?.AdjTaxAssessedValue);
		ValueBasisAmount = a.ValueOfBuildingID switch
		{
			LookupIds.ValueComputedAcv => a.ComputedActualCashValue,
			LookupIds.ValueMarketAppraisal => a.MarketAppraisal,
			LookupIds.ValueAdjustedTax => AdjustedTaxValue,
			_ => null,
		};
		RepairBasisAmount = a.CostOfRepairsID switch
		{
			LookupIds.RepairComputedDamages => a.ComputedDamages,
			LookupIds.RepairContractorEstimate => a.ContractorEstimate,
			LookupIds.RepairCommunityEstimate => a.CommunityEstimate,
			_ => null,
		};
		decimal pct = a.PercentDamage ?? 0m;
		Determination = a.DamageMessage;
		if (a.PhysicalDamage)
		{
			State = ResultState.Undetermined;
			PercentText = "–";
			StateHint = "Damage undetermined. The structure is reported with this reason and 0%.";
		}
		else if (a.DamageMessage == "Data Entry Incomplete" || Raw?.PercentDamaged == null)
		{
			State = ResultState.Incomplete;
			PercentText = "–";
			StateHint = "Enter the building value: square footage, base cost and depreciation, or choose another value basis.";
		}
		else
		{
			PercentText = pct.ToString("0.0") + "%";
			State = pct >= 50m ? ResultState.Substantial : pct >= 40m ? ResultState.Close : ResultState.NotSubstantial;
			StateHint = State switch
			{
				ResultState.Substantial => "50% or more: NFIP substantial damage requirements apply.",
				ResultState.Close => "Within 10 points of the 50% threshold. Double-check the inputs.",
				_ => "Below the 50% threshold.",
			};
		}
	}

	private void Validate()
	{
		List<ValidationIssue> issues = _validator.Validate(File, _app.Store.GetPreferences().LatLong);
		Dictionary<string, ValidationIssue> byField = issues
			.GroupBy(i => (i.Model == "Property" ? "P." : "A.") + i.ControlName)
			.ToDictionary(g => g.Key, g => g.OrderBy(i => i.Severity).First());
		foreach (FieldViewModel f in _fields)
		{
			string key = (f.RecordType == typeof(PropertyRecord) ? "P." : "A.") + f.Member;
			f.Issue = f.IsVisible && byField.TryGetValue(key, out ValidationIssue issue) ? issue : null;
		}
		// FEMA's green "suggested" rules (owner/mailing details) are counted separately so they don't drown out real gaps.
		List<ValidationIssue> important = issues.Where(i => i.Severity != IssueSeverity.Suggested).ToList();
		Issues.Clear();
		foreach (ValidationIssue i in important.OrderBy(i => i.Severity).ThenBy(i => SectionIndexFor(i)))
		{
			Issues.Add(new IssueItem(i, GoToIssueCommand));
		}
		RequiredIssueCount = issues.Count(i => i.Severity == IssueSeverity.Required);
		OtherIssueCount = important.Count - RequiredIssueCount;
		SuggestionCount = issues.Count - important.Count;
		foreach (SectionViewModel s in Sections)
		{
			List<ValidationIssue> mine = important.Where(i => SectionFor(i) == s).ToList();
			s.RequiredIssues = mine.Count(i => i.Severity == IssueSeverity.Required);
			s.OtherIssues = mine.Count(i => i.Severity != IssueSeverity.Required);
			s.RefreshStatus();
		}
	}

	private SectionViewModel SectionFor(ValidationIssue issue)
	{
		Type record = issue.Model == "Property" ? typeof(PropertyRecord) : typeof(AssessmentRecord);
		FieldViewModel field = _fields.FirstOrDefault(f => f.Member == issue.ControlName && f.RecordType == record);
		return field == null ? null : Sections.FirstOrDefault(s => s.AllFields.Contains(field));
	}

	private int SectionIndexFor(ValidationIssue i) => SectionFor(i) is SectionViewModel s ? Sections.IndexOf(s) : 99;

	[RelayCommand]
	private void GoToIssue(ValidationIssue issue)
	{
		if (SectionFor(issue) is SectionViewModel s)
		{
			SelectedSection = s;
			Type record = issue.Model == "Property" ? typeof(PropertyRecord) : typeof(AssessmentRecord);
			if (_fields.FirstOrDefault(f => f.Member == issue.ControlName && f.RecordType == record) is FieldViewModel field)
			{
				FocusFieldRequested?.Invoke(field);
			}
		}
	}

	[RelayCommand]
	private void Next()
	{
		if (CanGoNext)
		{
			SelectedSection = Sections[Sections.IndexOf(SelectedSection) + 1];
		}
	}

	[RelayCommand]
	private void Back()
	{
		if (CanGoBack)
		{
			SelectedSection = Sections[Sections.IndexOf(SelectedSection) - 1];
		}
	}

	[RelayCommand]
	private void CopyAddressToMailing()
	{
		Property.MailingOwnerFirstName = Property.OwnerFirstName;
		Property.MailingOwnerLastName = Property.OwnerLastName;
		Property.MailingStreetNumber = Property.StreetNumber;
		Property.MailingStreetName = Property.StreetName;
		Property.MailingStreetSuffixID = Property.StreetSuffixID;
		Property.MailingCity = Property.City;
		Property.MailingStateID = Property.StateID;
		Property.MailingCountyID = Property.CountyID;
		Property.MailingZipCode = Property.ZipCode;
		Property.MailingPhoneNumber = Property.PhoneNumber;
		ReloadFields();
		Edited();
	}

	private void ReloadFields()
	{
		_applying = true;
		try
		{
			foreach (FieldViewModel f in _fields)
			{
				if (f is ChoiceFieldViewModel c)
				{
					c.RefreshOptions();
				}
				f.Load();
			}
		}
		finally
		{
			_applying = false;
		}
	}

	// ───────────── Square footage ─────────────

	[RelayCommand]
	private void AddShape(string type)
	{
		ShapeRecord s = new ShapeRecord { PropertyID = Property.PropertyID, ShapeType = type, StoriesA = type == "c" ? null : 1, StoriesB = type is "l" or "t" or "u" ? 1 : null, StoriesC = type == "u" ? 1 : null };
		Shapes.Add(new ShapeViewModel(s, ShapesEdited, RemoveShape));
		ShapesEdited();
	}

	private void RemoveShape(ShapeViewModel shape)
	{
		Shapes.Remove(shape);
		ShapesEdited();
	}

	// ───────────── Photos ─────────────

	private void LoadPhotos()
	{
		Photos.Clear();
		// The report order: the starred photo first, then the others in their stored order (see MovePhoto).
		foreach (AttachmentRecord a in File.Attachments.Where(a => a.Active).OrderByDescending(a => a.DefaultImage))
		{
			Photos.Add(NewPhotoVm(a));
		}
		RefreshPhotoPositions();
	}

	private PhotoViewModel NewPhotoVm(AttachmentRecord a) => new(a, _app.Store.PhotoPath(a), Edited, MakeDefaultPhoto, RemovePhoto, p => _app.Dialogs.Open(p.Path)) { Mover = MovePhoto };

	public static readonly string[] PhotoPatterns = { "*.jpg", "*.jpeg", "*.png", "*.heic", "*.gif", "*.bmp", "*.tif", "*.tiff" };

	[RelayCommand]
	private async Task ChoosePhotos()
	{
		IReadOnlyList<string> files = await _app.Dialogs.PickFilesAsync("Add photos", true, "Images", PhotoPatterns);
		await AddPhotosAsync(files);
	}

	[ObservableProperty]
	public partial bool IsAddingPhotos { get; private set; }

	/// <summary>
	/// Copies photos into the database's attachments folder and attaches them (also used by drag and drop). Converting
	/// and copying happen off the UI thread, so a batch of large phone photos doesn't freeze the window.
	/// </summary>
	public async Task AddPhotosAsync(IEnumerable<string> paths)
	{
		int added = 0;
		List<(AttachmentRecord Attachment, string Source)> addedPhotos = new();
		string temp = Path.Combine(Path.GetTempPath(), "sde-photos");
		Guid assessmentId = Assessment.AssessmentID;
		IsAddingPhotos = true;
		try
		{
			foreach (string path in paths.Where(p => System.IO.File.Exists(p)).ToList())
			{
				AttachmentRecord a;
				try
				{
					a = await Task.Run(() =>
					{
						string stored = PhotoConversion.Compatible(path, temp);
						AttachmentRecord imported = _app.Store.ImportPhoto(assessmentId, stored, Path.GetFileNameWithoutExtension(path));
						if (stored != path)
						{
							System.IO.File.Delete(stored);
						}
						return imported;
					});
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					_app.Dialogs.Notify("Photo not added", ex.Message, isError: true);
					continue;
				}
				a.DefaultImage = !File.Attachments.Any(x => x.Active);
				File.Attachments.Add(a);
				Photos.Add(NewPhotoVm(a));
				addedPhotos.Add((a, path));
				added++;
			}
		}
		finally
		{
			IsAddingPhotos = false;
		}
		if (added > 0)
		{
			RefreshPhotoPositions();
			Edited();
			await AfterPhotosAddedAsync(addedPhotos);
		}
	}

	private void MakeDefaultPhoto(PhotoViewModel photo)
	{
		foreach (PhotoViewModel p in Photos)
		{
			p.Attachment.DefaultImage = ReferenceEquals(p, photo);
			p.RefreshDefault();
		}
		MoveReportPhotoFirst(photo);
		Edited();
	}

	/// <summary>Moves a photo to Recently deleted: it stays on the assessment, inactive, until restored or emptied.</summary>
	private void RemovePhoto(PhotoViewModel photo)
	{
		photo.Attachment.Active = false;
		Photos.Remove(photo);
		RefreshPhotoPositions();
		if (photo.IsDefault)
		{
			photo.Attachment.DefaultImage = false;
			if (Photos.FirstOrDefault() is PhotoViewModel first)
			{
				first.Attachment.DefaultImage = true;
				first.RefreshDefault();
			}
		}
		Edited();
	}

	// ───────────── Saving, switching, deleting ─────────────

	/// <summary>Saves immediately (also called when leaving the editor). A draft nobody has edited isn't saved.</summary>
	[RelayCommand]
	public void Save()
	{
		_saveTimer.Stop();
		if (IsDraft && !_dirty)
		{
			return;
		}
		try
		{
			// The assessment first: a new structure gets its ID here, which the shapes need.
			_app.Store.SaveAssessment(File);
			if (_shapesChanged)
			{
				_app.Store.SaveShapes(Property.PropertyID, Shapes.Select(s => s.Shape));
				_shapesChanged = false;
			}
			SaveState = "Saved";
			_saveErrorShown = false;
			_inBurst = false;
			_lastSaved = UndoService.Snapshot(File);
			if (IsDraft)
			{
				IsDraft = false;
				LoadOtherAssessments();
				OnPropertyChanged(nameof(HasOtherAssessments));
				_app.RaiseDataChanged();
			}
		}
		catch (AssessmentLockedException ex)
		{
			SaveState = "Not saved: Final";
			if (!_saveErrorShown)
			{
				_saveErrorShown = true;
				_app.Dialogs.Notify("Not saved", ex.Message, isError: true);
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			SaveState = "Not saved";
			// Autosave runs after every pause in typing; say so once, not after every keystroke.
			if (!_saveErrorShown)
			{
				_saveErrorShown = true;
				_app.Errors.Show("Couldn't save this assessment", ex);
			}
			else
			{
				AppLog.Write("Save failed again", ex);
			}
		}
	}

	private void LoadOtherAssessments()
	{
		OtherAssessments.Clear();
		foreach (AssessmentRecord a in _app.Store.GetAssessmentsForProperty(Property.PropertyID))
		{
			string label = (a.DateofInspection?.ToString("MMM d, yyyy") ?? "No date") + (a.PercentDamage is decimal p ? $" · {p:0.0}%" : "");
			OtherAssessments.Add(new AssessmentChoice(a.AssessmentID, label, SwitchToCommand));
		}
	}

	public bool HasOtherAssessments => OtherAssessments.Count > 1;

	[RelayCommand]
	private void SwitchTo(AssessmentChoice choice)
	{
		if (choice.Id != Assessment.AssessmentID)
		{
			Save();
			_main.OpenAssessment(choice.Id);
		}
	}

	[RelayCommand]
	private void NewAssessmentForStructure()
	{
		Save();
		if (IsDraft)
		{
			return;     // this one hasn't been started yet
		}
		_main.OpenDraft(_app.Store.NewDraft(_app.Store.GetProperty(Property.PropertyID), _app.Store.GetPreferences().UseDefaults));
	}

	[RelayCommand]
	private void DeleteAssessment()
	{
		if (IsDraft)
		{
			_saveTimer.Stop();
			_dirty = false;
			_main.ShowStructures(saveEditor: false);
			return;
		}
		int count = OtherAssessments.Count;
		Save();
		_saveTimer.Stop();
		try
		{
			if (count > 1)
			{
				_app.Store.TrashAssessment(Assessment.AssessmentID);
			}
			else
			{
				_app.Store.TrashProperty(Property.PropertyID);
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't delete", ex);
			return;
		}
		string what = count > 1 ? "This assessment" : "This structure";
		_app.Dialogs.Notify("Moved to Recently deleted", $"{what} can be restored from Recently deleted for {SdeStore.KeepDays} days.");
		_app.RaiseDataChanged();
		_main.ShowStructures(saveEditor: false);
	}

	[RelayCommand]
	private void Close() => _main.ShowStructures();

	// ───────────── Status, locks and history ─────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusLabel), nameof(IsLocked), nameof(IsEditable), nameof(LockText))]
	public partial AssessmentStatusRecord Status { get; private set; }

	public string StatusLabel => AssessmentStatuses.Label(Status?.Value ?? AssessmentStatus.Draft);

	/// <summary>Final and Letter sent: the editor is read-only until reopened.</summary>
	public bool IsLocked => Status?.IsLocked == true;

	public bool IsEditable => !IsLocked;

	public string LockText => !IsLocked ? null
		: $"{StatusLabel}" + (Status.ChangedOn is DateTime d ? $" since {d:MMM d, yyyy}" : "") + (string.IsNullOrEmpty(Status.ChangedBy) ? "" : $" ({Status.ChangedBy})")
			+ ". Reopen it to make changes.";

	/// <summary>An older version of SDE (which doesn't know about locks) changed this assessment after it was locked.</summary>
	[ObservableProperty]
	public partial bool ChangedInOlderVersion { get; private set; }

	private void LoadStatus()
	{
		if (IsDraft)
		{
			Status = new AssessmentStatusRecord { AssessmentID = Assessment.AssessmentID };
			return;
		}
		Status = _app.Store.GetStatus(Assessment.AssessmentID);
		ChangedInOlderVersion = _app.Store.ChangedSinceLocked(Assessment.AssessmentID);
	}

	/// <summary>The status menu: Draft, Reviewed, Final, Letter sent, Appeal. Leaving Final or Letter sent asks why.</summary>
	[RelayCommand]
	private async Task SetStatus(string name)
	{
		AssessmentStatus next = AssessmentStatuses.Parse(name);
		Save();
		if (IsDraft)
		{
			_app.Dialogs.Notify("Not started yet", "Enter something in this assessment first, then set its status.");
			return;
		}
		string reason = null;
		if (IsLocked && !AssessmentStatuses.IsLocked(next))
		{
			reason = await _app.Sheets.ShowAsync<string>(new ReopenSheetViewModel(Title, Status.Value));
			if (reason == null)
			{
				return;
			}
		}
		try
		{
			_app.Store.SetStatus(Assessment.AssessmentID, next, reason);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't change the status", ex);
			return;
		}
		LoadStatus();
		_app.RaiseDataChanged();
	}

	/// <summary>⋯, then Copy from another structure.</summary>
	[RelayCommand]
	private async Task CopyFrom()
	{
		Save();
		if (IsDraft)
		{
			_app.Dialogs.Notify("Not started yet", "Enter something in this assessment first, then copy into it.");
			return;
		}
		await _app.Sheets.ShowAsync(new CopyFromSheetViewModel(_app, Assessment.AssessmentID));
	}

	[RelayCommand]
	private void ShowHistory()
	{
		Save();
		IEnumerable<EditHistoryRecord> rows = IsDraft ? Enumerable.Empty<EditHistoryRecord>() : _app.Store.GetHistory(Assessment.AssessmentID);
		_ = _app.Sheets.ShowAsync(new HistorySheetViewModel(Title, rows));
	}

	// ───────────── Undo ─────────────

	public bool CanUndo => _app.Undo.CanUndo(Assessment.AssessmentID);

	public bool CanRedo => _app.Undo.CanRedo(Assessment.AssessmentID);

	private void RefreshUndo()
	{
		OnPropertyChanged(nameof(CanUndo));
		OnPropertyChanged(nameof(CanRedo));
	}

	/// <summary>Edit, then Undo: puts the assessment back to how it was before the last burst of edits.</summary>
	[RelayCommand]
	public void Undo() => Step(back: true);

	/// <summary>Edit, then Redo.</summary>
	[RelayCommand]
	public void Redo() => Step(back: false);

	private void Step(bool back)
	{
		Save();
		Guid id = Assessment.AssessmentID;
		if (back ? !CanUndo : !CanRedo)
		{
			return;
		}
		string current = UndoService.Snapshot(File);
		string target = back ? _app.Undo.Undo(id, current) : _app.Undo.Redo(id, current);
		try
		{
			// Saved like any other change (history shows "Undo"), then the editor reloads from the database.
			_app.Writes.Write(new[] { id }, () => _app.Store.SaveAssessment(UndoService.Read(target), new SaveContext { Via = SaveVia.Undo }));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// Put the stacks back the way they were.
			if (back)
			{
				_app.Undo.Redo(id, target);
			}
			else
			{
				_app.Undo.Undo(id, target);
			}
			_app.Errors.Show(back ? "Couldn't undo" : "Couldn't redo", ex);
		}
	}

	/// <summary>Report printing is wired up by the main view model (keeps this class independent of the reports module).</summary>
	[RelayCommand]
	private async Task Print(string kind)
	{
		Save();
		await _main.PrintAsync(kind, new[] { Assessment.AssessmentID });
	}
}
