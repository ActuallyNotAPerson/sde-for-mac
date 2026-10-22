using System.Text.RegularExpressions;
using Sde.Core;
using Sde.Core.Records;

namespace Sde.Core.Addresses;

/// <summary>
/// An address reduced to what identifies a building: the house number, the street name (lower case, directions and
/// ordinals written one way, without the suffix), the street suffix as FEMA's choice, the unit and the 5-digit ZIP.
/// Made by <see cref="AddressMatcher.KeyFor(PropertyRecord)"/>.
/// </summary>
public sealed record AddressKey(string HouseNumber, string Street, int? SuffixId, string Unit, string Zip)
{
	/// <summary>Without both a house number and a street name an address can't be matched.</summary>
	public bool CanMatch => HouseNumber != null && Street != null;

	/// <summary>The house number and street, the part every match must share.</summary>
	internal string IndexKey => HouseNumber + "|" + Street;
}

/// <summary>
/// Matches addresses written in different ways: "123 N. Main St., Apt 4", "123 North Main Street" and "123 north
/// main" are the same building. Case and punctuation are ignored, "North"/"N" and "First"/"1st" are the same, and the
/// street suffix is compared as FEMA's choice, recognised by name or by any abbreviation in FEMA's
/// StreetSuffixAbbreviation table ("st", "ave", "av"). When only one side has a suffix (or a ZIP), the other part
/// decides. Units are ignored unless <see cref="MatchUnit"/> is set. Used to find a record's structure when an import
/// matches by address; it never picks between several matches (the caller shows them as ambiguous).
/// </summary>
public sealed class AddressMatcher
{
	/// <summary>Words that start the unit part of an address.</summary>
	private static readonly HashSet<string> UnitWords = new(StringComparer.Ordinal)
	{
		"apt", "apartment", "unit", "ste", "suite", "bldg", "building", "rm", "room", "fl", "floor", "lot", "trlr", "trailer", "spc", "space", "no",
	};

	private static readonly Dictionary<string, string> Directions = new(StringComparer.Ordinal)
	{
		["north"] = "n", ["south"] = "s", ["east"] = "e", ["west"] = "w",
		["northeast"] = "ne", ["northwest"] = "nw", ["southeast"] = "se", ["southwest"] = "sw",
	};

	private static readonly Dictionary<string, string> Ordinals = new(StringComparer.Ordinal)
	{
		["first"] = "1st", ["second"] = "2nd", ["third"] = "3rd", ["fourth"] = "4th", ["fifth"] = "5th",
		["sixth"] = "6th", ["seventh"] = "7th", ["eighth"] = "8th", ["ninth"] = "9th", ["tenth"] = "10th",
	};

	private static readonly Regex HouseNumber = new(@"^\d+[a-z]?$", RegexOptions.CultureInvariant);

	private readonly Dictionary<string, int> _suffixes = new(StringComparer.Ordinal);

	public AddressMatcher(ReferenceData reference, bool matchUnit = false)
	{
		MatchUnit = matchUnit;
		// FEMA's suffix names ("Street"), their own abbreviations (LookupValue1 "st, str") and the abbreviation table.
		foreach (LookupRecord l in reference.OfType(LookupTypes.StreetSuffix))
		{
			Add(l.LookupFieldName, l.LookupID);
			foreach (string v in (l.LookupValue1 ?? "").Split(','))
			{
				Add(v, l.LookupID);
			}
		}
		foreach (StreetSuffixRecord a in reference.StreetSuffixes)
		{
			Add(a.StreetSuffixAbbreviation, a.StreetSuffixID);
		}
	}

	/// <summary>Whether the unit (apartment, suite) has to match as well.</summary>
	public bool MatchUnit { get; }

	/// <summary>The key of a structure's address (its unit is whatever follows the house number).</summary>
	public AddressKey KeyFor(PropertyRecord p) => KeyFor(p.StreetNumber, p.StreetName, p.StreetSuffixID, p.ZipCode);

