using Avalonia;
using Avalonia.Controls;

namespace Sde.App.Controls;

/// <summary>
/// Spreads its children evenly along a line, each centred on its point: the first at the left (plus
/// <see cref="Inset"/>), the last at the right (less <see cref="Inset"/>). Used for the clickable 0, 25, 50, 75 and 100
/// labels under a damage slider, so each sits under its place on the slider's track.
/// </summary>
public sealed class TickPanel : Panel
{
	public static readonly StyledProperty<double> InsetProperty = AvaloniaProperty.Register<TickPanel, double>(nameof(Inset), 10);

	static TickPanel()
	{
		AffectsArrange<TickPanel>(InsetProperty);
	}

	/// <summary>How far in from each end the first and last points are (half the slider thumb's width).</summary>
	public double Inset
	{
		get => GetValue(InsetProperty);
		set => SetValue(InsetProperty, value);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		double height = 0;
		foreach (Control child in Children)
		{
			child.Measure(Size.Infinity);
			height = Math.Max(height, child.DesiredSize.Height);
		}
		double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
		return new Size(width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		int count = Children.Count;
		double track = Math.Max(0, finalSize.Width - Inset * 2);
		for (int i = 0; i < count; i++)
		{
			Control child = Children[i];
			double centre = Inset + (count == 1 ? track / 2 : track * i / (count - 1));
			double w = child.DesiredSize.Width;
			double x = Math.Clamp(centre - w / 2, 0, Math.Max(0, finalSize.Width - w));
			child.Arrange(new Rect(x, 0, w, finalSize.Height));
		}
		return finalSize;
	}
}
