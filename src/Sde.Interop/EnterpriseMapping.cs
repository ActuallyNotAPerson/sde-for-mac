using System.Globalization;
using Sde.Core.Records;

namespace Sde.Interop;

/// <summary>
/// The Property fields Enterprise Import can fill, in the order of the original screen
/// (decomp/wpfSDE/wpfSDE.ImportExport/ucEnterpriseImport.xaml). Each maps to one column of the saved
/// EnterpriseImportSettings row (see <see cref="EnterpriseMapping.Fields"/>).
/// </summary>
public enum EnterpriseField
{
	OwnerFirstName,
	OwnerLastName,
	PhoneNumber,
	StreetNumber,
	StreetName,
	StreetSuffix,
	PostDirection,
	ApartmentUnit,
	City,
	State,
	County,
	Zip,
	NfipCommunityId,
	NfipCommunityName,
	Latitude,
	Longitude,
	Subdivision,
	ParcelNumber,
	LotNumber,
	Datum,
	FirstFloorElevation,
	StructureType,
	YearOfConstruction,
	TotalSquareFootage,
	FirmPanel,
	FirmZone,
	FirmPanelDate,
	FirmPanelSuffix,
	BaseFloodElevation,
	RegulatoryFloodway,
}

/// <summary>How an owner-name column is split (Lookup type 30, "ParsingName"). 0 = "Make Selection..." (use as is).</summary>
public enum NameParsing
{
	None = 0,
	/// <summary>429 "John" (last-name list shows "Doe"): the column holds just this name.</summary>
	AsIs = 429,
	/// <summary>430 "John Doe".</summary>
	FirstLast = 430,
	/// <summary>431 "Doe, John".</summary>
	LastCommaFirst = 431,
}

/// <summary>What the Street Number column holds (Lookup type 31, "ParsingAdd1").</summary>
public enum StreetNumberParsing
{
	None = 0,
	/// <summary>432 "Street Number".</summary>
	Number = 432,
	/// <summary>433 "Street Number Street Name (with suffix)".</summary>
	NumberNameSuffix = 433,
	/// <summary>434 "Street Number Street Name (without suffix)".</summary>
	NumberName = 434,
}

/// <summary>What the Street Name column holds (Lookup type 32, "ParsingAdd2").</summary>
public enum StreetNameParsing
{
	None = 0,
	/// <summary>435 "Street Name (with suffix)".</summary>
	NameSuffix = 435,
	/// <summary>436 "Street Name (without suffix)".</summary>
	Name = 436,
	/// <summary>437 "Street Number Street Name (with suffix)".</summary>
	NumberNameSuffix = 437,
	/// <summary>438 "Street Number Street Name (without suffix)".</summary>
	NumberName = 438,
}

/// <summary>What the Street Suffix column holds (Lookup type 33, "ParsingSuffix").</summary>
public enum SuffixParsing
{
	None = 0,
	/// <summary>439 "Street Suffix".</summary>
	Suffix = 439,
	/// <summary>440 "Street Name Street Suffix".</summary>
	NameSuffix = 440,
	/// <summary>441 "Street Number Street Name Street Suffix".</summary>
	NumberNameSuffix = 441,
}

/// <summary>Describes one <see cref="EnterpriseField"/>: its label and group on the original screen and its EnterpriseImportSettings column.</summary>
public sealed record EnterpriseFieldInfo(EnterpriseField Field, string Label, string Group, string SettingsColumn);

