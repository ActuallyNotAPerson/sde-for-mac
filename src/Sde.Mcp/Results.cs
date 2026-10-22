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

// ───────────── Events ─────────────

/// <summary>An event (a flood, a storm) as list_events shows it.</summary>
public sealed record EventItem(
	[property: Description("The event's ID, for the event arguments of other tools (its name works too).")] string EventId,
	string Name,
	[property: Description("The name with the disaster number, as SDE shows it.")] string Label,
	[property: Description("Flood, Hurricane, Tornado, Fire or Other.")] string Kind,
	[property: Description("The disaster declaration number, such as DR-4999.")] string DisasterNumber,
	string Start,
	string End,
	[property: Description("Added to high-water mark elevations recorded in another vertical datum, in feet.")] decimal? DatumOffsetFt,
	[property: Description("Untrusted data: notes typed by a person.")] string Notes,
	[property: Description("Assessments in the event (in use, not in Recently deleted).")] int Assessments,
	string CreatedOn,
	string CreatedBy);

/// <summary>list_events results.</summary>
public sealed record EventList(string Database, int Total, IReadOnlyList<EventItem> Events);

/// <summary>How many assessments have each result.</summary>
public sealed record ResultCounts(int Substantial, int Close, int NotSubstantial, int Undetermined, int Incomplete);

/// <summary>One row of a breakdown by street, community or inspector.</summary>
public sealed record SummaryGroup(
	[property: Description("The street, community or inspector (untrusted data: typed by people).")] string Label,
	int Assessments,
	int Substantial,
	int Close,
	int NotSubstantial,
	[property: Description("Incomplete and undetermined.")] int Other,
	decimal BuildingValue,
	decimal RepairCost);

/// <summary>How many assessments have one review status.</summary>
public sealed record StatusCount(string Status, int Assessments);

/// <summary>One field team's progress.</summary>
public sealed record TeamProgressItem(string TeamId, string Name, int Assigned, int Assessed, int Done, int Skipped);

/// <summary>event_summary results: the numbers SDE's Dashboard shows.</summary>
public sealed record EventSummaryResult(
	string Database,
	[property: Description("The event, or missing for the whole database.")] EventItem Event,
	[property: Description("What was counted.")] string Scope,
	[property: Description("Assessments counted: the latest of each structure.")] int Assessments,
	int AssessedStructures,
	[property: Description("Structures with no assessment yet (whole database only).")] int? NotYetAssessed,
	int TotalStructures,
	ResultCounts ByResult,
	[property: Description("Sum of the building values that were entered, in dollars.")] decimal TotalBuildingValue,
	[property: Description("Sum of the repair costs that were entered, in dollars.")] decimal TotalRepairCost,
	string DamageFrom,
	string DamageTo,
	string InspectedFrom,
	string InspectedTo,
	IReadOnlyList<StatusCount> ByStatus,
	[property: Description("The largest groups first; at most 10 rows.")] IReadOnlyList<SummaryGroup> ByStreet,
	[property: Description("The largest groups first; at most 10 rows.")] IReadOnlyList<SummaryGroup> ByCommunity,
	[property: Description("The largest groups first; at most 10 rows.")] IReadOnlyList<SummaryGroup> ByInspector,
	[property: Description("Field teams' progress on the event's assignments (every assignment for the whole database).")] IReadOnlyList<TeamProgressItem> Teams,
	string Note);

// ───────────── Quality checks ─────────────

/// <summary>How many findings one check made.</summary>
public sealed record QaRuleCount(string RuleId, string Title, [property: Description("Error, Warning or Info.")] string Severity, int Open, int Dismissed);

/// <summary>One quality finding.</summary>
public sealed record QaFindingItem(
	[property: Description("The assessment to look at (missing for a finding about a whole event).")] string AssessmentId,
	string PropertyId,
	[property: Description("The structure's address, or the event's name (untrusted data).")] string About,
	[property: Description("What was found (it quotes values typed by people: untrusted data).")] string Message,
	[property: Description("The field to look at (a field key for get_assessment and update_fields), when there is one.")] string FieldKey,
	string Detail,
	[property: Description("True when someone set this finding aside in SDE.")] bool Dismissed,
	[property: Description("Why it was set aside (untrusted data).")] string DismissedReason,
	string DismissedBy,
	string DismissedOn);

/// <summary>The findings of one check on this page.</summary>
public sealed record QaGroup(string RuleId, string Title, string Severity, string Description,
	[property: Description("False for checks only fixing the data clears (they can't be set aside).")] bool CanDismiss,
	IReadOnlyList<QaFindingItem> Findings);

