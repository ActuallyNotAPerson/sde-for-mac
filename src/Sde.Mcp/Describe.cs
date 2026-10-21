using System.Globalization;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>Turns stored records into the plain values the tools return (addresses, display values, results).</summary>
internal static class Describe
{
	public static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>The longest free-text value a list or history row shows; longer ones are shortened.</summary>
	public const int ShortTextLength = 300;

	/// <summary>"123 River St", or "(no address)".</summary>
	public static string Address(PropertyRecord p, ReferenceData reference)
	{
		if (p == null)
		{
			return "(no address)";
		}
		string address = string.Join(" ", new[] { p.StreetNumber, p.StreetName, reference.LookupName(p.StreetSuffixID) }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
		return address.Length == 0 ? "(no address)" : address;
	}

	public static string StructureType(int? structureTypeId) => structureTypeId switch
	{
		1 => "Residential",
		2 => "Non-residential",
		_ => null,
	};

	/// <summary>An assessment's result in a few words (the rule the Structures list uses).</summary>
	public static ResultSummary Result(AssessmentRecord a) => new ResultSummary(
		HasPercent(a.DamageMessage, a.PercentDamage) ? a.PercentDamage : null,
		a.DamageMessage ?? Determinations.IncompleteMessage,
		Determinations.Label(Determinations.Classify(a)),
		Determinations.DefaultSubstantialPercent);

	/// <summary>True when the result has a percentage worth showing (not incomplete or undetermined).</summary>
	public static bool HasPercent(string damageMessage, decimal? percent) =>
		Determinations.Classify(damageMessage, percent) is DeterminationClass.Substantial or DeterminationClass.Close or DeterminationClass.NotSubstantial;

	/// <summary>A field's value as SDE shows it, or null when it is empty.</summary>
	public static string Display(FieldInfo f, object value, ReferenceData reference)
	{
		string text = FieldCatalog.Display(f, value, reference);
		return string.IsNullOrEmpty(text) ? null : text;
	}

	/// <summary>A value stored in edit history, as SDE shows it (raw when it can't be read as the field's type).</summary>
	public static string History(string field, string canonical, ReferenceData reference)
	{
		if (string.IsNullOrEmpty(canonical))
		{
			return null;
		}
		FieldInfo f = FieldCatalog.Get(field);
		string text;
		try
		{
			text = f == null ? canonical : FieldCatalog.DisplayCanonical(f, canonical, reference);
		}
		catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
		{
			text = canonical;
		}
		return Shorten(text);
	}

	/// <summary>Cuts long text to <see cref="ShortTextLength"/> characters, saying so.</summary>
	public static string Shorten(string text, int max = ShortTextLength) =>
		text == null || text.Length <= max ? text : text[..max] + "… (shortened)";

	public static string Money(decimal? value) => value is decimal d ? d.ToString("C2", Us) : "not entered";

	public static string Number(decimal? value, string format = "#,##0.##") => value is decimal d ? d.ToString(format, Us) : "not entered";

	public static string Percent(decimal? value) => value is decimal d ? d.ToString("0.0", Us) + "%" : "not entered";

	public static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	public static string Time(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

	/// <summary>Reads a money amount stored as text in FEMA's element table ("1234.56").</summary>
	public static decimal? ParseMoney(string text) =>
		decimal.TryParse(text?.Replace("$", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : null;

	/// <summary>The element rows as the editor's "Damage by element" step shows them.</summary>
	public static List<ElementDamage> Elements(AssessmentFile file) => file.ElementPercentages
		.Select(e => new ElementDamage(e.ElementName, e.ElementPercentage, ParseMoney(e.ItemCost), e.UserDamage, ParseMoney(e.DamageValues)))
		.ToList();

	/// <summary>The kind of value a field takes, in words.</summary>
	public static string Kind(FieldKind kind) => kind switch
	{
		FieldKind.Text => "text",
		FieldKind.MultilineText => "multiline text",
		FieldKind.Number => "number",
		FieldKind.Date => "date",
		FieldKind.YesNo => "yes/no",
		_ => "choice",
	};

	/// <summary>What update_fields accepts for a field.</summary>
	public static string Accepts(FieldInfo f) => f.Kind switch
	{
		FieldKind.Text or FieldKind.MultilineText => $"Text, at most {FieldInput.MaxTextLength} characters. Empty clears it.",
		FieldKind.Number => "A number such as 12.5, without $ or units" + (f.Unit is null or "$" ? "" : $" (in {f.Unit})") + ". Empty clears it.",
		FieldKind.Date => "A date as yyyy-MM-dd. Empty clears it.",
		FieldKind.YesNo => "yes or no.",
		FieldKind.StructureType => "1 (Residential) or 2 (Non-residential), or the name.",
		_ => "One of the options: its ID or its exact name. Empty clears it.",
	};

	public static bool IsChoice(FieldKind kind) => kind is FieldKind.Lookup or FieldKind.State or FieldKind.County or FieldKind.StructureType;
}
