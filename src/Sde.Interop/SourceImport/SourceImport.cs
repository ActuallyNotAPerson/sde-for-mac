using System.Text.Json;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>What importing a row would do.</summary>
public enum ImportRowStatus
{
	/// <summary>A record not seen before: a new structure (and, for inspections, its assessment).</summary>
	NewStructure,
	/// <summary>Another inspection of a record already in SDE (or earlier in the file): a new assessment of that structure.</summary>
	NewInspection,
	/// <summary>
	/// A record not seen before that matches exactly one structure already in SDE (by parcel or address): its blank
	/// fields are filled in, and for inspections it gets a new assessment.
	/// </summary>
	Attach,
	/// <summary>Imported before, and some source values have changed since: those fields are updated.</summary>
	Update,
	/// <summary>Imported before, and nothing has changed in the source.</summary>
	Unchanged,
	/// <summary>Matches several structures in SDE. Nothing is imported: SDE doesn't guess between them.</summary>
	Ambiguous,
	/// <summary>Can't be imported (see <see cref="ImportRow.Problems"/>).</summary>
	Problem,
}

/// <summary>One record in the file and what importing it would do.</summary>
public sealed class ImportRow
{
	private PropertyRecord _property;

	/// <summary>The row's number in the file (the first row of a combined checklist).</summary>
	public int RowNumber { get; init; }

	public string RecordId { get; init; }

	/// <summary>"" when the profile has no inspection ID column (and always for structures).</summary>
	public string InspectionId { get; init; }

	public ImportRowStatus Status { get; internal set; }

	/// <summary>The assessment as it would be saved (calculated). Null for structures and for rows without a record ID.</summary>
	public AssessmentFile File { get; internal set; }

	/// <summary>The structure as it would be saved.</summary>
	public PropertyRecord Property
	{
		get => _property ?? File?.Property;
		internal set => _property = value;
	}

	/// <summary>For <see cref="ImportRowStatus.Update"/>: "Inspector: 'A. Smith' → 'B. Jones'", one per changed field.</summary>
	public List<string> Changes { get; } = new();

	/// <summary>Values that couldn't be used (left as they were); the row still imports.</summary>
	public List<string> Warnings { get; } = new();

	/// <summary>Reasons the row can't be imported (for an ambiguous match, the structures it matches).</summary>
	public List<string> Problems { get; } = new();

	/// <summary>Required ("red pin") validation issues still open after the import; complete them in the editor.</summary>
	public int MissingRequired { get; internal set; }

	/// <summary>
	/// The update would change an assessment that is Final (or whose letter was sent). It is left as it is and the
	/// import says so; reopen the assessment to take the new values.
	/// </summary>
	public bool IsLocked { get; internal set; }

	/// <summary>Whether <see cref="SourceImport.Import"/> imports this row. Defaults to <see cref="CanImport"/>.</summary>
	public bool Selected { get; set; }

	public bool CanImport => Status is not (ImportRowStatus.Problem or ImportRowStatus.Ambiguous) && Problems.Count == 0 && !IsLocked;

	/// <summary>Percent damaged after the import (1 decimal), as SDE will show it. Null for structures.</summary>
	public decimal? PercentDamage => File?.Assessment?.PercentDamage;

	public string DamageMessage => File?.Assessment?.DamageMessage;

	public string Address => Property is PropertyRecord p ? Text.Join(p.StreetNumber, p.StreetName) : null;

	public Guid? AssessmentID => File?.Assessment?.AssessmentID;

	/// <summary>Resolved source values to remember in the link (last import's merged with this one's).</summary>
	internal Dictionary<string, string> SourceValues { get; set; }

	/// <summary>Square footage to store as the structure's single total shape, when the import sets it.</summary>
	internal decimal? ShapeTotal { get; set; }
}

/// <summary>The result of applying a profile to a file: one row per record. Nothing is saved.</summary>
public sealed class SourcePreview
{
	public SourceShaped Shaped { get; init; }
	public SourceProfile Profile { get; init; }
	public List<ImportRow> Rows { get; } = new();

