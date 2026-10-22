using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sde.Gis.Features;

namespace Sde.Gis.Styles;

/// <summary>How a layer's features are coloured.</summary>
public enum StyleKind
{
	/// <summary>One symbol for every feature.</summary>
	Single,

	/// <summary>A symbol for each value of a field (unique values).</summary>
	Categorized,

	/// <summary>A symbol for each range of a number field (equal intervals), coloured along a ramp.</summary>
	Graduated,
}

/// <summary>The marker drawn for points.</summary>
public enum PointShape
{
	Circle,
	Square,
	Triangle,
	Diamond,
	Cross,
}

/// <summary>How one feature is drawn: fill, outline, and for points a marker shape and size. Engine-independent.</summary>
public sealed class Symbol
{
	/// <summary>Fill colour of polygons and point markers ("#RRGGBB" or "#RRGGBBAA").</summary>
	public string Fill { get; set; } = "#2F6FB3";

	/// <summary>Colour of lines and of polygon and marker outlines.</summary>
	public string Stroke { get; set; } = "#1B3F66";

	/// <summary>Line and outline width in points.</summary>
	public double StrokeWidth { get; set; } = 1.0;

	/// <summary>Point marker size (diameter) in points.</summary>
	public double PointSize { get; set; } = 8.0;

	public PointShape Shape { get; set; } = PointShape.Circle;

	public Symbol Clone() => (Symbol)MemberwiseClone();

	/// <summary>A copy with another fill, and an outline a little darker than it.</summary>
	public Symbol WithFill(Rgba fill)
	{
		Symbol s = Clone();
		s.Fill = fill.ToHex();
		s.Stroke = Rgba.Lerp(fill, new Rgba(0, 0, 0, fill.A), 0.35).ToHex();
		return s;
	}
}

/// <summary>One value of a categorised style.</summary>
public sealed class StyleCategory
{
	/// <summary>The field value as text (see <see cref="FieldValues.Format"/>); "" stands for features with no value.</summary>
	public string Value { get; set; }

	public string Label { get; set; }

	public Symbol Symbol { get; set; }

	/// <summary>Unticked categories aren't drawn.</summary>
	public bool Visible { get; set; } = true;
}

/// <summary>One range of a graduated style: Min ≤ value &lt; Max (the last class includes its Max).</summary>
public sealed class StyleClass
{
	public double Min { get; set; }

	public double Max { get; set; }

	public string Label { get; set; }

	public Symbol Symbol { get; set; }
}

/// <summary>A legend line: what the symbol stands for.</summary>
public sealed record LegendEntry(string Label, Symbol Symbol);

/// <summary>
/// A layer's symbology: single symbol, categorised (unique values) or graduated (equal intervals along a colour ramp).
/// Engine-independent data, stored as JSON in MapLayer.Style; the map engine asks <see cref="SymbolFor(Feature)"/>
/// how to draw each feature. Opacity and the scale range are kept in MapLayer's own columns (see <see cref="LayerDisplay"/>).
/// </summary>
public sealed class LayerStyle
{
	public StyleKind Kind { get; set; } = StyleKind.Single;

	/// <summary>The single symbol; for the other kinds, the symbol the others are based on.</summary>
	public Symbol Symbol { get; set; } = new Symbol();

	/// <summary>The field categories or classes are taken from.</summary>
	public string Field { get; set; }

	public List<StyleCategory> Categories { get; set; } = new List<StyleCategory>();

	public List<StyleClass> Classes { get; set; } = new List<StyleClass>();

	/// <summary>Drawn for values that no category or class covers; null leaves those features out.</summary>
	public Symbol OtherSymbol { get; set; }

	/// <summary>The name of the graduated style's colour ramp (see <see cref="ColorRamp.Presets"/>).</summary>
	public string Ramp { get; set; }

	/// <summary>A one-symbol style.</summary>
	public static LayerStyle SingleSymbol(Symbol symbol = null) => new LayerStyle { Kind = StyleKind.Single, Symbol = symbol ?? new Symbol() };

	/// <summary>
	/// A categorised style with one category per distinct value of <paramref name="field"/> in the layer (sorted;
	/// numbers in numeric order), coloured from <see cref="ColorRamp.Categories"/>. Features with no value get a grey "(no value)" category.
	/// </summary>
	public static LayerStyle Categorized(FeatureLayer layer, string field, Symbol baseSymbol = null)
	{
		ArgumentNullException.ThrowIfNull(layer);
		int index = layer.Fields.IndexOf(field);
		if (index < 0)
		{
			throw new GisException($"This layer has no field called {field}.", field);
		}
		List<object> values = layer.Features.Select(f => f[index]).ToList();
		return Categorized(field, values, baseSymbol);
	}