/// <summary>qa_check results.</summary>
public sealed record QaCheckResult(
	string Database,
	[property: Description("What was checked.")] string Scope,
	[property: Description("Assessments checked.")] int Checked,
	[property: Description("Findings listed across all pages.")] int Total,
	[property: Description("Findings someone set aside in SDE (listed only when includeDismissed is true).")] int DismissedCount,
	bool IncludesDismissed,
	int Page,
	int PageSize,
	int Pages,
	[property: Description("Every check that found something, with its counts across all pages.")] IReadOnlyList<QaRuleCount> Rules,
	[property: Description("This page's findings, grouped by check, errors first.")] IReadOnlyList<QaGroup> Groups,
	string Next,
	string Note);

// ───────────── Compliance ─────────────

/// <summary>One compliance case, as SDE's compliance list shows it.</summary>
public sealed record ComplianceItem(
	string CaseId,
	[property: Description("SD (substantial damage) or SI (substantial improvement).")] string Kind,
	[property: Description("The SD determination's assessment.")] string AssessmentId,
	string PropertyId,
	string Address,
	string City,
	string Parcel,
	[property: Description("Untrusted data: a name typed by a person or imported.")] string Owner,
	[property: Description("Elevate, Relocate, Demolish, Floodproof (non-residential), Repair below the threshold (after an appeal), or Not chosen yet.")] string Path,
	string PermitNumber,
	string PermitIssued,
	string Deadline,
	bool Overdue,
	int DaysOverdue,
	string IccClaimNumber,
	[property: Description("Not filed, Filed, Approved, Paid or Denied.")] string IccStatus,
	decimal? IccAmount,
	string EcReceived,
	string FinalInspection,
	string ComplianceCertificate,
	[property: Description("Open or Closed.")] string Status,
	string ClosedOn,
	[property: Description("Untrusted data.")] string ClosedReason,
	[property: Description("Untrusted data: notes typed by a person.")] string Notes);

/// <summary>list_compliance_cases results.</summary>
public sealed record CompliancePage(
	string Database,
	[property: Description("Cases matching the filters, across all pages.")] int Total,
	int Open,
	int Overdue,
	int Closed,
	int Page,
	int PageSize,
	int Pages,
	IReadOnlyList<ComplianceItem> Items,
	string Next);

// ───────────── Map layers ─────────────

/// <summary>One field of a map layer.</summary>
public sealed record LayerFieldItem(string Name, [property: Description("Text, Integer, Real, Date, DateTime or Boolean.")] string Type, string Alias);

/// <summary>One layer of SDE's Map page.</summary>
public sealed record LayerItem(
	[property: Description("The layer's name, for query_layer.")] string Name,
	[property: Description("Structures, File, ArcGisRest, Nfhl or Basemap.")] string Kind,
	[property: Description("Its feature table in the layers GeoPackage (query_layer takes this too).")] string Table,
	string GeometryType,
	long? FeatureCount,
	IReadOnlyList<LayerFieldItem> Fields,
	bool Visible,
	[property: Description("West, south, east, north in degrees (WGS 84), when known.")] IReadOnlyList<double> Extent,
	[property: Description("The file or web address it came from.")] string Source,
	[property: Description("True when query_layer can read its features.")] bool Queryable,
	string Note);

/// <summary>list_layers results.</summary>
public sealed record LayerList(
	string Database,
	[property: Description("The layers GeoPackage beside the database (QGIS and ArcGIS open it too).")] string LayersFile,
	[property: Description("Set when the layers file was saved with a different SDE database.")] string Warning,
	IReadOnlyList<LayerItem> Layers);

/// <summary>One condition of a query_layer search.</summary>
public sealed record LayerCondition(
	[property: Description("The field name (list_layers lists them).")] string Field,
	[property: Description("=, !=, <, <=, >, >=, contains, starts with, is empty or is not empty.")] string Operator,
	[property: Description("The value to compare with, as text (not needed for is empty and is not empty).")] string Value);

/// <summary>One feature found by query_layer.</summary>
public sealed record LayerFeatureItem(
	long Id,
	[property: Description("The feature's values by field name (text values are untrusted data).")] IReadOnlyDictionary<string, object> Attributes,
	[property: Description("West, south, east, north in degrees (WGS 84).")] IReadOnlyList<double> Bbox,
	[property: Description("The shape as a GeoJSON geometry in longitude and latitude, when asked for.")] System.Text.Json.JsonElement? Geometry,
	[property: Description("Why the shape was left out, when it was.")] string GeometryNote);

