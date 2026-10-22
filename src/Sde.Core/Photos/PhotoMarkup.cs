using Newtonsoft.Json;

namespace Sde.Core.Photos;

/// <summary>The kinds of mark that can be drawn on a photo.</summary>
public static class MarkupKinds
{
	/// <summary>A dashed line where the water reached, with an optional label ("Water line 3 ft 2 in").</summary>
	public const string WaterLine = "WaterLine";
	public const string Arrow = "Arrow";
	public const string Box = "Box";
	public const string Ellipse = "Ellipse";
	public const string Text = "Text";

	public static readonly IReadOnlyList<string> All = new[] { WaterLine, Arrow, Box, Ellipse, Text };
}

/// <summary>
/// One mark on a photo. Positions are fractions (0 to 1) of the upright photo's width and height, so the marks fit the
/// photo at any size: a thumbnail, a report, a full-size copy.
/// </summary>
public sealed class MarkupShape
{
	/// <summary>See <see cref="MarkupKinds"/>.</summary>
	public string Kind { get; set; }

	/// <summary>The start (a line or an arrow's tail), the top-left corner (a box or ellipse), or where text starts.</summary>
	public double X1 { get; set; }

	public double Y1 { get; set; }

	/// <summary>The end (a line or an arrow's head), or the bottom-right corner (a box or ellipse).</summary>
	public double X2 { get; set; }

	public double Y2 { get; set; }

	/// <summary>The words of a text mark, or a water line's label.</summary>
	public string Text { get; set; }

	/// <summary>"#RRGGBB".</summary>
	public string Color { get; set; }

	/// <summary>Line thickness as a fraction of the photo's long edge.</summary>
	public double Width { get; set; } = 0.006;
}

/// <summary>
/// The marks drawn on one photo (stored as JSON in the Mac-only PhotoMarkup table). The photo file itself never
/// changes: reports and the submission folder draw the marks onto copies, and FEMA's Export.json keeps the originals.
/// </summary>
public sealed class PhotoMarkup
{
	public const int MaxShapes = 200;

	public const int MaxTextLength = 200;

	public int Version { get; set; } = 1;

	public List<MarkupShape> Shapes { get; set; } = new();

	public bool IsEmpty => Shapes.Count == 0;

	/// <summary>The default colour of each kind: a water line is blue, everything else red.</summary>
	public static string DefaultColor(string kind) => kind == MarkupKinds.WaterLine ? "#1565C0" : "#E53935";

	/// <summary>Reads stored markup; null or unreadable JSON gives null. Unknown kinds and out-of-range positions are dropped or clamped.</summary>
	public static PhotoMarkup Parse(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}
		PhotoMarkup markup;
		try
		{
			markup = JsonConvert.DeserializeObject<PhotoMarkup>(json);
		}
		catch (JsonException)
		{
			return null;
		}
		if (markup?.Shapes == null)
		{
			return null;
		}
		markup.Shapes = markup.Shapes.Where(s => s != null && MarkupKinds.All.Contains(s.Kind)).Take(MaxShapes).ToList();
		foreach (MarkupShape s in markup.Shapes)
		{
			s.X1 = Clamp(s.X1);
			s.Y1 = Clamp(s.Y1);
			s.X2 = Clamp(s.X2);
			s.Y2 = Clamp(s.Y2);
			s.Width = Math.Clamp(double.IsFinite(s.Width) ? s.Width : 0.006, 0.001, 0.05);
			if (s.Text != null && s.Text.Length > MaxTextLength)
			{
				s.Text = s.Text[..MaxTextLength];
			}
			if (s.Color == null || s.Color.Length != 7 || s.Color[0] != '#')
			{
				s.Color = DefaultColor(s.Kind);
			}
		}
		return markup;
	}

	public string ToJson() => JsonConvert.SerializeObject(this);

	private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
}
