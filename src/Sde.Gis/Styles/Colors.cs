using System.Globalization;

namespace Sde.Gis.Styles;

/// <summary>A colour with alpha, written as "#RRGGBB" or "#RRGGBBAA" in style JSON.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
	/// <summary>Reads "#RGB", "#RRGGBB" or "#RRGGBBAA" (the # is optional).</summary>
	public static bool TryParse(string text, out Rgba color)
	{
		color = default;
		string s = (text ?? "").Trim().TrimStart('#');
		if (s.Length == 3)
		{
			s = string.Concat(s.Select(c => new string(c, 2)));
		}
		if ((s.Length != 6 && s.Length != 8) || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
		{
			return false;
		}
		color = s.Length == 6
			? new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v)
			: new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
		return true;
	}

	/// <summary>The colour, or <paramref name="fallback"/> when the text isn't a colour.</summary>
	public static Rgba Parse(string text, Rgba fallback) => TryParse(text, out Rgba c) ? c : fallback;

	/// <summary>"#RRGGBB", or "#RRGGBBAA" when not opaque.</summary>
	public string ToHex() => A == 255
		? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}")
		: string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");

	/// <summary>The colour a fraction <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>.</summary>
	public static Rgba Lerp(Rgba a, Rgba b, double t)
	{
		t = Math.Clamp(t, 0.0, 1.0);
		byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
		return new Rgba(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B), L(a.A, b.A));
	}

	public override string ToString() => ToHex();
}

/// <summary>A colour ramp for graduated styles: two or more colours, evenly spaced from low values to high.</summary>
public sealed class ColorRamp
{
	public ColorRamp()
	{
	}

	public ColorRamp(string name, params string[] stops)
	{
		Name = name;
		Stops = stops.ToList();
	}

	public string Name { get; set; }

	/// <summary>The colours, low to high, as "#RRGGBB".</summary>
	public List<string> Stops { get; set; } = new List<string>();

	/// <summary>The ramps offered in the style panel.</summary>
	public static IReadOnlyList<ColorRamp> Presets { get; } = new[]
	{
		new ColorRamp("Blues", "#EEF4FB", "#7FB0DD", "#0B4F8A"),
		new ColorRamp("Greens", "#EEF7EC", "#7FC38A", "#1D6B38"),
		new ColorRamp("Reds", "#FDEEEA", "#F08B6E", "#A51E1E"),
		new ColorRamp("Oranges", "#FFF2E2", "#F7A54A", "#A44A08"),
		new ColorRamp("Purples", "#F3F0F8", "#A493C9", "#4B2C82"),
		new ColorRamp("Yellow to red", "#FFF5B8", "#F59E2C", "#B3161B"),
		new ColorRamp("Blue to red", "#2B6CB0", "#F4F4F2", "#C53030"),
		new ColorRamp("Flood depth", "#DCEFFA", "#4A9BD8", "#0B2E6B"),
	};

	/// <summary>Distinct colours for categorised styles, used in turn (and again from the start after the last).</summary>
	public static IReadOnlyList<string> Categories { get; } = new[]
	{
		"#2F6FB3", "#E07A1F", "#3A9A48", "#C8383C", "#7D5BA6", "#8C5A3C",
		"#D46FA8", "#6E7781", "#A9A229", "#2AA3B5", "#F2B134", "#5C3A92",
	};

	/// <summary>A preset by name (any case), or Blues when there is none.</summary>
	public static ColorRamp Named(string name) =>
		Presets.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

	/// <summary>The colour at position <paramref name="t"/> from 0 (low) to 1 (high).</summary>
	public Rgba At(double t)
	{
		List<Rgba> stops = Stops.Select(s => Rgba.Parse(s, new Rgba(128, 128, 128))).ToList();
		if (stops.Count == 0)
		{
			return new Rgba(128, 128, 128);
		}
		if (stops.Count == 1 || double.IsNaN(t))
		{
			return stops[0];
		}
		t = Math.Clamp(t, 0.0, 1.0);
		double position = t * (stops.Count - 1);
		int i = Math.Min((int)Math.Floor(position), stops.Count - 2);
		return Rgba.Lerp(stops[i], stops[i + 1], position - i);
	}

	/// <summary><paramref name="n"/> colours spread evenly along the ramp.</summary>
	public List<Rgba> Sample(int n) => Enumerable.Range(0, Math.Max(1, n)).Select(i => At(n <= 1 ? 0.5 : (double)i / (n - 1))).ToList();
}
