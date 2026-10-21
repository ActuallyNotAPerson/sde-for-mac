using Sde.Core;

namespace Sde.Interop;

/// <summary>How a source value becomes an SDE value (see <see cref="SourceValues.TryResolve"/>).</summary>
public enum SourceFieldKind
{
	/// <summary>Kept as written.</summary>
	Text,
	/// <summary>A number; "$1,234", "40%" and "(5)" are accepted.</summary>
	Number,
	/// <summary>A date, US or ISO.</summary>
	Date,
	/// <summary>One of FEMA's choices (a Lookup of <see cref="SourceFieldInfo.LookupType"/>), matched by name.</summary>
	Lookup,
	/// <summary>Number of stories: a FEMA choice or a number; residential and non-residential use different choices.</summary>
	Stories,
	/// <summary>Residential or Non-Residential.</summary>
	StructureType,
	/// <summary>Yes or No ("Y", "True", "X"…).</summary>
	YesNo,
	/// <summary>A length in feet; "3 ft", "36 in" and "3' 6\"" are accepted.</summary>
	Depth,
	/// <summary>A flood duration; "48 hrs" or "2 days" also sets the units.</summary>
	Duration,
	/// <summary>An element's % damaged, 0–100.</summary>
	Percent,
	/// <summary>A structure field filled the way Enterprise Import fills it (address and name splitting, state and county matching).</summary>
	Property,
}

/// <summary>One SDE field a source column (or a constant) can fill.</summary>
/// <param name="Key">Stable name used in saved profiles and in the record of what was imported.</param>
/// <param name="Group">The editor step it belongs to.</param>
/// <param name="Property">For <see cref="SourceFieldKind.Property"/>: the Enterprise Import field it is parsed as.</param>
/// <param name="CalcId">For <see cref="SourceFieldKind.Percent"/>: the element's CalcID (4 Foundation … 17 Interiors).</param>
/// <param name="CustomField">For custom fields: the custom field's zOrder (1–3).</param>
public sealed record SourceFieldInfo(
	string Key,
	string Label,
	string Group,
	SourceFieldKind Kind,
	int LookupType = 0,
	EnterpriseField? Property = null,
	int CalcId = 0,
	int CustomField = 0)
{
	/// <summary>Whether text from the source can be translated to a FEMA choice or number in "Values to match".</summary>
	public bool Translatable => Kind is not (SourceFieldKind.Text or SourceFieldKind.Date) && !(Kind == SourceFieldKind.Property && LookupType == 0);

	/// <summary>Whether the field belongs to the structure (location, building, flood zone), so a structures import uses it.</summary>
	public bool AppliesToStructures => Group is SourceFields.LocationGroup or SourceFields.BuildingGroup or SourceFields.FloodZoneGroup;
}

/// <summary>
/// The SDE fields an import can fill, grouped like the editor's steps, plus header guessing for a new file. Column
/// names vary by system and by agency (and damage details often come from an inspection checklist), so these are only
/// starting guesses; the profile is what the import uses. Only structure fields are used when importing structures.
/// </summary>
public static class SourceFields
{
	public const string LocationGroup = "Location & owner";
	public const string BuildingGroup = "Building";
	public const string InspectionGroup = "Inspection";
	public const string FloodZoneGroup = "Flood zone";
	public const string CostGroup = "Cost & depreciation";
	public const string DamageGroup = "Damage by element";
	public const string ResultGroup = "Result";
	public const string NotesGroup = "Photos & notes";

	public static readonly IReadOnlyList<string> Groups = new[]
	{
		LocationGroup, BuildingGroup, InspectionGroup, FloodZoneGroup, CostGroup, DamageGroup, ResultGroup, NotesGroup,
	};

	/// <summary>FEMA's damage elements (Lookup type 2) by CalcID.</summary>
	public static readonly IReadOnlyList<(int CalcId, string Name)> Elements = new[]
	{
		(4, "Foundation"), (5, "Superstructure"), (6, "Roof Covering"), (7, "Exterior Finish"), (8, "Interior Finish"),
		(9, "Doors and Windows"), (10, "Cabinets and Countertops"), (11, "Floor Finish"), (12, "Plumbing"), (13, "Electrical"),
		(14, "Appliances"), (15, "HVAC"), (16, "Skirting / Forms Piers"), (17, "Interiors"),
	};

