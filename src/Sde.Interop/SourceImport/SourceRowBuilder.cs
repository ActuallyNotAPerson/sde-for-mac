using System.Globalization;
using System.Reflection;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>
/// Applies one row's resolved source values to an assessment and its structure, in the order the editor depends on:
/// structure fields → building type → the assessment's rows for that type → depreciation → inspection, cost and
/// result fields → element damage → custom fields → calculation → validation.
/// </summary>
internal sealed class SourceRowBuilder
{
	private static readonly string[] AddressTargets = { nameof(PropertyRecord.StreetNumber), nameof(PropertyRecord.StreetName), nameof(PropertyRecord.StreetSuffixID) };
	private static readonly string[] StateTargets = { nameof(PropertyRecord.StateID), nameof(PropertyRecord.CountyID) };

	/// <summary>The PropertyRecord columns each Enterprise field fills (address parts and state/county are re-read together).</summary>
	private static readonly Dictionary<EnterpriseField, string[]> PropertyTargets = new()
	{
		[EnterpriseField.OwnerFirstName] = new[] { nameof(PropertyRecord.OwnerFirstName) },
		[EnterpriseField.OwnerLastName] = new[] { nameof(PropertyRecord.OwnerLastName) },
		[EnterpriseField.PhoneNumber] = new[] { nameof(PropertyRecord.PhoneNumber) },
		[EnterpriseField.StreetNumber] = AddressTargets,
		[EnterpriseField.StreetName] = AddressTargets,
		[EnterpriseField.StreetSuffix] = AddressTargets,
		[EnterpriseField.PostDirection] = AddressTargets,
		[EnterpriseField.ApartmentUnit] = AddressTargets,
		[EnterpriseField.City] = new[] { nameof(PropertyRecord.City) },
		[EnterpriseField.State] = StateTargets,
		[EnterpriseField.County] = StateTargets,
		[EnterpriseField.Zip] = new[] { nameof(PropertyRecord.ZipCode) },
		[EnterpriseField.NfipCommunityId] = new[] { nameof(PropertyRecord.NFIPCID) },
		[EnterpriseField.NfipCommunityName] = new[] { nameof(PropertyRecord.NFIPCommunityName) },
		[EnterpriseField.Latitude] = new[] { nameof(PropertyRecord.Latitude) },
		[EnterpriseField.Longitude] = new[] { nameof(PropertyRecord.Longitude) },
		[EnterpriseField.Subdivision] = new[] { nameof(PropertyRecord.Subdivision) },
		[EnterpriseField.ParcelNumber] = new[] { nameof(PropertyRecord.ParcelNumber) },
		[EnterpriseField.LotNumber] = new[] { nameof(PropertyRecord.LotNumber) },
		[EnterpriseField.Datum] = new[] { nameof(PropertyRecord.Datum) },
		[EnterpriseField.FirstFloorElevation] = new[] { nameof(PropertyRecord.FirstFloorElevation) },
		[EnterpriseField.YearOfConstruction] = new[] { nameof(PropertyRecord.YearOfConstruction) },
		[EnterpriseField.TotalSquareFootage] = new[] { nameof(PropertyRecord.SquareFootage) },
		[EnterpriseField.FirmPanel] = new[] { nameof(PropertyRecord.FIRMPanelNumber) },
		[EnterpriseField.FirmZone] = new[] { nameof(PropertyRecord.FIRMZone) },
		[EnterpriseField.FirmPanelDate] = new[] { nameof(PropertyRecord.FIRMPanelDate) },
		[EnterpriseField.FirmPanelSuffix] = new[] { nameof(PropertyRecord.PanelSuffix) },
		[EnterpriseField.BaseFloodElevation] = new[] { nameof(PropertyRecord.BFE) },
		[EnterpriseField.RegulatoryFloodway] = new[] { nameof(PropertyRecord.RegulatoryFloodway) },
	};

