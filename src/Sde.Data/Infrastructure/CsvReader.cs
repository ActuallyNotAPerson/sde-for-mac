using System.Text;

namespace Sde.Data.Infrastructure;

/// <summary>A CSV cell; <see cref="Quoted"/> distinguishes "" (empty text) from an empty unquoted cell (NULL in mdb-export output).</summary>
internal readonly record struct CsvCell(string Text, bool Quoted)
{
	public bool IsNull => !Quoted && Text.Length == 0;
}

/// <summary>RFC 4180 reader for mdb-export output (quoted fields, doubled quotes, line breaks inside quotes).</summary>
internal static class CsvReader
{
	public static IEnumerable<List<CsvCell>> Read(TextReader reader)
	{
		List<CsvCell> row = new();
		StringBuilder cell = new();
		bool quoted = false;
		bool inQuotes = false;
		bool any = false;
		int ch;
		while ((ch = reader.Read()) != -1)
		{
			char c = (char)ch;
			any = true;
			if (inQuotes)
			{
				if (c == '"')
				{
					if (reader.Peek() == '"')
					{
						reader.Read();
						cell.Append('"');
					}
					else
					{
						inQuotes = false;
					}
				}
				else
				{
					cell.Append(c);
				}
				continue;
			}
			switch (c)
			{
			case '"':
				inQuotes = true;
				quoted = true;
				break;
			case ',':
				row.Add(new CsvCell(cell.ToString(), quoted));
				cell.Clear();
				quoted = false;
				break;
			case '\r':
				break;
			case '\n':
				row.Add(new CsvCell(cell.ToString(), quoted));
				yield return row;
				row = new();
				cell.Clear();
				quoted = false;
				any = false;
				break;
			default:
				cell.Append(c);
				break;
			}
		}
		if (any)
		{
			row.Add(new CsvCell(cell.ToString(), quoted));
			yield return row;
		}
	}
}
