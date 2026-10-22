using Microsoft.Data.Sqlite;
using Sde.Cli;

namespace Sde.Tests.Data;

/// <summary>The sde command line: only "init" makes a new database.</summary>
public class CommandLineTests
{
	/// <summary>Runs sde with <paramref name="args"/> and returns its exit code with what it printed.</summary>
	private static (int Code, string Output, string Errors) Run(params string[] args)
	{
		TextWriter oldOut = Console.Out;
		TextWriter oldError = Console.Error;
		StringWriter output = new StringWriter();
		StringWriter errors = new StringWriter();
		Console.SetOut(output);
		Console.SetError(errors);
		try
		{
			int code = Program.Main(args);
			return (code, output.ToString(), errors.ToString());
		}
		finally
		{
			Console.SetOut(oldOut);
			Console.SetError(oldError);
		}
	}

	/// <summary>Runs <paramref name="test"/> with a new temporary folder that doesn't exist yet, and deletes it afterwards.</summary>
	private static void InNewFolder(Action<string> test)
	{
		string folder = Path.Combine(Path.GetTempPath(), "sde-cli-" + Guid.NewGuid().ToString("N"));
		try
		{
			test(folder);
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
	}

	[Theory]
	[InlineData("info")]
	[InlineData("list")]
	[InlineData("lookups")]
	public void MistypedDatabasePath_IsAnError_AndCreatesNothing(string command)
	{
		InNewFolder(folder =>
		{
			string typo = Path.Combine(folder, "sde.sqlte");
			(int code, string _, string errors) = Run("--db", typo, command);
			Assert.NotEqual(0, code);
			Assert.Contains("There is no SDE database at " + typo + ".", errors);
			Assert.False(File.Exists(typo));
			Assert.False(Directory.Exists(folder));
		});
	}

	[Fact]
	public void Init_MakesTheDatabase_ThatTheOtherCommandsOpen()
	{
		InNewFolder(folder =>
		{
			string path = Path.Combine(folder, "event.sqlite");
			Assert.Equal(0, Run("--db", path, "init").Code);
			Assert.True(File.Exists(path));
			(int code, string output, string _) = Run("--db", path, "info");
			Assert.Equal(0, code);
			Assert.Contains("Structures:  0", output);
		});
	}
}
