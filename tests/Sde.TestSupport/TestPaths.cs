using Xunit;

namespace Sde.TestSupport;

public static class TestPaths
{
	private static readonly Lazy<string> _root = new(() =>
	{
		DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Sde.sln")))
		{
			dir = dir.Parent;
		}
		return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root (Sde.sln) above " + AppContext.BaseDirectory);
	});

	public static string Root => _root.Value;

	/// <summary>Why answer-key tests are skipped when FEMA's files aren't there.</summary>
	public const string AnswerKeyMissing = "Answer key not set up: run tools/answer-key/setup.sh";

	/// <summary>
	/// FEMA's original JSON tables from the unpacked installer (extracted/ is gitignored). Tests that read it are
	/// skipped when it isn't there.
	/// </summary>
	public static string FemaJson => RequireDir(Path.Combine(Root, "extracted", "JSON"));

	/// <summary>FEMA's shipped (empty) database from the unpacked installer; skipped like <see cref="FemaJson"/>.</summary>
	public static string FemaMdb => RequireFile(Path.Combine(Root, "extracted", "Database", "SDEDatabase.mdb"));

	/// <summary>The decompiled FEMA source (decomp/, made by tools/answer-key/setup.sh), or null when it isn't there.</summary>
	public static string Decomp => Directory.Exists(Path.Combine(Root, "decomp")) ? Path.Combine(Root, "decomp") : null;

	/// <summary>Committed facts taken from FEMA's code, so the format tests run without the answer key.</summary>
	public static string FemaFacts => Path.Combine(Root, "tests", "fixtures", "fema-facts");

	private static string RequireDir(string path)
	{
		Assert.SkipUnless(Directory.Exists(path), AnswerKeyMissing);
		return path;
	}

	private static string RequireFile(string path)
	{
		Assert.SkipUnless(File.Exists(path), AnswerKeyMissing);
		return path;
	}
}
