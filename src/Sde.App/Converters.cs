using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Sde.App.ViewModels.Editor;

namespace Sde.App;

/// <summary>Value converters used by the views.</summary>
public static class Converters
{
	private static IBrush Brush(string key) =>
		Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object value) && value is IBrush b ? b : Brushes.Gray;

	public static readonly IValueConverter StatusForeground = new FuncValueConverter<ResultState, IBrush>(s => Brush($"Status{s}Fg"));

	public static readonly IValueConverter StatusBackground = new FuncValueConverter<ResultState, IBrush>(s => Brush($"Status{s}Bg"));

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

	/// <summary>Required (red) / recommended (amber) / suggested (green) issue colour.</summary>
	public static readonly IValueConverter IssueBrush = new FuncValueConverter<Sde.Core.Assessments.IssueSeverity, IBrush>(s => Brush(s switch
	{
		Sde.Core.Assessments.IssueSeverity.Required => "RequiredFg",
		Sde.Core.Assessments.IssueSeverity.Recommended => "RecommendedFg",
		_ => "SuggestedFg",
	}));
}
