using System.Globalization;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Field;

/// <summary>A choice in a list: its ID (as text) and name.</summary>
public sealed record OptionDto(string Id, string Name);

/// <summary>One field as the phone shows it. <see cref="Value"/> is <see cref="FieldCatalog.Canonical"/> text.</summary>
public sealed record FieldDto(string Key, string Label, int Step, string Kind, string Unit, string Value, IReadOnlyList<OptionDto> Options);

/// <summary>An editor step and its fields.</summary>
public sealed record StepDto(int Step, string Name, IReadOnlyList<FieldDto> Fields);

/// <summary>Every field the phone form knows, with the choices that don't depend on a particular assessment.</summary>
public sealed record LookupsDto(IReadOnlyList<string> Steps, IReadOnlyList<FieldDto> Fields);

/// <summary>A search result.</summary>
public sealed record StructureDto(Guid PropertyId, Guid? AssessmentId, string Address, string City, string Owner, string Parcel,
	string RecordId, string StructureType, string Result);

/// <summary>An assessment as the phone edits it: only the fields that apply to it now.</summary>
public sealed record AssessmentDto(Guid AssessmentId, Guid PropertyId, string Address, string StructureType, string Status, bool Locked,
	string Result, int PhotoCount, IReadOnlyList<StepDto> Steps);

/// <summary>A change sent by the phone: a field key and its new value as text (empty or null clears it).</summary>
public sealed record ChangeDto(string Key, string Value);

public sealed record ChangesRequest(IReadOnlyList<ChangeDto> Changes);

/// <summary>A new structure: 1 residential or 2 non-residential, plus any first values (address, owner…).</summary>
public sealed record AddStructureRequest(int StructureType, IReadOnlyList<ChangeDto> Changes);

public sealed record PairRequest(string Token, string Pin, string Name);

/// <summary>A value the phone sent that can't be used, with a message for the inspector.</summary>
public sealed class FieldInputException(string message) : Exception(message);

/// <summary>What the phone form reads and how it checks and applies changes. No web code here, so it is easy to test.</summary>
public static class FieldApi
{
	public const int MaxResults = 50;
	public const int MaxTextLength = 2000;

	public static string KindName(FieldKind kind) => kind switch
	{
		FieldKind.MultilineText => "multiline",
		FieldKind.YesNo => "yesno",
		FieldKind.StructureType => "structureType",
		_ => kind.ToString().ToLowerInvariant(),
	};

	public static LookupsDto Lookups(ReferenceData reference)
	{
		// Choices for a blank residential assessment; counties depend on the state, so they come with each assessment.
		AssessmentFile template = AssessmentFile.CreateNew(new PropertyRecord { StructureTypeID = 1 });
		List<FieldDto> fields = FieldCatalog.All.Select(f => new FieldDto(f.Key, f.Label, f.Step, KindName(f.Kind), f.Unit, null,
			f.Kind == FieldKind.County ? Array.Empty<OptionDto>() : OptionsOf(f, template, reference))).ToList();
		return new LookupsDto(FieldCatalog.Steps, fields);
	}

	private static IReadOnlyList<OptionDto> OptionsOf(FieldInfo f, AssessmentFile file, ReferenceData reference) =>
		FieldCatalog.Options(f, file, reference).Select(o => new OptionDto(o.Id.ToString(CultureInfo.InvariantCulture), o.Name)).ToList();