/// <summary>
/// An Enterprise Import column mapping: which source column fills each Property field, plus the name/address
/// parsing modes. Converts to and from the EnterpriseImportSettings table row the original saved
/// (decomp/BLL/BLL.Service/EnterpriseImportSettingViewService.cs, ucEnterpriseImport.xaml.cs:459-517), so saved
/// mappings and user-settings files interoperate with FEMA's SDE: unmapped columns are null, parsing modes are the
/// lookup IDs as text ("0" = "Make Selection...").
/// </summary>
public sealed class EnterpriseMapping
{
	public static readonly IReadOnlyList<EnterpriseFieldInfo> Fields = new EnterpriseFieldInfo[]
	{
		new(EnterpriseField.OwnerFirstName, "Structure Owner First Name", Owner, nameof(EnterpriseImportSettingsRecord.OwnersFirstName)),
		new(EnterpriseField.OwnerLastName, "Structure Owner Last Name", Owner, nameof(EnterpriseImportSettingsRecord.OwnersLastName)),
		new(EnterpriseField.PhoneNumber, "Phone Number", Owner, nameof(EnterpriseImportSettingsRecord.PhoneNumber)),
		new(EnterpriseField.StreetNumber, "Street Number", Owner, nameof(EnterpriseImportSettingsRecord.AddressLine1)),
		new(EnterpriseField.StreetName, "Street Name", Owner, nameof(EnterpriseImportSettingsRecord.AddressLine2)),
		new(EnterpriseField.StreetSuffix, "Street Suffix", Owner, nameof(EnterpriseImportSettingsRecord.StreetSuffix)),
		new(EnterpriseField.PostDirection, "Post Geographical Direction (prepends to address)", Owner, nameof(EnterpriseImportSettingsRecord.PostGeoDir)),
		new(EnterpriseField.ApartmentUnit, "Apartment, Unit, Etc. (optional)", Owner, nameof(EnterpriseImportSettingsRecord.ApartmentUnit)),
		new(EnterpriseField.City, "City", Owner, nameof(EnterpriseImportSettingsRecord.City)),
		new(EnterpriseField.State, "State", Owner, nameof(EnterpriseImportSettingsRecord.State)),
		new(EnterpriseField.County, "County/Parish", Owner, nameof(EnterpriseImportSettingsRecord.County)),
		new(EnterpriseField.Zip, "Zip Code", Owner, nameof(EnterpriseImportSettingsRecord.Zip)),
		new(EnterpriseField.NfipCommunityId, "NFIP Community ID", Community, nameof(EnterpriseImportSettingsRecord.NFIPID)),
		new(EnterpriseField.NfipCommunityName, "NFIP Community Name", Community, nameof(EnterpriseImportSettingsRecord.NFIPName)),
		new(EnterpriseField.Latitude, "Latitude", Community, nameof(EnterpriseImportSettingsRecord.Latitude)),
		new(EnterpriseField.Longitude, "Longitude", Community, nameof(EnterpriseImportSettingsRecord.Longitude)),
		new(EnterpriseField.Subdivision, "Subdivision", SubdivisionGroup, nameof(EnterpriseImportSettingsRecord.Subdivision)),
		new(EnterpriseField.ParcelNumber, "Parcel Number", SubdivisionGroup, nameof(EnterpriseImportSettingsRecord.ParcelNum)),
		new(EnterpriseField.LotNumber, "Lot Number", SubdivisionGroup, nameof(EnterpriseImportSettingsRecord.LotNum)),
		new(EnterpriseField.Datum, "Datum", SubdivisionGroup, nameof(EnterpriseImportSettingsRecord.Datum)),
		new(EnterpriseField.FirstFloorElevation, "First Floor Elevation", SubdivisionGroup, nameof(EnterpriseImportSettingsRecord.FFE)),
		new(EnterpriseField.StructureType, "Residential or Non-Residential", Structure, nameof(EnterpriseImportSettingsRecord.StructureType)),
		new(EnterpriseField.YearOfConstruction, "Year of Construction", Structure, nameof(EnterpriseImportSettingsRecord.YrOfCon)),
		new(EnterpriseField.TotalSquareFootage, "Total Square Footage", Structure, nameof(EnterpriseImportSettingsRecord.TotalSqFt)),
		new(EnterpriseField.FirmPanel, "FIRM Panel", Structure, nameof(EnterpriseImportSettingsRecord.FirmPanel)),
		new(EnterpriseField.FirmZone, "FIRM Zone", Structure, nameof(EnterpriseImportSettingsRecord.FirmZone)),
		new(EnterpriseField.FirmPanelDate, "Date of FIRM Panel", Structure, nameof(EnterpriseImportSettingsRecord.DateOfFirmPanel)),
		new(EnterpriseField.FirmPanelSuffix, "Suffix", Structure, nameof(EnterpriseImportSettingsRecord.NFIPSuffix)),
		new(EnterpriseField.BaseFloodElevation, "Base Flood Elevation", Structure, nameof(EnterpriseImportSettingsRecord.BFE)),
		new(EnterpriseField.RegulatoryFloodway, "Regulatory Floodway", Structure, nameof(EnterpriseImportSettingsRecord.RegFloodway)),
	};

	private const string Owner = "Owner/Address Information";
	private const string Community = "Community Information";
	private const string SubdivisionGroup = "Subdivision Information";
	private const string Structure = "Structure/NFIP Information";

