using System.Text.RegularExpressions;

namespace Sde.App.Tests;

/// <summary>
/// Keeps the views on the design tokens: no colour, font size or font weight is written into a view (Views/*.axaml),
/// only token names (Styles/Tokens.axaml) and the classes in Styles/. Menus show a chevron icon, not a typed "▾" or "⋯".
/// </summary>
public class StyleGuardTests
{
	private static IEnumerable<string> ViewFiles() =>
		Directory.GetFiles(Path.Combine(TestPaths.Root, "src", "Sde.App", "Views"), "*.axaml").OrderBy(f => f, StringComparer.Ordinal);

	/// <summary>"MainWindow.axaml:12: FontSize=&quot;12&quot;" for every line of every view that <paramref name="pattern"/> matches.</summary>
	private static List<string> Find(Regex pattern)
	{
		List<string> found = new();
		foreach (string file in ViewFiles())
		{
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				foreach (Match m in pattern.Matches(lines[i]))
				{
					found.Add($"{Path.GetFileName(file)}:{i + 1}: {m.Value}");
				}
			}
		}
		return found;
	}

	private static void AssertNone(List<string> found, string what) =>
		Assert.True(found.Count == 0, $"{found.Count} {what} in Views (use the tokens and classes in Styles/):\n" + string.Join("\n", found));

	[Fact]
	public void TheViewsAreFound() => Assert.True(ViewFiles().Count() > 20);

	[Fact]
	public void NoHexColours() => AssertNone(Find(new Regex("#[0-9A-Fa-f]{3,8}\\b")), "hex colours");

	/// <summary>A brush written as a colour name ("White", "Red") is as fixed as a hex value; only Transparent is allowed.</summary>
	[Fact]
	public void NoNamedColours() =>
		AssertNone(Find(new Regex("\\b(Foreground|Background|BorderBrush|Fill|Stroke|Color)=\"(?!\\{)(?!Transparent\")[^\"]*\"")), "colour literals");

	[Fact]
	public void NoFontSizeLiterals() => AssertNone(Find(new Regex("\\bFontSize=\"(?!\\{)[^\"]*\"")), "font sizes");

	[Fact]
	public void NoFontWeightLiterals() => AssertNone(Find(new Regex("\\bFontWeight=\"(?!\\{)[^\"]*\"")), "font weights");

	[Fact]
	public void NoTypedChevronsOrEllipsisButtons() => AssertNone(Find(new Regex("[▾⋯]")), "typed ▾ or ⋯ (use DropDownButton or the More icon)");

	/// <summary>The guard itself: each rule catches what it should and lets tokens through.</summary>
	[Theory]
	[InlineData("<TextBlock FontSize=\"12\" />", true)]
	[InlineData("<TextBlock FontSize=\"{StaticResource FontSizeBody}\" />", false)]
	[InlineData("<TextBlock FontWeight=\"SemiBold\" />", true)]
	[InlineData("<Border Background=\"#66000000\" />", true)]
	[InlineData("<TextBlock Foreground=\"White\" />", true)]
	[InlineData("<Border Background=\"Transparent\" />", false)]
	[InlineData("<Border Background=\"{DynamicResource CardBg}\" />", false)]
	[InlineData("<Button Content=\"Export ▾\" />", true)]
	public void TheGuardCatchesLiterals(string xaml, bool caught)
	{
		Regex[] rules =
		{
			new Regex("#[0-9A-Fa-f]{3,8}\\b"),
			new Regex("\\b(Foreground|Background|BorderBrush|Fill|Stroke|Color)=\"(?!\\{)(?!Transparent\")[^\"]*\""),
			new Regex("\\bFontSize=\"(?!\\{)[^\"]*\""),
			new Regex("\\bFontWeight=\"(?!\\{)[^\"]*\""),
			new Regex("[▾⋯]"),
		};
		Assert.Equal(caught, rules.Any(r => r.IsMatch(xaml)));
	}
}