	/// <summary>Building fields → PropertyRecord columns, in the order they must be applied (type, then stories, then use).</summary>
	private static readonly (string Key, string Target)[] BuildingTargets =
	{
		("StructureType", nameof(PropertyRecord.StructureTypeID)),
		("Stories", nameof(PropertyRecord.StoryTypeID)),
		("ResidenceType", nameof(PropertyRecord.ResidenceTypeID)),
		("Foundation", nameof(PropertyRecord.FoundationTypeID)),
		("Superstructure", nameof(PropertyRecord.SuperStructureTypeID)),
		("RoofCovering", nameof(PropertyRecord.RoofCoveringTypeID)),
		("ExteriorFinish", nameof(PropertyRecord.ExteriorFinishTypeID)),
		("Hvac", nameof(PropertyRecord.HVACTypeID)),
		("StructureUse", nameof(PropertyRecord.StructureUseID)),
		("Sprinkler", nameof(PropertyRecord.SprinklerSystem)),
		("Conveyance", nameof(PropertyRecord.Conveyance)),
		("StructureQuality", nameof(PropertyRecord.StructureQualityID)),
		("StructureInformation", nameof(PropertyRecord.StructureInformation)),
	};

	/// <summary>Assessment fields set directly (depreciation, durations and elements are handled separately).</summary>
	private static readonly Dictionary<string, string> AssessmentTargets = new()
	{
		["InspectorName"] = nameof(AssessmentRecord.InspectorName),
		["InspectorPhone"] = nameof(AssessmentRecord.InspectorPhoneNumber),
		["DateOfInspection"] = nameof(AssessmentRecord.DateofInspection),
		["DateDamageOccurred"] = nameof(AssessmentRecord.DateDamageOccurred),
		["CauseOfDamage"] = nameof(AssessmentRecord.CauseofDamageID),
		["CauseDescription"] = nameof(AssessmentRecord.CauseOfDamageDescription),
		["FloodDurationUnits"] = nameof(AssessmentRecord.DurationOfFloodUnitsID),
		["FloodElevation"] = nameof(AssessmentRecord.DepthOfFlood),
		["DepthAboveLowestFloor"] = nameof(AssessmentRecord.DepthOfFloodAboveFFE),
		["DamageUndetermined"] = nameof(AssessmentRecord.PhysicalDamage),
		["UndeterminedReason"] = nameof(AssessmentRecord.UndeterminedDamageTypeID),
		["BaseCost"] = nameof(AssessmentRecord.BaseCost),
		["GeographicAdjustment"] = nameof(AssessmentRecord.GeographicAdjustment),
		["CostData"] = nameof(AssessmentRecord.CostData),
		["CostDataDate"] = nameof(AssessmentRecord.CostDataDate),
		["ValueBasis"] = nameof(AssessmentRecord.ValueOfBuildingID),
		["MarketAppraisal"] = nameof(AssessmentRecord.MarketAppraisal),
		["TaxAssessedValue"] = nameof(AssessmentRecord.TaxAssessedValue),
		["FactorAdjustment"] = nameof(AssessmentRecord.FactorAdjustment),
		["RepairBasis"] = nameof(AssessmentRecord.CostOfRepairsID),
		["ContractorEstimate"] = nameof(AssessmentRecord.ContractorEstimate),
		["CommunityEstimate"] = nameof(AssessmentRecord.CommunityEstimate),
		["ExistingImprovementPercent"] = nameof(AssessmentRecord.ExistingImprovementPercent),
	};

	private readonly SourceProfile _profile;
	private readonly SdeStore _store;
	private readonly ReferenceData _ref;
	private readonly AssessmentCalculator _calc;
	private readonly AssessmentValidator _validator;
	private readonly IReadOnlyDictionary<string, int> _columns;
	private readonly DefaultValuesRecord _defaults;
	private readonly bool _checkLatLong;
	private readonly PropertyRowParser _parser;

