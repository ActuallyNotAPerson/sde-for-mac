using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>
/// A horizontal bar made of coloured pieces, drawn with plain shapes (the dashboard has no chart library). Each piece
/// is a share of the full width. It is decoration: the numbers beside it say the same thing in words.
/// </summary>
public sealed class StackedBar : Control
{
	public static readonly StyledProperty<IReadOnlyList<BarSegment>> SegmentsProperty =
		AvaloniaProperty.Register<StackedBar, IReadOnlyList<BarSegment>>(nameof(Segments));

	static StackedBar() => AffectsRender<StackedBar>(SegmentsProperty);

	public StackedBar() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

	public IReadOnlyList<BarSegment> Segments
	{
		get => GetValue(SegmentsProperty);
		set => SetValue(SegmentsProperty, value);
	}

	public override void Render(DrawingContext context)
	{
		Rect all = new Rect(Bounds.Size);
		IBrush track = Brush("SubtleBg", Brushes.LightGray);
		context.DrawRectangle(track, null, new RoundedRect(all, 4));
		if (Segments == null || all.Width < 1)
		{
			return;
		}
		using (context.PushClip(new RoundedRect(all, 4)))
		{
			double x = 0;
			foreach (BarSegment s in Segments)
			{
				double w = Math.Clamp(s.Fraction, 0, 1) * all.Width;
				context.DrawRectangle(Brush(s.ColourKey, Brushes.Gray), null, new Rect(x, 0, w, all.Height));
				x += w;
			}
		}
	}

	private IBrush Brush(string key, IBrush fallback) =>
		this.TryFindResource(key, ActualThemeVariant, out object value) && value is IBrush b ? b : fallback;
}
