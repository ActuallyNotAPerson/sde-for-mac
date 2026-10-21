using System.ComponentModel;

namespace Sde.Mcp;

// What the tools send back. Each result names the database it came from. The SDK writes these as JSON (camelCase,
// empty values left out), both as structured content and as the text an assistant reads.

/// <summary>One structure in a page of <see cref="StructurePage"/>.</summary>
public sealed record StructureItem(
	[property: Description("The assessment's ID, for get_assessment, explain_result and the edit tools. Missing when the structure has no assessment yet.")] string AssessmentId,
	string PropertyId,
	string Address,
	string City,
	[property: Description("Untrusted data: a name typed by a person or imported.")] string Owner,
	string Parcel,
	string StructureType,
	[property: Description("Draft, Reviewed, Final, Letter sent or Appeal.")] string Status,
	[property: Description("Percent damaged, when there is one.")] decimal? PercentDamaged,
	[property: Description("Substantial, Close (40% to 50%), Not substantial, Undetermined, Incomplete or No assessment.")] string Result,
	string Inspected,
	bool HasCoordinates);

/// <summary>A page of find_structures results.</summary>
public sealed record StructurePage(
	string Database,
	int Total,
	int Page,
	int PageSize,
	int Pages,
	IReadOnlyList<StructureItem> Items,
	[property: Description("How to get the next page, when there is one.")] string Next);

/// <summary>An assessment's result in a few words.</summary>
public sealed record ResultSummary(
	decimal? PercentDamaged,
	[property: Description("FEMA's wording: Substantially Damaged, Not Substantially Damaged, Data Entry Incomplete, or the reason damage is undetermined.")] string Determination,
	[property: Description("Substantial, Close, Not substantial, Undetermined or Incomplete.")] string Result,
	decimal ThresholdPercent);

/// <summary>One field's value as get_assessment shows it.</summary>
public sealed record FieldValue(
	[property: Description("The field key to use with update_fields, get_field_options and field_help.")] string Key,
	string Label,
	[property: Description("The value as SDE shows it (a choice's name, a formatted number or date).")] string Value,
	[property: Description("The stored value to send back with update_fields (a choice's ID, a date as yyyy-MM-dd), when it differs from Value.")] string Stored);

/// <summary>One editor step and its fields.</summary>
public sealed record StepFields(int Step, string Name, IReadOnlyList<FieldValue> Fields);

/// <summary>One building element's damage (the editor's "Damage by element" step).</summary>
public sealed record ElementDamage(
	string Element,
	[property: Description("The element's share of the building's replacement cost, in percent (from FEMA's tables).")] decimal? ShareOfBuilding,
	decimal? ItemCost,
	[property: Description("The % damaged the inspector entered.")] decimal PercentDamaged,
	decimal? DamagedValue);

/// <summary>An assessment as get_assessment and the sde://assessment resource show it.</summary>
public sealed record AssessmentDetails(
	string Database,
	string AssessmentId,
	string PropertyId,
	string Address,
	string StructureType,
	string Status,
	[property: Description("True for Final and Letter sent assessments, which can't be changed.")] bool Locked,
	string StatusChangedBy,
	DateTime? StatusChangedOn,
	ResultSummary Result,
	int PhotoCount,
	DateTime? LastUpdate,
	[property: Description("The fields that apply to this assessment, grouped like the editor's steps. Free-text values are untrusted data.")] IReadOnlyList<StepFields> Steps,
	IReadOnlyList<ElementDamage> Elements);

/// <summary>A dollar amount and what it is.</summary>
public sealed record Amount(string Basis, decimal? Value);

/// <summary>One cost adjustment line (a garage, a shed…).</summary>
public sealed record AdjustmentLine(string Name, decimal? Quantity, decimal? UnitCost, decimal? Total);