	public SourceRowBuilder(SourceProfile profile, SdeStore store, IReadOnlyDictionary<string, int> columns)
	{
		_profile = profile;
		_store = store;
		_ref = store.Reference;
		_calc = store.Calculator;
		_validator = new AssessmentValidator(_ref);
		_columns = columns;
		_defaults = profile.UseDefaultValues ? store.GetDefaults() : null;
		_checkLatLong = store.GetPreferences().LatLong;
		_parser = new PropertyRowParser(profile.ToEnterpriseMapping(), _ref);
	}

	/// <summary>Every mapped field's resolved value for the row (blank cells fall back to the constant; blanks are left out).</summary>
	public Dictionary<string, string> ReadValues(SourceRow row, List<string> warnings)
	{
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		foreach (SourceFieldInfo field in SourceFields.All)
		{
			SourceFieldMap map = _profile[field.Key];
			if (map == null)
			{
				continue;
			}
			string text = _columns.TryGetValue(field.Key, out int i) && i >= 0 ? Text.Clean(row[i]) : null;
			text ??= Text.Clean(map.Constant);
			if (text == null)
			{
				continue;
			}
			if (SourceValues.TryResolve(field, text, map, _ref, out string resolved))
			{
				values[field.Key] = resolved;
			}
			else
			{
				warnings.Add($"{field.Label} '{text}' {Unusable(field)}; left as it was");
			}
		}
		return values;
	}

	/// <summary>A new assessment (with every value) of a new structure, or of an existing one whose blank fields are filled in.</summary>
	public void BuildNew(ImportRow row, PropertyRecord property, bool isNewStructure, Dictionary<string, string> values)
	{
		if (isNewStructure && _defaults != null)
		{
			DefaultValues.ApplyToProperty(_defaults, property);
		}
		string datum = property.Datum;
		AssessmentFile file = AssessmentFile.CreateNew(property, _defaults);
		if (!isNewStructure)
		{
			property.Datum = datum; // the defaults' datum is for new structures only
		}
		row.File = file;
		row.SourceValues = values;
		Apply(row, file, values, values.Keys.ToHashSet(StringComparer.Ordinal), fillBlanksOnly: !isNewStructure, isNewStructure);
	}

	/// <summary>An assessment imported before: only the <paramref name="changed"/> fields are applied.</summary>
	public void BuildUpdate(ImportRow row, AssessmentFile file, Dictionary<string, string> values, HashSet<string> changed)
	{
		row.File = file;
		Apply(row, file, values, changed, fillBlanksOnly: false, isNewStructure: false);
	}

	/// <summary>
	/// A structure on its own (a structures import): a new one (from the default values when the profile says so), an
	/// existing one whose blank fields are filled in, or one imported before with only the <paramref name="keys"/> that
	/// changed. Inspection, cost and damage fields aren't used.
	/// </summary>
	public void BuildStructure(ImportRow row, PropertyRecord p, Dictionary<string, string> values, HashSet<string> keys, bool fillBlanksOnly, bool isNewStructure)
	{
		if (isNewStructure && _defaults != null)
		{
			DefaultValues.ApplyToProperty(_defaults, p);
		}
		row.Property = p;
		ApplyStructure(row, p, values, keys, fillBlanksOnly, isNewStructure);
	}

	/// <summary>The address fields of a row on a scratch structure, parsed as the import would (for matching by address).</summary>
	public PropertyRecord ReadAddress(Dictionary<string, string> values)
	{
		PropertyRecord p = new PropertyRecord();
		HashSet<string> keys = new[] { EnterpriseField.StreetNumber, EnterpriseField.StreetName, EnterpriseField.StreetSuffix, EnterpriseField.PostDirection,
			EnterpriseField.ApartmentUnit, EnterpriseField.Zip }.Select(f => f.ToString()).ToHashSet(StringComparer.Ordinal);
		ApplyPropertyFields(p, values, keys, fillBlanksOnly: false, new List<string>());
		return p;
	}

