using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.Photos;

namespace Sde.App.ViewModels.Editor;

/// <summary>
/// Field data coming in to the editor: photo order and the details stored in photos, the online look-up of
/// coordinates and flood zone, and damage suggested from a depth table. Nothing from these is ever applied without the
/// inspector choosing it.
/// </summary>
public sealed partial class AssessmentEditorViewModel
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private List<DepthDamageTable> _depthTables = new();
	private Dictionary<string, decimal> _suggested = new(StringComparer.Ordinal);

	/// <summary>Called at the end of the constructor.</summary>
	private void InitFieldData()
	{
		foreach (SectionViewModel s in Sections)
		{
			if (s.AllFields.Any(f => f.RecordType == typeof(PropertyRecord) && f.Member is nameof(PropertyRecord.Latitude) or nameof(PropertyRecord.FIRMZone)))
			{
				s.OnlineLookupCommand = LookUpOnlineCommand;
			}
			if (s.AllFields.Any(f => f.RecordType == typeof(PropertyRecord) && f.Member == nameof(PropertyRecord.BFE)))
			{
				s.ElevationCertificateCommand = ImportElevationCertificateCommand;
			}
		}
		_depthTables = _app.DepthTables.All().ToList();
		LoadSuggestions();
		Elements.CollectionChanged += (_, _) => ApplySuggestionsToRows();
		// Recalculate() raises Assessment after every edit: the depth, the building type or the cause may have changed.
		PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(Assessment))
			{
				RefreshDepthHelper();
			}
			else if (e.PropertyName == nameof(SelectedSection) && SelectedSection is DamageSectionViewModel)
			{
				// Tables may have been added or changed in Settings while this assessment stayed open.
				_depthTables = _app.DepthTables.All().ToList();
				RefreshDepthHelper();
			}
		};
		RefreshDepthHelper();
	}

	// ───────────── Photo order ─────────────

	/// <summary>
	/// Moves a photo to a place in the list (drag and drop, and the Move earlier / Move later buttons). The list's order
	/// is the order the reports print the photos in, and it is saved: the store gives the photos new attachment IDs in
	/// this order. The starred report photo always stays first, because reports print it first.
	/// </summary>
	public void MovePhotoTo(PhotoViewModel photo, int index)
	{
		int from = Photos.IndexOf(photo);
		if (from < 0 || photo.IsDefault)
		{
			return;
		}
		int first = Photos.Count > 0 && Photos[0].IsDefault ? 1 : 0;
		int to = Math.Clamp(index, first, Photos.Count - 1);
		if (to == from)
		{
			return;
		}
		Photos.Move(from, to);
		SyncAttachmentOrder();
		RefreshPhotoPositions();
		Edited();
	}

	private void MovePhoto(PhotoViewModel photo, int places) => MovePhotoTo(photo, Photos.IndexOf(photo) + places);

	/// <summary>After the star moves: the report photo goes to the front.</summary>
	private void MoveReportPhotoFirst(PhotoViewModel photo)
	{
		int from = Photos.IndexOf(photo);
		if (from > 0)
		{
			Photos.Move(from, 0);
			SyncAttachmentOrder();
		}
		RefreshPhotoPositions();
	}

	/// <summary>The assessment's attachment list in the order shown (photos in Recently deleted after them).</summary>
	private void SyncAttachmentOrder()
	{
		List<AttachmentRecord> removed = File.Attachments.Where(a => !a.Active).ToList();
		File.Attachments.Clear();
		File.Attachments.AddRange(Photos.Select(p => p.Attachment));
		File.Attachments.AddRange(removed);
	}

	private void RefreshPhotoPositions()
	{
		int first = Photos.Count > 0 && Photos[0].IsDefault ? 1 : 0;
		for (int i = 0; i < Photos.Count; i++)
		{
			PhotoViewModel p = Photos[i];
			p.Position = i + 1;
			p.CanMoveEarlier = !p.IsDefault && i > first;
			p.CanMoveLater = !p.IsDefault && i < Photos.Count - 1;
		}
	}

	// ───────────── Details stored in photos ─────────────

	private decimal? _offerLatitude;
	private decimal? _offerLongitude;
	private DateTime? _offerDate;

	/// <summary>A card on the Photos step offering the photos' position or date (never applied without a click).</summary>
	[ObservableProperty]
	public partial bool HasPhotoOffer { get; private set; }

	[ObservableProperty]
	public partial string PhotoOfferText { get; private set; }

	[ObservableProperty]
	public partial bool CanUsePhotoLocation { get; private set; }

	[ObservableProperty]
	public partial bool CanUsePhotoDate { get; private set; }

	/// <summary>
	/// After photos were added: reads the date and GPS position stored in each original file, saves them with the
	/// photo, and offers them when the structure has no coordinates or the assessment has no inspection date.
	/// </summary>
	private async Task AfterPhotosAddedAsync(List<(AttachmentRecord Attachment, string Source)> added)
	{
		List<(AttachmentRecord Attachment, string Name, Sde.Interop.Photos.PhotoInfo Info)> details = await Task.Run(() =>
			added.Select(x => (x.Attachment, Path.GetFileName(x.Source), ReadDetails(x.Source))).ToList());

		// Saved now (not after the usual pause) so the photos exist in the database for their details.
		Save();
		if (!IsDraft && SaveState == "Saved")
		{
			try
			{
				foreach ((AttachmentRecord a, string name, Sde.Interop.Photos.PhotoInfo info) in details)
				{
					_app.Store.SavePhotoInfo(new PhotoInfoRecord
					{
						FileName = a.FileName,
						AssessmentID = Assessment.AssessmentID,
						TakenOn = info.TakenOn,
						Latitude = info.Latitude,
						Longitude = info.Longitude,
						OriginalName = name,
					});
				}
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				AppLog.Write("Couldn't save the photos' details", ex);
			}
		}

		List<string> lines = new List<string>();
		_offerLatitude = null;
		_offerLongitude = null;
		_offerDate = null;
		if (Property.Latitude == null || Property.Longitude == null)
		{
			foreach ((AttachmentRecord _, string name, Sde.Interop.Photos.PhotoInfo info) in details)
			{
				if (info.Latitude is double lat && info.Longitude is double lon)
				{
					_offerLatitude = Math.Round((decimal)lat, 6);
					_offerLongitude = Math.Round((decimal)lon, 6);
					lines.Add($"{name} was taken at {_offerLatitude.Value.ToString("0.000000", CultureInfo.InvariantCulture)}, {_offerLongitude.Value.ToString("0.000000", CultureInfo.InvariantCulture)}. This structure has no coordinates yet.");
					break;
				}
			}
		}
		if (Assessment.DateofInspection == null)
		{
			foreach ((AttachmentRecord _, string name, Sde.Interop.Photos.PhotoInfo info) in details)
			{
				if (info.TakenOn is DateTime taken)
				{
					_offerDate = taken.Date;
					lines.Add($"{name} was taken on {taken.ToString("MMM d, yyyy", Us)}. This assessment has no inspection date yet.");
					break;
				}
			}
		}
		CanUsePhotoLocation = _offerLatitude != null;
		CanUsePhotoDate = _offerDate != null;
		PhotoOfferText = string.Join(Environment.NewLine, lines);
		HasPhotoOffer = lines.Count > 0;
	}

	private static Sde.Interop.Photos.PhotoInfo ReadDetails(string path)
	{
		try
		{
			return PhotoMetadata.Read(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return new Sde.Interop.Photos.PhotoInfo(null, null, null);
		}
	}

	/// <summary>Uses the photo's GPS position as the structure's latitude and longitude.</summary>
	[RelayCommand]
	private void UsePhotoLocation()
	{
		if (_offerLatitude == null || _offerLongitude == null)
		{
			return;
		}
		ApplyChanges(new[]
		{
			new FieldChange(FieldCatalog.Find(FieldModel.Property, nameof(PropertyRecord.Latitude)).Key, _offerLatitude),
			new FieldChange(FieldCatalog.Find(FieldModel.Property, nameof(PropertyRecord.Longitude)).Key, _offerLongitude),
		});
		CanUsePhotoLocation = false;
		_offerLatitude = null;
		_offerLongitude = null;
		HasPhotoOffer = CanUsePhotoDate;
	}

	/// <summary>Uses the date the photo was taken as the inspection date.</summary>
	[RelayCommand]
	private void UsePhotoDate()
	{
		if (_offerDate == null)
		{
			return;
		}
		ApplyChanges(new[] { new FieldChange(FieldCatalog.Find(FieldModel.Assessment, nameof(AssessmentRecord.DateofInspection)).Key, _offerDate) });
		CanUsePhotoDate = false;
		_offerDate = null;
		HasPhotoOffer = CanUsePhotoLocation;
	}

	[RelayCommand]
	private void DismissPhotoOffer()
	{
		HasPhotoOffer = false;
		CanUsePhotoLocation = false;
		CanUsePhotoDate = false;
	}

	/// <summary>Sets fields as if they had been typed: the editor's rules, autosave, undo and edit history all apply.</summary>
	private void ApplyChanges(IEnumerable<FieldChange> changes)
	{
		FieldRules.Apply(File, changes, _calculator);
		ReloadFields();
		Edited();
	}

	// ───────────── Online look-up ─────────────

	/// <summary>
	/// Location or Flood zone step, then Look up online: the Census geocoder (when there's an address but no coordinates)
	/// and FEMA's flood map, shown as current and proposed values. The ticked ones are applied as edits, and history
	/// records where they came from, since the online flood map can differ from the effective FIRM.
	/// </summary>
	[RelayCommand]
	private async Task LookUpOnline()
	{
		AutoFillSheetViewModel sheet = new AutoFillSheetViewModel(_app, Property);
		Task<object> closed = _app.Sheets.ShowAsync(sheet);
		sheet.Start();
		if (await closed is AutoFillChoice choice && choice.Values.Count > 0)
		{
			ApplyOnline(choice);
		}
	}

	private void ApplyOnline(AutoFillChoice choice)
	{
		ApplyChanges(choice.Values.Select(OnlineLookup.Change).ToList());
		if (IsDraft)
		{
			_dirty = true;
		}
		Save();
		if (IsDraft || SaveState != "Saved")
		{
			return;
		}
		try
		{
			_app.Store.WriteEvent(Property.PropertyID, Assessment.AssessmentID, OnlineLookup.EventLabel(choice.FromNfhl, choice.FromGeocoder, choice.RetrievedOn),
				new SaveContext { Via = SaveVia.Online });
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't record where the looked-up values came from", ex);
		}
	}

	// ───────────── Depth table suggestions ─────────────

	/// <summary>The community's depth tables for this kind of building.</summary>
	public IReadOnlyList<DepthDamageTable> DepthTableChoices => _depthTables.Where(t => t.AppliesTo(Property)).ToList();

	public bool HasDepthTables => DepthTableChoices.Count > 0;

	public bool HasSeveralDepthTables => DepthTableChoices.Count > 1;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanSuggestDamage))]
	[NotifyCanExecuteChangedFor(nameof(SuggestDamageCommand))]
	public partial DepthDamageTable SelectedDepthTable { get; set; }

	public bool CanSuggestDamage => SelectedDepthTable != null && Assessment.DepthOfFloodAboveFFE != null && !IsUndetermined;

	public string DepthHelperText => Assessment.DepthOfFloodAboveFFE is decimal d
		? $"Water {d.ToString("0.##", CultureInfo.InvariantCulture)} ft above the lowest floor (from the Inspection step). Suggestions fill in elements you haven't entered yet; check each one."
		: "Enter the depth of water above the lowest floor on the Inspection step (flood damage) to use a depth table.";

	public int SuggestedCount => Elements.Count(e => e.IsSuggested);

	public bool HasSuggestions => SuggestedCount > 0;

	public string SuggestionsText => SuggestedCount == 1
		? "1 value is a suggestion from the depth table. Accept it, or change it, before making this assessment Final."
		: $"{SuggestedCount} values are suggestions from the depth table. Accept them, or change them, before making this assessment Final.";

	/// <summary>What the last Suggest did.</summary>
	[ObservableProperty]
	public partial string DepthResultText { get; private set; }

	private void RefreshDepthHelper()
	{
		IReadOnlyList<DepthDamageTable> choices = DepthTableChoices;
		if (SelectedDepthTable == null || !choices.Contains(SelectedDepthTable))
		{
			SelectedDepthTable = choices.FirstOrDefault(t => t.Name == SelectedDepthTable?.Name) ?? choices.FirstOrDefault();
		}
		OnPropertyChanged(nameof(DepthTableChoices));
		OnPropertyChanged(nameof(HasDepthTables));
		OnPropertyChanged(nameof(HasSeveralDepthTables));
		OnPropertyChanged(nameof(CanSuggestDamage));
		OnPropertyChanged(nameof(DepthHelperText));
		SuggestDamageCommand.NotifyCanExecuteChanged();
	}

	private void LoadSuggestions()
	{
		_suggested = IsDraft
			? new Dictionary<string, decimal>(StringComparer.Ordinal)
			: _app.Store.GetDamageSuggestions(Assessment.AssessmentID).ToDictionary(s => s.ElementName, s => s.SuggestedPercent, StringComparer.Ordinal);
		ApplySuggestionsToRows();
	}

	private void ApplySuggestionsToRows()
	{
		foreach (ElementRowViewModel e in Elements)
		{
			e.Accept = AcceptSuggestion;
			e.SuggestedPercent = _suggested.TryGetValue(e.Name ?? "", out decimal v) ? v : null;
			e.PropertyChanged -= OnElementChanged;
			e.PropertyChanged += OnElementChanged;
		}
		RefreshSuggestionState();
	}

	private void OnElementChanged(object sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ElementRowViewModel.IsSuggested))
		{
			RefreshSuggestionState();
		}
	}

	private void RefreshSuggestionState()
	{
		OnPropertyChanged(nameof(SuggestedCount));
		OnPropertyChanged(nameof(HasSuggestions));
		OnPropertyChanged(nameof(SuggestionsText));
	}

	/// <summary>
	/// Damage step, then Suggest from depth table: fills each element's % damaged from the chosen table at the entered
	/// depth, for elements the inspector hasn't entered yet (or that still hold an earlier suggestion). Each filled value
	/// is marked "suggested" until it is accepted or changed, and the assessment can't be made Final until then.
	/// </summary>
	[RelayCommand(CanExecute = nameof(CanSuggestDamage))]
	private void SuggestDamage()
	{
		DepthDamageTable table = SelectedDepthTable;
		if (table == null || Assessment.DepthOfFloodAboveFFE is not decimal depth)
		{
			return;
		}
		Dictionary<string, decimal> values = table.Suggest(Elements.Select(e => e.Name), depth);
		if (values.Count == 0)
		{
			DepthResultText = $"\"{table.Name}\" has no values for this building's elements.";
			return;
		}
		List<DamageSuggestionRecord> rows = new List<DamageSuggestionRecord>();
		int kept = 0;
		foreach (ElementRowViewModel e in Elements)
		{
			if (!values.TryGetValue(e.Name, out decimal v))
			{
				continue;
			}
			if (e.UserDamage != 0 && !e.IsSuggested)
			{
				kept++;
				continue;
			}
			e.SuggestedPercent = v;
			e.UserDamage = v;
			rows.Add(new DamageSuggestionRecord { ElementName = e.Name, SuggestedPercent = e.UserDamage, DepthFt = depth, TableName = table.Name });
		}
		if (rows.Count > 0)
		{
			if (IsDraft)
			{
				_dirty = true;
			}
			Save();
			if (IsDraft || SaveState != "Saved")
			{
				return;
			}
			try
			{
				_app.Store.AddDamageSuggestions(Assessment.AssessmentID, rows);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_app.Errors.Show("Couldn't record the suggestions", ex);
			}
			LoadSuggestions();
		}
		string filled = rows.Count == 1 ? "Suggested 1 element" : $"Suggested {rows.Count} elements";
		string left = kept == 0 ? "" : kept == 1 ? " 1 you had entered was left as it was." : $" {kept} you had entered were left as they were.";
		DepthResultText = $"{filled} from \"{table.Name}\" at {depth.ToString("0.##", CultureInfo.InvariantCulture)} ft.{left}";
	}

	/// <summary>The inspector takes one suggested value as their own.</summary>
	private void AcceptSuggestion(ElementRowViewModel row) => Accept(new[] { row });

	/// <summary>Accept all: every value still showing "suggested" becomes the inspector's own.</summary>
	[RelayCommand]
	private void AcceptAllSuggestions() => Accept(Elements.Where(e => e.IsSuggested).ToList());

	private void Accept(IReadOnlyList<ElementRowViewModel> rows)
	{
		if (rows.Count == 0)
		{
			return;
		}
		if (!IsDraft)
		{
			try
			{
				_app.Store.AcceptDamageSuggestions(Assessment.AssessmentID, rows.Select(r => r.Name).ToList());
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_app.Errors.Show("Couldn't accept the suggestion", ex);
				return;
			}
		}
		foreach (ElementRowViewModel r in rows)
		{
			_suggested.Remove(r.Name);
			r.SuggestedPercent = null;
		}
		RefreshSuggestionState();
	}
}
