using System.Globalization;
using System.Text;

namespace Sde.Interop.Internal;

/// <summary>An attribute table read from a .dbf: the field names and, per live record, its values as text.</summary>
internal sealed class DbfTable
{
	public DbfTable(IReadOnlyList<string> fields, IReadOnlyList<(int RecordNumber, List<string> Values)> records)
	{
		Fields = fields;
		Records = records;
	}

	/// <summary>Field names in file order (memo and other fields that hold no readable text are left out).</summary>
	public IReadOnlyList<string> Fields { get; }

	/// <summary>One entry per record that is not marked deleted, with its 1-based position in the file.</summary>
	public IReadOnlyList<(int RecordNumber, List<string> Values)> Records { get; }
}

/// <summary>
/// Reads dBASE III/IV/FoxPro .dbf tables, the attribute table of a shapefile, with no outside library. Values come back
/// as text the way <see cref="SheetReader"/> gives spreadsheet cells: numbers in invariant form, dates as ISO dates, logical
/// fields as TRUE/FALSE. Text is decoded with the code page named by a sibling .cpg file, else by the header's language
/// driver byte, else Windows-1252.
/// </summary>
internal static class DbfReader
{
	static DbfReader()
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
	}

	private sealed class Field
	{
		public string Name;
		public char Type;
		public int Offset;
		public int Length;
		public int Decimals;
	}

	/// <summary>The language driver byte (header offset 29) to a Windows or DOS code page. Anything else reads as Windows-1252.</summary>
	private static readonly Dictionary<byte, int> LanguageDrivers = new()
	{
		[0x01] = 437, [0x02] = 850, [0x03] = 1252, [0x04] = 10000, [0x13] = 932, [0x14] = 850, [0x15] = 437, [0x16] = 850, [0x17] = 865,
		[0x18] = 437, [0x19] = 437, [0x1A] = 850, [0x1B] = 437, [0x1C] = 863, [0x1D] = 850, [0x1F] = 852, [0x22] = 852, [0x23] = 852,
		[0x24] = 860, [0x25] = 850, [0x26] = 866, [0x37] = 850, [0x40] = 852, [0x4D] = 936, [0x4E] = 949, [0x4F] = 950, [0x50] = 874,
		[0x57] = 1252, [0x58] = 1252, [0x59] = 1252, [0x64] = 852, [0x65] = 866, [0x66] = 865, [0x67] = 861, [0x6A] = 737, [0x6B] = 857,
		[0x6C] = 863, [0x78] = 950, [0x79] = 949, [0x7A] = 936, [0x7B] = 932, [0x7C] = 874, [0x7D] = 1255, [0x7E] = 1256,
		[0x96] = 10007, [0x97] = 10029, [0x98] = 10006, [0xC8] = 1250, [0xC9] = 1251, [0xCA] = 1254, [0xCB] = 1253, [0xCC] = 1257,
	};

	/// <summary>The .dbf a path names: itself, or for a .shp (or .shx, .prj…) the .dbf with the same name beside it.</summary>
	public static string ResolveDbfPath(string path)
	{
		if (string.Equals(Path.GetExtension(path), ".dbf", StringComparison.OrdinalIgnoreCase))
		{
			return path;
		}
		string sibling = FindSibling(path, ".dbf");
		return sibling ?? throw new FileNotFoundException($"The shapefile '{Path.GetFileName(path)}' has no attribute table: there is no '{Path.GetFileNameWithoutExtension(path)}.dbf' beside it.", path);
	}

	public static DbfTable Read(string path)
	{
		string dbfPath = ResolveDbfPath(path);
		byte[] bytes;
		using (FileStream fs = new FileStream(dbfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
		using (MemoryStream ms = new MemoryStream())
		{
			fs.CopyTo(ms);
			bytes = ms.ToArray();
		}
		string cpgPath = FindSibling(dbfPath, ".cpg");
		string cpg = cpgPath == null ? null : File.ReadAllText(cpgPath);
		return Parse(bytes, cpg);
	}

	/// <summary>Reads a table from the bytes of a .dbf. <paramref name="cpg"/> is the text of its .cpg file, or null.</summary>
	internal static DbfTable Parse(byte[] bytes, string cpg)
	{
		if (bytes.Length < 33)
		{
			throw new InvalidDataException("This file is too short to be a dBASE (.dbf) table.");
		}
		// dBASE II to IV, FoxBase, Visual FoxPro and the memo-bearing variants of each.
		if (bytes[0] is not (0x02 or 0x03 or 0x04 or 0x05 or 0x30 or 0x31 or 0x32 or 0x83 or 0x8B or 0x8E or 0xCB or 0xE5 or 0xF5 or 0xFB))
		{
			throw new InvalidDataException("This file is not a dBASE (.dbf) table.");
		}
		uint recordCount = BitConverter.ToUInt32(bytes, 4);
		int headerSize = BitConverter.ToUInt16(bytes, 8);
		int recordSize = BitConverter.ToUInt16(bytes, 10);
		Encoding encoding = EncodingFor(cpg, bytes[29]);

		List<Field> fields = new();
		int pos = 32, offset = 1;
		while (pos + 32 <= bytes.Length && bytes[pos] != 0x0D)
		{
			int nameEnd = Array.IndexOf(bytes, (byte)0, pos, 11);
			if (nameEnd < 0)
			{
				nameEnd = pos + 11;
			}
			fields.Add(new Field
			{
				Name = encoding.GetString(bytes, pos, nameEnd - pos).Trim(),
				Type = (char)bytes[pos + 11],
				Length = bytes[pos + 16],
				Decimals = bytes[pos + 17],
				Offset = offset,
			});
			offset += bytes[pos + 16];
			pos += 32;
		}
		if (recordSize < offset)
		{
			throw new InvalidDataException("The record length in this .dbf file is smaller than its fields; the file is damaged.");
		}
		if (headerSize < pos + 1)
		{
			headerSize = pos + 1;
		}

		List<Field> readable = fields.Where(f => IsReadable(f.Type)).ToList();
		List<(int, List<string>)> records = new();
		for (long r = 0; r < recordCount; r++)
		{
			long start = headerSize + r * recordSize;
			if (start + recordSize > bytes.Length || bytes[start] == 0x1A)
			{
				break;
			}
			if (bytes[start] == (byte)'*')
			{
				continue;
			}
			List<string> values = new(readable.Count);
			foreach (Field f in readable)
			{
				int at = (int)start + f.Offset;
				values.Add(Value(bytes, at, Math.Min(f.Length, bytes.Length - at), f, encoding));
			}
			records.Add(((int)r + 1, values));
		}
		return new DbfTable(readable.Select(f => f.Name).ToList(), records);
	}

	/// <summary>Text, numeric, date, logical, integer and timestamp fields are read; memo, binary and general fields are skipped.</summary>
	private static bool IsReadable(char type) => type is 'C' or 'N' or 'F' or 'D' or 'L' or 'I' or 'T' or '+' or 'O' or '@';

	private static string Value(byte[] bytes, int at, int length, Field f, Encoding encoding)
	{
		switch (f.Type)
		{
			case 'C':
				return encoding.GetString(bytes, at, length).TrimEnd(' ', '\0');
			case 'N':
			case 'F':
				return Number(encoding.GetString(bytes, at, length));
			case 'D':
				string d = Encoding.ASCII.GetString(bytes, at, length).Trim();
				return DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
					? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
			case 'L':
				return (char)bytes[at] switch
				{
					'Y' or 'y' or 'T' or 't' => "TRUE",
					'N' or 'n' or 'F' or 'f' => "FALSE",
					_ => "",
				};
			case 'I':
			case '+':
				return length >= 4 ? BitConverter.ToInt32(bytes, at).ToString(CultureInfo.InvariantCulture) : "";
			case 'O':
				return length >= 8 ? BitConverter.ToDouble(bytes, at).ToString("R", CultureInfo.InvariantCulture) : "";
			case 'T':
			case '@':
				// Julian day number, then milliseconds since midnight.
				if (length < 8)
				{
					return "";
				}
				int day = BitConverter.ToInt32(bytes, at);
				int millis = BitConverter.ToInt32(bytes, at + 4);
				if (day <= 0)
				{
					return "";
				}
				DateTime stamp = new DateTime(1970, 1, 1).AddDays(day - 2440588).AddMilliseconds(millis);
				return stamp.TimeOfDay == TimeSpan.Zero ? stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
			default:
				return "";
		}
	}

	/// <summary>A numeric field's text in invariant form ("  39.1000" becomes "39.1"); blank and overflowed ("****") fields are empty.</summary>
	private static string Number(string text)
	{
		string t = text.Trim(' ', '\0');
		if (t.Length == 0 || t.Contains('*'))
		{
			return "";
		}
		if (!decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n))
		{
			return t;
		}
		// Dropping trailing zeros: dividing by 1.000…0 rescales the decimal to its shortest form.
		return (n / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
	}

	private static Encoding EncodingFor(string cpg, byte languageDriver)
	{
		Encoding fromCpg = ParseCpg(cpg);
		if (fromCpg != null)
		{
			return fromCpg;
		}
		int codePage = LanguageDrivers.TryGetValue(languageDriver, out int cp) ? cp : 1252;
		try
		{
			return Encoding.GetEncoding(codePage);
		}
		catch (ArgumentException)
		{
			return Encoding.GetEncoding(1252);
		}
	}

	/// <summary>A .cpg file's one line ("UTF-8", "1252", "ANSI 1252", "ISO-8859-1"…) as an encoding; null when blank or unknown.</summary>
	internal static Encoding ParseCpg(string cpg)
	{
		string text = cpg?.Trim().Trim('\0', '﻿').Trim();
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}
		string upper = text.ToUpperInvariant().Replace("_", "-");
		if (upper.Replace("-", "").Replace(" ", "") is "UTF8" or "UTF8BOM")
		{
			return new UTF8Encoding(false);
		}
		// Digits only, or a prefix such as "ANSI 1252", "CP1252", "WINDOWS-1252", "OEM 437".
		string digits = new string(upper.Where(char.IsDigit).ToArray());
		if (!upper.StartsWith("ISO") && digits.Length is >= 3 and <= 5 && int.TryParse(digits, out int codePage))
		{
			try
			{
				return Encoding.GetEncoding(codePage);
			}
			catch (ArgumentException)
			{
			}
		}
		try
		{
			return Encoding.GetEncoding(text);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	/// <summary>The file beside <paramref name="path"/> with the same name and another extension, whatever its letter case; null when absent.</summary>
	private static string FindSibling(string path, string extension)
	{
		string folder = Path.GetDirectoryName(Path.GetFullPath(path));
		string name = Path.GetFileNameWithoutExtension(path);
		string exact = Path.Combine(folder, name + extension);
		if (File.Exists(exact))
		{
			return exact;
		}
		return Directory.EnumerateFiles(folder, name + ".*")
			.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase));
	}
}