	private void Apply(ImportRow row, AssessmentFile file, Dictionary<string, string> values, HashSet<string> keys, bool fillBlanksOnly, bool isNewStructure)
	{
		AssessmentRecord a = file.Assessment;
		List<string> warn = row.Warnings;
		bool Has(string key) => keys.Contains(key) && values.ContainsKey(key);

		// 1–3. Structure fields and the building.
		bool buildingChanged = ApplyStructure(row, file.Property, values, keys, fillBlanksOnly, isNewStructure);

		// 4–5. The rows the editor shows for this building, then the condition rating.
		_calc.NormalizeRows(file);
		if (Has("DepreciationRating"))
		{
			_calc.ApplyDepreciationRating(file, LookupId(LookupTypes.DepreciationRating, values["DepreciationRating"]));
		}
		else if (buildingChanged && a.DepreciationRatingID is int rating && rating != LookupIds.DepreciationOther)
		{
			_calc.ApplyDepreciationRating(file, rating); // the % depends on the building type, stories and use
		}
		if (Has("DepreciationPercent") || Has("DepreciationExplanation"))
		{
			if (a.DepreciationRatingID == LookupIds.DepreciationOther)
			{
				if (Has("DepreciationPercent"))
				{
					a.DepreciationValue = Clamp(SourceValues.ParseNumber(values["DepreciationPercent"]), "Depreciation %", warn);
				}
				if (Has("DepreciationExplanation"))
				{
					a.DepreciationExplanation = values["DepreciationExplanation"];
				}
			}
			else
			{
				warn.Add("A depreciation % and explanation are used only with the “Other” condition rating; ignored");
			}
		}

		// 6–7. Inspection, cost and result fields.
		foreach ((string key, string target) in AssessmentTargets)
		{
			if (Has(key) && Convert(SourceFields.Get(key), values[key]) is object value)
			{
				Set(a, target, value, fillBlanksOnly: false);
			}
		}
		if (Has("FloodDuration"))
		{
			string[] parts = values["FloodDuration"].Split(' ');
			a.DurationOfFlood = Math.Round(SourceValues.ParseNumber(parts[0]), 0, MidpointRounding.AwayFromZero);
			if (parts.Length > 1 && !Has("FloodDurationUnits"))
			{
				a.DurationOfFloodUnitsID = LookupId(LookupTypes.DurationUnits, parts[1] == "hours" ? "Hours" : "Days");
			}
		}

		// Element damage by CalcID.
		foreach ((int calcId, string name) in SourceFields.Elements)
		{
			string key = SourceFields.ElementKey(calcId);
			if (!Has(key))
			{
				continue;
			}
			ElementPercentageRecord element = file.ElementPercentages.FirstOrDefault(e => e.CalcID == calcId);
			if (element == null)
			{
				warn.Add($"{name} damage isn't recorded for this type of building; ignored");
				continue;
			}
			element.UserDamage = Clamp(SourceValues.ParseNumber(values[key]), name + " % damaged", warn);
		}

		// 8. Custom fields, and the record ID in its custom field.
		for (int z = 1; z <= 3; z++)
		{
			string key = SourceFields.CustomKey(z);
			if (Has(key) && z != _profile.RecordIdCustomField)
			{
				SetCustomField(file, z, values[key]);
			}
		}
		if (_profile.RecordIdCustomField is >= 1 and <= 3)
		{
			SetCustomField(file, _profile.RecordIdCustomField, row.InspectionId.Length > 0 ? row.RecordId + " / " + row.InspectionId : row.RecordId);
		}

		// 9–10. Calculate as SDE would, then count what is still required.
		try
		{
			_calc.Recalculate(file);
		}
		catch (Exception e)
		{
			row.Problems.Add("SDE couldn't calculate this assessment: " + e.Message);
			return;
		}
		Validate(row);
	}