	/// <summary>Problems with the profile (nothing can be imported until they are fixed).</summary>
	public List<string> MappingProblems { get; } = new();

	/// <summary>Things worth knowing about the profile that don't stop the import (fields a mode doesn't use).</summary>
	public List<string> Notes { get; } = new();

	/// <summary>Source text that needs a translation to be used.</summary>
	public List<SourceUnmatched> Unmatched { get; init; } = new();

	/// <summary>
	/// The building value comes from a column (Adj. Tax Assessed Value) that nobody has confirmed holds the building
	/// value only. One of <see cref="MappingProblems"/>; setting <see cref="SourceProfile.ValueIsBuildingOnly"/> clears it.
	/// </summary>
	public bool NeedsBuildingValueConfirmation { get; internal set; }

	/// <summary>Where that value comes from, for the question: a column name, or "the value typed for every row".</summary>
	public string BuildingValueSource { get; internal set; }

	public int Count(ImportRowStatus status) => Rows.Count(r => r.Status == status);

	/// <summary>Updates that won't be made because the assessment is Final or its letter was sent.</summary>
	public int LockedCount => Rows.Count(r => r.IsLocked);

	/// <summary>Selected rows that would write something (Unchanged rows write nothing).</summary>
	public int ToImportCount => MappingProblems.Count > 0 ? 0 : Rows.Count(r => r.Selected && r.CanImport && r.Status != ImportRowStatus.Unchanged);

	public bool CanImport => ToImportCount > 0;
}

public sealed class SourceImportResult
{
	/// <summary>New assessments (inspections) or new structures (structures).</summary>
	public int Created { get; init; }

	/// <summary>Existing structures linked to their record for the first time (structures only).</summary>
	public int Linked { get; init; }

	public int Updated { get; init; }

	/// <summary>Selected rows that had nothing new.</summary>
	public int Unchanged { get; init; }

	/// <summary>Every selected assessment in the file, written or unchanged (what "Review" shows). Empty for structures.</summary>
	public List<Guid> AssessmentIds { get; init; } = new();

	/// <summary>Every selected structure in the file, written or unchanged.</summary>
	public List<Guid> StructureIds { get; init; } = new();

	/// <summary>Updates that weren't made because the assessment is Final or its letter was sent.</summary>
	public int Locked => LockedIds.Count;

	/// <summary>The assessments behind <see cref="Locked"/>.</summary>
	public List<Guid> LockedIds { get; init; } = new();

	/// <summary>The import's batch in edit history (null when nothing was written).</summary>
	public string BatchId { get; init; }
}

