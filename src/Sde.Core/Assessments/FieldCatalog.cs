using System.Globalization;
using System.Reflection;
using Sde.Core.Records;

namespace Sde.Core.Assessments;

/// <summary>How a field's value is edited, compared and shown.</summary>
public enum FieldKind
{
	Text,
	MultilineText,
	Number,
	Date,
	/// <summary>A yes/no tick box (a bool column).</summary>
	YesNo,
	/// <summary>One of FEMA's choices: a Lookup of <see cref="FieldInfo.LookupType"/>.</summary>
	Lookup,
	State,
	County,
	/// <summary>Residential (1) or non-residential (2).</summary>
	StructureType,
}

/// <summary>Which record a field is stored on.</summary>
public enum FieldModel
{
	Property,
	Assessment,
}

/// <summary>
/// One field a person can edit on an assessment or its structure. <see cref="Member"/> is the record property (= FEMA
/// database column); <see cref="Key"/> is "Property.Member" or "Assessment.Member", which edit history, bulk edit, help
/// and the phone form use to refer to it.
/// </summary>
/// <param name="Step">The editor step it's on (1 Location &amp; owner … 8 Photos &amp; notes).</param>
public sealed record FieldInfo(FieldModel Model, string Member, string Label, int Step, FieldKind Kind, int LookupType = 0)
{
	public string Key => Model + "." + Member;

	/// <summary>
	/// Identifies one building (address, owner, mailing details, parcel, coordinates): never set on many structures at
	/// once or copied from another structure.
	/// </summary>
	public bool PerStructure { get; init; }

	/// <summary>Display format for numbers, e.g. "N2" for money.</summary>
	public string Format { get; init; } = "0.##";

	/// <summary>"$", "ft", "%" … shown with the value.</summary>
	public string Unit { get; init; }

	/// <summary>When the field applies (e.g. only to residential buildings, or only to flood damage). Null = always.</summary>
	public Func<AssessmentFile, bool> AppliesWhen { get; init; }

	public bool AppliesTo(AssessmentFile file) => AppliesWhen?.Invoke(file) ?? true;

	public Type RecordType => Model == FieldModel.Property ? typeof(PropertyRecord) : typeof(AssessmentRecord);

	public PropertyInfo RecordProperty => RecordType.GetProperty(Member);
}

/// <summary>
/// Every editable field on an assessment and its structure, grouped like the editor's steps (the editor shows each of
/// these; a test keeps the two in step). Also reads, writes, compares and shows field values, so bulk edit, copy-from,
/// edit history and the phone form all treat a field the same way.
/// </summary>
public static class FieldCatalog
{
	public const int LocationStep = 1;
	public const int BuildingStep = 2;
	public const int InspectionStep = 3;
	public const int FloodZoneStep = 4;
	public const int CostStep = 5;
	public const int DamageStep = 6;
	public const int ResultStep = 7;
	public const int NotesStep = 8;

	/// <summary>The editor's steps, in order (index 0 = step 1).</summary>
	public static readonly IReadOnlyList<string> Steps = new[]
	{
		"Location & owner", "Building", "Inspection", "Flood zone", "Cost & depreciation", "Damage by element", "Result", "Photos & notes",
	};

	public static readonly IReadOnlyList<FieldInfo> All = Build();