	/// <summary>Structure fields, then the building type, stories, use and the other building choices. Returns whether the building changed.</summary>
	private bool ApplyStructure(ImportRow row, PropertyRecord p, Dictionary<string, string> values, HashSet<string> keys, bool fillBlanksOnly, bool isNewStructure)
	{
		List<string> warn = row.Warnings;
		bool Has(string key) => keys.Contains(key) && values.ContainsKey(key);

		// 1–2. Structure fields, through Enterprise Import's parser (address and owner splitting, state/county…).
		decimal? oldSquareFeet = p.SquareFootage;
		ApplyPropertyFields(p, values, keys, fillBlanksOnly, warn);
		if (Has(nameof(EnterpriseField.TotalSquareFootage)) && p.SquareFootage != oldSquareFeet && p.SquareFootage is decimal sqft)
		{
			List<ShapeRecord> shapes = isNewStructure || p.PropertyID == Guid.Empty ? new List<ShapeRecord>() : _store.GetShapes(p.PropertyID);
			if (shapes.Count == 0 || (shapes.Count == 1 && shapes[0].ShapeType == "c"))
			{
				row.ShapeTotal = sqft;
			}
			else
			{
				p.SquareFootage = oldSquareFeet;
				warn.Add($"Square footage {sqft:N0} wasn't used because the structure's area is drawn as {shapes.Count} shapes; change it in the editor");
			}
		}

		// 3. Building type, stories, use and the other building choices.
		bool buildingChanged = false;
		foreach ((string key, string target) in BuildingTargets)
		{
			if (!Has(key))
			{
				continue;
			}
			object value = key switch
			{
				"StructureType" => values[key] == SourceValues.Residential ? 1 : 2,
				"Stories" => StoryId(values[key], p.StructureTypeID, warn),
				_ => Convert(SourceFields.Get(key), values[key]),
			};
			if (value != null && Set(p, target, value, fillBlanksOnly))
			{
				buildingChanged |= key is "StructureType" or "Stories" or "StructureUse" or "ResidenceType";
			}
		}
		if (buildingChanged)
		{
			FixStories(p, warn);
			if (p.StructureTypeID == 2 && p.StructureUseID is int use && p.StoryTypeID is int story
				&& !(_ref.Lookup(use)?.LookupValue1 ?? "").Split(',').Select(s => s.Trim()).Contains(story.ToString(CultureInfo.InvariantCulture)))
			{
				warn.Add($"Structure use '{_ref.LookupName(use)}' isn't offered for '{_ref.LookupName(story)}' buildings; it was left blank");
				p.StructureUseID = null;
			}
		}
		return buildingChanged;
	}

	public void Validate(ImportRow row) =>
		row.MissingRequired = _validator.Validate(row.File, _checkLatLong).Count(i => i.Severity == IssueSeverity.Required);

	private void ApplyPropertyFields(PropertyRecord p, Dictionary<string, string> values, HashSet<string> keys, bool fillBlanksOnly, List<string> warn)
	{
		List<EnterpriseField> fields = PropertyTargets.Keys.Where(f => keys.Contains(f.ToString()) && values.ContainsKey(f.ToString())).ToList();
		if (fields.Count == 0)
		{
			return;
		}
		// Address parts and state/county are read together, so a change to one re-reads the whole address.
		HashSet<string> targets = fields.SelectMany(f => PropertyTargets[f]).ToHashSet();
		HashSet<EnterpriseField> read = PropertyTargets.Keys.Where(f => PropertyTargets[f].Any(targets.Contains)).ToHashSet();
		EnterpriseRow parsed = _parser.Parse(0, f => read.Contains(f) && values.TryGetValue(f.ToString(), out string v) ? v : null);
		warn.AddRange(parsed.Warnings);
		foreach (string target in targets)
		{
			object value = typeof(PropertyRecord).GetProperty(target)!.GetValue(parsed.Property);
			if (value != null && !(value is string s && string.IsNullOrWhiteSpace(s)))
			{
				Set(p, target, value, fillBlanksOnly);
			}
		}
	}

