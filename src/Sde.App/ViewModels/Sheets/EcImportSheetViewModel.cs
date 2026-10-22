using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Interop.ElevationCertificates;

namespace Sde.App.ViewModels.Sheets;

/// <summary>One value on the elevation certificate sheet: what the assessment has, and what the certificate says.</summary>
public sealed partial class EcRowViewModel : ObservableObject
{
	public EcRowViewModel(string key, string label, string current, string proposed, string value, bool isChecked, bool editable)
	{
		Key = key;
		Label = label;
		Current = string.IsNullOrEmpty(current) ? "(blank)" : current;
		Proposed = proposed;
		Value = value;
		IsChecked = isChecked;
		IsEditable = editable;
	}

	public string Key { get; }

	public string Label { get; }

	public string Current { get; }

	/// <summary>The certificate's value as the editor will show it (read from the PDF).</summary>
	public string Proposed { get; }

	public bool IsEditable { get; }

	public bool IsReadOnly => !IsEditable;

	/// <summary>The value to save: read from the PDF, or typed on the entry sheet.</summary>
	[ObservableProperty]
	public partial string Value { get; set; }

	[ObservableProperty]
	public partial bool IsChecked { get; set; }

	public string CheckName => "Use " + Label;
}

/// <summary>
/// Puts an elevation certificate on the open assessment (C11). When SDE can read the PDF (FEMA's fillable form) it
/// lists the certificate's values beside the current ones to tick; when it can't (a scan or a flattened copy) it shows
/// an entry sheet to type them. Either way the PDF is kept with the structure. Closes with the
/// <see cref="EcImportResult"/>.
/// </summary>
public sealed partial class EcImportSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly EcImportPreview _preview;

	public EcImportSheetViewModel(AppServices app, Guid assessmentId, string pdfPath) : base("Elevation certificate")
	{
		_app = app;
		_preview = EcImport.Preview(app.Store, assessmentId, pdfPath);
		FileName = Path.GetFileName(pdfPath);
		CanRead = _preview.Read.HasValues;
		Message = CanRead
			? $"SDE read {FileName}. Tick the values to use; the ones that match already are left unticked."
			: _preview.Read.Message + (_preview.Read.Source == EcSource.Unreadable ? "" : " Type the values from the certificate, then Save. The PDF is kept with the structure either way.");
		Notes = _preview.Notes;
		AddressWarning = _preview.AddressWarning;
		if (CanRead)
		{
			foreach (EcProposedChange c in _preview.Changes)
			{
				Rows.Add(new EcRowViewModel(c.Key, c.Label, c.Current, c.Proposed, c.Value, c.Differs, editable: false));
			}
		}
		else if (_preview.Read.Source != EcSource.Unreadable)
		{
			AssessmentFile file = app.Store.LoadAssessment(assessmentId);
			foreach (string key in EcImport.EntryKeys)
			{
				FieldInfo f = FieldCatalog.Get(key);
				string current = FieldCatalog.Display(f, FieldCatalog.GetValue(file, f), app.Store.Reference);
				Rows.Add(new EcRowViewModel(key, f.Label, current, null, "", false, editable: true));
			}
		}
	}

	public override SheetSize Size => SheetSize.Large;

	public string FileName { get; }

	/// <summary>True when the values were read from the PDF; false for the entry sheet.</summary>
	public bool CanRead { get; }

	public bool IsEntrySheet => !CanRead && Rows.Count > 0;

	public string Message { get; }

	public IReadOnlyList<string> Notes { get; }

	public bool HasNotes => Notes.Count > 0;

	public string AddressWarning { get; }

	public bool HasAddressWarning => AddressWarning != null;

	public ObservableCollection<EcRowViewModel> Rows { get; } = new();

	public bool HasRows => Rows.Count > 0;

	/// <summary>A PDF that can't be opened isn't kept.</summary>
	public bool CanAttach => _preview.Read.Source != EcSource.Unreadable;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; }

	public bool HasError => !string.IsNullOrEmpty(Error);

	/// <summary>Saves the ticked (or typed) values and keeps the PDF.</summary>
	[RelayCommand]
	private void Save()
	{
		List<FieldInputChange> changes = Rows
			.Where(r => r.IsEditable ? !string.IsNullOrWhiteSpace(r.Value) : r.IsChecked)
			.Select(r => new FieldInputChange(r.Key, r.Value?.Trim()))
			.ToList();
		Apply(changes);
	}

	/// <summary>Keeps the PDF with the structure without changing any value.</summary>
	[RelayCommand]
	private void AttachOnly() => Apply(new List<FieldInputChange>());

	private void Apply(List<FieldInputChange> changes)
	{
		if (!CanAttach)
		{
			return;
		}
		try
		{
			EcImportResult result = _app.Writes.Write(new[] { _preview.AssessmentID }, () => EcImport.Apply(_app.Store, _preview, changes));
			Close(result);
		}
		catch (FieldInputException ex)
		{
			Error = ex.Message + " Nothing was saved.";
		}
		catch (Exception ex) when (ex is AssessmentLockedException or AssessmentCheckedOutException or ArgumentException or FileNotFoundException)
		{
			Error = ex.Message;
		}
	}
}