/// <summary>
/// Turns records from another system (a permit system's inspections, a spreadsheet, an assessor roll) into SDE
/// assessments or structures. Every assessment or structure is linked to its record (ExternalLink or StructureLink,
/// both Mac-only), so importing an updated file again only changes the fields whose source value changed: edits made
/// in SDE are kept, a blank cell never clears a value, and the same file twice changes nothing.
///
/// Matching: a record imported before finds its assessment (record + inspection ID) or structure (record ID). A record
/// seen before with a new inspection ID gets a new assessment of the same structure. A new record is matched to a
/// structure already in SDE as the profile says (<see cref="SourceMatchBy"/>): by parcel number or by address, or,
/// for inspections matched by record ID, by a parcel number that matches exactly one structure with no imported
/// assessment (v1.5). A record that matches several structures is shown as ambiguous and not imported.
/// </summary>
public static class SourceImport
{
	/// <summary>Builds every row (loading and recalculating linked assessments). Nothing is saved.</summary>
	public static SourcePreview Preview(SourceShaped shaped, SourceProfile profile, SdeStore store, CancellationToken ct = default)
	{
		ReferenceData reference = store.Reference;
		bool structures = profile.Mode == SourceMode.Structures;
		SourcePreview preview = new SourcePreview
		{
			Shaped = shaped,
			Profile = profile,
			Unmatched = SourceValues.Unmatched(shaped, profile, reference).Where(u => !structures || SourceFields.Get(u.FieldKey).AppliesToStructures).ToList(),
		};
		preview.MappingProblems.AddRange(shaped.Problems);
		SourceTable table = shaped.Table;
		int recordColumn = table.IndexOf(profile.RecordIdColumn);
		int inspectionColumn = structures ? -1 : table.IndexOf(profile.InspectionIdColumn);
		if (string.IsNullOrWhiteSpace(profile.RecordIdColumn))
		{
			preview.MappingProblems.Add(structures
				? "Choose the column that identifies each structure in the file (for example its parcel number)."
				: "Choose the column that holds the record ID.");
		}
		else if (recordColumn < 0)
		{
			preview.MappingProblems.Add($"The record ID column '{profile.RecordIdColumn}' is not in the sheet.");
		}
		if (!structures && !string.IsNullOrWhiteSpace(profile.InspectionIdColumn) && inspectionColumn < 0)
		{
			preview.MappingProblems.Add($"The inspection ID column '{profile.InspectionIdColumn}' is not in the sheet.");
		}
		if (profile.MatchBy == SourceMatchBy.Parcel && !profile.IsMapped(nameof(EnterpriseField.ParcelNumber)))
		{
			preview.MappingProblems.Add("To match by parcel number, choose the column that holds it (Location & owner → Parcel number).");
		}
		if (profile.MatchBy == SourceMatchBy.Address && !profile.IsMapped(nameof(EnterpriseField.StreetNumber)) && !profile.IsMapped(nameof(EnterpriseField.StreetName)))
		{
			preview.MappingProblems.Add("To match by address, choose the columns that hold it (Location & owner → Street number and name).");
		}
		Dictionary<string, int> columns = new(StringComparer.Ordinal);
		foreach (SourceFieldInfo field in SourceFields.All)
		{
			SourceFieldMap map = profile[field.Key];
			if (map == null || string.IsNullOrWhiteSpace(map.Column))
			{
				continue;
			}
			int i = table.IndexOf(map.Column);
			if (i < 0)
			{
				preview.MappingProblems.Add($"Column '{map.Column}' (mapped to {field.Label}) is not in the sheet.");
			}
			columns[field.Key] = i;
		}
		if (structures)
		{
			int ignored = SourceFields.All.Count(f => !f.AppliesToStructures && profile.IsMapped(f.Key));
			if (ignored > 0)
			{
				preview.Notes.Add(ignored == 1
					? "1 mapped field is about an inspection, so it isn't used when importing structures."
					: $"{ignored} mapped fields are about an inspection, so they aren't used when importing structures.");
			}
		}
		else
		{
			CheckBuildingValue(preview, profile, table, reference);
		}
		if (preview.MappingProblems.Count > 0)
		{
			return preview;
		}

		SourceRowBuilder builder = new SourceRowBuilder(profile, store, columns);
		Matcher matcher = new Matcher(profile, store, builder, structures);
		Dictionary<string, int> seenKeys = new(StringComparer.OrdinalIgnoreCase);
		foreach (SourceRow source in table.Rows)
		{
			ct.ThrowIfCancellationRequested();
			string recordId = recordColumn >= 0 ? Text.Clean(source[recordColumn]) : null;
			string inspectionId = inspectionColumn >= 0 ? Text.Clean(source[inspectionColumn]) ?? "" : "";
			ImportRow row = new ImportRow { RowNumber = source.RowNumber, RecordId = recordId, InspectionId = inspectionId };
			preview.Rows.Add(row);
			if (recordId == null)
			{
				Fail(row, "No record ID");
				continue;
			}
			string key = Key(recordId, inspectionId);
			if (seenKeys.TryGetValue(key, out int first))
			{
				Fail(row, structures ? $"Same record as row {first}"
					: inspectionId.Length > 0 ? $"Same record and inspection as row {first}"
					: $"Same record as row {first}; choose an inspection ID column to import each inspection");
				continue;
			}
			seenKeys[key] = row.RowNumber;

			Dictionary<string, string> values = builder.ReadValues(source, row.Warnings);
			try
			{
				if (structures)
				{
					BuildStructure(row, values, builder, matcher, store);
				}
				else
				{
					BuildInspection(row, key, values, builder, matcher, store);
				}
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				row.Problems.Add("Couldn't build the " + (structures ? "structure" : "assessment") + ": " + e.Message);
			}
			if (row.Problems.Count > 0 && row.Status != ImportRowStatus.Ambiguous)
			{
				row.Status = ImportRowStatus.Problem;
			}
			row.Selected = row.CanImport;
		}
		return preview;
	}

