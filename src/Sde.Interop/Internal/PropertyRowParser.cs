using Sde.Core;
using Sde.Core.Records;

namespace Sde.Interop.Internal;

/// <summary>
/// Turns one row of values into a PropertyRecord (EnterpriseImportService.ImportDataUsingSettings, row loop): owner-name
/// and address splitting, state/county/suffix matching, FIRM lookups, coordinates. Used by Enterprise Import and the
/// inspection and parcel import (SourceImport). Unusable values become warnings; whether the row is importable is the caller's decision.
/// </summary>
internal sealed class PropertyRowParser
{
	private readonly EnterpriseMapping _m;
	private readonly ReferenceData _ref;

	public PropertyRowParser(EnterpriseMapping mapping, ReferenceData reference)
	{
		_m = mapping;
		_ref = reference;
	}

	/// <param name="get">The cleaned value for a field (null when blank or unmapped).</param>
	public EnterpriseRow Parse(int rowNumber, Func<EnterpriseField, string> get)
	{
		PropertyRecord p = new PropertyRecord { Active = true };
		EnterpriseRow row = new EnterpriseRow { RowNumber = rowNumber, Property = p };
		List<string> warn = row.Warnings;

		Owner(p, get, warn);
		Address(p, get, warn);

		p.City = get(EnterpriseField.City);
		State(p, get, warn);
		p.ZipCode = get(EnterpriseField.Zip);
		if (get(EnterpriseField.PhoneNumber) is string phone)
		{
			// GetNumbers: digits only.
			p.PhoneNumber = new string(phone.Where(char.IsDigit).ToArray());
		}
		p.NFIPCID = get(EnterpriseField.NfipCommunityId);
		p.NFIPCommunityName = get(EnterpriseField.NfipCommunityName);
		p.Latitude = Coordinate(get(EnterpriseField.Latitude), "Latitude", 90m, warn);
		p.Longitude = Coordinate(get(EnterpriseField.Longitude), "Longitude", 180m, warn);
		p.Subdivision = get(EnterpriseField.Subdivision);
		p.ParcelNumber = get(EnterpriseField.ParcelNumber);
		p.LotNumber = get(EnterpriseField.LotNumber);
		p.Datum = get(EnterpriseField.Datum);
		p.FirstFloorElevation = Number(get(EnterpriseField.FirstFloorElevation), EnterpriseField.FirstFloorElevation, warn);
		p.BFE = Number(get(EnterpriseField.BaseFloodElevation), EnterpriseField.BaseFloodElevation, warn);
		p.SquareFootage = Number(get(EnterpriseField.TotalSquareFootage), EnterpriseField.TotalSquareFootage, warn);
		if (p.SquareFootage < 0m)
		{
			warn.Add($"Total Square Footage {p.SquareFootage} is negative and was left blank");
			p.SquareFootage = null;
		}
		if (get(EnterpriseField.YearOfConstruction) is string year)
		{
			if (Text.TryParseInt(year, out int y))
			{
				p.YearOfConstruction = y;
			}
			else
			{
				warn.Add($"Year of Construction '{year}' is not a whole number");
			}
		}
		p.FIRMPanelNumber = get(EnterpriseField.FirmPanel);
		p.PanelSuffix = get(EnterpriseField.FirmPanelSuffix);
		if (get(EnterpriseField.FirmPanelDate) is string date)
		{
			if (Text.TryParseDate(date, out DateTime d))
			{
				p.FIRMPanelDate = d;
			}
			else
			{
				warn.Add($"Date of FIRM Panel '{date}' is not a date");
			}
		}
		p.FIRMZone = Lookup(get(EnterpriseField.FirmZone), LookupTypes.FirmZone, EnterpriseField.FirmZone, warn);
		p.RegulatoryFloodway = Lookup(get(EnterpriseField.RegulatoryFloodway), LookupTypes.RegulatoryFloodway, EnterpriseField.RegulatoryFloodway, warn);
		StructureType(p, get, warn);
		return row;
	}

	// ── Owner name (Lookup type 30) ──

	private void Owner(PropertyRecord p, Func<EnterpriseField, string> get, List<string> warn)
	{
		if (get(EnterpriseField.OwnerFirstName) is string first)
		{
			p.OwnerFirstName = SplitName(first, _m.FirstNameParsing, last: false, warn);
		}
		if (get(EnterpriseField.OwnerLastName) is string last)
		{
			p.OwnerLastName = SplitName(last, _m.LastNameParsing, last: true, warn);
		}
	}

