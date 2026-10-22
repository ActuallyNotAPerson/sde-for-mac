using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Sde.App.ViewModels.Editor;

namespace Sde.App;

/// <summary>Value converters used by the views.</summary>
public static class Converters
{
	/// <summary>The name of the result's text colour, e.g. "StatusSubstantialFg", for <see cref="ThemeBrush"/>.</summary>
	public static readonly IValueConverter StatusForegroundKey = new FuncValueConverter<ResultState, string>(s => $"Status{s}Fg");

	/// <summary>The name of the result's background colour, e.g. "StatusSubstantialBg", for <see cref="ThemeBrush"/>.</summary>
	public static readonly IValueConverter StatusBackgroundKey = new FuncValueConverter<ResultState, string>(s => $"Status{s}Bg");

	/// <summary>"$1,234.56" or "—" for null.</summary>
	public static readonly IValueConverter Money = new FuncValueConverter<decimal?, string>(v => v is decimal d ? d.ToString("C2", CultureInfo.GetCultureInfo("en-US")) : "–");

	/// <summary>"$1,235" (whole dollars) or "—".</summary>
	public static readonly IValueConverter MoneyWhole = new FuncValueConverter<decimal?, string>(v => v is decimal d ? d.ToString("C0", CultureInfo.GetCultureInfo("en-US")) : "–");

	/// <summary>"12.5%" or "—".</summary>
	public static readonly IValueConverter Percent = new FuncValueConverter<decimal?, string>(v => v is decimal d ? d.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "–");

	/// <summary>"1,450 sq ft" or "—".</summary>
	public static readonly IValueConverter SquareFeet = new FuncValueConverter<decimal?, string>(v => v is decimal d ? d.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " sq ft" : "–");

	/// <summary>True when the structure-list filter equals the parameter (e.g. "Substantial").</summary>
	public static readonly IValueConverter FilterIs = new FuncValueConverter<Sde.App.ViewModels.StructureFilter, string, bool>((f, p) => f.ToString() == p);

	public static readonly IValueConverter BoldIf = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

	public static readonly IValueConverter IsPositive = new FuncValueConverter<int, bool>(v => v > 0);

	public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(v => v == 0);

	/// <summary>The name of the issue's colour, required (red) / recommended (amber) / suggested (green), for <see cref="ThemeBrush"/>.</summary>
	public static readonly IValueConverter IssueBrushKey = new FuncValueConverter<Sde.Core.Assessments.IssueSeverity, string>(s => s switch
	{
		Sde.Core.Assessments.IssueSeverity.Required => "RequiredFg",
		Sde.Core.Assessments.IssueSeverity.Recommended => "RecommendedFg",
		_ => "SuggestedFg",
	});
}
