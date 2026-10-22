using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Photos;

namespace Sde.App.Views;

/// <summary>
/// The picture on the photo markup sheet. It shows the photo with its marks (drawn by the same code the reports use)
/// fitted to the space, and turns a drag into a mark: while dragging it draws a dashed outline of the mark, and on
/// release it hands the two points, as fractions of the picture, to <see cref="PhotoMarkupSheetViewModel.AddShape"/>.
/// </summary>
public sealed class MarkupCanvas : Control
{
	public static readonly StyledProperty<Bitmap> SourceProperty =
		AvaloniaProperty.Register<MarkupCanvas, Bitmap>(nameof(Source));

	private Point? _start;
	private Point _current;

	static MarkupCanvas() => AffectsRender<MarkupCanvas>(SourceProperty);

	public MarkupCanvas()
	{
		ClipToBounds = true;
		Cursor = new Cursor(StandardCursorType.Cross);
	}

	public Bitmap Source
	{
		get => GetValue(SourceProperty);
		set => SetValue(SourceProperty, value);
	}

	private PhotoMarkupSheetViewModel Sheet => DataContext as PhotoMarkupSheetViewModel;

	/// <summary>Where the picture sits: as large as fits, centred.</summary>
	private Rect PictureRect()
	{
		if (Source == null || Source.Size.Width <= 0 || Source.Size.Height <= 0)
		{
			return default;
		}
		double scale = Math.Min(Bounds.Width / Source.Size.Width, Bounds.Height / Source.Size.Height);
		double w = Source.Size.Width * scale;
		double h = Source.Size.Height * scale;
		return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
	}

	public override void Render(DrawingContext context)
	{
		context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
		Rect picture = PictureRect();
		if (picture.Width < 1)
		{
			return;
		}
		context.DrawImage(Source, picture);
		if (_start is not Point a || Sheet is not PhotoMarkupSheetViewModel sheet)
		{
			return;
		}
		Color colour = Color.TryParse(sheet.Colour?.Hex, out Color c) ? c : Colors.Red;
		Pen halo = new Pen(new SolidColorBrush(Colors.White, 0.8), 4);
		Pen pen = new Pen(new SolidColorBrush(colour), 2, DashStyle.Dash);
		Point b = _current;
		using (context.PushClip(picture))
		{
			switch (sheet.Tool)
			{
				case MarkupKinds.Box:
					context.DrawRectangle(null, halo, new Rect(a, b));
					context.DrawRectangle(null, pen, new Rect(a, b));
					break;
				case MarkupKinds.Ellipse:
					context.DrawEllipse(null, halo, new Rect(a, b));
					context.DrawEllipse(null, pen, new Rect(a, b));
					break;
				case MarkupKinds.Text:
					context.DrawLine(pen, new Point(b.X - 8, b.Y), new Point(b.X + 8, b.Y));
					context.DrawLine(pen, new Point(b.X, b.Y - 8), new Point(b.X, b.Y + 8));
					break;
				default:
					context.DrawLine(halo, a, b);
					context.DrawLine(pen, a, b);
					break;
			}
		}
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (Sheet?.IsEditable != true || PictureRect().Width < 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			return;
		}
		_start = Inside(e.GetPosition(this));
		_current = _start.Value;
		e.Pointer.Capture(this);
		e.Handled = true;
		InvalidateVisual();
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (_start != null)
		{
			_current = Inside(e.GetPosition(this));
			InvalidateVisual();
		}
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (_start is not Point a)
		{
			return;
		}
		Point b = Inside(e.GetPosition(this));
		_start = null;
		e.Pointer.Capture(null);
		e.Handled = true;
		Rect r = PictureRect();
		if (r.Width >= 1 && r.Height >= 1)
		{
			Sheet?.AddShape((a.X - r.X) / r.Width, (a.Y - r.Y) / r.Height, (b.X - r.X) / r.Width, (b.Y - r.Y) / r.Height);
		}
		InvalidateVisual();
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		_start = null;
		InvalidateVisual();
	}

	private Point Inside(Point p)
	{
		Rect r = PictureRect();
		return new Point(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));
	}
}
