using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Data;

/// <summary>
/// Who is saving, and how. Passed to the store's save methods so edit history can say who changed what, and so locks
/// are enforced in one place.
/// </summary>
public sealed class SaveContext
{
	/// <summary>The person saving; null means <see cref="SdeStore.UserName"/>.</summary>
	public string User { get; init; }

	/// <summary>Where the change came from: App, Bulk edit, Import, Phone, Restore, ...</summary>
	public string Via { get; init; } = SaveVia.App;

	/// <summary>Groups the rows of one bulk edit or import, so they can be listed and reverted together. Never merged.</summary>
	public string BatchId { get; init; }

	/// <summary>Lets a save change a Final or Letter sent assessment (only for reopening and for restoring a backup).</summary>
	public bool AllowLocked { get; init; }

	/// <summary>An event to record with the save, e.g. "Imported from SDE_1". For a new assessment it replaces "Assessment created".</summary>
	public string Event { get; init; }

	/// <summary>The assessment's version the writer loaded (see <see cref="VersionStamp"/>); null doesn't check.</summary>
	public long? ExpectedVersion { get; init; }

	/// <summary>The structure's version the writer loaded; null doesn't check.</summary>
	public long? ExpectedPropertyVersion { get; init; }

	/// <summary>The assessment's LastUpdate the writer loaded, which catches saves by versions older than v2.5; null doesn't check.</summary>
	public DateTime? ExpectedLastUpdate { get; init; }

	/// <summary>The structure's LastUpdate the writer loaded; null doesn't check.</summary>
	public DateTime? ExpectedPropertyLastUpdate { get; init; }

	/// <summary>True when the save must be refused if someone else saved since the writer loaded (see <see cref="ConcurrencyException"/>).</summary>
	public bool HasExpectations => ExpectedVersion != null || ExpectedPropertyVersion != null || ExpectedLastUpdate != null || ExpectedPropertyLastUpdate != null;

	/// <summary>This context, refusing the save if the database has moved on from <paramref name="loaded"/> (null: no check).</summary>
	public SaveContext Expecting(VersionStamp loaded) => new SaveContext
	{
		User = User,
		Via = Via,
		BatchId = BatchId,
		AllowLocked = AllowLocked,
		Event = Event,
		ExpectedVersion = loaded?.Assessment,
		ExpectedPropertyVersion = loaded?.Property,
		ExpectedLastUpdate = loaded?.AssessmentLastUpdate,
		ExpectedPropertyLastUpdate = loaded?.PropertyLastUpdate,
	};

	/// <summary>An ordinary edit in the app.</summary>
	public static SaveContext App { get; } = new SaveContext();

	/// <summary>A new context for one batch (bulk edit, import) with its own BatchId.</summary>
	public static SaveContext Batch(string via, string eventLabel = null) =>
		new SaveContext { Via = via, BatchId = Guid.NewGuid().ToString("N"), Event = eventLabel };

	/// <summary>This context with a BatchId of its own when it has none (one bulk change is one batch in edit history).</summary>
	public SaveContext AsBatch() => BatchId != null ? this : new SaveContext
	{
		User = User,
		Via = Via,
		BatchId = Guid.NewGuid().ToString("N"),
		AllowLocked = AllowLocked,
		Event = Event,
		ExpectedVersion = ExpectedVersion,
		ExpectedPropertyVersion = ExpectedPropertyVersion,
		ExpectedLastUpdate = ExpectedLastUpdate,
		ExpectedPropertyLastUpdate = ExpectedPropertyLastUpdate,
	};
}

/// <summary>Values for <see cref="SaveContext.Via"/> (stored as text, so new ones can be added freely).</summary>
public static class SaveVia
{
	public const string App = "App";
	public const string BulkEdit = "Bulk edit";
	public const string CopyFrom = "Copy from";
	public const string Import = "Import";
	public const string Phone = "Phone";
	public const string Restore = "Restore";
	public const string Undo = "Undo";
	public const string Online = "Online lookup";
	public const string PhotoFolder = "Photo folder";
	public const string DepthTable = "Depth table";

	/// <summary>The map: Set location, dragging a structure, copying values from a map layer.</summary>
	public const string Map = "Map";

	/// <summary>An AI assistant connected through SDE's MCP server.</summary>
	public const string AiAssistant = "AI assistant";

	/// <summary>Checking a work package back in from another Mac.</summary>
	public const string WorkPackage = "Work package";

