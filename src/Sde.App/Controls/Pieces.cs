using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Sde.App.Controls;

/// <summary>
/// The colour family of a badge, banner, toast or KPI tile. Each one uses a pair of tokens (for example DangerFg on
/// DangerBg), so it reads in both themes.
/// </summary>
public enum Tone
{
	Neutral,
	Accent,
	Success,
	Warning,
	Danger,
	Info,
	/// <summary>The fifth status hue (results that are undetermined).</summary>
	Violet,
}

/// <summary>Sets one pseudo-class per <see cref="Tone"/> (":neutral", ":danger"…) so control themes can colour by tone.</summary>
internal static class TonePseudoClasses
{
	public static void Apply(IPseudoClasses classes, Tone tone)
	{
		foreach (Tone t in Enum.GetValues<Tone>())
		{
			classes.Set(":" + t.ToString().ToLowerInvariant(), t == tone);
		}
	}
}

/// <summary>A small rounded label: a count in the sidebar, a status, "Suggested". <c>&lt;c:Badge Text="3" Tone="Danger" /&gt;</c></summary>
public class Badge : TemplatedControl
{
	public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<Badge, string>(nameof(Text));

	public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<Badge, Tone>(nameof(Tone));

	public Badge() => TonePseudoClasses.Apply(PseudoClasses, Tone.Neutral);

	public string Text
	{
		get => GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}

	public Tone Tone
	{
		get => GetValue(ToneProperty);
		set => SetValue(ToneProperty, value);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ToneProperty)
		{
			TonePseudoClasses.Apply(PseudoClasses, Tone);
		}
	}
}

/// <summary>
/// A number with a label and a caption, such as "2 / Substantially damaged / 50% or more". It is a button, so a tile
/// can filter a list; <see cref="IsActive"/> outlines it in its tone while that filter is on.
/// </summary>
public class KpiTile : Button
{
	public static readonly StyledProperty<string> ValueProperty = AvaloniaProperty.Register<KpiTile, string>(nameof(Value));

	public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<KpiTile, string>(nameof(Label));

	public static readonly StyledProperty<string> CaptionProperty = AvaloniaProperty.Register<KpiTile, string>(nameof(Caption));

	public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<KpiTile, Tone>(nameof(Tone));

	public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<KpiTile, bool>(nameof(IsActive));

	public KpiTile() => TonePseudoClasses.Apply(PseudoClasses, Tone.Neutral);

	public string Value
	{
		get => GetValue(ValueProperty);
		set => SetValue(ValueProperty, value);
	}

	public string Label
	{
		get => GetValue(LabelProperty);
		set => SetValue(LabelProperty, value);
	}

	public string Caption
	{
		get => GetValue(CaptionProperty);
		set => SetValue(CaptionProperty, value);
	}

	public Tone Tone
	{
		get => GetValue(ToneProperty);
		set => SetValue(ToneProperty, value);
	}

	public bool IsActive
	{
		get => GetValue(IsActiveProperty);
		set => SetValue(IsActiveProperty, value);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ToneProperty)
		{
			TonePseudoClasses.Apply(PseudoClasses, Tone);
		}
		else if (change.Property == IsActiveProperty)
		{
			PseudoClasses.Set(":active", IsActive);
		}
	}
}

/// <summary>
/// A message across the top of a page or card: the sample database, a locked assessment, "Changed elsewhere". The
/// content (usually buttons) sits on the right, or under the text with <c>Classes="stacked"</c>.
/// <c>&lt;c:Banner Tone="Warning" Text="…"&gt;&lt;Button…/&gt;&lt;/c:Banner&gt;</c>
/// </summary>
public class Banner : ContentControl
{
	public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<Banner, string>(nameof(Title));

	public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<Banner, string>(nameof(Text));

	public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<Banner, Tone>(nameof(Tone), Tone.Info);

	/// <summary>The icon's kind; by default one that suits the tone (Info, Warning, ErrorCircle, CheckCircle).</summary>
	public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<Banner, string>(nameof(Icon));

	public Banner()
	{
		TonePseudoClasses.Apply(PseudoClasses, Tone.Info);
		SetCurrentValue(IconProperty, DefaultIcon(Tone.Info));
	}

	public string Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string Text
	{
		get => GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}

