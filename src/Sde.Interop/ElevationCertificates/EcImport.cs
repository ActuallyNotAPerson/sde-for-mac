using System.Text.Json;
using Sde.Core;
using Sde.Core.Addresses;
using Sde.Core.Assessments;
using Sde.Core.ElevationCertificates;
using Sde.Data;

namespace Sde.Interop.ElevationCertificates;

/// <summary>One value an elevation certificate proposes, beside what the assessment has now.</summary>
public sealed record EcProposedChange(string Key, string Label, string Current, string Proposed, string Value)
{
	/// <summary>False when the assessment already has this value; the confirm sheet leaves it unticked.</summary>
	public bool Differs => !string.Equals(Current, Proposed, StringComparison.Ordinal);
}

/// <summary>What importing a certificate onto an assessment would do. Nothing is changed by making one.</summary>
public sealed class EcImportPreview
{
	public Guid AssessmentID { get; init; }

	public string PdfPath { get; init; }

	public EcReadResult Read { get; init; }

	/// <summary>The values the certificate proposes, in the order of FEMA's form.</summary>
	public IReadOnlyList<EcProposedChange> Changes { get; init; } = Array.Empty<EcProposedChange>();

	/// <summary>What was left out and why ("A5 is in NAD 1927…"), for the sheet.</summary>
	public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

	/// <summary>
	/// A warning when the certificate's address (A2) isn't the structure's, so a certificate isn't put on the wrong
	/// building by mistake; null when they match or the certificate has no address.
	/// </summary>
	public string AddressWarning { get; init; }
}

/// <summary>What <see cref="EcImport.Apply"/> did.</summary>
public sealed class EcImportResult
{
	/// <summary>How many fields changed (0 when only the PDF was attached).</summary>
	public int Changed { get; init; }

	/// <summary>The stored copy of the PDF.</summary>
	public DocumentRecord Document { get; init; }

	/// <summary>The edit history batch of the changes, or null when nothing changed.</summary>
	public string BatchId { get; init; }
}

/// <summary>
/// Puts an elevation certificate on an assessment (C11): <see cref="Preview"/> reads it and lists the proposed values
/// beside the current ones; <see cref="Apply"/> saves the values the person ticked (checked as the editor checks them,
/// recorded as "EC import" in edit history) and keeps a copy of the PDF with the structure. A certificate SDE can't
/// read is attached the same way, with the values typed into the entry sheet.
/// </summary>
public static class EcImport
{
	/// <summary>Reads the certificate and compares it with the assessment.</summary>
	public static EcImportPreview Preview(SdeStore store, Guid assessmentId, string pdfPath)
	{
		ArgumentNullException.ThrowIfNull(store);
		AssessmentFile file = store.LoadAssessment(assessmentId) ?? throw new ArgumentException("That assessment no longer exists.");
		EcReadResult read = EcReader.Read(pdfPath);
		if (!read.HasValues)
		{
			return new EcImportPreview { AssessmentID = assessmentId, PdfPath = pdfPath, Read = read };
		}
		ReferenceData r = store.Reference;
		EcFieldProposal proposal = ElevationCertificateMapping.ToFieldChanges(read.Values, r);
		List<EcProposedChange> changes = new();
		// The values are checked in order on a second copy, as the editor would take them, and shown as it would show them.
		AssessmentFile copy = store.LoadAssessment(assessmentId);
		foreach (FieldInputChange c in proposal.Changes)
		{
			FieldInfo f = FieldCatalog.Get(c.Key);
			if (f == null)
			{
				continue;
			}
			string current = FieldCatalog.Display(f, FieldCatalog.GetValue(file, f), r);
			try
			{
				FieldInput.Apply(copy, new[] { c }, r, store.Calculator);
			}
			catch (FieldInputException ex)
			{
				// A value the editor wouldn't take is reported, never offered.
				proposal.Notes.Add($"{f.Label}: \"{c.Value}\" wasn't used ({ex.Message})");
				continue;
			}
			changes.Add(new EcProposedChange(f.Key, f.Label, current ?? "", FieldCatalog.Display(f, FieldCatalog.GetValue(copy, f), r) ?? "", c.Value));
		}
		return new EcImportPreview
		{
			AssessmentID = assessmentId,
			PdfPath = pdfPath,
			Read = read,
			Changes = changes,
			Notes = proposal.Notes.ToList(),
			AddressWarning = AddressWarning(file, read.Values, r),
		};
	}

