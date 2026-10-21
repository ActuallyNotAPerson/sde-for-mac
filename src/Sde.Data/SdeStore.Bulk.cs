using System.Globalization;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>What a bulk change did: how many assessments changed, which were left alone because they are locked.</summary>
public sealed class BulkResult
{
	public string BatchId { get; init; }

	public int Changed { get; set; }

	/// <summary>Assessments that weren't changed because they are Final or their letter was sent.</summary>
	public List<Guid> Locked { get; } = new();

	/// <summary>Values that weren't put back by a revert because someone changed them again since.</summary>
	public int ChangedSince { get; set; }
}

/// <summary>A field FEMA's Search and Replace offers (Lookup type 28), with the catalog field it edits.</summary>
public sealed record SearchField(string Label, FieldInfo Field, bool IsText);

/// <summary>One value Search and Replace would change.</summary>
public sealed record SearchMatch(Guid PropertyID, Guid? AssessmentID, string Address, string Before, string After, bool Locked);

/// <summary>Changes to many assessments at once: bulk edit, copy from, search and replace, and reverting them.</summary>
public sealed partial class SdeStore
{
	/// <summary>
	/// Saves many assessments in one transaction (all or nothing), as one batch in edit history. Locked assessments are
	/// skipped (unless the context allows it) and listed in the result.
	/// </summary>
	public BulkResult SaveAssessments(IReadOnlyList<AssessmentFile> files, SaveContext context)
	{
		SaveContext ctx = context.BatchId != null ? context : new SaveContext { User = context.User, Via = context.Via, BatchId = Guid.NewGuid().ToString("N"), AllowLocked = context.AllowLocked, Event = context.Event };
		AssessmentCalculator calc = Calculator;
		foreach (AssessmentFile f in files)
		{
			calc.Recalculate(f);
		}
		BulkResult result = new BulkResult { BatchId = ctx.BatchId };
		Database.InTransaction((c, tx) =>
		{
			foreach (AssessmentFile f in files)
			{
				if (!ctx.AllowLocked && GetStatus(c, tx, f.Assessment.AssessmentID).IsLocked)
				{
					AssessmentFile stored = LoadAssessment(c, tx, f.Assessment.AssessmentID);
					if (stored != null && AssessmentDiff.Compare(stored, f, Reference).Count > 0)
					{
						result.Locked.Add(f.Assessment.AssessmentID);
					}
					continue;
				}
				AssessmentFile before = LoadAssessment(c, tx, f.Assessment.AssessmentID);
				if (before != null && AssessmentDiff.Compare(before, f, Reference).Count == 0)
				{
					continue;
				}
				SaveTracked(c, tx, f, ctx);
				result.Changed++;
			}
		});
		return result;
	}

	/// <summary>Sets the same fields on many assessments (bulk edit), applying the editor's rules to each.</summary>
	public BulkResult ApplyChanges(IReadOnlyCollection<Guid> assessmentIds, IReadOnlyList<FieldChange> changes, SaveContext context)
	{
		AssessmentCalculator calc = Calculator;
		List<AssessmentFile> files = LoadAssessments(assessmentIds);
		foreach (AssessmentFile f in files)
		{
			FieldRules.Apply(f, changes, calc);
		}
		return SaveAssessments(files, context);
	}

	/// <summary>
	/// Puts back what one bulk edit, copy or replace changed, as a new batch ("Revert"). A value someone has changed
	/// again since is left as it is and counted in <see cref="BulkResult.ChangedSince"/>.
	/// </summary>
	public BulkResult RevertBatch(string batchId, SaveContext context = null)
	{
		List<EditHistoryRecord> rows = GetBatch(batchId).Where(r => r.Field != null && FieldCatalog.Get(r.Field) != null).ToList();
		Dictionary<Guid, AssessmentFile> files = new Dictionary<Guid, AssessmentFile>();
		int changedSince = 0;
		foreach (EditHistoryRecord row in rows.AsEnumerable().Reverse())
		{
			Guid? id = row.AssessmentID;
			if (id == null)
			{
				continue;
			}
			if (!files.TryGetValue(id.Value, out AssessmentFile file))
			{
				file = LoadAssessment(id.Value);
				if (file == null)
				{
					continue;
				}
				files[id.Value] = file;
			}
			FieldInfo f = FieldCatalog.Get(row.Field);
			if (FieldCatalog.Canonical(FieldCatalog.GetValue(file, f)) != row.NewValue)
			{
				changedSince++;
				continue;
			}
			FieldCatalog.SetValue(file, f, row.OldValue == null ? null : FieldCatalog.Convert(row.OldValue, f.RecordProperty.PropertyType));
		}
		SaveContext ctx = context ?? SaveContext.Batch(SaveVia.Undo, "Reverted a change made to several assessments");
		BulkResult result = SaveAssessments(files.Values.ToList(), ctx);
		result.ChangedSince = changedSince;
		return result;
	}