	// Declared after Elements: static fields initialise in order.
	public static readonly IReadOnlyList<SourceFieldInfo> All = Build();

	private static readonly Dictionary<string, SourceFieldInfo> ByKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);

	public static SourceFieldInfo Get(string key) => key != null && ByKey.TryGetValue(key, out SourceFieldInfo f) ? f : null;

	public static string ElementKey(int calcId) => "Element." + calcId;

	public static string CustomKey(int zOrder) => "Custom." + zOrder;

	private static List<SourceFieldInfo> Build()
	{
		List<SourceFieldInfo> f = new();
		void P(EnterpriseField field, string label, string group, int lookupType = 0) =>
			f.Add(new SourceFieldInfo(field.ToString(), label, group, SourceFieldKind.Property, lookupType, field));
		void A(string key, string label, string group, SourceFieldKind kind, int lookupType = 0) =>
			f.Add(new SourceFieldInfo(key, label, group, kind, lookupType));

		// 1. Location & owner
		P(EnterpriseField.StreetNumber, "Street number", LocationGroup);
		P(EnterpriseField.StreetName, "Street name", LocationGroup);
		P(EnterpriseField.StreetSuffix, "Street suffix", LocationGroup);
		P(EnterpriseField.PostDirection, "Street direction", LocationGroup);
		P(EnterpriseField.ApartmentUnit, "Unit", LocationGroup);
		P(EnterpriseField.City, "City", LocationGroup);
		P(EnterpriseField.State, "State", LocationGroup);
		P(EnterpriseField.County, "County / parish", LocationGroup);
		P(EnterpriseField.Zip, "ZIP code", LocationGroup);
		P(EnterpriseField.OwnerFirstName, "Owner first name", LocationGroup);
		P(EnterpriseField.OwnerLastName, "Owner last name", LocationGroup);
		P(EnterpriseField.PhoneNumber, "Owner phone", LocationGroup);
		P(EnterpriseField.ParcelNumber, "Parcel number", LocationGroup);
		P(EnterpriseField.LotNumber, "Lot number", LocationGroup);
		P(EnterpriseField.Subdivision, "Subdivision", LocationGroup);
		P(EnterpriseField.Latitude, "Latitude", LocationGroup);
		P(EnterpriseField.Longitude, "Longitude", LocationGroup);

		// 2. Building
		A("StructureType", "Structure type", BuildingGroup, SourceFieldKind.StructureType);
		A("Stories", "Number of stories", BuildingGroup, SourceFieldKind.Stories, LookupTypes.Stories);
		A("ResidenceType", "Residence type", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.ResidenceType);
		A("Foundation", "Foundation", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.Foundation);
		A("Superstructure", "Superstructure", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.Superstructure);
		A("RoofCovering", "Roof covering", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.RoofCovering);
		A("ExteriorFinish", "Exterior finish", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.ExteriorFinish);
		A("Hvac", "Heating & cooling", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.Hvac);
		A("StructureUse", "Structure use", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.StructureUse);
		A("Sprinkler", "Sprinkler system", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.SprinklerSystem);
		A("Conveyance", "Conveyance (elevators)", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.YesNo);
		P(EnterpriseField.YearOfConstruction, "Year built", BuildingGroup);
		P(EnterpriseField.TotalSquareFootage, "Square footage", BuildingGroup);
		A("StructureQuality", "Construction quality", BuildingGroup, SourceFieldKind.Lookup, LookupTypes.StructureQuality);
		A("StructureInformation", "Notes about the structure", BuildingGroup, SourceFieldKind.Text);

		// 3. Inspection
		A("InspectorName", "Inspector", InspectionGroup, SourceFieldKind.Text);
		A("InspectorPhone", "Inspector phone", InspectionGroup, SourceFieldKind.Text);
		A("DateOfInspection", "Inspection date", InspectionGroup, SourceFieldKind.Date);
		A("DateDamageOccurred", "Date damage occurred", InspectionGroup, SourceFieldKind.Date);
		A("CauseOfDamage", "Cause of damage", InspectionGroup, SourceFieldKind.Lookup, LookupTypes.CauseOfDamage);
		A("CauseDescription", "Describe the cause", InspectionGroup, SourceFieldKind.Text);
		A("FloodDuration", "Flood duration", InspectionGroup, SourceFieldKind.Duration);
		A("FloodDurationUnits", "Duration units", InspectionGroup, SourceFieldKind.Lookup, LookupTypes.DurationUnits);
		A("FloodElevation", "Flood elevation (ft)", InspectionGroup, SourceFieldKind.Depth);
		A("DepthAboveLowestFloor", "Depth above lowest floor (ft)", InspectionGroup, SourceFieldKind.Depth);
		A("DamageUndetermined", "Damage undetermined", InspectionGroup, SourceFieldKind.YesNo);
		A("UndeterminedReason", "Reason damage is undetermined", InspectionGroup, SourceFieldKind.Lookup, LookupTypes.UndeterminedDamageType);

		// 4. Flood zone
		P(EnterpriseField.NfipCommunityId, "NFIP community ID", FloodZoneGroup);
		P(EnterpriseField.NfipCommunityName, "NFIP community name", FloodZoneGroup);
		P(EnterpriseField.FirmPanel, "FIRM panel number", FloodZoneGroup);
		P(EnterpriseField.FirmPanelSuffix, "Panel suffix", FloodZoneGroup);
		P(EnterpriseField.FirmPanelDate, "FIRM panel date", FloodZoneGroup);
		P(EnterpriseField.FirmZone, "FIRM zone", FloodZoneGroup, LookupTypes.FirmZone);
		P(EnterpriseField.BaseFloodElevation, "Base flood elevation (BFE)", FloodZoneGroup);
		P(EnterpriseField.RegulatoryFloodway, "Regulatory floodway", FloodZoneGroup, LookupTypes.RegulatoryFloodway);
		P(EnterpriseField.FirstFloorElevation, "Elevation of lowest floor", FloodZoneGroup);
		P(EnterpriseField.Datum, "Datum", FloodZoneGroup);

		// 5. Cost & depreciation
		A("BaseCost", "Base cost per sq ft", CostGroup, SourceFieldKind.Number);
		A("GeographicAdjustment", "Geographic adjustment", CostGroup, SourceFieldKind.Number);
		A("CostData", "Cost data source", CostGroup, SourceFieldKind.Text);
		A("CostDataDate", "Cost data date", CostGroup, SourceFieldKind.Date);
		A("DepreciationRating", "Condition rating", CostGroup, SourceFieldKind.Lookup, LookupTypes.DepreciationRating);
		A("DepreciationPercent", "Depreciation % (for “Other”)", CostGroup, SourceFieldKind.Number);
		A("DepreciationExplanation", "Why “Other”?", CostGroup, SourceFieldKind.Text);

		// 6. Damage by element (Lookup type 2)
		foreach ((int calcId, string name) in Elements)
		{
			f.Add(new SourceFieldInfo(ElementKey(calcId), name + " % damaged", DamageGroup, SourceFieldKind.Percent, CalcId: calcId));
		}

		// 7. Result
		A("ValueBasis", "Value of building based on", ResultGroup, SourceFieldKind.Lookup, LookupTypes.ValueOfBuildingBasis);
		A("MarketAppraisal", "Professional market appraisal", ResultGroup, SourceFieldKind.Number);
		A("TaxAssessedValue", "Tax assessed value", ResultGroup, SourceFieldKind.Number);
		A("FactorAdjustment", "Tax factor adjustment", ResultGroup, SourceFieldKind.Number);
		A("RepairBasis", "Cost of repairs based on", ResultGroup, SourceFieldKind.Lookup, LookupTypes.CostOfRepairsBasis);
		A("ContractorEstimate", "Contractor's estimate", ResultGroup, SourceFieldKind.Number);
		A("CommunityEstimate", "Community's estimate", ResultGroup, SourceFieldKind.Number);
		A("ExistingImprovementPercent", "Pre-existing improvements %", ResultGroup, SourceFieldKind.Number);

		// 8. Photos & notes: the three assessment custom fields (renamable in Settings)
		for (int z = 1; z <= 3; z++)
		{
			f.Add(new SourceFieldInfo(CustomKey(z), "Custom field " + z, NotesGroup, SourceFieldKind.Text, CustomField: z));
		}
		return f;
	}

	// ───────────────────────── Guessing ─────────────────────────

	/// <summary>Usual names for the record ID in permit systems, inspection reports and assessor rolls.</summary>
	internal static readonly string[] RecordIdNames =
	{
		"Record ID", "Record Number", "Record No", "Record #", "Record", "Alt ID", "B1_ALT_ID", "Permit Number", "Permit #", "Permit No",
		"Case Number", "Case #", "Case ID", "Application Number",
	};

	internal static readonly string[] InspectionIdNames =
	{
		"Inspection ID", "Inspection Number", "Inspection No", "Inspection #", "Insp ID", "Insp #", "Inspection Sequence", "G6_ACT_NUM", "Activity Number",
	};

	internal static readonly string[] ItemNames =
	{
		"Checklist Item", "Checklist Item Name", "Guidesheet Item", "Guide Sheet Item", "Guidesheet Item Name", "Item", "Item Name", "Checklist Question", "Question",
	};

	internal static readonly string[] ValueNames =
	{
		"Checklist Item Value", "Checklist Value", "Item Value", "Guidesheet Item Value", "Item Status", "Checklist Item Status", "Value", "Answer", "Item Result",
	};

	/// <summary>Common permit-system and assessor header names for each field (compared ignoring case, spaces and punctuation).</summary>
	private static readonly Dictionary<string, string[]> Synonyms = new()
	{
		["StreetNumber"] = new[] { "Street Number", "Street No", "Street #", "House Number", "House #", "House No", "Address Number" },
		["StreetName"] = new[] { "Street Name", "Street" },
		["StreetSuffix"] = new[] { "Street Suffix", "Street Type", "Suffix" },
		["PostDirection"] = new[] { "Street Direction", "Direction", "Street Dir", "Dir", "Prefix", "Street Prefix", "Pre Direction" },
		["ApartmentUnit"] = new[] { "Unit", "Unit #", "Unit Number", "Unit No", "Apt", "Suite" },
		["City"] = new[] { "City", "Town" },
		["State"] = new[] { "State", "ST" },
		["County"] = new[] { "County", "Parish" },
		["Zip"] = new[] { "Zip", "Zip Code", "ZIP5", "Postal Code" },
		["OwnerFirstName"] = new[] { "Owner First Name", "Owner First" },
		["OwnerLastName"] = new[] { "Owner Last Name", "Owner Last" },
		["PhoneNumber"] = new[] { "Owner Phone", "Owner Phone Number", "Phone", "Phone Number", "Contact Phone" },
		["ParcelNumber"] = new[] { "Parcel Number", "Parcel #", "Parcel No", "Parcel ID", "Parcel", "APN", "PIN" },
		["LotNumber"] = new[] { "Lot", "Lot Number", "Lot #" },
		["Subdivision"] = new[] { "Subdivision" },
		["Latitude"] = new[] { "Latitude", "Lat" },
		["Longitude"] = new[] { "Longitude", "Lon", "Long", "Lng" },
		["StructureType"] = new[] { "Structure Type", "Building Type", "Property Type", "Residential or Non-Residential", "Occupancy Class" },
		["Stories"] = new[] { "Number of Stories", "Stories", "No of Stories", "Story", "Floors", "Number of Floors" },
		["ResidenceType"] = new[] { "Residence Type", "Dwelling Type", "Housing Type" },
		["Foundation"] = new[] { "Foundation Type", "Foundation" },
		["Superstructure"] = new[] { "Superstructure", "Superstructure Type", "Construction Type", "Framing", "Frame Type" },
		["RoofCovering"] = new[] { "Roof Covering", "Roof Type", "Roof Covering Type", "Roofing", "Roof Material" },
		["ExteriorFinish"] = new[] { "Exterior Finish", "Exterior Finish Type", "Exterior Wall", "Siding", "Siding Type", "Exterior" },
		["Hvac"] = new[] { "HVAC Type", "Heating Cooling", "Heating and Cooling", "Heating", "HVAC System" },
		["StructureUse"] = new[] { "Structure Use", "Building Use", "Occupancy", "Occupancy Type", "Use", "Use Type" },
		["Sprinkler"] = new[] { "Sprinkler", "Sprinkler System", "Sprinklers", "Sprinklered" },
		["Conveyance"] = new[] { "Conveyance", "Elevator", "Elevators" },
		["YearOfConstruction"] = new[] { "Year Built", "YearBuilt", "Yr Built", "Year of Construction", "Built" },
		["TotalSquareFootage"] = new[] { "Square Feet", "Square Footage", "Sq Ft", "SqFt", "Living Area", "Building Area", "Building Sq Ft", "Building Square Feet", "Gross Area", "Floor Area", "Total Area" },
		["StructureQuality"] = new[] { "Construction Quality", "Quality", "Building Quality", "Grade" },
		["InspectorName"] = new[] { "Inspector", "Inspector Name", "Inspector Full Name", "Inspected By", "Assigned Inspector" },
		["InspectorPhone"] = new[] { "Inspector Phone", "Inspector Phone Number" },
		["DateOfInspection"] = new[] { "Inspection Date", "Result Date", "Completed Date", "Inspection Completed Date", "Date of Inspection", "Inspected Date", "Status Date" },
		["DateDamageOccurred"] = new[] { "Date of Damage", "Damage Date", "Date Damage Occurred", "Incident Date", "Event Date", "Disaster Date", "Date of Loss", "Loss Date" },
		["CauseOfDamage"] = new[] { "Cause of Damage", "Damage Cause", "Cause", "Damage Type", "Disaster Type", "Hazard", "Hazard Type", "Event Type", "Peril" },
		["CauseDescription"] = new[] { "Cause Description", "Damage Description", "Describe the Cause" },
		["FloodDuration"] = new[] { "Flood Duration", "Duration of Flooding", "Duration", "Days Flooded", "Hours Flooded", "Water Duration" },
		["FloodDurationUnits"] = new[] { "Duration Units", "Flood Duration Units" },
		["FloodElevation"] = new[] { "Flood Elevation", "Water Surface Elevation", "High Water Elevation", "HWM Elevation" },
		["DepthAboveLowestFloor"] = new[] { "Water Depth", "Flood Depth", "Depth of Flooding", "Depth of Water", "Water Level", "Interior Water Depth", "High Water Mark", "Water Depth Inside", "Depth Above Lowest Floor", "Depth Above First Floor", "Water Line" },
		["DamageUndetermined"] = new[] { "Damage Undetermined", "Undetermined" },
		["UndeterminedReason"] = new[] { "Undetermined Reason", "Reason Undetermined", "No Inspection Reason" },
		["NfipCommunityId"] = new[] { "NFIP Community ID", "NFIP CID", "Community ID", "CID", "NFIP ID" },
		["NfipCommunityName"] = new[] { "NFIP Community Name", "NFIP Community", "Community Name" },
		["FirmPanel"] = new[] { "FIRM Panel", "FIRM Panel Number", "Panel Number", "Map Panel" },
		["FirmPanelSuffix"] = new[] { "Panel Suffix", "FIRM Suffix" },
		["FirmPanelDate"] = new[] { "FIRM Panel Date", "FIRM Date", "Panel Date", "Map Date", "Effective Date" },
		["FirmZone"] = new[] { "FIRM Zone", "Flood Zone", "FLD_ZONE", "Zone" },
		["BaseFloodElevation"] = new[] { "BFE", "Base Flood Elevation" },
		["RegulatoryFloodway"] = new[] { "Floodway", "Regulatory Floodway", "In Floodway" },
		["FirstFloorElevation"] = new[] { "First Floor Elevation", "FFE", "Lowest Floor Elevation", "Finished Floor Elevation" },
		["Datum"] = new[] { "Datum", "Vertical Datum" },
		["BaseCost"] = new[] { "Base Cost", "Base Cost per Sq Ft", "Base Cost Per Square Foot", "Cost per Sq Ft", "Cost Per Square Foot" },
		["GeographicAdjustment"] = new[] { "Geographic Adjustment", "Local Multiplier", "Location Factor", "Local Cost Multiplier", "City Cost Index" },
		["CostData"] = new[] { "Cost Data", "Cost Data Source", "Cost Source" },
		["CostDataDate"] = new[] { "Cost Data Date" },
		["DepreciationRating"] = new[] { "Condition", "Condition Rating", "Building Condition", "Structure Condition", "Depreciation Rating", "Pre-Damage Condition" },
		["DepreciationPercent"] = new[] { "Depreciation", "Depreciation %", "Depreciation Percent", "Depreciation Percentage" },
		["DepreciationExplanation"] = new[] { "Depreciation Explanation", "Condition Explanation" },
		["ValueBasis"] = new[] { "Value Basis", "Value of Building Based On" },
		["MarketAppraisal"] = new[] { "Market Value", "Appraised Value", "Appraisal", "Market Appraisal", "Professional Market Appraisal", "Fair Market Value", "Pre-Damage Market Value" },
		["TaxAssessedValue"] = new[] { "Improved Value", "Improvement Value", "Building Value", "Structure Value", "Assessed Value", "Tax Assessed Value", "Assessed Improvement Value", "Improvements Value" },
		["FactorAdjustment"] = new[] { "Factor Adjustment", "Tax Factor", "Assessment Factor", "Equalization Factor" },
		["RepairBasis"] = new[] { "Repair Basis", "Cost of Repairs Based On" },
		["ContractorEstimate"] = new[] { "Contractor Estimate", "Contractor's Estimate", "Contractor Bid", "Repair Bid" },
		["CommunityEstimate"] = new[] { "Estimated Repair Cost", "Repair Cost Estimate", "Repair Estimate", "Estimated Damage", "Damage Estimate", "Cost of Repairs", "Estimated Cost of Repairs", "Community Estimate", "Damage Amount", "Estimated Loss" },
		["ExistingImprovementPercent"] = new[] { "Pre-Existing Improvements", "Existing Improvements" },
	};

	/// <summary>
	/// A first profile for a file: the layout (one row per checklist item when the sheet has item and value columns),
	/// the record and inspection ID columns, and every field whose header looks like a known name. Element damage
	/// columns are recognised by names like "Foundation % Damage" or "% Damaged - Plumbing". A file with no record ID
	/// but a parcel column (an assessor roll) is taken to be a list of structures, matched by parcel.
	/// </summary>
	public static SourceProfile Guess(SourceTable raw)
	{
		SourceProfile profile = new SourceProfile();
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
		string Pick(IEnumerable<string> names)
		{
			HashSet<string> wanted = names.Select(Norm).ToHashSet();
			string c = raw.Columns.FirstOrDefault(col => !used.Contains(col) && wanted.Contains(Norm(col)));
			if (c != null)
			{
				used.Add(c);
			}
			return c;
		}
		profile.RecordIdColumn = Pick(RecordIdNames);
		profile.InspectionIdColumn = Pick(InspectionIdNames);
		string item = Pick(ItemNames);
		string value = item == null ? null : Pick(ValueNames);
		if (item != null && value != null)
		{
			profile.Layout = SourceLayout.OneRowPerChecklistItem;
			profile.ItemColumn = item;
			profile.ValueColumn = value;
		}
		else if (item != null)
		{
			used.Remove(item);
		}

		SourceShaped shaped = SourceTable.Shape(raw, profile);
		GuessFields(shaped.Table, profile, used);
		if (profile.RecordIdColumn == null && profile[nameof(EnterpriseField.ParcelNumber)]?.Column is string parcel)
		{
			profile.RecordIdColumn = parcel;
			profile.Mode = SourceMode.Structures;
			profile.MatchBy = SourceMatchBy.Parcel;
		}
		return profile;
	}

	/// <summary>Maps unmapped fields to unused columns whose header looks like the field (see <see cref="Guess"/>).</summary>
	public static void GuessFields(SourceTable table, SourceProfile profile, ISet<string> usedColumns = null)
	{
		HashSet<string> used = new(usedColumns ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
		foreach (string c in new[] { profile.RecordIdColumn, profile.InspectionIdColumn, profile.ItemColumn, profile.ValueColumn, profile.FilterColumn })
		{
			if (c != null)
			{
				used.Add(c);
			}
		}
		foreach (SourceFieldMap m in profile.Fields.Values)
		{
			if (m.Column != null)
			{
				used.Add(m.Column);
			}
		}
		void Map(string key, string column)
		{
			profile.Map(key).Column = column;
			used.Add(column);
		}

		// Element damage first, so "Checklist: Foundation" with numbers is damage, not the foundation type.
		foreach (string column in table.Columns.Where(c => !used.Contains(c)))
		{
			(string bare, bool checklist) = Bare(column);
			foreach ((int calcId, string name) in Elements)
			{
				string key = ElementKey(calcId);
				if (profile.IsMapped(key))
				{
					continue;
				}
				bool named = ElementNames(name).Contains(bare);
				bool plain = checklist && ElementBareNames(name).Contains(bare) && MostlyNumbers(table, column);
				if (named || plain)
				{
					Map(key, column);
					break;
				}
			}
		}
		foreach (SourceFieldInfo field in All.Where(f => f.Kind != SourceFieldKind.Percent && f.CustomField == 0))
		{
			if (profile.IsMapped(field.Key) || !Synonyms.TryGetValue(field.Key, out string[] names))
			{
				continue;
			}
			HashSet<string> wanted = names.Append(field.Label).Select(Norm).ToHashSet();
			string match = table.Columns.FirstOrDefault(c => !used.Contains(c) && wanted.Contains(Bare(c).Name));
			if (match != null)
			{
				Map(field.Key, match);
			}
		}

		// Owner: one "Owner" / "Owner Name" column holds the whole name.
		if (!profile.IsMapped("OwnerFirstName") && !profile.IsMapped("OwnerLastName")
			&& table.Columns.FirstOrDefault(c => !used.Contains(c) && Bare(c).Name is "owner" or "ownername" or "ownerfullname" or "primaryowner" or "primaryownername") is string owner)
		{
			Map("OwnerFirstName", owner);
			profile.Map("OwnerLastName").Column = owner;
			// "Doe, John" when most names have a comma, otherwise "John Doe".
			List<string> names = table.Rows.Select(r => r[table.IndexOf(owner)]).Where(v => !string.IsNullOrWhiteSpace(v)).Take(50).ToList();
			profile.FirstNameParsing = profile.LastNameParsing =
				names.Count > 0 && names.Count(v => v.Contains(',')) * 2 > names.Count ? NameParsing.LastCommaFirst : NameParsing.FirstLast;
		}

		// Address: one "Address" column holds the whole street address ("Full Address", "Site Address", "Address Line 1").
		if (!profile.IsMapped("StreetNumber") && !profile.IsMapped("StreetName")
			&& table.Columns.FirstOrDefault(c => !used.Contains(c) && Bare(c).Name is "address" or "fulladdress" or "siteaddress" or "situsaddress"
				or "streetaddress" or "propertyaddress" or "addressline1" or "primaryaddress" or "locationaddress") is string whole)
		{
			Map("StreetName", whole);
			profile.StreetNameParsing = StreetNameParsing.NumberNameSuffix;
		}
		else if (profile.IsMapped("StreetName") && profile.StreetNameParsing == StreetNameParsing.None)
		{
			profile.StreetNameParsing = profile.IsMapped("StreetSuffix") ? StreetNameParsing.Name : StreetNameParsing.NameSuffix;
		}
		if (profile.IsMapped("StreetSuffix") && profile.SuffixParsing == SuffixParsing.None)
		{
			profile.SuffixParsing = SuffixParsing.Suffix;
		}
		if (profile.IsMapped("StreetNumber") && profile.StreetNumberParsing == StreetNumberParsing.None)
		{
			profile.StreetNumberParsing = StreetNumberParsing.Number;
		}
	}

	/// <summary>The normalised header without a "Checklist:" prefix, and whether it had one.</summary>
	private static (string Name, bool Checklist) Bare(string column)
	{
		if (column.StartsWith(SourceTable.ChecklistPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return (Norm(column[SourceTable.ChecklistPrefix.Length..]), true);
		}
		return (Norm(column), false);
	}

	private static HashSet<string> ElementBareNames(string element)
	{
		HashSet<string> names = new() { Norm(element), Norm(element.Replace(" and ", " ")) };
		if (element.StartsWith("Skirting", StringComparison.Ordinal))
		{
			names.Add("skirting");
		}
		return names;
	}

	private static HashSet<string> ElementNames(string element)
	{
		HashSet<string> names = new();
		foreach (string e in ElementBareNames(element))
		{
			foreach (string pre in new[] { "damage", "damaged", "percentdamage", "percentdamaged", "pctdamage", "pctdamaged" })
			{
				names.Add(pre + e);
			}
			foreach (string post in new[] { "damage", "damaged", "percentdamage", "percentdamaged", "damagepercent", "damagedpercent", "pctdamage", "pctdamaged", "damagepct", "percent", "pct" })
			{
				names.Add(e + post);
			}
		}
		return names;
	}

	private static bool MostlyNumbers(SourceTable table, string column)
	{
		int i = table.IndexOf(column);
		List<string> values = table.Rows.Select(r => r[i]).Where(v => !string.IsNullOrWhiteSpace(v)).Take(50).ToList();
		return values.Count > 0 && values.Count(v => Internal.Text.TryParseDecimal(v, out _)) * 2 > values.Count;
	}

	/// <summary>Lower-case letters and digits only ("Foundation % Damage" → "foundationdamage").</summary>
	internal static string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