	/// <summary>
	/// The key of an address in parts. <paramref name="streetNumber"/> may carry the unit ("123 Apt 4", "123 4B"), and
	/// <paramref name="streetName"/> may carry the whole rest of the address ("N Main St Apt 4").
	/// </summary>
	public AddressKey KeyFor(string streetNumber, string streetName, int? suffixId, string zip = null)
	{
		List<string> number = Tokens(streetNumber);
		List<string> street = Tokens(streetName);
		if (number.Count == 0 && street.Count > 0 && HouseNumber.IsMatch(street[0]))
		{
			number.Add(street[0]);
			street.RemoveAt(0);
		}
		string house = number.Count > 0 && HouseNumber.IsMatch(number[0]) ? ParcelNumbers.WithoutLeadingZeros(number[0]) : null;
		List<string> unit = number.Skip(house == null ? 0 : 1).Where(t => !UnitWords.Contains(t)).ToList();

		int cut = street.FindIndex(t => UnitWords.Contains(t));
		if (cut >= 0)
		{
			unit.AddRange(street.Skip(cut + 1));
			street = street.Take(cut).ToList();
		}
		street = street.Select(t => Directions.GetValueOrDefault(t) ?? Ordinals.GetValueOrDefault(t) ?? t).ToList();

		// A trailing suffix ("Main St"), or a suffix before a trailing direction ("Main St N").
		int? suffix = suffixId;
		if (street.Count >= 3 && Directions.ContainsValue(street[^1]) && _suffixes.TryGetValue(street[^2], out int beforeDirection))
		{
			suffix ??= beforeDirection;
			street.RemoveAt(street.Count - 2);
		}
		else if (street.Count >= 2 && _suffixes.TryGetValue(street[^1], out int last))
		{
			suffix ??= last;
			street.RemoveAt(street.Count - 1);
		}

		string zip5 = new string((zip ?? "").Where(char.IsDigit).Take(5).ToArray());
		return new AddressKey(
			house,
			street.Count == 0 ? null : string.Join(" ", street),
			suffix,
			unit.Count == 0 ? null : string.Join(" ", unit.Select(ParcelNumbers.WithoutLeadingZeros)),
			zip5.Length == 5 ? zip5 : null);
	}

	/// <summary>Whether two keys are the same building (see the class notes).</summary>
	public bool Same(AddressKey a, AddressKey b)
	{
		if (a == null || b == null || !a.CanMatch || !b.CanMatch || a.IndexKey != b.IndexKey)
		{
			return false;
		}
		if (a.SuffixId is int sa && b.SuffixId is int sb && sa != sb)
		{
			return false;
		}
		if (a.Zip != null && b.Zip != null && a.Zip != b.Zip)
		{
			return false;
		}
		return !MatchUnit || a.Unit == b.Unit;
	}

	/// <summary>An index of <paramref name="items"/> by address, for finding every item at an address quickly.</summary>
	public AddressIndex<T> Index<T>(IEnumerable<T> items, Func<T, AddressKey> keyOf) => new AddressIndex<T>(this, items, keyOf);

	/// <summary>Lower-case words of letters and digits, with "#" read as a unit ("N. Main St., #4" → n, main, st, unit, 4).</summary>
	private static List<string> Tokens(string text) =>
		Regex.Split((text ?? "").Replace("#", " unit ").ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(t => t.Length > 0).ToList();

	private void Add(string text, int suffixId)
	{
		string key = string.Concat(Tokens(text));
		if (key.Length > 0)
		{
			_suffixes.TryAdd(key, suffixId);
		}
	}
}

/// <summary>Items (structures) by address; see <see cref="AddressMatcher.Index{T}"/>.</summary>
public sealed class AddressIndex<T>
{
	private readonly AddressMatcher _matcher;
	private readonly ILookup<string, (T Item, AddressKey Key)> _byStreet;

	internal AddressIndex(AddressMatcher matcher, IEnumerable<T> items, Func<T, AddressKey> keyOf)
	{
		_matcher = matcher;
		_byStreet = items.Select(i => (Item: i, Key: keyOf(i))).Where(x => x.Key.CanMatch).ToLookup(x => x.Key.IndexKey, StringComparer.Ordinal);
	}

	/// <summary>Every item at the address (none, one, or several when the match is ambiguous).</summary>
	public IReadOnlyList<T> Find(AddressKey key) =>
		key == null || !key.CanMatch ? Array.Empty<T>() : _byStreet[key.IndexKey].Where(x => _matcher.Same(key, x.Key)).Select(x => x.Item).ToList();
}

/// <summary>Parcel numbers written in different ways: "0123-0045.67 00", "0123004567 00" and "0123004567-00" are one parcel.</summary>
public static class ParcelNumbers
{
	/// <summary>
	/// The parcel number without spaces, dashes or dots, in upper case; with <paramref name="ignoreLeadingZeros"/>,
	/// without leading zeros too ("00123" → "123"). Null when there is nothing left.
	/// </summary>
	public static string Normalize(string parcel, bool ignoreLeadingZeros = false)
	{
		string s = new string((parcel ?? "").Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '.').ToArray()).ToUpperInvariant();
		if (ignoreLeadingZeros && s.Length > 0)
		{
			s = WithoutLeadingZeros(s);
		}
		return s.Length == 0 ? null : s;
	}

	/// <summary>"007" → "7", "000" → "0".</summary>
	internal static string WithoutLeadingZeros(string s)
	{
		string trimmed = s.TrimStart('0');
		return trimmed.Length == 0 && s.Length > 0 ? "0" : trimmed;
	}
}