	// ───────────── Search and replace ─────────────

	/// <summary>The fields FEMA's Search and Replace offers (Lookup type 28), in FEMA's order, that this app can edit.</summary>
	public List<SearchField> SearchFields()
	{
		List<SearchField> fields = new List<SearchField>();
		foreach (LookupRecord l in Reference.OfType(LookupTypes.SearchAndReplaceField).OrderBy(l => l.zOrder))
		{
			FieldModel model = string.Equals(l.LookupValue1, "Property", StringComparison.OrdinalIgnoreCase) ? FieldModel.Property : FieldModel.Assessment;
			FieldInfo f = FieldCatalog.All.FirstOrDefault(x => x.Model == model && string.Equals(x.Member, l.LookupValue2, StringComparison.OrdinalIgnoreCase));
			if (f != null)
			{
				fields.Add(new SearchField(l.LookupFieldName, f, f.Kind is FieldKind.Text or FieldKind.MultilineText));
			}
		}
		return fields;
	}

	/// <summary>
	/// What replacing <paramref name="search"/> with <paramref name="replace"/> in a field would change. Text fields
	/// replace the matching part only, ignoring case (FEMA's version also lowercased the rest of the value); number and
	/// date fields match the whole value. Structure fields are listed once per structure.
	/// </summary>
	public List<SearchMatch> PreviewReplace(SearchField field, string search, string replace)
	{
		List<SearchMatch> matches = new List<SearchMatch>();
		if (string.IsNullOrEmpty(search))
		{
			return matches;
		}
		HashSet<Guid> seenProperties = new HashSet<Guid>();
		Dictionary<Guid, AssessmentStatusRecord> statuses = GetStatuses();
		foreach (AssessmentFile file in LoadAllAssessments())
		{
			if (field.Field.Model == FieldModel.Property && !seenProperties.Add(file.Property.PropertyID))
			{
				continue;
			}
			object value = FieldCatalog.GetValue(file, field.Field);
			string before = field.IsText ? value as string : FieldCatalog.Display(field.Field, value, Reference);
			string after = Replaced(field, value, search, replace);
			if (after == null || after == before)
			{
				continue;
			}
			bool locked = field.Field.Model == FieldModel.Assessment && statuses.TryGetValue(file.Assessment.AssessmentID, out AssessmentStatusRecord s) && s.IsLocked;
			matches.Add(new SearchMatch(file.Property.PropertyID, field.Field.Model == FieldModel.Assessment ? file.Assessment.AssessmentID : null,
				AddressLabel(file.Property), before, after, locked));
		}
		return matches;
	}

	/// <summary>Replaces as previewed, in one transaction and one batch. Locked assessments are skipped.</summary>
	public BulkResult Replace(SearchField field, string search, string replace, SaveContext context = null)
	{
		SaveContext ctx = context ?? SaveContext.Batch(SaveVia.BulkEdit, $"Search and replace in {field.Label}");
		HashSet<Guid> seenProperties = new HashSet<Guid>();
		List<AssessmentFile> changed = new List<AssessmentFile>();
		foreach (AssessmentFile file in LoadAllAssessments())
		{
			if (field.Field.Model == FieldModel.Property && !seenProperties.Add(file.Property.PropertyID))
			{
				continue;
			}
			object value = FieldCatalog.GetValue(file, field.Field);
			string after = Replaced(field, value, search, replace);
			if (after == null)
			{
				continue;
			}
			FieldCatalog.SetValue(file, field.Field, after.Length == 0 ? null : (object)after);
			changed.Add(file);
		}
		return SaveAssessments(changed, ctx);
	}

	/// <summary>The new text for a value, or null when it doesn't match.</summary>
	private static string Replaced(SearchField field, object value, string search, string replace)
	{
		if (field.IsText)
		{
			string text = value as string;
			return text != null && text.Contains(search, StringComparison.OrdinalIgnoreCase)
				? text.Replace(search, replace ?? "", StringComparison.OrdinalIgnoreCase)
				: null;
		}
		string canonical = FieldCatalog.Canonical(value);
		string wanted = FieldCatalog.Canonical(TryConvert(search, field.Field));
		if (canonical == null || wanted == null || canonical != wanted)
		{
			return null;
		}
		object next = TryConvert(replace, field.Field);
		return next == null && !string.IsNullOrEmpty(replace) ? null : FieldCatalog.Canonical(next) ?? "";
	}

	private static object TryConvert(string text, FieldInfo f)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		try
		{
			if (f.Kind == FieldKind.Date && DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out DateTime d))
			{
				return FieldCatalog.Convert(d, f.RecordProperty.PropertyType);
			}
			return FieldCatalog.Convert(text.Trim().Replace("$", "").Replace(",", ""), f.RecordProperty.PropertyType);
		}
		catch (FormatException)
		{
			return null;
		}
	}
}
