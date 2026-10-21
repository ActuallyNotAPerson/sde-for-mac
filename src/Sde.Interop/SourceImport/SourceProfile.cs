using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sde.Interop;

/// <summary>How the file is laid out. The names are kept from v1.5 so its saved profiles still read.</summary>
public enum SourceLayout
{
	/// <summary>Each row is one record (an inspection, a permit, a parcel), with a column per value.</summary>
	OneRowPerInspection,

	/// <summary>
	/// Each row is one checklist item of a record: an item-name column and a value column. Rows with the same record
	/// and inspection ID are combined into one, with a "Checklist: &lt;item&gt;" column per item.
	/// </summary>
	OneRowPerChecklistItem,
}

/// <summary>What each row of the file becomes.</summary>
public enum SourceMode
{
	/// <summary>One assessment per record (or per record and inspection), with its structure.</summary>
	Inspections,

	/// <summary>
	/// Structures only (a parcel list, an assessor roll, a building inventory): each record creates or updates one
	/// structure, linked to it in the StructureLink table. No assessments are made.
	/// </summary>
	Structures,
}

/// <summary>How a record not imported before is matched to a structure already in SDE.</summary>
public enum SourceMatchBy
{
	/// <summary>
	/// Only by the record ID. For inspections, a new record is still added to an existing structure when its parcel
	/// number matches exactly one structure that has no imported assessment (as v1.5 did).
	/// </summary>
	RecordId,

	/// <summary>By parcel number, ignoring spaces, dashes and dots (and leading zeros when the profile says so).</summary>
	Parcel,

	/// <summary>By house number and street name, ignoring case, punctuation and how the street suffix is written.</summary>
	Address,
}

/// <summary>Where one SDE field's value comes from: a column, or the same constant for every row.</summary>
public sealed class SourceFieldMap
{
	/// <summary>The source column (after the checklist pivot), or null.</summary>
	public string Column { get; set; }

	/// <summary>The value to use for every row (when no column is mapped, or the cell is blank), e.g. "Flood" or "1.15".</summary>
	public string Constant { get; set; }

	/// <summary>Source text → a FEMA choice name (or a number), e.g. "Slab on grade" → "Slab - on - Grade", "Major" → "75". Matched ignoring case.</summary>
	public Dictionary<string, string> Translate { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	[JsonIgnore]
	public bool IsMapped => !string.IsNullOrWhiteSpace(Column) || !string.IsNullOrWhiteSpace(Constant);
}

/// <summary>
/// How one file layout maps to SDE: the source's name, what each row becomes, how records are matched, the ID
/// columns, an optional filter, and a column or constant per field with any translations. Saved as a JSON file, so it
/// survives a new database and can be shared. Profiles saved by v1.5 (Accela import) still read: they become
/// Inspections profiles whose source is "Accela".
/// </summary>
public sealed class SourceProfile
{
	/// <summary>The source name a profile gets when nobody has named it.</summary>
	public const string DefaultSourceName = "Imported";