	/// <summary>A categorised style for the given values.</summary>
	public static LayerStyle Categorized(string field, IEnumerable<object> values, Symbol baseSymbol = null)
	{
		baseSymbol ??= new Symbol();
		List<object> distinct = values.Where(v => v != null && FieldValues.Format(v).Trim().Length > 0)
			.GroupBy(FieldValues.Format, StringComparer.Ordinal).Select(g => g.First()).ToList();
		bool numeric = distinct.All(v => v is long or double);
		IEnumerable<object> ordered = numeric
			? distinct.OrderBy(v => Convert.ToDouble(v, CultureInfo.InvariantCulture))
			: distinct.OrderBy(v => FieldValues.Format(v), StringComparer.OrdinalIgnoreCase);
		LayerStyle style = new LayerStyle { Kind = StyleKind.Categorized, Field = field, Symbol = baseSymbol.Clone() };
		int n = 0;
		foreach (object v in ordered)
		{
			string text = FieldValues.Format(v);
			Rgba color = Rgba.Parse(ColorRamp.Categories[n % ColorRamp.Categories.Count], new Rgba(128, 128, 128));
			style.Categories.Add(new StyleCategory { Value = text, Label = text, Symbol = baseSymbol.WithFill(color) });
			n++;
		}
		if (values.Any(v => v == null || FieldValues.Format(v).Trim().Length == 0))
		{
			style.Categories.Add(new StyleCategory { Value = "", Label = "(no value)", Symbol = baseSymbol.WithFill(new Rgba(0xB8, 0xBC, 0xC2)) });
		}
		return style;
	}

	/// <summary>A graduated style of <paramref name="classCount"/> equal intervals over the layer's values of a number field.</summary>
	public static LayerStyle Graduated(FeatureLayer layer, string field, int classCount, ColorRamp ramp = null, Symbol baseSymbol = null)
	{
		ArgumentNullException.ThrowIfNull(layer);
		LayerField f = layer.Fields[field] ?? throw new GisException($"This layer has no field called {field}.", field);
		if (f.Type is not (FieldType.Integer or FieldType.Real))
		{
			throw new GisException($"{f.DisplayName} doesn't hold numbers, so it can't be shown in graduated colours.", field);
		}
		List<double> values = layer.Features.Select(x => x[field]).Where(v => v != null).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToList();
		if (values.Count == 0)
		{
			throw new GisException($"{f.DisplayName} has no values to colour by.", field);
		}
		return Graduated(field, values.Min(), values.Max(), classCount, ramp, baseSymbol, f.Type == FieldType.Integer);
	}

	/// <summary>A graduated style of equal intervals from <paramref name="min"/> to <paramref name="max"/>.</summary>
	public static LayerStyle Graduated(string field, double min, double max, int classCount, ColorRamp ramp = null, Symbol baseSymbol = null, bool wholeNumbers = false)
	{
		if (classCount < 1 || classCount > 20)
		{
			throw new ArgumentOutOfRangeException(nameof(classCount), "Use 1 to 20 classes.");
		}
		if (max < min)
		{
			(min, max) = (max, min);
		}
		ramp ??= ColorRamp.Presets[0];
		baseSymbol ??= new Symbol();
		LayerStyle style = new LayerStyle { Kind = StyleKind.Graduated, Field = field, Ramp = ramp.Name, Symbol = baseSymbol.Clone() };
		int n = max > min ? classCount : 1;
		double width = n == 1 ? 0 : (max - min) / n;
		List<Rgba> colors = ramp.Sample(n);
		for (int i = 0; i < n; i++)
		{
			double lo = min + i * width;
			double hi = i == n - 1 ? max : min + (i + 1) * width;
			style.Classes.Add(new StyleClass { Min = lo, Max = hi, Label = $"{Number(lo, wholeNumbers)} to {Number(hi, wholeNumbers)}", Symbol = baseSymbol.WithFill(colors[i]) });
		}
		return style;
	}

	/// <summary>How to draw a feature, or null when it shouldn't be drawn.</summary>
	public Symbol SymbolFor(Feature feature)
	{
		if (Kind == StyleKind.Single)
		{
			return Symbol;
		}
		return SymbolFor(feature == null || Field == null ? null : feature[Field]);
	}

	/// <summary>How to draw a feature with this value in <see cref="Field"/>, or null when it shouldn't be drawn.</summary>
	public Symbol SymbolFor(object value)
	{
		switch (Kind)
		{
			case StyleKind.Categorized:
			{
				string text = value == null ? "" : FieldValues.Format(value).Trim();
				StyleCategory c = Categories.FirstOrDefault(x => string.Equals(x.Value ?? "", text, StringComparison.Ordinal));
				return c == null ? OtherSymbol : c.Visible ? c.Symbol : null;
			}
			case StyleKind.Graduated:
			{
				if (value == null || !FieldValues.TryConvert(FieldType.Real, value, out object n) || n == null)
				{
					return OtherSymbol;
				}
				double v = (double)n;
				for (int i = 0; i < Classes.Count; i++)
				{
					StyleClass k = Classes[i];
					bool last = i == Classes.Count - 1;
					if (v >= k.Min && (v < k.Max || (last && v <= k.Max)))
					{
						return k.Symbol;
					}
				}
				return OtherSymbol;
			}
			default:
				return Symbol;
		}
	}