	/// <summary>
	/// Saves the selected rows that have something new, with their links, in one transaction and as one batch in edit
	/// history, and returns what happened. <paramref name="sourceFile"/> is remembered in the links (name only).
	/// It is one transaction, so it can't be stopped halfway: all of it is saved, or none.
	/// </summary>
	public static SourceImportResult Import(SdeStore store, SourcePreview preview, string sourceFile)
	{
		if (preview.MappingProblems.Count > 0)
		{
			throw new InvalidOperationException("The import profile has problems: " + string.Join(" ", preview.MappingProblems));
		}
		SourceProfile profile = preview.Profile;
		string source = profile.EffectiveSourceName;
		List<ImportRow> selected = preview.Rows.Where(r => r.Selected && r.CanImport).ToList();
		List<ImportRow> write = selected.Where(r => r.Status != ImportRowStatus.Unchanged).ToList();
		List<Guid> lockedInPreview = preview.Rows.Where(r => r.IsLocked && r.AssessmentID is Guid).Select(r => r.AssessmentID.Value).ToList();
		string fileName = string.IsNullOrEmpty(sourceFile) ? null : Path.GetFileName(sourceFile);
		SaveContext ctx = SaveContext.Batch(SaveVia.Import, "Imported from " + (fileName ?? source));

		if (profile.Mode == SourceMode.Structures)
		{
			store.SaveStructureImport(write.Select(r => new ImportedStructure
			{
				Property = r.Property,
				ShapeTotal = r.ShapeTotal,
				Link = new StructureLinkRecord
				{
					Source = source,
					RecordId = r.RecordId,
					SourceValues = SerializeValues(r.SourceValues),
					SourceFile = fileName,
				},
			}).ToList(), ctx);
			return new SourceImportResult
			{
				Created = write.Count(r => r.Status == ImportRowStatus.NewStructure),
				Linked = write.Count(r => r.Status == ImportRowStatus.Attach),
				Updated = write.Count(r => r.Status == ImportRowStatus.Update),
				Unchanged = selected.Count - write.Count,
				StructureIds = selected.Select(r => r.Property.PropertyID).Distinct().ToList(),
				BatchId = write.Count > 0 ? ctx.BatchId : null,
			};
		}

		IReadOnlyList<Guid> locked = store.SaveImport(write.Select(r => new ImportedAssessment
		{
			File = r.File,
			ShapeTotal = r.ShapeTotal,
			Link = new ExternalLinkRecord
			{
				Source = source,
				RecordId = r.RecordId,
				InspectionId = r.InspectionId ?? "",
				SourceValues = SerializeValues(r.SourceValues),
				SourceFile = fileName,
			},
		}).ToList(), ctx);
		return new SourceImportResult
		{
			Created = write.Count(r => r.Status != ImportRowStatus.Update),
			Updated = write.Count(r => r.Status == ImportRowStatus.Update) - locked.Count,
			LockedIds = lockedInPreview.Concat(locked).Distinct().ToList(),
			Unchanged = selected.Count - write.Count,
			AssessmentIds = selected.Select(r => r.File.Assessment.AssessmentID).ToList(),
			StructureIds = selected.Select(r => r.File.Property.PropertyID).Distinct().ToList(),
			BatchId = write.Count > locked.Count ? ctx.BatchId : null,
		};
	}