	/// <summary>The source name of profiles saved by v1.5, which only imported from Accela.</summary>
	public const string V15SourceName = "Accela";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() },
	};

	public string Name { get; set; } = "Import";

	/// <summary>
	/// The system or list the records come from, in the person's words ("Permits", "County assessor", "Accela"). It is
	/// written to every link the import makes, so two sources with the same record IDs never collide, and it's shown
	/// next to the record ID in Structures.
	/// </summary>
	public string SourceName { get; set; } = DefaultSourceName;

	public SourceMode Mode { get; set; }

	public SourceMatchBy MatchBy { get; set; }

	/// <summary>For matching by parcel: "00123" and "123" are the same parcel.</summary>
	public bool ParcelIgnoreLeadingZeros { get; set; }

	/// <summary>For matching by address: the unit (apartment, suite) must match too. Off: units are ignored.</summary>
	public bool AddressMatchUnit { get; set; }

	/// <summary>
	/// The person confirmed that the column used as the building value (Adj. Tax Assessed Value) holds the value of the
	/// building only, not land and building together. Required before that basis is set (FEMA P-758: the market value
	/// of the structure excludes the land).
	/// </summary>
	public bool ValueIsBuildingOnly { get; set; }

	public SourceLayout Layout { get; set; }

	/// <summary>For <see cref="SourceLayout.OneRowPerChecklistItem"/>: the column naming the checklist item.</summary>
	public string ItemColumn { get; set; }

	/// <summary>For <see cref="SourceLayout.OneRowPerChecklistItem"/>: the column holding the item's value.</summary>
	public string ValueColumn { get; set; }

	/// <summary>The record ID (e.g. "BLD-2026-00123", or the parcel number for an assessor roll). Required.</summary>
	public string RecordIdColumn { get; set; }

	/// <summary>The inspection ID within the record; null = one assessment per record. Not used for structures.</summary>
	public string InspectionIdColumn { get; set; }

	/// <summary>Only rows whose value in this column is one of <see cref="FilterValues"/> are imported (e.g. Inspection Type).</summary>
	public string FilterColumn { get; set; }

	public List<string> FilterValues { get; set; } = new();

	public NameParsing FirstNameParsing { get; set; }
	public NameParsing LastNameParsing { get; set; }
	public StreetNumberParsing StreetNumberParsing { get; set; }
	public StreetNameParsing StreetNameParsing { get; set; }
	public SuffixParsing SuffixParsing { get; set; }

	/// <summary>New assessments and structures start from the "Enter Default Data" values (Settings), with the file's values on top.</summary>
	public bool UseDefaultValues { get; set; } = true;

	/// <summary>The assessment custom field (1–3) that receives the record ID, so it reaches the FEMA export; 0 = none.</summary>
	public int RecordIdCustomField { get; set; }

	/// <summary>Field key (<see cref="SourceFieldInfo.Key"/>) → where its value comes from.</summary>
	public Dictionary<string, SourceFieldMap> Fields { get; set; } = new(StringComparer.Ordinal);

	/// <summary>Keys the file had that this version doesn't know (reported by <see cref="FromJson(string, out List{string})"/>, not saved).</summary>
	[JsonExtensionData]
	public Dictionary<string, JsonElement> Unknown { get; set; }

	/// <summary>The source name to write to links: <see cref="SourceName"/>, or <see cref="DefaultSourceName"/> when blank.</summary>
	[JsonIgnore]
	public string EffectiveSourceName => string.IsNullOrWhiteSpace(SourceName) ? DefaultSourceName : SourceName.Trim();

	/// <summary>The map for a field, or null when the field isn't mapped.</summary>
	public SourceFieldMap this[string key] => Fields.TryGetValue(key, out SourceFieldMap m) && m.IsMapped ? m : null;

	public bool IsMapped(string key) => this[key] != null;

	/// <summary>The map for a field, created when missing.</summary>
	public SourceFieldMap Map(string key)
	{
		if (!Fields.TryGetValue(key, out SourceFieldMap m))
		{
			m = new SourceFieldMap();
			Fields[key] = m;
		}
		return m;
	}

	/// <summary>The mapping in the Enterprise Import form, for the shared address and owner parsing.</summary>
	internal EnterpriseMapping ToEnterpriseMapping()
	{
		EnterpriseMapping m = new EnterpriseMapping
		{
			FirstNameParsing = FirstNameParsing,
			LastNameParsing = LastNameParsing,
			StreetNumberParsing = StreetNumberParsing,
			StreetNameParsing = StreetNameParsing,
			SuffixParsing = SuffixParsing,
		};
		foreach (SourceFieldInfo f in SourceFields.All.Where(f => f.Property is not null && IsMapped(f.Key)))
		{
			m[f.Property.Value] = f.Key;
		}
		return m;
	}

	public string ToJson()
	{
		Dictionary<string, SourceFieldMap> fields = Fields;
		Fields = new Dictionary<string, SourceFieldMap>(
			fields.Where(kv => kv.Value.IsMapped || kv.Value.Translate.Count > 0).OrderBy(kv => FieldOrder(kv.Key)),
			StringComparer.Ordinal);
		Dictionary<string, JsonElement> unknown = Unknown;
		Unknown = null;
		try
		{
			return JsonSerializer.Serialize(this, JsonOptions);
		}
		finally
		{
			Fields = fields;
			Unknown = unknown;
		}
	}

	/// <summary>
	/// Reads a saved profile. Settings and fields this version doesn't know are dropped and listed in
	/// <paramref name="warnings"/>. A v1.5 profile (no SourceName) imports inspections from "Accela", as it always did.
	/// </summary>
	public static SourceProfile FromJson(string json, out List<string> warnings)
	{
		warnings = new List<string>();
		SourceProfile p = JsonSerializer.Deserialize<SourceProfile>(json, JsonOptions) ?? new SourceProfile();
		bool hasSourceName;
		using (JsonDocument doc = JsonDocument.Parse(json))
		{
			hasSourceName = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(nameof(SourceName), out _);
		}
		if (!hasSourceName)
		{
			p.SourceName = V15SourceName;
		}
		if (p.Unknown != null)
		{
			warnings.AddRange(p.Unknown.Keys.Select(k => $"Setting '{k}' isn't used by this version and was ignored."));
			p.Unknown = null;
		}
		Dictionary<string, SourceFieldMap> fields = new(StringComparer.Ordinal);
		foreach ((string key, SourceFieldMap map) in p.Fields ?? new Dictionary<string, SourceFieldMap>())
		{
			if (SourceFields.Get(key) == null)
			{
				warnings.Add($"Field '{key}' isn't known to this version and was ignored.");
				continue;
			}
			map.Translate = new Dictionary<string, string>(map.Translate ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
			fields[key] = map;
		}
		p.Fields = fields;
		p.FilterValues ??= new List<string>();
		if (p.RecordIdCustomField is < 0 or > 3)
		{
			warnings.Add($"Custom field {p.RecordIdCustomField} doesn't exist; the record ID won't be copied to a custom field.");
			p.RecordIdCustomField = 0;
		}
		return p;
	}

	public static SourceProfile FromJson(string json) => FromJson(json, out _);

	public SourceProfile Clone() => FromJson(ToJson());

	private static int FieldOrder(string key)
	{
		for (int i = 0; i < SourceFields.All.Count; i++)
		{
			if (SourceFields.All[i].Key == key)
			{
				return i;
			}
		}
		return int.MaxValue;
	}
}