	/// <summary>Sets a property; with <paramref name="fillBlanksOnly"/>, only when it is blank. Returns whether it changed.</summary>
	private static bool Set(object target, string property, object value, bool fillBlanksOnly)
	{
		PropertyInfo pi = target.GetType().GetProperty(property)!;
		object current = pi.GetValue(target);
		if (fillBlanksOnly && !(current == null || current is string s && string.IsNullOrWhiteSpace(s)))
		{
			return false;
		}
		if (Equals(current, value))
		{
			return false;
		}
		pi.SetValue(target, value);
		return true;
	}

	/// <summary>A resolved value as the record's type: a lookup ID, a number, a date, a flag or text.</summary>
	private object Convert(SourceFieldInfo field, string resolved) => field.Kind switch
	{
		SourceFieldKind.Lookup => LookupId(field.LookupType, resolved),
		SourceFieldKind.Number or SourceFieldKind.Depth or SourceFieldKind.Percent => SourceValues.ParseNumber(resolved),
		SourceFieldKind.Date => DateTime.Parse(resolved, CultureInfo.InvariantCulture),
		SourceFieldKind.YesNo => resolved == "Yes",
		_ => resolved,
	};

	private int? LookupId(int lookupType, string name) =>
		_ref.OfType(lookupType).FirstOrDefault(l => l.LookupFieldName == name)?.LookupID;

	/// <summary>
	/// Stories as SDE's choice for the building type: residential has "One Story" and "Two or More Stories",
	/// non-residential "1 Story", "2 thru 4" and "5 or more".
	/// </summary>
	private int? StoryId(string resolved, int? structureType, List<string> warn)
	{
		if (decimal.TryParse(resolved, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal n))
		{
			if (structureType == null)
			{
				warn.Add($"Number of stories '{resolved}' needs the structure type (residential or not); left as it was");
				return null;
			}
			return structureType == 1 ? (n <= 1 ? 104 : 105) : (n <= 1 ? 101 : n <= 4 ? 102 : 103);
		}
		return LookupId(LookupTypes.Stories, resolved);
	}

	/// <summary>Swaps a stories choice of the other building type for the equivalent one.</summary>
	private void FixStories(PropertyRecord p, List<string> warn)
	{
		int? fixedId = (p.StructureTypeID, p.StoryTypeID) switch
		{
			(1, 101) => 104,
			(1, 102 or 103) => 105,
			(2, 104) => 101,
			(2, 105) => 102,
			_ => null,
		};
		if (fixedId is int id)
		{
			if (p.StoryTypeID == 105)
			{
				warn.Add("“Two or More Stories” was read as “2 thru 4” for this non-residential building");
			}
			p.StoryTypeID = id;
		}
	}

	private void SetCustomField(AssessmentFile file, int zOrder, string text)
	{
		CustomFieldRecord field = _ref.CustomFields.FirstOrDefault(c => c.zOrder == zOrder);
		if (field == null)
		{
			return;
		}
		CustomFieldResultRecord row = file.CustomFields.FirstOrDefault(r => r.CustomFieldID == field.CustomFieldID);
		if (row == null)
		{
			row = new CustomFieldResultRecord { CustomFieldID = field.CustomFieldID, zOrder = field.zOrder, ResultType = "assessment" };
			file.CustomFields.Add(row);
		}
		row.Result = text;
	}

	private static decimal Clamp(decimal value, string label, List<string> warn)
	{
		if (value is >= 0m and <= 100m)
		{
			return value;
		}
		decimal clamped = Math.Clamp(value, 0m, 100m);
		warn.Add($"{label} {SourceValues.Number(value)} is outside 0–100; {SourceValues.Number(clamped)} was used");
		return clamped;
	}

	private static string Unusable(SourceFieldInfo field) => field.Kind switch
	{
		SourceFieldKind.Number or SourceFieldKind.Percent => "is not a number",
		SourceFieldKind.Date => "is not a date",
		SourceFieldKind.Depth => "is not a length in feet or inches",
		SourceFieldKind.Duration => "is not a duration in hours or days",
		SourceFieldKind.StructureType => "is neither Residential nor Non-Residential",
		SourceFieldKind.YesNo => "is neither yes nor no",
		_ => "isn't one of FEMA's choices",
	};
}