	/// <summary>
	/// 430 "John Doe": the last word is the last name, the words before it the first name (the original took
	/// words [0] and [1]). 431 "Doe, John": split at the first comma. Otherwise the value as is.
	/// </summary>
	private static string SplitName(string value, NameParsing mode, bool last, List<string> warn)
	{
		switch (mode)
		{
			case NameParsing.FirstLast:
			{
				string[] words = Words(value);
				if (words.Length < 2)
				{
					return last ? null : value;
				}
				return last ? words[^1] : string.Join(" ", words[..^1]);
			}
			case NameParsing.LastCommaFirst:
			{
				int comma = value.IndexOf(',');
				if (comma < 0)
				{
					if (last)
					{
						warn.Add($"Owner name '{value}' has no comma; used as the last name");
					}
					return last ? value : null;
				}
				return Text.Clean(last ? value[..comma] : value[(comma + 1)..]);
			}
			default:
				return value;
		}
	}

	// ── Street address (Lookup types 31-33) ──

	private void Address(PropertyRecord p, Func<EnterpriseField, string> get, List<string> warn)
	{
		string number = null, name = null, suffix = null;
		bool suffixExpected = false;

		if (get(EnterpriseField.StreetNumber) is string line1)
		{
			switch (_m.StreetNumberParsing)
			{
				case StreetNumberParsing.NumberNameSuffix:
					(number, string rest) = SplitFirst(line1);
					(name, suffix) = SplitSuffix(rest);
					suffixExpected = true;
					break;
				case StreetNumberParsing.NumberName:
					(number, name) = SplitFirst(line1);
					break;
				default:
					number = line1;
					break;
			}
		}

		if (get(EnterpriseField.StreetName) is string line2)
		{
			switch (_m.StreetNameParsing)
			{
				case StreetNameParsing.NameSuffix:
					(name, suffix) = SplitSuffix(line2);
					suffixExpected = true;
					break;
				case StreetNameParsing.NumberNameSuffix:
				{
					(number, string rest) = SplitFirst(line2);
					(name, suffix) = SplitSuffix(rest);
					suffixExpected = true;
					break;
				}
				case StreetNameParsing.NumberName:
					(number, name) = SplitFirst(line2);
					break;
				default:
					name = line2;
					break;
			}
		}

		if (get(EnterpriseField.StreetSuffix) is string line3)
		{
			suffixExpected = true;
			suffix = _m.SuffixParsing is SuffixParsing.NameSuffix or SuffixParsing.NumberNameSuffix ? Words(line3)[^1] : line3;
		}

		// "Apartment, Unit, Etc." was appended to the street number and the post-direction put in front of the name.
		p.StreetNumber = Text.Clean(Text.Join(number, get(EnterpriseField.ApartmentUnit)));
		p.StreetName = Text.Clean(Text.Join(get(EnterpriseField.PostDirection), name));
		if (suffix != null)
		{
			p.StreetSuffixID = StreetSuffix(suffix);
			if (p.StreetSuffixID == null)
			{
				warn.Add($"Street suffix '{suffix}' is not recognised");
			}
		}
		else if (suffixExpected)
		{
			warn.Add("No street suffix found");
		}
	}

	/// <summary>Splits off a trailing street suffix only when the last word is one; otherwise it all stays in the name.</summary>
	private (string Name, string Suffix) SplitSuffix(string text)
	{
		string[] words = Words(text);
		if (words.Length >= 2 && StreetSuffix(words[^1]) != null)
		{
			return (string.Join(" ", words[..^1]), words[^1]);
		}
		return (Text.Clean(text), null);
	}

	private static (string First, string Remainder) SplitFirst(string text)
	{
		string[] words = Words(text);
		return words.Length == 0 ? (null, null) : (words[0], words.Length > 1 ? string.Join(" ", words[1..]) : null);
	}

