using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace Sde.App.Tests;

/// <summary>
/// Text tokens keep at least 4.5:1 contrast (WCAG AA for body text) on the backgrounds they are used on, in the light
/// and the dark theme. The colours are read from the app's resources (Styles/Tokens.axaml), so this checks what ships.
/// </summary>
public class ContrastTests
{
	/// <summary>Text (or icon) token, background token. Every pair is checked in both themes.</summary>
	public static readonly (string Text, string Background)[] Pairs =
	{
		("TextPrimary", "BgApp"), ("TextPrimary", "BgSidebar"), ("TextPrimary", "BgSurface"), ("TextPrimary", "BgSubtle"), ("TextPrimary", "BgHover"), ("TextPrimary", "AccentSoft"),
		("TextSecondary", "BgApp"), ("TextSecondary", "BgSidebar"), ("TextSecondary", "BgSurface"), ("TextSecondary", "BgSubtle"), ("TextSecondary", "BgHover"), ("TextSecondary", "AccentSoft"),
		("TextTertiary", "BgApp"), ("TextTertiary", "BgSidebar"), ("TextTertiary", "BgSurface"), ("TextTertiary", "BgSubtle"), ("TextTertiary", "BgHover"),
		("Accent", "BgApp"), ("Accent", "BgSidebar"), ("Accent", "BgSurface"), ("Accent", "AccentSoft"),
		("OnAccent", "AccentFill"), ("OnAccent", "AccentFillHover"), ("OnAccent", "AccentFillPressed"), ("OnAccent", "DangerFill"), ("OnAccent", "DangerFillHover"),
		("DangerFg", "DangerBg"), ("DangerFg", "BgSurface"), ("DangerFg", "BgApp"),
		("WarningFg", "WarningBg"), ("WarningFg", "BgSurface"), ("WarningFg", "BgApp"),
		("SuccessFg", "SuccessBg"), ("SuccessFg", "BgSurface"), ("SuccessFg", "BgApp"),
		("InfoFg", "InfoBg"), ("InfoFg", "BgSurface"), ("VioletFg", "VioletBg"), ("NeutralFg", "NeutralBg"),
		("TextPrimary", "DangerBg"), ("TextPrimary", "WarningBg"), ("TextPrimary", "SuccessBg"), ("TextPrimary", "InfoBg"), ("TextPrimary", "NeutralBg"),
		("TextOnTooltip", "BgTooltip"),
		// The older names the result colours are looked up by
		("StatusSubstantialFg", "StatusSubstantialBg"), ("StatusCloseFg", "StatusCloseBg"), ("StatusNotSubstantialFg", "StatusNotSubstantialBg"),
		("StatusIncompleteFg", "StatusIncompleteBg"), ("StatusUndeterminedFg", "StatusUndeterminedBg"),
		("StatusSubstantialFg", "CardBg"), ("StatusCloseFg", "CardBg"), ("StatusNotSubstantialFg", "CardBg"), ("StatusIncompleteFg", "CardBg"),
		("RequiredFg", "CardBg"), ("RecommendedFg", "CardBg"), ("SuggestedFg", "CardBg"), ("TextMuted", "CardBg"), ("TextMuted", "SidebarBg"),
	};

	private static Color ColorOf(string key, ThemeVariant theme)
	{
		Assert.True(Application.Current!.TryGetResource(key, theme, out object value), $"No resource \"{key}\" in the {theme} theme");
		ISolidColorBrush brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
		return brush.Color;
	}

	private static double Luminance(Color c)
	{
		static double Channel(byte v)
		{
			double s = v / 255.0;
			return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
		}
		return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
	}

	public static double Ratio(Color a, Color b)
	{
		double la = Luminance(a);
		double lb = Luminance(b);
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	[AvaloniaFact]
	public void TextTokens_HaveAtLeast4Point5To1_InBothThemes()
	{
		List<string> failures = new();
		foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
		{
			foreach ((string text, string background) in Pairs)
			{
				Color bg = ColorOf(background, theme);
				Assert.Equal(255, bg.A);                     // backgrounds are opaque, so the ratio is exact
				double ratio = Ratio(ColorOf(text, theme), bg);
				if (ratio < 4.5)
				{
					failures.Add($"{theme}: {text} on {background} is {ratio:0.00}:1");
				}
			}
		}
		Assert.True(failures.Count == 0, string.Join("\n", failures));
	}

	[AvaloniaFact]
	public void TheThemesDiffer_AndTheAccentIsTheBrandBlue()
	{
		Assert.NotEqual(ColorOf("BgSurface", ThemeVariant.Light), ColorOf("BgSurface", ThemeVariant.Dark));
		Assert.Equal(Color.Parse("#1E5AA8"), ColorOf("Accent", ThemeVariant.Light));
		Assert.Equal(Color.Parse("#1E5AA8"), ColorOf("AccentFill", ThemeVariant.Light));
		Assert.True(Application.Current!.TryGetResource("BrandAccentColor", null, out object brand));
		Assert.Equal(Color.Parse("#1E5AA8"), (Color)brand);
		// Fluent's own accent (check boxes, sliders, switches) is the brand blue, not the Mac's accent colour.
		Assert.True(Application.Current.TryGetResource("SystemAccentColor", ThemeVariant.Light, out object fluent));
		Assert.Equal(Color.Parse("#1E5AA8"), (Color)fluent);
	}

	[Fact]
	public void TheRatioIsComputedAsWcagSays()
	{
		Assert.Equal(21.0, Ratio(Colors.Black, Colors.White), 2);
		Assert.Equal(1.0, Ratio(Colors.Gray, Colors.Gray), 2);
	}
}
