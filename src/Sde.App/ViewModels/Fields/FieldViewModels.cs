using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Help;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.App.ViewModels.Fields;

/// <summary>A choice in a drop-down: a lookup ID (null = nothing selected) and its label.</summary>
public sealed record ChoiceOption(int? Id, string Name)
{
	public static readonly ChoiceOption None = new(null, "(none)");

	public override string ToString() => Name;
}

/// <summary>
/// One editable field on a record (PropertyRecord or AssessmentRecord). Reads and writes the record property
/// by name, raises <see cref="Changed"/> when the user edits it, and shows the validation issue for that field.
/// Field names are the record (= FEMA database) column names, which is also what FEMA's ValidationRule table uses.
/// </summary>
public abstract partial class FieldViewModel : ObservableObject
{
	private readonly Func<object> _target;
	private readonly PropertyInfo _member;
	private bool _loading;

	protected FieldViewModel(string label, Func<object> target, Type recordType, string member)
	{
		Label = label;
		_target = target;
		Member = member;
		RecordType = recordType;
		_member = recordType.GetProperty(member) ?? throw new ArgumentException($"{recordType.Name} has no {member}");
	}

	public string Label { get; }

	/// <summary>PropertyRecord or AssessmentRecord (FEMA's ValidationRule "ModelName" Property / Assessment).</summary>
	public Type RecordType { get; }

	/// <summary>Record property / FEMA column name.</summary>
	public string Member { get; }

	public string Help { get; init; }

	/// <summary>This field's key in <see cref="FieldCatalog"/> and the field help, e.g. "Property.StreetName".</summary>
	public string HelpKey => (RecordType == typeof(PropertyRecord) ? "Property." : "Assessment.") + Member;

	/// <summary>Set by the form that can show help for a field; without it the "?" button stays hidden.</summary>
	public Action<FieldViewModel> HelpHandler { get; set; }

	/// <summary>The "?" beside the label is shown: there is help for this field and something to show it.</summary>
	public bool HasHelp => HelpHandler != null && FieldHelp.Has(HelpKey);

	/// <summary>The "?" button: opens what to enter here and the manual page.</summary>
	[RelayCommand]
	private void ShowHelp() => HelpHandler?.Invoke(this);

	public string Placeholder { get; init; }

	/// <summary>Spans the full width of the form.</summary>
	public bool IsWide { get; init; }

	/// <summary>Shown only when this returns true (re-evaluated after every edit).</summary>
	public Func<bool> VisibleWhen { get; init; }

	/// <summary>Editable only when this returns true.</summary>
	public Func<bool> EnabledWhen { get; init; }

	[ObservableProperty]
	public partial bool IsVisible { get; set; } = true;

	[ObservableProperty]
	public partial bool IsEnabled { get; set; } = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasIssue), nameof(IssueMessage), nameof(IsRequiredIssue), nameof(IsRecommendedIssue), nameof(IsSuggestedIssue), nameof(ShowIssueInline))]
	public partial ValidationIssue Issue { get; set; }

	public bool HasIssue => Issue != null;
	public string IssueMessage => Issue?.Message?.Trim();
	public bool IsRequiredIssue => Issue?.Severity == IssueSeverity.Required;
	public bool IsRecommendedIssue => Issue?.Severity == IssueSeverity.Recommended;
	public bool IsSuggestedIssue => Issue?.Severity == IssueSeverity.Suggested;

	/// <summary>Required and recommended issues are shown under the field; FEMA's green "suggestions" are not (too noisy).</summary>
	public bool ShowIssueInline => Issue != null && Issue.Severity != IssueSeverity.Suggested;

	/// <summary>Required by FEMA's validation rules (red pin).</summary>
	public bool IsRequired { get; init; }

	public event Action<FieldViewModel> Changed;

	public void Refresh()
	{
		IsVisible = VisibleWhen?.Invoke() ?? true;
		IsEnabled = EnabledWhen?.Invoke() ?? true;
	}

	/// <summary>Re-reads the value from the record (after it was changed by code).</summary>
	public void Load()
	{
		_loading = true;
		try
		{
			LoadValue(_member.GetValue(_target()));
		}
		finally
		{
			_loading = false;
		}
	}

	protected abstract void LoadValue(object value);

	protected void Store(object value)
	{
		if (_loading)
		{
			return;
		}
		object converted = FieldCatalog.Convert(value, _member.PropertyType);
		_member.SetValue(_target(), converted);
		Changed?.Invoke(this);
	}
}