	public Tone Tone
	{
		get => GetValue(ToneProperty);
		set => SetValue(ToneProperty, value);
	}

	public string Icon
	{
		get => GetValue(IconProperty);
		set => SetValue(IconProperty, value);
	}

	/// <summary>The icon a tone uses when none is given.</summary>
	public static string DefaultIcon(Tone tone) => tone switch
	{
		Tone.Warning => "Warning",
		Tone.Danger => "ErrorCircle",
		Tone.Success => "CheckCircle",
		_ => "Info",
	};

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ToneProperty)
		{
			TonePseudoClasses.Apply(PseudoClasses, Tone);
			if (!IsSet(IconProperty) || Icon == DefaultIcon((Tone)change.OldValue))
			{
				SetCurrentValue(IconProperty, DefaultIcon(Tone));
			}
		}
	}
}

/// <summary>
/// A short message that appears at the bottom of the window and goes away by itself ("Backup saved"), with an
/// optional action such as Undo. Shown by the window's toast list (MainWindow.Toasts).
/// </summary>
public class Toast : TemplatedControl
{
	public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<Toast, string>(nameof(Title));

	public static readonly StyledProperty<string> MessageProperty = AvaloniaProperty.Register<Toast, string>(nameof(Message));

	public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<Toast, Tone>(nameof(Tone), Tone.Success);

	/// <summary>The action button's text, such as "Undo"; no button when empty.</summary>
	public static readonly StyledProperty<string> ActionTextProperty = AvaloniaProperty.Register<Toast, string>(nameof(ActionText));

	public static readonly StyledProperty<ICommand> ActionCommandProperty = AvaloniaProperty.Register<Toast, ICommand>(nameof(ActionCommand));

	public static readonly StyledProperty<ICommand> CloseCommandProperty = AvaloniaProperty.Register<Toast, ICommand>(nameof(CloseCommand));

	public Toast() => TonePseudoClasses.Apply(PseudoClasses, Tone.Success);

	public string Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string Message
	{
		get => GetValue(MessageProperty);
		set => SetValue(MessageProperty, value);
	}

	public Tone Tone
	{
		get => GetValue(ToneProperty);
		set => SetValue(ToneProperty, value);
	}

	public string ActionText
	{
		get => GetValue(ActionTextProperty);
		set => SetValue(ActionTextProperty, value);
	}

	public ICommand ActionCommand
	{
		get => GetValue(ActionCommandProperty);
		set => SetValue(ActionCommandProperty, value);
	}

	public ICommand CloseCommand
	{
		get => GetValue(CloseCommandProperty);
		set => SetValue(CloseCommandProperty, value);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ToneProperty)
		{
			TonePseudoClasses.Apply(PseudoClasses, Tone);
		}
	}
}

/// <summary>
/// What a page or list shows when there is nothing in it: an icon, a title, a sentence saying what will appear here,
/// and optional buttons (the content).
/// </summary>
public class EmptyState : ContentControl
{
	public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Icon));

	public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Title));

	public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Text));

	public string Icon
	{
		get => GetValue(IconProperty);
		set => SetValue(IconProperty, value);
	}

	public string Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string Text
	{
		get => GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}
}

/// <summary>A page's title and one-line description, with the page's main buttons (the content) on the right.</summary>
public class PageHeader : ContentControl
{
	public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<PageHeader, string>(nameof(Title));

	public static readonly StyledProperty<string> SubtitleProperty = AvaloniaProperty.Register<PageHeader, string>(nameof(Subtitle));

	public string Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string Subtitle
	{
		get => GetValue(SubtitleProperty);
		set => SetValue(SubtitleProperty, value);
	}
}

/// <summary>
/// The top of a card: an icon in a tinted tile, the card's title and a sentence about it, with the card's main
/// buttons (the content) on the right. Used on the long pages (Import &amp; export, Settings) so each card reads at a glance.
/// </summary>
public class CardHeader : ContentControl
{
	public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<CardHeader, string>(nameof(Icon));

	public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<CardHeader, string>(nameof(Title));

	public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<CardHeader, string>(nameof(Text));

	public string Icon
	{
		get => GetValue(IconProperty);
		set => SetValue(IconProperty, value);
	}

	public string Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string Text
	{
		get => GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}
}
