import com.healthmarketscience.jackcess.Column;
import com.healthmarketscience.jackcess.DataType;
import com.healthmarketscience.jackcess.Database;
import com.healthmarketscience.jackcess.DatabaseBuilder;
import com.healthmarketscience.jackcess.Row;
import com.healthmarketscience.jackcess.Table;
import java.io.File;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Replaces text in the text and memo columns of an Access database's assessment tables (never FEMA's reference tables,
 * which hold real place names), in place: used to keep the committed .mdb test
 * fixture in step with tests/fixtures/mdb-source when synthetic names or places change, without rebuilding it from
 * FEMA's installer.
 *
 * Usage (Java 11 or later, the Jackcess jars listed in make_fixture.sh):
 *   java -cp "$JACKCESS_JARS/*" tools/mdb-fixture/ReplaceText.java path/to/SDEDatabase.mdb "old=new" ["old=new" ...]
 */
public class ReplaceText
{
	private static final List<String> Tables = List.of("Property", "Assessment", "Attachment", "AdditionalAdjustment", "CustomField_Results", "LastFiveAssesments");

	public static void main(String[] args) throws Exception
	{
		Map<String, String> pairs = new LinkedHashMap<>();
		for (int i = 1; i < args.length; i++)
		{
			int eq = args[i].indexOf('=');
			pairs.put(args[i].substring(0, eq), args[i].substring(eq + 1));
		}
		int changed = 0;
		try (Database db = DatabaseBuilder.open(new File(args[0])))
		{
			for (String name : db.getTableNames())
			{
				if (!Tables.contains(name))
				{
					continue;
				}
				Table table = db.getTable(name);
				for (Row row : table)
				{
					boolean dirty = false;
					for (Column c : table.getColumns())
					{
						if (c.getType() != DataType.TEXT && c.getType() != DataType.MEMO)
						{
							continue;
						}
						Object v = row.get(c.getName());
						if (!(v instanceof String s))
						{
							continue;
						}
						String next = s;
						for (Map.Entry<String, String> p : pairs.entrySet())
						{
							next = next.replace(p.getKey(), p.getValue());
						}
						if (!next.equals(s))
						{
							row.put(c.getName(), next);
							dirty = true;
						}
					}
					if (dirty)
					{
						table.updateRow(row);
						changed++;
					}
				}
			}
		}
		System.out.println("Changed " + changed + " rows.");
	}
}
