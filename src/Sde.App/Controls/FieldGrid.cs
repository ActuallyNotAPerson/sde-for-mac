using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Sde.App.Controls;

/// <summary>
/// Lays out a form's fields in equal columns (two by default), filling each row left to right. A field whose root has
/// the class "wide" takes a whole row; a hidden field takes no space. Used as an ItemsControl's panel for the field
/// templates in App.axaml, so the fields share the card's width instead of keeping a fixed width. When the card is too
/// narrow for every column to keep <see cref="MinColumnWidth"/>, it uses fewer columns.
/// </summary>
public sealed class FieldGrid : Panel
{
	public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<FieldGrid, int>(nameof(Columns), 2);

	public static readonly StyledProperty<double> ColumnSpacingProperty = AvaloniaProperty.Register<FieldGrid, double>(nameof(ColumnSpacing), 20);

	public static readonly StyledProperty<double> RowSpacingProperty = AvaloniaProperty.Register<FieldGrid, double>(nameof(RowSpacing), 14);

	public static readonly StyledProperty<double> MinColumnWidthProperty = AvaloniaProperty.Register<FieldGrid, double>(nameof(MinColumnWidth), 220);

	private int _columns = 2;

	static FieldGrid()
	{
		AffectsMeasure<FieldGrid>(ColumnsProperty, ColumnSpacingProperty, RowSpacingProperty, MinColumnWidthProperty);
	}

	public int Columns
	{
		get => GetValue(ColumnsProperty);
		set => SetValue(ColumnsProperty, value);
	}

	public double ColumnSpacing
	{
		get => GetValue(ColumnSpacingProperty);
		set => SetValue(ColumnSpacingProperty, value);
	}

	public double RowSpacing
	{
		get => GetValue(RowSpacingProperty);
		set => SetValue(RowSpacingProperty, value);
	}

	/// <summary>The narrowest a column may be before the grid drops to fewer columns.</summary>
	public double MinColumnWidth
	{
		get => GetValue(MinColumnWidthProperty);
		set => SetValue(MinColumnWidthProperty, value);
	}

	/// <summary>How many columns fit in <paramref name="width"/>: at most <see cref="Columns"/>, at least one.</summary>
	private int ColumnsFor(double width)
	{
		int fit = (int)Math.Floor((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing));
		return Math.Max(1, Math.Min(Math.Max(1, Columns), fit));
	}

	/// <summary>The field itself: an ItemsControl wraps each template in a ContentPresenter.</summary>
	private static Control Field(Control child) => child is ContentPresenter p && p.Child is Control inner ? inner : child;

	private static bool Shows(Control child) => child.IsVisible && Field(child).IsVisible;

	private static bool IsWide(Control child) => Field(child).Classes.Contains("wide");

	/// <summary>The fields in rows: each row is the children on it and how many columns each takes.</summary>
	private List<List<(Control Child, int Span)>> Rows()
	{
		int columns = _columns;
		List<List<(Control, int)>> rows = new();
		List<(Control, int)> row = new();
		int used = 0;
		foreach (Control child in Children)
		{
			if (!Shows(child))
			{
				continue;
			}
			int span = IsWide(child) ? columns : 1;
			if (used + span > columns && row.Count > 0)
			{
				rows.Add(row);
				row = new();
				used = 0;
			}
			row.Add((child, span));
			used += span;
		}
		if (row.Count > 0)
		{
			rows.Add(row);
		}
		return rows;
	}

	/// <summary>
	/// A whole number of pixels: a text box at a fractional position loses its right border under its own background.
	/// </summary>
	private double ColumnWidth(double width) => Math.Max(0, Math.Floor((width - ColumnSpacing * (_columns - 1)) / _columns));

	private double SpanWidth(double column, int span) => column * span + ColumnSpacing * (span - 1);

	protected override Size MeasureOverride(Size availableSize)
	{
		double width = double.IsInfinity(availableSize.Width) ? 520 : availableSize.Width;
		_columns = ColumnsFor(width);
		double column = ColumnWidth(width);
		foreach (Control child in Children)
		{
			if (!Shows(child))
			{
				child.Measure(new Size(column, double.PositiveInfinity));
			}
		}
		double height = 0;
		List<List<(Control Child, int Span)>> rows = Rows();
		foreach (List<(Control Child, int Span)> row in rows)
		{
			double rowHeight = 0;
			foreach ((Control child, int span) in row)
			{
				child.Measure(new Size(SpanWidth(column, span), double.PositiveInfinity));
				rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
			}
			height += rowHeight;
		}
		height += RowSpacing * Math.Max(0, rows.Count - 1);
		return new Size(width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		_columns = ColumnsFor(finalSize.Width);
		double column = ColumnWidth(finalSize.Width);
		double y = 0;
		foreach (Control child in Children)
		{
			if (!Shows(child))
			{
				child.Arrange(new Rect(0, 0, 0, 0));
			}
		}
		foreach (List<(Control Child, int Span)> row in Rows())
		{
			double x = 0;
			double rowHeight = 0;
			foreach ((Control child, int span) in row)
			{
				// The same constraint as in measure (a no-op then); a new one when the arrange width differs.
				child.Measure(new Size(SpanWidth(column, span), double.PositiveInfinity));
				rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
			}
			foreach ((Control child, int span) in row)
			{
				double w = SpanWidth(column, span);
				child.Arrange(new Rect(x, y, w, rowHeight));
				x += w + ColumnSpacing;
			}
			y += rowHeight + RowSpacing;
		}
		return finalSize;
	}
}
