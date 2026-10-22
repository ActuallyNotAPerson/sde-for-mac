using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Sde.App.Controls;

/// <summary>
/// One icon from Styles/Icons.axaml (Phosphor, regular weight), drawn in the current text colour, so it follows the
/// button or label it sits in, in both themes. <c>&lt;c:Icon Kind="Map" /&gt;</c> shows the "IconMap" resource; a
/// view model can also bind <see cref="Kind"/> to a full key such as "IconMap". <see cref="Data"/> takes any geometry
/// drawn on Phosphor's 256 x 256 grid.
/// </summary>
public sealed class Icon : Control
{
	/// <summary>The icon's name, such as "Map" or "CaretDown" (the resource "IconMap"); a name starting with "Icon" is used as it is.</summary>
	public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<Icon, string>(nameof(Kind));

	/// <summary>The shape to draw, on a 256 x 256 grid. Set from <see cref="Kind"/> when that is used.</summary>
	public static readonly StyledProperty<Geometry> DataProperty = AvaloniaProperty.Register<Icon, Geometry>(nameof(Data));

	/// <summary>Width and height in pixels (16 by default; the IconSize tokens are 14, 16 and 20).</summary>
	public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);

	/// <summary>The colour: inherited from the surrounding text unless set.</summary>
	public static readonly StyledProperty<IBrush> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

	static Icon()
	{
		AffectsRender<Icon>(DataProperty, ForegroundProperty);
		AffectsMeasure<Icon>(SizeProperty);
		FocusableProperty.OverrideDefaultValue<Icon>(false);
	}

	public string Kind
	{
		get => GetValue(KindProperty);
		set => SetValue(KindProperty, value);
	}

	public Geometry Data
	{
		get => GetValue(DataProperty);
		set => SetValue(DataProperty, value);
	}

	public double Size
	{
		get => GetValue(SizeProperty);
		set => SetValue(SizeProperty, value);
	}

	public IBrush Foreground
	{
		get => GetValue(ForegroundProperty);
		set => SetValue(ForegroundProperty, value);
	}

	/// <summary>The resource key for a kind: "Map" gives "IconMap"; "IconMap" stays as it is.</summary>
	public static string ResourceKey(string kind) =>
		string.IsNullOrEmpty(kind) ? null : kind.StartsWith("Icon", StringComparison.Ordinal) ? kind : "Icon" + kind;

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == KindProperty)
		{
			Resolve();
		}
	}

	protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
	{
		base.OnAttachedToLogicalTree(e);
		Resolve();
	}

	/// <summary>Looks the kind up in the app's resources (icons don't change with the theme, so once is enough).</summary>
	private void Resolve()
	{
		string key = ResourceKey(Kind);
		if (key == null)
		{
			return;
		}
		if (this.TryFindResource(key, out object value) && value is Geometry geometry)
		{
			SetCurrentValue(DataProperty, geometry);
		}
		else if (Application.Current?.TryFindResource(key, out object fromApp) == true && fromApp is Geometry appGeometry)
		{
			SetCurrentValue(DataProperty, appGeometry);
		}
	}

	protected override Size MeasureOverride(Size availableSize) => new Size(Size, Size);

	public override void Render(DrawingContext context)
	{
		Geometry data = Data;
		IBrush brush = Foreground;
		if (data == null || brush == null)
		{
			return;
		}
		double size = Size;
		double scale = size / 256.0;
		double left = Math.Round((Bounds.Width - size) / 2);
		double top = Math.Round((Bounds.Height - size) / 2);
		using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(left, top)))
		{
			context.DrawGeometry(brush, null, data);
		}
	}
}