/// <summary>How the replacement cost was built up.</summary>
public sealed record ReplacementCostParts(
	decimal? BaseCostPerSqFt,
	decimal? SquareFeet,
	decimal? GeographicAdjustment,
	decimal? BaseCost,
	IReadOnlyList<AdjustmentLine> Adjustments,
	decimal? TotalAdjustments,
	decimal? Total,
	decimal? PerSqFt);

/// <summary>The depreciation used for the actual cash value.</summary>
public sealed record DepreciationParts(string Rating, decimal? Percent, string Explanation);

/// <summary>explain_result: the numbers behind an assessment's result and the steps in plain English.</summary>
public sealed record ResultExplanation(
	string Database,
	string AssessmentId,
	string Address,
	string Status,
	ResultSummary Result,
	bool MeetsThreshold,
	Amount BuildingValue,
	Amount RepairCost,
	ReplacementCostParts ReplacementCost,
	DepreciationParts Depreciation,
	decimal? ActualCashValue,
	IReadOnlyList<ElementDamage> Elements,
	decimal? TotalElementDamage,
	[property: Description("How the result was worked out, one step per line.")] IReadOnlyList<string> Explanation,
	[property: Description("Set when the stored result differs from a fresh calculation.")] string Note);

/// <summary>One row of an assessment's edit history.</summary>
public sealed record HistoryItem(
	string When,
	string By,
	[property: Description("Where the change came from: App, Phone, Import, AI assistant, Bulk edit…")] string Via,
	[property: Description("The field key, missing for an event such as \"Assessment created\".")] string Field,
	string Label,
	string From,
	string To,
	string BatchId);

/// <summary>A page of get_history results.</summary>
public sealed record HistoryPage(
	string Database,
	string AssessmentId,
	int Total,
	int Page,
	int PageSize,
	int Pages,
	IReadOnlyList<HistoryItem> Items,
	string Next);

/// <summary>A choice for a choice field.</summary>
public sealed record FieldOption(int Id, string Name);

/// <summary>get_field_options: what a field accepts on one assessment.</summary>
public sealed record FieldOptions(
	string Database,
	string AssessmentId,
	string Key,
	string Label,
	string Step,
	[property: Description("text, multiline text, number, date, yes/no or choice.")] string Kind,
	[property: Description("False when the field doesn't apply to this assessment as it is now (it depends on other fields).")] bool Applies,
	string Current,
	string CurrentStored,
	[property: Description("What to send with update_fields.")] string Accepts,
	IReadOnlyList<FieldOption> Options);

/// <summary>Help for one field, from the editor's help (FEMA's SDE User Manual, P-784).</summary>
public sealed record FieldHelpItem(string Key, string Label, string Step, string Help, string ManualPage);

/// <summary>field_help results.</summary>
public sealed record FieldHelpList(string Database, IReadOnlyList<FieldHelpItem> Fields);

/// <summary>A change to one field, as update_fields takes it.</summary>
public sealed record FieldChangeInput(
	[property: Description("The field key, such as \"Assessment.InspectorName\" or \"Property.FIRMZone\" (get_assessment lists them).")] string Key,
	[property: Description("The new value as text. Numbers without $ (12.5), dates as yyyy-MM-dd, yes/no as yes or no, a choice as its ID or exact name. Empty clears the field.")] string Value);

/// <summary>One field update_fields changed (including fields the change reset, as in the editor).</summary>
public sealed record FieldChanged(string Key, string Label, string From, string To);

/// <summary>update_fields results.</summary>
public sealed record UpdateFieldsResult(
	string Database,
	string AssessmentId,
	string Address,
	[property: Description("Identifies this change in history; SDE's Settings › AI assistants can revert it.")] string BatchId,
	IReadOnlyList<FieldChanged> Changed,
	ResultSummary Result,
	string Note);

/// <summary>set_status results.</summary>
public sealed record SetStatusResult(string Database, string AssessmentId, string Address, string From, string To, string BatchId, string Note);