	/// <summary>The legend lines for this style, in order.</summary>
	public List<LegendEntry> Legend(string layerName = null)
	{
		List<LegendEntry> entries = Kind switch
		{
			StyleKind.Categorized => Categories.Where(c => c.Visible).Select(c => new LegendEntry(c.Label ?? c.Value, c.Symbol)).ToList(),
			StyleKind.Graduated => Classes.Select(c => new LegendEntry(c.Label, c.Symbol)).ToList(),
			_ => new List<LegendEntry> { new LegendEntry(layerName ?? "", Symbol) },
		};
		if (Kind != StyleKind.Single && OtherSymbol != null)
		{
			entries.Add(new LegendEntry("Other", OtherSymbol));
		}
		return entries;
	}

	public string ToJson() => JsonSerializer.Serialize(this, StyleJson.Options);

	/// <summary>Reads style JSON; a missing or damaged value gives the default single-symbol style rather than an error.</summary>
	public static LayerStyle FromJson(string json) => StyleJson.Read<LayerStyle>(json) ?? new LayerStyle();

	private static string Number(double v, bool whole) =>
		whole ? Math.Round(v).ToString("N0", CultureInfo.InvariantCulture) : v.ToString(Math.Abs(v) >= 100 ? "N0" : "0.##", CultureInfo.InvariantCulture);
}

/// <summary>Labels for a layer: which field, how big, with or without a halo. Stored as JSON in MapLayer.Labels.</summary>
public sealed class LabelStyle
{
	public bool Enabled { get; set; } = true;

	/// <summary>The field whose value is the label text.</summary>
	public string Field { get; set; }

	/// <summary>Text size in points.</summary>
	public double Size { get; set; } = 11;

	public string Color { get; set; } = "#1F2328";

	/// <summary>An outline around the letters so labels read over any background.</summary>
	public bool Halo { get; set; } = true;

	public string HaloColor { get; set; } = "#FFFFFF";

	/// <summary>Halo width in points.</summary>
	public double HaloWidth { get; set; } = 1.5;

	/// <summary>Labels show only when zoomed in past this scale (1:MinScale), when set.</summary>
	public double? MinScale { get; set; }

	/// <summary>Labels hide when zoomed in past this scale (1:MaxScale), when set.</summary>
	public double? MaxScale { get; set; }

	/// <summary>The label for a feature, or null when labels are off or the feature has no value.</summary>
	public string TextFor(Feature feature)
	{
		if (!Enabled || feature == null || string.IsNullOrEmpty(Field))
		{
			return null;
		}
		string text = FieldValues.Format(feature[Field]).Trim();
		return text.Length == 0 ? null : text;
	}

	public bool IsVisibleAt(double scale) => Enabled && LayerDisplay.IsWithin(scale, MinScale, MaxScale);

	public string ToJson() => JsonSerializer.Serialize(this, StyleJson.Options);

	/// <summary>Reads label JSON; a missing or damaged value gives labels turned off.</summary>
	public static LabelStyle FromJson(string json) => StyleJson.Read<LabelStyle>(json) ?? new LabelStyle { Enabled = false };
}

/// <summary>
/// Opacity and scale range, the display settings MapLayer keeps in its own columns (Opacity, MinScale, MaxScale).
/// Scales are map scale denominators: 24000 means 1:24,000.
/// </summary>
public sealed class LayerDisplay
{
	/// <summary>0 (invisible) to 1 (solid).</summary>
	public double Opacity { get; set; } = 1.0;

	/// <summary>The layer shows only when zoomed in past 1:MinScale (null or 0 for no limit).</summary>
	public double? MinScale { get; set; }

	/// <summary>The layer hides when zoomed in past 1:MaxScale (null or 0 for no limit).</summary>
	public double? MaxScale { get; set; }

	/// <summary>Opacity held to 0 to 1.</summary>
	public double ClampedOpacity => double.IsNaN(Opacity) ? 1.0 : Math.Clamp(Opacity, 0.0, 1.0);

	public bool IsVisibleAt(double scale) => ClampedOpacity > 0 && IsWithin(scale, MinScale, MaxScale);

	/// <summary>True when a scale denominator is inside the range (a zero or missing end is no limit).</summary>
	public static bool IsWithin(double scale, double? minScale, double? maxScale)
	{
		if (minScale is double min && min > 0 && scale > min)
		{
			return false;
		}
		if (maxScale is double max && max > 0 && scale < max)
		{
			return false;
		}
		return true;
	}
}

/// <summary>The JSON settings for style and label JSON: camelCase names, enums as words, nulls left out.</summary>
public static class StyleJson
{
	public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
	};

	internal static T Read<T>(string json) where T : class
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<T>(json, Options);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