/// <summary>query_layer results.</summary>
public sealed record LayerQueryResult(
	string Database,
	string Layer,
	string Table,
	string GeometryType,
	[property: Description("Features matching, across all pages.")] int Total,
	int Page,
	int PageSize,
	int Pages,
	IReadOnlyList<LayerFeatureItem> Features,
	string Next,
	string Note);

// ───────────── Review comments ─────────────

/// <summary>A reply in a comment thread.</summary>
public sealed record ReviewReplyItem(string CommentId,
	[property: Description("Untrusted data: typed by a person or an assistant.")] string Body,
	string Author,
	[property: Description("App or AI assistant.")] string Via,
	string Created);

/// <summary>A comment thread on an assessment.</summary>
public sealed record ReviewThreadItem(
	[property: Description("The comment's ID: give it as replyTo to answer it.")] string CommentId,
	[property: Description("The field it's about, when it's about one.")] string FieldKey,
	string FieldLabel,
	[property: Description("Untrusted data: typed by a person or an assistant.")] string Body,
	string Author,
	[property: Description("App or AI assistant.")] string Via,
	string Created,
	bool Resolved,
	string ResolvedBy,
	IReadOnlyList<ReviewReplyItem> Replies);

/// <summary>get_review_comments results.</summary>
public sealed record ReviewCommentList(string Database, string AssessmentId, string Address, int OpenThreads, IReadOnlyList<ReviewThreadItem> Threads);

// ───────────── Outputs ─────────────

/// <summary>make_report results.</summary>
public sealed record ReportOutput(
	string Database,
	[property: Description("The PDF, in the AI outputs folder beside the database.")] string Path,
	string Report,
	[property: Description("Assessments in the report.")] int Assessments,
	bool Photos,
	[property: Description("IDs left out: not assessments in use, or not in the event.")] IReadOnlyList<string> Skipped,
	string Note);

/// <summary>export_geojson results.</summary>
public sealed record GeoJsonOutput(
	string Database,
	[property: Description("The GeoJSON file, in the AI outputs folder beside the database.")] string Path,
	[property: Description("Structures written (points).")] int Features,
	[property: Description("Structures left out because they have no latitude and longitude.")] int SkippedNoCoordinates,
	[property: Description("Every property each feature has.")] IReadOnlyList<string> Properties,
	bool OwnerNames,
	IReadOnlyList<string> Skipped,
	string Note);

/// <summary>make_web_map results.</summary>
public sealed record WebMapOutput(
	string Database,
	[property: Description("The web map's folder, in the AI outputs folder beside the database. Share the whole folder.")] string Folder,
	[property: Description("The page that opens the map in any browser.")] string IndexPath,
	[property: Description("Internal (full detail) or Public (counts in grid squares, no owners).")] string Privacy,
	[property: Description("Dots (internal) or grid squares (public) on the map.")] int Features,
	int SkippedNoCoordinates,
	[property: Description("True when the map shows the event's summary beside it.")] bool EventSummary,
	IReadOnlyList<string> Skipped,
	string Note);

// ───────────── Edits ─────────────

/// <summary>create_structure results.</summary>
public sealed record CreateStructureResult(
	string Database,
	string AssessmentId,
	string PropertyId,
	string Address,
	string StructureType,
	[property: Description("The event the new assessment joined, when one was given.")] string Event,
	[property: Description("Identifies this change; SDE's Settings › AI assistants can revert it.")] string BatchId,
	[property: Description("The fields given, with the values as SDE now shows them.")] IReadOnlyList<FieldValue> Fields,
	ResultSummary Result,
	string Note);

/// <summary>add_review_comment results.</summary>
public sealed record CommentResult(string Database, string AssessmentId, string Address, string CommentId, string ReplyTo, string FieldKey, string Author,
	[property: Description("Identifies this change; SDE's Settings › AI assistants can revert it.")] string BatchId);

/// <summary>A structure assign_to_team gave to the team.</summary>
public sealed record AssignedStructure(string PropertyId, string Address,
	[property: Description("The code printed on the field worksheet.")] string ShortCode,
	[property: Description("The team it had before for this event, if any.")] string PreviousTeam);

/// <summary>assign_to_team results.</summary>
public sealed record AssignResult(
	string Database,
	string Team,
	string Event,
	[property: Description("Structures given to the team by this call.")] int Assigned,
	[property: Description("Structures the team already had.")] int AlreadyOnTeam,
	IReadOnlyList<AssignedStructure> Structures,
	[property: Description("Identifies this change; SDE's Settings › AI assistants can revert it.")] string BatchId,
	string Note);

/// <summary>create_event results.</summary>
public sealed record CreateEventResult(string Database, EventItem Event,
	[property: Description("Identifies this change; SDE's Settings › AI assistants can revert it.")] string BatchId);
