using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Sde.App;

/// <summary>
/// Colours a control with a brush resource whose name comes from a binding, for example
/// <c>app:ThemeBrush.Background="{Binding State, Converter={x:Static app:Converters.StatusBackgroundKey}}"</c>.
/// The brush is looked up like DynamicResource, so it changes when the Mac switches between light and dark. A
/// converter that returns the brush itself would keep the old theme's colour until the bound value changed.
/// </summary>
public static class ThemeBrush
{
	/// <summary>The name of the brush resource for the text colour (TextBlock.Foreground).</summary>
	public static readonly AttachedProperty<string> ForegroundProperty =
		AvaloniaProperty.RegisterAttached<Control, string>("Foreground", typeof(ThemeBrush));

	/// <summary>The name of the brush resource for the background (Border.Background, Panel.Background).</summary>
	public static readonly AttachedProperty<string> BackgroundProperty =
		AvaloniaProperty.RegisterAttached<Control, string>("Background", typeof(ThemeBrush));

	/// <summary>The name of the brush resource for a shape's fill (Ellipse.Fill).</summary>
	public static readonly AttachedProperty<string> FillProperty =
		AvaloniaProperty.RegisterAttached<Control, string>("Fill", typeof(ThemeBrush));

	static ThemeBrush()
	{
		ForegroundProperty.Changed.AddClassHandler<Control>((control, e) => Follow(control, TextElement.ForegroundProperty, e.NewValue as string));
		BackgroundProperty.Changed.AddClassHandler<Control>((control, e) => Follow(control, Border.BackgroundProperty, e.NewValue as string));
		FillProperty.Changed.AddClassHandler<Control>((control, e) => Follow(control, Shape.FillProperty, e.NewValue as string));
	}

	public static string GetForeground(Control control) => control.GetValue(ForegroundProperty);

	public static void SetForeground(Control control, string value) => control.SetValue(ForegroundProperty, value);

	public static string GetBackground(Control control) => control.GetValue(BackgroundProperty);

	public static void SetBackground(Control control, string value) => control.SetValue(BackgroundProperty, value);

	public static string GetFill(Control control) => control.GetValue(FillProperty);

	public static void SetFill(Control control, string value) => control.SetValue(FillProperty, value);

	/// <summary>
	/// Binds the brush property to the named resource in the control's current theme, replacing the binding for the
	/// previous name. A missing name, or one that isn't a brush resource, gives grey.
	/// </summary>
	private static void Follow(Control control, AvaloniaProperty target, string key) =>
		control.Bind(target, control.GetResourceObservable(key ?? string.Empty, value => value as IBrush ?? Brushes.Gray));
}
