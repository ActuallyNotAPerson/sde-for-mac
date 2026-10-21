using Microsoft.Data.Sqlite;
using Sde.Data;

namespace Sde.TestSupport;

/// <summary>A fresh SDE database in a temporary folder, deleted on dispose.</summary>
public sealed class TempDatabase : IDisposable
{
	public TempDatabase()
	{
		Folder = Path.Combine(Path.GetTempPath(), "sde-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Folder);
		Database = SdeDatabase.Create(Path.Combine(Folder, "sde.sqlite"));
		Store = new SdeStore(Database);
	}

	public string Folder { get; }
	public SdeDatabase Database { get; }
	public SdeStore Store { get; }

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try
		{
			Directory.Delete(Folder, recursive: true);
		}
		catch (IOException)
		{
		}
	}
}