	private static string[] Words(string text) => (text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

	/// <summary>
	/// LookupViewService.GetStreetSuffix: a type-20 lookup by name ("Street"), then the StreetSuffixAbbreviation table
	/// ("st"). Also accepts the lookup's own abbreviation (LookupValue1) and a trailing period ("St.").
	/// </summary>
	private int? StreetSuffix(string text)
	{
		string s = text?.Trim().TrimEnd('.');
		if (string.IsNullOrEmpty(s))
		{
			return null;
		}
		LookupRecord byName = _ref.Lookups.FirstOrDefault(l => l.LookupTypeID == LookupTypes.StreetSuffix && Same(l.LookupFieldName, s));
		if (byName != null)
		{
			return byName.LookupID;
		}
		StreetSuffixRecord abbreviation = _ref.StreetSuffixes.FirstOrDefault(a => Same(a.StreetSuffixAbbreviation, s));
		if (abbreviation != null)
		{
			return abbreviation.StreetSuffixID;
		}
		LookupRecord byValue = _ref.Lookups.FirstOrDefault(l => l.LookupTypeID == LookupTypes.StreetSuffix
			&& (l.LookupValue1 ?? "").Split(',').Any(v => Same(v, s)));
		return byValue?.LookupID;
	}

	// ── State and county ──

	private void State(PropertyRecord p, Func<EnterpriseField, string> get, List<string> warn)
	{
		string county = get(EnterpriseField.County);
		if (get(EnterpriseField.State) is not string state)
		{
			if (county != null)
			{
				warn.Add($"County '{county}' was ignored because no state is mapped");
			}
			return;
		}
		StateRecord s = _ref.States.FirstOrDefault(x => Same(x.State, state) || Same(x.StateAbb, state));
		if (s == null)
		{
			warn.Add($"State '{state}' is not recognised");
			if (county != null)
			{
				warn.Add($"County '{county}' was ignored because the state is not recognised");
			}
			return;
		}
		p.StateID = s.StateID;
		if (county == null)
		{
			return;
		}
		string bare = System.Text.RegularExpressions.Regex.Replace(county, @"\s+(County|Parish|Borough|Census Area|Municipality)$", "",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		CountyRecord c = _ref.Counties.FirstOrDefault(x => x.StateID == s.StateID && (Same(x.County, county) || Same(x.County, bare)));
		if (c == null)
		{
			warn.Add($"County '{county}' is not a county of {s.State}");
			return;
		}
		p.CountyID = c.CountyID;
	}

	// ── Structure type ──

	private void StructureType(PropertyRecord p, Func<EnterpriseField, string> get, List<string> warn)
	{
		if (!_m.IsMapped(EnterpriseField.StructureType))
		{
			return;
		}
		string value = get(EnterpriseField.StructureType);
		if (value != null && Same(value, _m.ResidentialText))
		{
			p.StructureTypeID = 1;
		}
		else if (value != null && Same(value, _m.NonResidentialText))
		{
			p.StructureTypeID = 2;
		}
		else
		{
			warn.Add($"Structure type '{value}' is neither '{_m.ResidentialText}' nor '{_m.NonResidentialText}'; it was left blank");
		}
	}

	// ── Values ──

	/// <summary>GetSingleLookupByName: a lookup of the type with this name, ignoring case.</summary>
	private int? Lookup(string text, int lookupType, EnterpriseField field, List<string> warn)
	{
		if (text == null)
		{
			return null;
		}
		LookupRecord l = _ref.Lookups.FirstOrDefault(x => x.LookupTypeID == lookupType && Same(x.LookupFieldName, text));
		if (l == null)
		{
			warn.Add($"{EnterpriseImport.Label(field)} '{text}' is not recognised");
		}
		return l?.LookupID;
	}

	private static decimal? Number(string text, EnterpriseField field, List<string> warn)
	{
		if (text == null)
		{
			return null;
		}
		if (Text.TryParseDecimal(text, out decimal d))
		{
			return d;
		}
		warn.Add($"{EnterpriseImport.Label(field)} '{text}' is not a number");
		return null;
	}

	private static decimal? Coordinate(string text, string name, decimal limit, List<string> warn)
	{
		if (text == null)
		{
			return null;
		}
		if (!Text.TryParseDecimal(text, out decimal d))
		{
			warn.Add($"{name} '{text}' is not a number");
			return null;
		}
		if (d < -limit || d > limit)
		{
			warn.Add($"{name} {d} is outside -{limit} to {limit}");
			return null;
		}
		return Math.Round(d, 6, MidpointRounding.AwayFromZero);
	}

	private static bool Same(string a, string b) =>
		a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