	/// <summary>
	/// Saves the ticked values (<paramref name="keys"/>, in the certificate's order) in one edit history batch and keeps
	/// a copy of the PDF with the structure, with the values read from it. With no keys only the PDF is attached.
	/// Refused, with nothing changed, for a Final, Letter sent or checked-out assessment.
	/// </summary>
	public static EcImportResult Apply(SdeStore store, EcImportPreview preview, IReadOnlyCollection<string> keys, SaveContext context = null)
	{
		ArgumentNullException.ThrowIfNull(preview);
		HashSet<string> chosen = new HashSet<string>(keys ?? Array.Empty<string>(), StringComparer.Ordinal);
		return Apply(store, preview, preview.Changes.Where(c => chosen.Contains(c.Key)).Select(c => new FieldInputChange(c.Key, c.Value)).ToList(), context);
	}

	/// <summary>
	/// The same with the values given directly: the ticked proposals, or values typed from a certificate SDE couldn't
	/// read (the entry sheet). They are checked as the editor checks them; a refused value changes nothing and keeps no
	/// PDF (<see cref="FieldInputException"/>).
	/// </summary>
	public static EcImportResult Apply(SdeStore store, EcImportPreview preview, IReadOnlyList<FieldInputChange> changes, SaveContext context = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(preview);
		changes ??= Array.Empty<FieldInputChange>();
		SaveContext ctx = new SaveContext
		{
			User = context?.User,
			Via = SaveVia.EcImport,
			BatchId = Guid.NewGuid().ToString("N"),
			Event = context?.Event ?? "Elevation certificate imported" + (changes.Count == 0 ? " (attached only)" : ""),
			ExpectedVersion = context?.ExpectedVersion,
			ExpectedPropertyVersion = context?.ExpectedPropertyVersion,
			ExpectedLastUpdate = context?.ExpectedLastUpdate,
			ExpectedPropertyLastUpdate = context?.ExpectedPropertyLastUpdate,
		};

		AssessmentFile saved = store.Apply(preview.AssessmentID, f => FieldInput.Apply(f, changes, store.Reference, store.Calculator), ctx);
		int changed = changes.Count == 0 ? 0 : store.GetBatch(ctx.BatchId).Count(h => h.Field != null);
		string fields = preview.Read?.Values == null ? null : JsonSerializer.Serialize(preview.Read.Values.Fields);
		DocumentRecord document = store.AddDocument(saved.Property.PropertyID, preview.PdfPath, DocumentKinds.ElevationCertificate,
			"Elevation certificate", preview.AssessmentID, fields, ctx);
		return new EcImportResult { Changed = changed, Document = document, BatchId = changed == 0 ? null : ctx.BatchId };
	}

	/// <summary>
	/// The fields the entry sheet offers when a certificate can't be read (a scan or a flattened copy): the ones the
	/// certificate's sections A5, B1, B4, B5, B9 and C2 hold that are typed rather than chosen.
	/// </summary>
	public static readonly IReadOnlyList<string> EntryKeys = new[]
	{
		"Property.Latitude", "Property.Longitude", "Property.NFIPCID", "Property.FIRMPanelNumber", "Property.PanelSuffix",
		"Property.BFE", "Property.FirstFloorElevation", "Property.Datum",
	};

	private static string AddressWarning(AssessmentFile file, ElevationCertificateValues ec, ReferenceData reference)
	{
		if (string.IsNullOrWhiteSpace(ec.StreetAddress))
		{
			return null;
		}
		AddressMatcher matcher = new AddressMatcher(reference);
		AddressKey theirs = matcher.KeyFor(null, ec.StreetAddress, null, ec.ZipCode);
		AddressKey ours = matcher.KeyFor(file.Property);
		if (!theirs.CanMatch || !ours.CanMatch || matcher.Same(theirs, ours))
		{
			return null;
		}
		return $"This certificate is for {ec.StreetAddress.Trim()}{(string.IsNullOrWhiteSpace(ec.City) ? "" : ", " + ec.City.Trim())}, not this structure's address. Check that it's the right certificate before importing it.";
	}
}