	/// <summary>
	/// Structures whose address, city, ZIP, owner, parcel number or record ID contain every word of
	/// <paramref name="query"/> (any case). An empty query lists the most recently changed.
	/// </summary>
	public static IReadOnlyList<StructureDto> Search(SdeStore store, string query)
	{
		string[] words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Dictionary<Guid, PropertyRecord> properties = store.GetProperties().ToDictionary(p => p.PropertyID);
		List<StructureDto> results = new();
		foreach (StructureSummary s in store.ListStructures())
		{
			properties.TryGetValue(s.PropertyID, out PropertyRecord p);
			string haystack = string.Join(" ", s.Address, s.City, p?.ZipCode, s.Owner, p?.ParcelNumber, s.ExternalId);
			if (words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase)))
			{
				results.Add(new StructureDto(s.PropertyID, s.AssessmentID, Blank(s.Address, "(no address)"), s.City, s.Owner, p?.ParcelNumber,
					s.ExternalId, TypeName(s.StructureTypeID), ResultText(s.PercentDamage, s.DamageMessage)));
				if (results.Count >= MaxResults)
				{
					break;
				}
			}
		}
		return results;
	}

	public static AssessmentDto Describe(SdeStore store, AssessmentFile file)
	{
		ReferenceData r = store.Reference;
		AssessmentStatusRecord status = store.GetStatus(file.Assessment.AssessmentID);
		List<StepDto> steps = new();
		foreach (IGrouping<int, FieldInfo> step in FieldCatalog.All.Where(f => f.AppliesTo(file)).GroupBy(f => f.Step).OrderBy(g => g.Key))
		{
			List<FieldDto> fields = step.Select(f => new FieldDto(f.Key, f.Label, f.Step, KindName(f.Kind), f.Unit,
				FieldCatalog.Canonical(FieldCatalog.GetValue(file, f)), OptionsOf(f, file, r))).ToList();
			steps.Add(new StepDto(step.Key, FieldCatalog.Steps[step.Key - 1], fields));
		}
		PropertyRecord p = file.Property;
		string address = string.Join(" ", new[] { p.StreetNumber, p.StreetName, r.LookupName(p.StreetSuffixID) }.Where(x => !string.IsNullOrWhiteSpace(x)));
		return new AssessmentDto(file.Assessment.AssessmentID, p.PropertyID, Blank(address, "(no address)"), TypeName(p.StructureTypeID),
			AssessmentStatuses.Label(status.Value), status.IsLocked, ResultText(file.Assessment.PercentDamage, file.Assessment.DamageMessage),
			file.Attachments.Count, steps);
	}

	/// <summary>
	/// Applies the phone's changes in order: each key must be a known field that applies to the assessment at that
	/// point (after the changes before it), and each value must suit the field. Every change then goes through
	/// <see cref="FieldRules.Apply"/>, so dependent fields reset as they do in the editor. Throws
	/// <see cref="FieldInputException"/> with a plain-English message for the first problem.
	/// </summary>
	public static void Apply(AssessmentFile file, IEnumerable<ChangeDto> changes, ReferenceData reference, AssessmentCalculator calculator)
	{
		foreach (ChangeDto change in changes ?? Array.Empty<ChangeDto>())
		{
			FieldInfo f = FieldCatalog.Get(change?.Key) ?? throw new FieldInputException("The phone sent a field SDE doesn't know. Reload the page and try again.");
			if (!f.AppliesTo(file))
			{
				throw new FieldInputException($"{f.Label} doesn't apply to this assessment.");
			}
			object value = Convert(f, change.Value, file, reference);
			FieldRules.Apply(file, new[] { new FieldChange(f.Key, value) }, calculator);
		}
	}

	/// <summary>Checks and converts one value to the field's column type (null clears it).</summary>
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

	/// <summary>A new structure and assessment (not saved), set up as the Mac's "New structure" does, with the phone's first values.</summary>
	public static AssessmentFile NewStructure(SdeStore store, AddStructureRequest request)
	{
		if (request == null || request.StructureType is not (1 or 2))
		{
			throw new FieldInputException("Choose residential or non-residential.");
		}
		bool useDefaults = store.GetPreferences()?.UseDefaults == true;
		DefaultValuesRecord defaults = useDefaults ? store.GetDefaults() : null;
		PropertyRecord p = new PropertyRecord { StructureTypeID = request.StructureType };
		if (defaults != null)
		{
			DefaultValues.ApplyToProperty(defaults, p);
		}
		AssessmentFile file = AssessmentFile.CreateNew(p, defaults);
		Apply(file, request.Changes, store.Reference, store.Calculator);
		store.Calculator.Recalculate(file);
		return file;
	}

	/// <summary>A new assessment (not saved) for a structure that has none yet.</summary>
	public static AssessmentFile NewAssessment(SdeStore store, Guid propertyId)
	{
		PropertyRecord p = store.GetProperty(propertyId);
		if (p == null || !p.Active)
		{
			return null;
		}
		bool useDefaults = store.GetPreferences()?.UseDefaults == true;
		AssessmentFile file = AssessmentFile.CreateNew(p, useDefaults ? store.GetDefaults() : null);
		store.Calculator.Recalculate(file);
		return file;
	}

	private static string TypeName(int? structureType) => structureType switch
	{
		1 => "Residential",
		2 => "Non-residential",
		_ => "",
	};

	private static string ResultText(decimal? percent, string message) =>
		percent is decimal pd ? pd.ToString("0.0", CultureInfo.InvariantCulture) + "%" + (string.IsNullOrWhiteSpace(message) ? "" : ", " + message) : (message ?? "");

	private static string Blank(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
}