	/// <summary>Flood depth from high-water marks or a depth grid.</summary>
	public const string DepthTool = "Depth tool";

	/// <summary>Values read from an elevation certificate.</summary>
	public const string EcImport = "EC import";
}

/// <summary>Where an assessment is in the review workflow. An assessment with no status row is a Draft.</summary>
public enum AssessmentStatus
{
	Draft,
	Reviewed,
	Final,
	LetterSent,
	Appeal,
}

/// <summary>Plain-English names and lock rules for <see cref="AssessmentStatus"/>.</summary>
public static class AssessmentStatuses
{
	/// <summary>Final and Letter sent assessments can't be changed until they are reopened (with a reason).</summary>
	public static bool IsLocked(AssessmentStatus s) => s == AssessmentStatus.Final || s == AssessmentStatus.LetterSent;

	public static string Label(AssessmentStatus s) => s switch
	{
		AssessmentStatus.LetterSent => "Letter sent",
		_ => s.ToString(),
	};

	/// <summary>Reads a stored status; anything unknown counts as a Draft.</summary>
	public static AssessmentStatus Parse(string text) =>
		Enum.TryParse(text, ignoreCase: true, out AssessmentStatus s) ? s : AssessmentStatus.Draft;
}

/// <summary>A row of the AssessmentStatus table (Mac-only, never exported).</summary>
[Table("AssessmentStatus")]
public sealed class AssessmentStatusRecord
{
	[Key] public Guid AssessmentID { get; set; }

	/// <summary>The stored status text; use <see cref="Value"/>.</summary>
	public string Status { get; set; } = nameof(AssessmentStatus.Draft);

	public DateTime? ChangedOn { get; set; }

	public string ChangedBy { get; set; }

	/// <summary>Why it was reopened, or a note on the change.</summary>
	public string Reason { get; set; }

	/// <summary>The assessment's LastUpdate when it was locked. If it differs now, an older version of the app changed it.</summary>
	public DateTime? LockedLastUpdate { get; set; }

	[NotMapped]
	public AssessmentStatus Value
	{
		get => AssessmentStatuses.Parse(Status);
		set => Status = value.ToString();
	}

	[NotMapped] public bool IsLocked => AssessmentStatuses.IsLocked(Value);
}

/// <summary>One row of edit history: a changed field, or an event (Field is null) such as "Assessment created".</summary>
[Table("EditHistory")]
public sealed class EditHistoryRecord
{
	[Key, AutoIncrement] public long HistoryID { get; set; }

	public Guid? PropertyID { get; set; }

	public Guid? AssessmentID { get; set; }

	public DateTime ChangedOn { get; set; }

	public string ChangedBy { get; set; }

	public string Via { get; set; }

	public string BatchId { get; set; }

	/// <summary>The field key (<see cref="FieldCatalog"/>, or a key from <see cref="AssessmentDiff"/>); null for an event.</summary>
	public string Field { get; set; }

	public string Label { get; set; }

	public string OldValue { get; set; }

	public string NewValue { get; set; }

	[NotMapped] public bool IsEvent => Field == null;
}

/// <summary>A per-database setting stored as JSON (MacSetting table).</summary>
[Table("MacSetting")]
public sealed class MacSettingRecord
{
	[Key] public string Key { get; set; }

	public string Value { get; set; }
}

/// <summary>A named filter and column layout for the Structures list.</summary>
[Table("SavedView")]
public sealed class SavedViewRecord
{
	[Key] public string Name { get; set; }

	/// <summary>The view's state as JSON (the app decides its shape).</summary>
	public string State { get; set; }

	public DateTime? UpdatedOn { get; set; }
}

/// <summary>Thrown when a save would change a Final or Letter sent assessment.</summary>
public sealed class AssessmentLockedException : InvalidOperationException
{
	public AssessmentLockedException(Guid assessmentId, AssessmentStatus status, IReadOnlyList<FieldDiff> changes)
		: base($"This assessment is {AssessmentStatuses.Label(status)}, so it can't be changed. Reopen it first (Status, then Reopen) and give a reason.")
	{
		AssessmentID = assessmentId;
		Status = status;
		Changes = changes;
	}

	public Guid AssessmentID { get; }

	public AssessmentStatus Status { get; }

	/// <summary>What the refused save would have changed.</summary>
	public IReadOnlyList<FieldDiff> Changes { get; }
}
