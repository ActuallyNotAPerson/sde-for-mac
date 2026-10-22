using System.Globalization;

namespace Sde.Core.Assessments;

/// <summary>A value typed somewhere other than the editor (the phone form, an AI assistant, a map layer) for one field.</summary>
public sealed record FieldInputChange(string Key, string Value);

/// <summary>A value that can't be used, with a plain-English message saying why.</summary>
public sealed class FieldInputException(string message) : Exception(message);

/// <summary>
/// Checks and applies values that arrive as text from outside the editor: the phone form, the AI assistant server and
/// copying values from a map layer onto structures all go through here, so they accept the same values and change
/// the same dependent fields as the editor does.
/// </summary>
public static class FieldInput
{
	/// <summary>The longest text a field accepts this way.</summary>
	public const int MaxTextLength = 2000;

	/// <summary>
	/// Applies changes in order: each key must be a known field that applies to the assessment at that point (after the
	/// changes before it), and each value must suit the field. Every change then goes through
	/// <see cref="FieldRules.Apply"/>, so dependent fields reset as they do in the editor. Throws
	/// <see cref="FieldInputException"/> for the first problem; <paramref name="unknownFieldMessage"/> replaces the
	/// message for a key that isn't a field.
	/// </summary>
	public static void Apply(AssessmentFile file, IEnumerable<FieldInputChange> changes, ReferenceData reference, AssessmentCalculator calculator,
		string unknownFieldMessage = null)
	{
		foreach (FieldInputChange change in changes ?? Array.Empty<FieldInputChange>())
		{
			FieldInfo f = FieldCatalog.Get(change?.Key)
				?? throw new FieldInputException(unknownFieldMessage ?? $"There's no field called \"{change?.Key}\".");
			if (!f.AppliesTo(file))
			{
				throw new FieldInputException($"{f.Label} doesn't apply to this assessment.");
			}
			object value = Convert(f, change.Value, file, reference);
			FieldRules.Apply(file, new[] { new FieldChange(f.Key, value) }, calculator);
		}
	}

	/// <summary>Checks and converts one value to the field's column type (null or empty clears it).</summary>
	public static object Convert(FieldInfo f, string text, AssessmentFile file, ReferenceData reference)
	{
		text = text?.Trim();
		Type type = f.RecordProperty.PropertyType;
		if (string.IsNullOrEmpty(text))
		{
			if (f.Kind == FieldKind.StructureType)
			{
				throw new FieldInputException("Choose residential or non-residential.");
			}
			return FieldCatalog.Convert(null, type);
		}
		switch (f.Kind)
		{
			case FieldKind.Text:
			case FieldKind.MultilineText:
				if (text.Length > MaxTextLength)
				{
					throw new FieldInputException($"{f.Label} is too long (at most {MaxTextLength} characters).");
				}
				return FieldCatalog.Convert(text, type);
			case FieldKind.Number:
				if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal n))
				{
					throw new FieldInputException($"{f.Label}: type a number.");
				}
				try
				{
					return FieldCatalog.Convert(n.ToString(CultureInfo.InvariantCulture), type);
				}
				catch (Exception ex) when (ex is FormatException or OverflowException)
				{
					throw new FieldInputException($"{f.Label}: type a whole number in range.");
				}
			case FieldKind.Date:
				if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) || d.Year < 1800 || d.Year > 2200)
				{
					throw new FieldInputException($"{f.Label}: choose a date.");
				}
				return FieldCatalog.Convert(d.Date, type);
			case FieldKind.YesNo:
				bool yes = text.ToLowerInvariant() switch
				{
					"true" or "1" or "yes" => true,
					"false" or "0" or "no" => false,
					_ => throw new FieldInputException($"{f.Label}: choose yes or no."),
				};
				return FieldCatalog.Convert(yes, type);
			default:
				// A choice: it must be one of the choices offered for this assessment right now.
				if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
					|| !FieldCatalog.Options(f, file, reference).Any(o => o.Id == id))
				{
					throw new FieldInputException($"{f.Label}: choose one of the choices in the list.");
				}
				return FieldCatalog.Convert(id, type);
		}
	}
}
