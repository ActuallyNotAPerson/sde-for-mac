// Reference data and user settings records (FEMA DAL models, same names/order).

namespace Sde.Core.Records;

[Table("Lookup")]
public sealed class LookupRecord
{
	[Key, AutoIncrement] public int LookupID { get; set; }
	public int LookupTypeID { get; set; }
	public string LookupFieldName { get; set; }
	public string LookupValue1 { get; set; }
	public string LookupValue2 { get; set; }
	public int? zOrder { get; set; }
	public int? OldID { get; set; }

	public override string ToString() => LookupFieldName;
}

[Table("LookupType")]
public sealed class LookupTypeRecord
{
	[Key, AutoIncrement] public int LookupTypeID { get; set; }
	public string LookupType { get; set; }
}

[Table("State")]
public sealed class StateRecord
{
	[Key, AutoIncrement] public int StateID { get; set; }
	public string StateAbb { get; set; }
	public string State { get; set; }

	public override string ToString() => State;
}

[Table("County")]
public sealed class CountyRecord
{
	[Key, AutoIncrement] public int CountyID { get; set; }
	public string County { get; set; }
	public int StateID { get; set; }

	public override string ToString() => County;
}

[Table("StreetSuffixAbbreviation")]
public sealed class StreetSuffixRecord
{
	[Key, AutoIncrement] public int StreetSuffixAbbreviationID { get; set; }
	public string StreetSuffixAbbreviation { get; set; }
	public int StreetSuffixID { get; set; }
}

/// <summary>Which standard cost adjustments (Lookup type 23) apply to each residence type (Lookup type 11).</summary>
[Table("CostAdjustment_ResidenceType")]
public sealed class CostAdjustmentResidenceTypeRecord
{
	[Key, AutoIncrement] public int ID { get; set; }
	public int ResidenceType { get; set; }
	public int LookupID { get; set; }
}

/// <summary>
/// Depreciation % by condition rating (Lookup type 24), number of stories (type 14) and structure use (type 15).
/// Residential rows use story = use = 0.
/// </summary>
[Table("DepreciationValue")]
public sealed class DepreciationValueRecord
{
	[Key, AutoIncrement] public int ID { get; set; }
	public int DepreciationLookupID { get; set; }
	public decimal? DepreciationPercentage { get; set; }
	public int StoriesLookupID { get; set; }
	public int StructureUseLookupID { get; set; }
	public decimal? OriginalDepreciationPercentage { get; set; }
}

[Table("ValidationRule")]
public sealed class ValidationRuleRecord
{
	public string AssessmentType { get; set; }
	public string ControlName { get; set; }
	public string FieldName { get; set; }
	public string PinColor { get; set; }
	public string PinMessage { get; set; }
	public string Tab { get; set; }
	[Key, AutoIncrement] public int ValidationID { get; set; }
	public string ValidationRule { get; set; }
	public string ValidationType { get; set; }
	public int ValidationTypeID { get; set; }
	public string ModelName { get; set; }
}

[Table("LatLongValidation")]
public sealed class LatLongValidationRecord
{
	public double LatMax { get; set; }
	public double LatMin { get; set; }
	public double LongMax { get; set; }
	public double LongMin { get; set; }
	public string State { get; set; }
	public string StateAbb { get; set; }
	[Key] public int StateID { get; set; }
}

[Table("CustomField")]
public sealed class CustomFieldRecord
{
	[Key] public Guid CustomFieldID { get; set; }
	public string CustomField { get; set; }
	public DateTime DateOfLastMod { get; set; }
	public bool IsDefault { get; set; }
	public bool isUnique { get; set; }
	public string OriginalDatabaseID { get; set; }
	public int zOrder { get; set; }
}

/// <summary>"Enter Default Data": values pre-filled into new properties/assessments (DAL DefaultModel).</summary>
[Table("Default")]
public sealed class DefaultValuesRecord
{
	public string BFE { get; set; }
	public string CauseOfDamage { get; set; }
	public string City { get; set; }
	public string CommunityUse { get; set; }
	public string CostDataDate { get; set; }
	public string CostDataRef { get; set; }
	public int? County { get; set; }
	public string DateDamagedOccurred { get; set; }
	public string DateOfFirmPanel { get; set; }
	public string DateOfInspection { get; set; }
	public string Datum { get; set; }
	[Key, AutoIncrement] public int DefaultValueID { get; set; }
	public string DurationOfFlood { get; set; }
	public string DurationOfFloodUnit { get; set; }
	public string FirmPanelNumber { get; set; }
	public int? FirmZone { get; set; }
	public string InspectedBy { get; set; }
	public string InspectedPhone { get; set; }
	/// <summary>Geographic adjustment (cost multiplier).</summary>
	public string LocalMultiplier { get; set; }
	public string NFIPCommunityID { get; set; }
	public string NFIPCommunityName { get; set; }
	public int? RegulatoryFloodway { get; set; }
	public int? State { get; set; }
	public string Suffix { get; set; }
	public string YearOfConstruction { get; set; }
	public string Zip { get; set; }
	[JetScale(JetScaleAttribute.Currency)] public decimal? BaseCost { get; set; }
}

[Table("Notes")]
public sealed class NoteRecord
{
	[Key] public Guid NoteID { get; set; }
	public string Notes { get; set; }
	public string NoteName { get; set; }
	public bool Active { get; set; }
}

[Table("UserDefault")]
public sealed class UserPreferencesRecord
{
	[Key, AutoIncrement] public int UserDefaultID { get; set; }
	public bool LatLong { get; set; }
	public bool AutoSpellCheck { get; set; }
	public bool UseDefaults { get; set; }
}

[Table("DatabaseInfo")]
public sealed class DatabaseInfoRecord
{
	[Key] public Guid DataBaseID { get; set; }
	public string DataBaseName { get; set; }
	public string DataBaseType { get; set; }
	public string ServerConnectionString { get; set; }
}

[Table("EnterpriseImportSettings")]
public sealed class EnterpriseImportSettingsRecord
{
	[Key, AutoIncrement] public int EIS_ID { get; set; }
	public string EISGuid { get; set; }
	public string EISName { get; set; }
	public string AddressLine1 { get; set; }
	public string AddressLine1Parsing { get; set; }
	public string AddressLine2 { get; set; }
	public string AddressLine2Parsing { get; set; }
	public string ApartmentUnit { get; set; }
	public string BFE { get; set; }
	public string City { get; set; }
	public string County { get; set; }
	public string DateOfFirmPanel { get; set; }
	public string Datum { get; set; }
	public string FFE { get; set; }
	public string FirmPanel { get; set; }
	public string FirmZone { get; set; }
	public string Latitude { get; set; }
	public string Longitude { get; set; }
	public string LotNum { get; set; }
	public string NFIPID { get; set; }
	public string NFIPName { get; set; }
	public string NFIPSuffix { get; set; }
	public string OwnersFirstName { get; set; }
	public string OwnersFirstNameParsing { get; set; }
	public string OwnersLastName { get; set; }
	public string OwnersLastNameParsing { get; set; }
	public string ParcelNum { get; set; }
	public string PhoneNumber { get; set; }
	public string PostGeoDir { get; set; }
	public string RegFloodway { get; set; }
	public string State { get; set; }
	public string StreetSuffix { get; set; }
	public string StreetSuffixParsing { get; set; }
	public string StructureType { get; set; }
	public string Subdivision { get; set; }
	public string TotalSqFt { get; set; }
	public string YrOfCon { get; set; }
	public string Zip { get; set; }
}