public sealed partial class TextFieldViewModel(string label, Func<object> target, Type recordType, string member)
	: FieldViewModel(label, target, recordType, member)
{
	public bool IsMultiline { get; init; }

	/// <summary>Saved notes that can be added to this field (multi-line fields in the editor).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSnippets), nameof(SnippetItems))]
	public partial IReadOnlyList<Sde.Core.Records.NoteRecord> Snippets { get; set; }

	public bool HasSnippets => Snippets is { Count: > 0 };

	/// <summary>The Insert snippet menu.</summary>
	public IReadOnlyList<Avalonia.Controls.MenuItem> SnippetItems => (Snippets ?? Array.Empty<Sde.Core.Records.NoteRecord>())
		.Select(n => new Avalonia.Controls.MenuItem { Header = n.NoteName, Command = InsertSnippetCommand, CommandParameter = n }).ToList();

	/// <summary>Adds a snippet's text after what's already there.</summary>
	[CommunityToolkit.Mvvm.Input.RelayCommand]
	private void InsertSnippet(Sde.Core.Records.NoteRecord note)
	{
		if (note == null)
		{
			return;
		}
		Text = string.IsNullOrWhiteSpace(Text) ? note.Notes : Text.TrimEnd() + Environment.NewLine + note.Notes;
	}

	public int MaxLength { get; init; } = 255;

	[ObservableProperty]
	public partial string Text { get; set; }

	partial void OnTextChanged(string value) => Store(string.IsNullOrEmpty(value) ? null : value);

	protected override void LoadValue(object value) => Text = value as string;
}

public sealed partial class NumberFieldViewModel(string label, Func<object> target, Type recordType, string member)
	: FieldViewModel(label, target, recordType, member)
{
	/// <summary>Display format for the box, e.g. "N2", "N0", "N6".</summary>
	public string Format { get; init; } = "0.##";

	public string Prefix { get; init; }

	public string Suffix { get; init; }

	public decimal Minimum { get; init; } = decimal.MinValue;

	public decimal Maximum { get; init; } = decimal.MaxValue;

	[ObservableProperty]
	public partial decimal? Value { get; set; }

	partial void OnValueChanged(decimal? value) => Store(value);

	protected override void LoadValue(object value) => Value = value switch
	{
		null => null,
		string s => decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal d) ? d : null,
		_ => Convert.ToDecimal(value),
	};
}

public sealed partial class DateFieldViewModel(string label, Func<object> target, Type recordType, string member)
	: FieldViewModel(label, target, recordType, member)
{
	[ObservableProperty]
	public partial DateTime? Date { get; set; }

	partial void OnDateChanged(DateTime? value) => Store(value?.Date);

	protected override void LoadValue(object value) => Date = value switch
	{
		DateTime d => d,
		string s when DateTime.TryParse(s, System.Globalization.CultureInfo.GetCultureInfo("en-US"), System.Globalization.DateTimeStyles.None, out DateTime d) => d,
		_ => null,
	};
}

public sealed partial class BoolFieldViewModel(string label, Func<object> target, Type recordType, string member)
	: FieldViewModel(label, target, recordType, member)
{
	[ObservableProperty]
	public partial bool Value { get; set; }

	partial void OnValueChanged(bool value) => Store(value);

	protected override void LoadValue(object value) => Value = value is true;
}

public sealed partial class ChoiceFieldViewModel : FieldViewModel
{
	private readonly Func<IReadOnlyList<ChoiceOption>> _options;
	private object _lastValue;

	public ChoiceFieldViewModel(string label, Func<object> target, Type recordType, string member, Func<IReadOnlyList<ChoiceOption>> options)
		: base(label, target, recordType, member)
	{
		_options = options;
		Options = WithNone(options());
	}

	[ObservableProperty]
	public partial IReadOnlyList<ChoiceOption> Options { get; set; }

	[ObservableProperty]
	public partial ChoiceOption Selected { get; set; }

	partial void OnSelectedChanged(ChoiceOption value)
	{
		// ComboBox sets null while its items are replaced; ignore that.
		if (value != null)
		{
			Store(value.Id);
		}
	}

	/// <summary>Rebuilds the option list (e.g. counties after the state changed) and keeps the selection when still valid.</summary>
	public void RefreshOptions()
	{
		IReadOnlyList<ChoiceOption> next = WithNone(_options());
		if (!next.SequenceEqual(Options))
		{
			Options = next;
			LoadValue(_lastValue);
		}
	}

	protected override void LoadValue(object value)
	{
		_lastValue = value;
		int? id = value switch
		{
			null => null,
			string s => int.TryParse(s, out int i) ? i : null,
			_ => Convert.ToInt32(value),
		};
		Selected = Options.FirstOrDefault(o => o.Id == id) ?? Options.FirstOrDefault(o => o.Id == null);
	}

	private static IReadOnlyList<ChoiceOption> WithNone(IReadOnlyList<ChoiceOption> options) =>
		options.Any(o => o.Id == null) ? options : new[] { ChoiceOption.None }.Concat(options).ToList();
}