	private static readonly Dictionary<string, FieldInfo> ByKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);

	public static FieldInfo Get(string key) => key != null && ByKey.TryGetValue(key, out FieldInfo f) ? f : null;

	public static FieldInfo Find(FieldModel model, string member) => Get(model + "." + member);

	/// <summary>The history key for an element's % damaged, e.g. "Element.Foundation".</summary>
	public static string ElementKey(string elementName) => "Element." + elementName;

	public static bool IsFlood(AssessmentFile f) => f.Assessment.CauseofDamageID is LookupIds.CauseFlood or LookupIds.CauseFloodAndWind;

	private static List<FieldInfo> Build()
	{
		List<FieldInfo> list = new();
		FieldInfo Add(FieldInfo f)
		{
			list.Add(f);
			return f;
		}
		FieldInfo P(string member, string label, int step, FieldKind kind = FieldKind.Text, int lookup = 0) =>
			Add(new FieldInfo(FieldModel.Property, member, label, step, kind, lookup));
		FieldInfo A(string member, string label, int step, FieldKind kind = FieldKind.Text, int lookup = 0) =>
			Add(new FieldInfo(FieldModel.Assessment, member, label, step, kind, lookup));
		Func<AssessmentFile, bool> res = f => f.Property.StructureTypeID == 1;
		Func<AssessmentFile, bool> nonRes = f => f.Property.StructureTypeID == 2;
		Func<AssessmentFile, bool> flood = IsFlood;

		// 1. Location & owner: all of it identifies one building.
		FieldInfo L(string member, string label, FieldKind kind = FieldKind.Text, int lookup = 0) =>
			new FieldInfo(FieldModel.Property, member, label, LocationStep, kind, lookup) { PerStructure = true };
		foreach (FieldInfo f in new[]
		{
			L(nameof(PropertyRecord.StreetNumber), "Street number"),
			L(nameof(PropertyRecord.StreetName), "Street name"),
			L(nameof(PropertyRecord.StreetSuffixID), "Street suffix", FieldKind.Lookup, LookupTypes.StreetSuffix),
			L(nameof(PropertyRecord.City), "City"),
			L(nameof(PropertyRecord.StateID), "State", FieldKind.State),
			L(nameof(PropertyRecord.CountyID), "County / parish", FieldKind.County),
			L(nameof(PropertyRecord.ZipCode), "ZIP code"),
			L(nameof(PropertyRecord.OwnerFirstName), "Owner first name"),
			L(nameof(PropertyRecord.OwnerLastName), "Owner last name"),
			L(nameof(PropertyRecord.PhoneNumber), "Owner phone"),
			L(nameof(PropertyRecord.CellPhone), "Owner cell phone"),
			L(nameof(PropertyRecord.MailingOwnerFirstName), "Mailing first name"),
			L(nameof(PropertyRecord.MailingOwnerLastName), "Mailing last name"),
			L(nameof(PropertyRecord.MailingCareOf), "Mailing care of"),
			L(nameof(PropertyRecord.MailingStreetNumber), "Mailing street number"),
			L(nameof(PropertyRecord.MailingStreetName), "Mailing street name"),
			L(nameof(PropertyRecord.MailingStreetSuffixID), "Mailing street suffix", FieldKind.Lookup, LookupTypes.StreetSuffix),
			L(nameof(PropertyRecord.MailingCity), "Mailing city"),
			L(nameof(PropertyRecord.MailingStateID), "Mailing state", FieldKind.State),
			L(nameof(PropertyRecord.MailingCountyID), "Mailing county / parish", FieldKind.County),
			L(nameof(PropertyRecord.MailingZipCode), "Mailing ZIP code"),
			L(nameof(PropertyRecord.MailingPhoneNumber), "Mailing phone"),
			L(nameof(PropertyRecord.ParcelNumber), "Parcel number"),
			L(nameof(PropertyRecord.LotNumber), "Lot number"),
			L(nameof(PropertyRecord.Subdivision), "Subdivision"),
			L(nameof(PropertyRecord.Latitude), "Latitude", FieldKind.Number) with { Format = "0.######" },
			L(nameof(PropertyRecord.Longitude), "Longitude", FieldKind.Number) with { Format = "0.######" },
		})
		{
			Add(f);
		}

		// 2. Building
		P(nameof(PropertyRecord.StructureTypeID), "Structure type", BuildingStep, FieldKind.StructureType);
		P(nameof(PropertyRecord.StoryTypeID), "Number of stories", BuildingStep, FieldKind.Lookup, LookupTypes.Stories);
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.ResidenceTypeID), "Residence type", BuildingStep, FieldKind.Lookup, LookupTypes.ResidenceType) { AppliesWhen = res });
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.FoundationTypeID), "Foundation", BuildingStep, FieldKind.Lookup, LookupTypes.Foundation) { AppliesWhen = res });
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.SuperStructureTypeID), "Superstructure", BuildingStep, FieldKind.Lookup, LookupTypes.Superstructure) { AppliesWhen = res });
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.RoofCoveringTypeID), "Roof covering", BuildingStep, FieldKind.Lookup, LookupTypes.RoofCovering) { AppliesWhen = res });
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.ExteriorFinishTypeID), "Exterior finish", BuildingStep, FieldKind.Lookup, LookupTypes.ExteriorFinish) { AppliesWhen = res });
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.HVACTypeID), "Heating & cooling", BuildingStep, FieldKind.Lookup, LookupTypes.Hvac) { AppliesWhen = res });
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.StructureUseID), "Structure use", BuildingStep, FieldKind.Lookup, LookupTypes.StructureUse) { AppliesWhen = nonRes });
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.SprinklerSystem), "Sprinkler system", BuildingStep, FieldKind.Lookup, LookupTypes.SprinklerSystem) { AppliesWhen = nonRes });
		Add(new FieldInfo(FieldModel.Property, nameof(PropertyRecord.Conveyance), "Conveyance (elevators)", BuildingStep, FieldKind.Lookup, LookupTypes.YesNo) { AppliesWhen = nonRes });
		P(nameof(PropertyRecord.YearOfConstruction), "Year built", BuildingStep, FieldKind.Number);
		P(nameof(PropertyRecord.StructureQualityID), "Construction quality", BuildingStep, FieldKind.Lookup, LookupTypes.StructureQuality);
		P(nameof(PropertyRecord.StructureInformation), "Notes about the structure", BuildingStep, FieldKind.MultilineText);

		// 3. Inspection
		A(nameof(AssessmentRecord.InspectorName), "Inspector", InspectionStep);
		A(nameof(AssessmentRecord.InspectorPhoneNumber), "Inspector phone", InspectionStep);
		A(nameof(AssessmentRecord.DateofInspection), "Inspection date", InspectionStep, FieldKind.Date);
		A(nameof(AssessmentRecord.DateDamageOccurred), "Date damage occurred", InspectionStep, FieldKind.Date);
		A(nameof(AssessmentRecord.CauseofDamageID), "Cause of damage", InspectionStep, FieldKind.Lookup, LookupTypes.CauseOfDamage);
		Add(new FieldInfo(FieldModel.Assessment, nameof(AssessmentRecord.CauseOfDamageDescription), "Describe the cause", InspectionStep, FieldKind.Text)
		{ AppliesWhen = f => f.Assessment.CauseofDamageID == LookupIds.CauseOther });
		Add(new FieldInfo(FieldModel.Assessment, nameof(AssessmentRecord.DurationOfFlood), "Flood duration", InspectionStep, FieldKind.Number) { AppliesWhen = flood, Format = "0" });
		Add(new FieldInfo(FieldModel.Assessment, nameof(AssessmentRecord.DurationOfFloodUnitsID), "Flood duration units", InspectionStep, FieldKind.Lookup, LookupTypes.DurationUnits) { AppliesWhen = flood });
		Add(new FieldInfo(FieldModel.Assessment, nameof(AssessmentRecord.DepthOfFlood), "Flood elevation", InspectionStep, FieldKind.Number) { AppliesWhen = flood, Unit = "ft" });
		Add(new FieldInfo(FieldModel.Assessment, nameof(AssessmentRecord.DepthOfFloodAboveFFE), "Depth above lowest floor", InspectionStep, FieldKind.Number) { AppliesWhen = flood, Unit = "ft" });
		A(nameof(AssessmentRecord.PhysicalDamage), "Damage undetermined", InspectionStep, FieldKind.YesNo);
		Add(new FieldInfo(FieldModel.Assessment, nameof(AssessmentRecord.UndeterminedDamageTypeID), "Reason damage is undetermined", InspectionStep, FieldKind.Lookup, LookupTypes.UndeterminedDamageType)
		{ AppliesWhen = f => f.Assessment.PhysicalDamage });

		// 4. Flood zone
		P(nameof(PropertyRecord.NFIPCID), "NFIP community ID", FloodZoneStep);
		P(nameof(PropertyRecord.NFIPCommunityName), "Community name", FloodZoneStep);
		P(nameof(PropertyRecord.CommunityInformation), "Community information", FloodZoneStep, FieldKind.MultilineText);
		P(nameof(PropertyRecord.FIRMPanelNumber), "FIRM panel number", FloodZoneStep);
		P(nameof(PropertyRecord.PanelSuffix), "Panel suffix", FloodZoneStep);
		P(nameof(PropertyRecord.FIRMPanelDate), "FIRM panel date", FloodZoneStep, FieldKind.Date);
		P(nameof(PropertyRecord.FIRMZone), "FIRM zone", FloodZoneStep, FieldKind.Lookup, LookupTypes.FirmZone);
		P(nameof(PropertyRecord.BFE), "Base flood elevation (BFE)", FloodZoneStep, FieldKind.Number).WithUnit("ft", list);
		P(nameof(PropertyRecord.RegulatoryFloodway), "Regulatory floodway", FloodZoneStep, FieldKind.Lookup, LookupTypes.RegulatoryFloodway);
		P(nameof(PropertyRecord.FirstFloorElevation), "Elevation of lowest floor", FloodZoneStep, FieldKind.Number).WithUnit("ft", list);
		P(nameof(PropertyRecord.Datum), "Datum", FloodZoneStep);

		// 5. Cost & depreciation
		A(nameof(AssessmentRecord.BaseCost), "Base cost per sq ft", CostStep, FieldKind.Number).WithMoney(list);
		A(nameof(AssessmentRecord.GeographicAdjustment), "Geographic adjustment", CostStep, FieldKind.Number).WithFormat("0.###", list);
		A(nameof(AssessmentRecord.CostData), "Cost data source", CostStep);
		A(nameof(AssessmentRecord.CostDataDate), "Cost data date", CostStep, FieldKind.Date);
		A(nameof(AssessmentRecord.DepreciationRatingID), "Condition rating", CostStep, FieldKind.Lookup, LookupTypes.DepreciationRating);
		Add(new FieldInfo(FieldModel.Assessment, nameof(AssessmentRecord.DepreciationValue), "Depreciation", CostStep, FieldKind.Number)
		{ Format = "0.0", Unit = "%", AppliesWhen = f => f.Assessment.DepreciationRatingID == LookupIds.DepreciationOther });
		Add(new FieldInfo(FieldModel.Assessment, nameof(AssessmentRecord.DepreciationExplanation), "Why “Other” depreciation", CostStep, FieldKind.MultilineText)
		{ AppliesWhen = f => f.Assessment.DepreciationRatingID == LookupIds.DepreciationOther });

		// 7. Result
		A(nameof(AssessmentRecord.ValueOfBuildingID), "Building value basis", ResultStep, FieldKind.Lookup, LookupTypes.ValueOfBuildingBasis);
		A(nameof(AssessmentRecord.CostOfRepairsID), "Repair cost basis", ResultStep, FieldKind.Lookup, LookupTypes.CostOfRepairsBasis);
		A(nameof(AssessmentRecord.MarketAppraisal), "Professional market appraisal", ResultStep, FieldKind.Number).WithMoney(list);
		A(nameof(AssessmentRecord.TaxAssessedValue), "Tax assessed value", ResultStep, FieldKind.Number).WithMoney(list);
		A(nameof(AssessmentRecord.FactorAdjustment), "Tax factor adjustment", ResultStep, FieldKind.Number);
		A(nameof(AssessmentRecord.ContractorEstimate), "Contractor's estimate", ResultStep, FieldKind.Number).WithMoney(list);
		A(nameof(AssessmentRecord.CommunityEstimate), "Community's estimate", ResultStep, FieldKind.Number).WithMoney(list);
		A(nameof(AssessmentRecord.ExistingImprovementPercent), "Pre-existing improvements", ResultStep, FieldKind.Number).WithUnit("%", list);
		return list;
	}

	private static FieldInfo WithUnit(this FieldInfo f, string unit, List<FieldInfo> list) => Replace(f, f with { Unit = unit }, list);

	private static FieldInfo WithMoney(this FieldInfo f, List<FieldInfo> list) => Replace(f, f with { Unit = "$", Format = "N2" }, list);

	private static FieldInfo WithFormat(this FieldInfo f, string format, List<FieldInfo> list) => Replace(f, f with { Format = format }, list);

	private static FieldInfo Replace(FieldInfo old, FieldInfo next, List<FieldInfo> list)
	{
		list[list.IndexOf(old)] = next;
		return next;
	}

	// ───────────────────────── Reading and writing values ─────────────────────────

	public static object GetValue(AssessmentFile file, FieldInfo f) => f.RecordProperty.GetValue(Target(file, f));

	/// <summary>
	/// Sets a field, converting the value to the column's type (numbers and dates are kept as text in a few FEMA
	/// columns). Null clears it; a non-nullable column gets its default.
	/// </summary>
	public static void SetValue(AssessmentFile file, FieldInfo f, object value) =>
		f.RecordProperty.SetValue(Target(file, f), Convert(value, f.RecordProperty.PropertyType));

	private static object Target(AssessmentFile file, FieldInfo f) => f.Model == FieldModel.Property ? file.Property : file.Assessment;

	/// <summary>Converts an edited value to a record property's type (the rules the editor's fields have always used).</summary>
	public static object Convert(object value, Type propertyType)
	{
		Type t = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
		object converted = value switch
		{
			null => null,
			string s when s.Length == 0 && t != typeof(string) => null,
			string s when t == typeof(string) => s.Length == 0 ? null : s,
			// Some FEMA tables (e.g. Default) keep numbers and dates as text.
			DateTime d when t == typeof(string) => d.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
			IFormattable n when t == typeof(string) => n.ToString(null, CultureInfo.InvariantCulture),
			string s when t == typeof(int) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture),
			string s when t == typeof(decimal) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture),
			string s when t == typeof(DateTime) => DateTime.Parse(s, CultureInfo.InvariantCulture),
			string s when t == typeof(bool) => s is "1" || bool.Parse(s),
			_ when t == typeof(int) => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
			_ when t == typeof(decimal) => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
			_ when t == typeof(DateTime) => (DateTime)value,
			_ when t == typeof(bool) => (bool)value,
			_ => value,
		};
		if (converted == null && propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) == null)
		{
			converted = Activator.CreateInstance(propertyType);
		}
		return converted;
	}

	/// <summary>
	/// A value as stable text for comparing and storing (edit history): invariant numbers without trailing zeros,
	/// dates as yyyy-MM-dd, lookups as their ID, yes/no as True/False. Empty text and null are both null.
	/// </summary>
	public static string Canonical(object value) => value switch
	{
		null => null,
		string s => s.Length == 0 ? null : s,
		DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
		decimal m => m.ToString("0.############", CultureInfo.InvariantCulture),
		double d => d.ToString("0.############", CultureInfo.InvariantCulture),
		bool b => b ? "True" : "False",
		IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
		_ => value.ToString(),
	};

	/// <summary>A field's value as a person reads it: the choice's name, "Yes"/"No", "Oct 7, 2026", "$1,234.00".</summary>
	public static string Display(FieldInfo f, object value, ReferenceData reference)
	{
		if (value == null || value is string { Length: 0 })
		{
			return "";
		}
		switch (f.Kind)
		{
			case FieldKind.Lookup:
				return reference.LookupName(ToInt(value)) ?? value.ToString();
			case FieldKind.State:
				return reference.StateName(ToInt(value)) ?? value.ToString();
			case FieldKind.County:
				return reference.CountyName(ToInt(value)) ?? value.ToString();
			case FieldKind.StructureType:
				return ToInt(value) switch
				{
					1 => "Residential",
					2 => "Non-residential",
					_ => value.ToString(),
				};
			case FieldKind.YesNo:
				return value is true or "True" or "1" ? "Yes" : "No";
			case FieldKind.Date:
				return value is DateTime d || DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
					? d.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"))
					: value.ToString();
			case FieldKind.Number:
				if (value is IConvertible && decimal.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal n))
				{
					string text = n.ToString(f.Format, CultureInfo.GetCultureInfo("en-US"));
					return f.Unit switch
					{
						null => text,
						"$" => "$" + text,
						"%" => text + "%",
						_ => text + " " + f.Unit,
					};
				}
				return value.ToString();
			default:
				return value.ToString();
		}
	}

	/// <summary>Shows a value stored by <see cref="Canonical"/>.</summary>
	public static string DisplayCanonical(FieldInfo f, string canonical, ReferenceData reference)
	{
		if (string.IsNullOrEmpty(canonical))
		{
			return "";
		}
		return f == null ? canonical : Display(f, Convert(canonical, f.RecordProperty.PropertyType), reference);
	}

	private static int? ToInt(object value) => value switch
	{
		int i => i,
		string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null,
		IConvertible c => System.Convert.ToInt32(c, CultureInfo.InvariantCulture),
		_ => null,
	};

	/// <summary>
	/// The choices for a choice field on this assessment, in the order the editor shows them: stories depend on the
	/// structure type, structure uses on the number of stories, counties on the state.
	/// </summary>
	public static IReadOnlyList<(int Id, string Name)> Options(FieldInfo f, AssessmentFile file, ReferenceData reference)
	{
		PropertyRecord p = file.Property;
		switch (f.Kind)
		{
			case FieldKind.StructureType:
				return new[] { (1, "Residential"), (2, "Non-residential") };
			case FieldKind.State:
				return reference.States.OrderBy(s => s.State).Select(s => (s.StateID, s.State)).ToList();
			case FieldKind.County:
				int? state = f.Member == nameof(PropertyRecord.MailingCountyID) ? p.MailingStateID : p.StateID;
				return reference.CountiesIn(state).Select(c => (c.CountyID, c.County)).ToList();
			case FieldKind.Lookup:
				IEnumerable<LookupRecord> lookups = reference.OfType(f.LookupType);
				if (f.LookupType == LookupTypes.Stories)
				{
					bool residential = p.StructureTypeID == 1;
					lookups = lookups.Where(l => (l.LookupValue1 ?? "").Contains("non", StringComparison.OrdinalIgnoreCase) == !residential);
				}
				else if (f.LookupType == LookupTypes.StructureUse)
				{
					string stories = p.StoryTypeID?.ToString(CultureInfo.InvariantCulture) ?? "—";
					lookups = lookups.Where(l => (l.LookupValue1 ?? "").Contains(stories));
				}
				return lookups.Select(l => (l.LookupID, l.LookupFieldName)).ToList();
			default:
				return Array.Empty<(int, string)>();
		}
	}
}