	// ───────────────────────── Inspections ─────────────────────────

	private static void BuildInspection(ImportRow row, string key, Dictionary<string, string> values, SourceRowBuilder builder, Matcher matcher, SdeStore store)
	{
		string recordId = row.RecordId;
		if (matcher.Links.TryGetValue(key, out ExternalLinkRecord link) && store.LoadAssessment(link.AssessmentID) is AssessmentFile existing)
		{
			BuildUpdate(row, existing, DeserializeValues(link.SourceValues), values, builder);
			if (row.Status == ImportRowStatus.Update && matcher.IsLocked(existing.Assessment.AssessmentID))
			{
				row.IsLocked = true;
			}
			matcher.FileRecords.TryAdd(recordId, existing.Property);
		}
		else if (matcher.FileRecords.TryGetValue(recordId, out PropertyRecord sameRecord))
		{
			row.Status = ImportRowStatus.NewInspection;
			builder.BuildNew(row, sameRecord, isNewStructure: false, values);
		}
		else if (matcher.LinkedRecords.TryGetValue(recordId, out Guid linkedProperty) && store.GetProperty(linkedProperty) is PropertyRecord linkedStructure)
		{
			row.Status = ImportRowStatus.NewInspection;
			matcher.FileRecords[recordId] = linkedStructure;
			builder.BuildNew(row, linkedStructure, isNewStructure: false, values);
		}
		else if (matcher.Find(values, row) is PropertyRecord match)
		{
			row.Status = ImportRowStatus.Attach;
			matcher.Claim(match, recordId, row.RowNumber);
			matcher.FileRecords[recordId] = match;
			builder.BuildNew(row, match, isNewStructure: false, values);
		}
		else if (row.Status != ImportRowStatus.Ambiguous)
		{
			row.Status = ImportRowStatus.NewStructure;
			PropertyRecord p = new PropertyRecord { Active = true };
			matcher.FileRecords[recordId] = p;
			builder.BuildNew(row, p, isNewStructure: true, values);
			if (string.IsNullOrWhiteSpace(p.StreetNumber) && string.IsNullOrWhiteSpace(p.StreetName))
			{
				row.Problems.Add("No street number or street name");
			}
		}
	}

	private static void BuildUpdate(ImportRow row, AssessmentFile existing, Dictionary<string, string> last, Dictionary<string, string> values, SourceRowBuilder builder)
	{
		HashSet<string> changed = Changed(row, last, values);
		if (changed.Count == 0)
		{
			row.Status = ImportRowStatus.Unchanged;
			row.File = existing;
			builder.Validate(row);
			return;
		}
		row.Status = ImportRowStatus.Update;
		builder.BuildUpdate(row, existing, values, changed);
	}

	// ───────────────────────── Structures ─────────────────────────

	private static void BuildStructure(ImportRow row, Dictionary<string, string> values, SourceRowBuilder builder, Matcher matcher, SdeStore store)
	{
		if (matcher.StructureLinks.TryGetValue(row.RecordId, out StructureLinkRecord link) && store.GetProperty(link.PropertyID) is PropertyRecord { Active: true } existing)
		{
			HashSet<string> changed = Changed(row, DeserializeValues(link.SourceValues), values);
			row.Property = existing;
			row.Status = changed.Count == 0 ? ImportRowStatus.Unchanged : ImportRowStatus.Update;
			if (changed.Count > 0)
			{
				builder.BuildStructure(row, existing, values, changed, fillBlanksOnly: false, isNewStructure: false);
			}
			matcher.Claim(existing, row.RecordId, row.RowNumber);
			return;
		}
		if (matcher.Find(values, row) is PropertyRecord match)
		{
			row.Status = ImportRowStatus.Attach;
			row.SourceValues = values;
			matcher.Claim(match, row.RecordId, row.RowNumber);
			builder.BuildStructure(row, match, values, values.Keys.ToHashSet(StringComparer.Ordinal), fillBlanksOnly: true, isNewStructure: false);
			return;
		}
		if (row.Status == ImportRowStatus.Ambiguous || row.Problems.Count > 0)
		{
			return;
		}
		row.Status = ImportRowStatus.NewStructure;
		row.SourceValues = values;
		PropertyRecord p = new PropertyRecord { Active = true };
		builder.BuildStructure(row, p, values, values.Keys.ToHashSet(StringComparer.Ordinal), fillBlanksOnly: false, isNewStructure: true);
		if (string.IsNullOrWhiteSpace(p.StreetNumber) && string.IsNullOrWhiteSpace(p.StreetName))
		{
			row.Problems.Add("No street number or street name");
		}
	}