	private readonly Dictionary<EnterpriseField, string> _columns = new();

	/// <summary>EIS_ID of the saved row; 0 for a mapping that hasn't been saved.</summary>
	public int Id { get; set; }

	/// <summary>EISGuid (never filled in by the original; kept for round trips).</summary>
	public string Guid { get; set; }

	/// <summary>EISName, e.g. "Enterprise Import Setting (10/07/2026 14:05)".</summary>
	public string Name { get; set; }

	public NameParsing FirstNameParsing { get; set; }
	public NameParsing LastNameParsing { get; set; }
	public StreetNumberParsing StreetNumberParsing { get; set; }
	public StreetNameParsing StreetNameParsing { get; set; }
	public SuffixParsing SuffixParsing { get; set; }

	/// <summary>Text in the structure-type column meaning residential (screen default "Residential"; not saved in the settings row).</summary>
	public string ResidentialText { get; set; } = "Residential";

	/// <summary>Text meaning non-residential (screen default "Non-Residential"; not saved in the settings row).</summary>
	public string NonResidentialText { get; set; } = "Non-Residential";

	/// <summary>The source column mapped to a field, or null. Setting null or blank unmaps it.</summary>
	public string this[EnterpriseField field]
	{
		get => _columns.TryGetValue(field, out string c) ? c : null;
		set
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				_columns.Remove(field);
			}
			else
			{
				_columns[field] = value;
			}
		}
	}

	/// <summary>The mapped fields and their source columns.</summary>
	public IReadOnlyDictionary<EnterpriseField, string> Columns => _columns;

	public bool IsMapped(EnterpriseField field) => _columns.ContainsKey(field);

	/// <summary>The name the original gave a mapping saved after an import (ucEnterpriseImport.xaml.cs:508-509).</summary>
	public static string DefaultName(DateTime now) =>
		"Enterprise Import Setting (" + now.ToString("g", DateTimeFormatInfo.InvariantInfo) + ")";

	public static EnterpriseMapping FromRecord(EnterpriseImportSettingsRecord r)
	{
		EnterpriseMapping m = new EnterpriseMapping
		{
			Id = r.EIS_ID,
			Guid = r.EISGuid,
			Name = r.EISName,
			FirstNameParsing = Parse<NameParsing>(r.OwnersFirstNameParsing),
			LastNameParsing = Parse<NameParsing>(r.OwnersLastNameParsing),
			StreetNumberParsing = Parse<StreetNumberParsing>(r.AddressLine1Parsing),
			StreetNameParsing = Parse<StreetNameParsing>(r.AddressLine2Parsing),
			SuffixParsing = Parse<SuffixParsing>(r.StreetSuffixParsing),
		};
		foreach (EnterpriseFieldInfo f in Fields)
		{
			m[f.Field] = (string)typeof(EnterpriseImportSettingsRecord).GetProperty(f.SettingsColumn)!.GetValue(r);
		}
		return m;
	}

	public EnterpriseImportSettingsRecord ToRecord()
	{
		EnterpriseImportSettingsRecord r = new EnterpriseImportSettingsRecord
		{
			EIS_ID = Id,
			EISGuid = Guid,
			EISName = Name,
			OwnersFirstNameParsing = ((int)FirstNameParsing).ToString(CultureInfo.InvariantCulture),
			OwnersLastNameParsing = ((int)LastNameParsing).ToString(CultureInfo.InvariantCulture),
			AddressLine1Parsing = ((int)StreetNumberParsing).ToString(CultureInfo.InvariantCulture),
			AddressLine2Parsing = ((int)StreetNameParsing).ToString(CultureInfo.InvariantCulture),
			StreetSuffixParsing = ((int)SuffixParsing).ToString(CultureInfo.InvariantCulture),
		};
		foreach (EnterpriseFieldInfo f in Fields)
		{
			typeof(EnterpriseImportSettingsRecord).GetProperty(f.SettingsColumn)!.SetValue(r, this[f.Field]);
		}
		return r;
	}

	/// <summary>Lookup ID text → enum; unknown or blank → None (the original's Convert.ToInt32 would have thrown on blank).</summary>
	private static T Parse<T>(string text)
		where T : struct, Enum
	{
		if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && Enum.IsDefined(typeof(T), id))
		{
			return (T)Enum.ToObject(typeof(T), id);
		}
		return default;
	}
}