	// ───────────────────────── Shared ─────────────────────────

	/// <summary>
	/// The fields whose value is present now and differs from the last import (a blank cell never clears anything),
	/// listed in <see cref="ImportRow.Changes"/>; the row remembers the last import's values merged with these.
	/// </summary>
	private static HashSet<string> Changed(ImportRow row, Dictionary<string, string> last, Dictionary<string, string> values)
	{
		HashSet<string> changed = values.Where(kv => !last.TryGetValue(kv.Key, out string old) || old != kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
		Dictionary<string, string> merged = new(last, StringComparer.Ordinal);
		foreach ((string k, string v) in values)
		{
			merged[k] = v;
		}
		row.SourceValues = merged;
		foreach (SourceFieldInfo f in SourceFields.All.Where(f => changed.Contains(f.Key)))
		{
			row.Changes.Add($"{f.Label}: {(last.TryGetValue(f.Key, out string old) ? "'" + old + "'" : "(not imported)")} → '{values[f.Key]}'");
		}
		return changed;
	}

	/// <summary>
	/// Flag 4: a value used as the building value (Adj. Tax Assessed Value) must be the building's value only, not
	/// land and building together. Until the person confirms it, the import refuses.
	/// </summary>
	private static void CheckBuildingValue(SourcePreview preview, SourceProfile profile, SourceTable table, ReferenceData reference)
	{
		SourceFieldMap value = profile["TaxAssessedValue"];
		SourceFieldMap basis = profile["ValueBasis"];
		if (value == null || basis == null || profile.ValueIsBuildingOnly)
		{
			return;
		}
		string taxBasis = reference.LookupName(LookupIds.ValueAdjustedTax);
		SourceFieldInfo field = SourceFields.Get("ValueBasis");
		bool Uses(string text) => SourceValues.TryResolve(field, text, basis, reference, out string resolved) && resolved == taxBasis;
		int column = table.IndexOf(basis.Column);
		bool used = (Text.Clean(basis.Constant) is string constant && Uses(constant))
			|| (column >= 0 && table.Rows.Any(r => Text.Clean(r[column]) is string cell && Uses(cell)));
		if (!used)
		{
			return;
		}
		string from = string.IsNullOrWhiteSpace(value.Column) ? "the value typed for every row" : $"the column '{value.Column}'";
		preview.NeedsBuildingValueConfirmation = true;
		preview.BuildingValueSource = from;
		preview.MappingProblems.Add($"The building value would come from {from} ({taxBasis}). FEMA counts the building only, never the land. "
			+ "Confirm that it is the building (improvement) value, not land and building together, before importing.");
	}

	private static void Fail(ImportRow row, string problem)
	{
		row.Problems.Add(problem);
		row.Status = ImportRowStatus.Problem;
		row.Selected = false;
	}

	private static string Key(string recordId, string inspectionId) => recordId.Trim() + "\u001f" + (inspectionId ?? "").Trim();

	internal static string SerializeValues(Dictionary<string, string> values) =>
		values == null ? null : JsonSerializer.Serialize(new SortedDictionary<string, string>(values, StringComparer.Ordinal));

	internal static Dictionary<string, string> DeserializeValues(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new Dictionary<string, string>(StringComparer.Ordinal);
		}
		try
		{
			return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(), StringComparer.Ordinal);
		}
		catch (JsonException)
		{
			return new Dictionary<string, string>(StringComparer.Ordinal);
		}
	}

	/// <summary>What one preview knows about the database: links from this source, and structures to match against.</summary>
	private sealed class Matcher
	{
		private readonly SourceProfile _profile;
		private readonly SourceRowBuilder _builder;
		private readonly ReferenceData _ref;
		private readonly bool _structures;
		private readonly Dictionary<Guid, AssessmentStatusRecord> _statuses;
		private readonly HashSet<Guid> _excluded;
		private readonly ILookup<string, PropertyRecord> _byExactParcel;
		private readonly ILookup<string, PropertyRecord> _byParcel;
		private readonly AddressMatcher _addresses;
		private readonly AddressIndex<PropertyRecord> _byAddress;
		private readonly Dictionary<Guid, (string RecordId, int Row)> _claimedBy = new();

		public Matcher(SourceProfile profile, SdeStore store, SourceRowBuilder builder, bool structures)
		{
			_profile = profile;
			_builder = builder;
			_ref = store.Reference;
			_structures = structures;
			string source = profile.EffectiveSourceName;
			if (structures)
			{
				foreach (StructureLinkRecord link in store.GetStructureLinks(source))
				{
					StructureLinks.TryAdd(link.RecordId, link);
				}
				// A structure already linked to another record of this source isn't free for a new one.
				_excluded = StructureLinks.Values.Select(l => l.PropertyID).ToHashSet();
			}
			else
			{
				foreach (ExternalLinkRecord link in store.GetExternalLinks(source))
				{
					Links.TryAdd(Key(link.RecordId, link.InspectionId), link);
					LinkedRecords.TryAdd(link.RecordId, link.PropertyID);
				}
				_statuses = store.GetStatuses();
				// v1.5's rule for matching by record ID: only structures with no imported assessment from any source.
				_excluded = profile.MatchBy == SourceMatchBy.RecordId ? store.LinkedPropertyIds() : new HashSet<Guid>();
			}
			List<PropertyRecord> properties = store.GetProperties();
			switch (profile.MatchBy)
			{
				case SourceMatchBy.RecordId when !structures:
					_byExactParcel = properties.Where(p => !string.IsNullOrWhiteSpace(p.ParcelNumber))
						.ToLookup(p => p.ParcelNumber.Trim(), StringComparer.OrdinalIgnoreCase);
					break;
				case SourceMatchBy.Parcel:
					_byParcel = properties.Where(p => ParcelKey(p.ParcelNumber) != null).ToLookup(p => ParcelKey(p.ParcelNumber), StringComparer.Ordinal);
					break;
				case SourceMatchBy.Address:
					_addresses = new AddressMatcher(_ref, profile.AddressMatchUnit);
					_byAddress = _addresses.Index(properties, _addresses.KeyFor);
					break;
			}
		}

		/// <summary>Inspections: links by record + inspection.</summary>
		public Dictionary<string, ExternalLinkRecord> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Inspections: the structure of each record imported before.</summary>
		public Dictionary<string, Guid> LinkedRecords { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Inspections: structures used earlier in this file, by record.</summary>
		public Dictionary<string, PropertyRecord> FileRecords { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Structures: links by record.</summary>
		public Dictionary<string, StructureLinkRecord> StructureLinks { get; } = new(StringComparer.OrdinalIgnoreCase);

		public bool IsLocked(Guid assessmentId) => _statuses != null && _statuses.TryGetValue(assessmentId, out AssessmentStatusRecord s) && s.IsLocked;

		public void Claim(PropertyRecord p, string recordId, int rowNumber)
		{
			if (p.PropertyID != Guid.Empty)
			{
				_claimedBy.TryAdd(p.PropertyID, (recordId, rowNumber));
			}
		}

		/// <summary>
		/// The one structure in SDE this new record belongs to, or null. Several matches make the row
		/// <see cref="ImportRowStatus.Ambiguous"/> (listing them); matching by record ID keeps v1.5's behaviour.
		/// </summary>
		public PropertyRecord Find(Dictionary<string, string> values, ImportRow row)
		{
			switch (_profile.MatchBy)
			{
				case SourceMatchBy.RecordId:
					return _structures ? null : LegacyParcelMatch(values, row);
				case SourceMatchBy.Parcel:
				{
					if (!values.TryGetValue(nameof(EnterpriseField.ParcelNumber), out string parcel) || ParcelKey(parcel) is not string key)
					{
						row.Warnings.Add("No parcel number to match with; a new structure will be created");
						return null;
					}
					return Choose(_byParcel[key].ToList(), row, "parcel " + parcel);
				}
				default:
				{
					AddressKey key = _addresses.KeyFor(_builder.ReadAddress(values));
					if (!key.CanMatch)
					{
						row.Warnings.Add("No house number and street to match with; a new structure will be created");
						return null;
					}
					return Choose(_byAddress.Find(key).ToList(), row, "this address");
				}
			}
		}

		private PropertyRecord Choose(List<PropertyRecord> matches, ImportRow row, string what)
		{
			List<PropertyRecord> free = matches.Where(p => !_excluded.Contains(p.PropertyID)).ToList();
			if (_structures)
			{
				// Two records of one file can't both become the same structure.
				List<PropertyRecord> taken = free.Where(p => _claimedBy.ContainsKey(p.PropertyID)).ToList();
				if (taken.Count > 0 && taken.Count == free.Count)
				{
					(string other, int at) = _claimedBy[taken[0].PropertyID];
					row.Problems.Add($"Matches the same structure as record {other} (row {at}); not imported");
					return null;
				}
				free = free.Except(taken).ToList();
			}
			if (free.Count <= 1)
			{
				return free.FirstOrDefault();
			}
			row.Status = ImportRowStatus.Ambiguous;
			string list = string.Join("; ", free.Take(5).Select(Describe)) + (free.Count > 5 ? $"; and {free.Count - 5} more" : "");
			row.Problems.Add($"Ambiguous: {free.Count} structures have {what} ({list}). Not imported: fix or delete the duplicates, or match by something else.");
			return null;
		}

		/// <summary>v1.5: a parcel number (exact, ignoring case and spaces around it) shared with exactly one free structure.</summary>
		private PropertyRecord LegacyParcelMatch(Dictionary<string, string> values, ImportRow row)
		{
			if (!values.TryGetValue(nameof(EnterpriseField.ParcelNumber), out string parcel))
			{
				return null;
			}
			List<PropertyRecord> free = _byExactParcel[parcel.Trim()].Where(p => !_excluded.Contains(p.PropertyID)).ToList();
			if (free.Count == 1)
			{
				if (_claimedBy.TryGetValue(free[0].PropertyID, out (string RecordId, int Row) other))
				{
					row.Warnings.Add($"Parcel {parcel} is already used by record {other.RecordId} in this file; a new structure will be created.");
					return null;
				}
				return free[0];
			}
			if (free.Count > 1)
			{
				row.Warnings.Add($"{free.Count} structures have parcel {parcel}; a new structure will be created. (Delete the duplicate first to attach to it.)");
			}
			return null;
		}

		private string ParcelKey(string parcel) => ParcelNumbers.Normalize(parcel, _profile.ParcelIgnoreLeadingZeros);

		private string Describe(PropertyRecord p)
		{
			string street = Text.Join(p.StreetNumber, p.StreetName, _ref.LookupName(p.StreetSuffixID));
			string parcel = string.IsNullOrWhiteSpace(p.ParcelNumber) ? null : "parcel " + p.ParcelNumber.Trim();
			return string.Join(", ", new[] { Text.Clean(street) ?? "no address", Text.Clean(p.City), parcel }.Where(x => x != null));
		}
	}
}
