using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Core.Quality;

/// <summary>How much a quality finding matters.</summary>
public enum QaSeverity
{
	/// <summary>Worth a look; nothing is wrong as such.</summary>
	Info,

	/// <summary>Probably a mistake; check it.</summary>
	Warning,

	/// <summary>Wrong or blocking; fix it.</summary>
	Error,
}

/// <summary>
/// One thing a quality check found. <see cref="RuleId"/>, <see cref="ObjectId"/> and <see cref="Detail"/> together
/// identify it, and are what a dismissal (QaDismissal) records: the detail names the exact finding (the other
/// structure, the value checked), so dismissing one doesn't hide a new one about something else.
/// </summary>
public sealed record QaFinding
{
	/// <summary>The check that found it (a <see cref="QaRuleIds"/> constant).</summary>
	public string RuleId { get; init; }

	public QaSeverity Severity { get; init; }

	/// <summary>What it is about: an assessment, a structure (PropertyID) or an event, as the rule says.</summary>
	public Guid ObjectId { get; init; }

	/// <summary>The assessment to open for it (for a structure finding, the newest assessment checked); null for an event finding.</summary>
	public Guid? AssessmentId { get; init; }

	/// <summary>The structure it is about; null for an event finding.</summary>
	public Guid? PropertyId { get; init; }

	/// <summary>Which finding exactly, as stable text ("" when the rule finds at most one thing per object).</summary>
	public string Detail { get; init; } = "";

	/// <summary>What was found, in plain English.</summary>
	public string Message { get; init; }

	/// <summary>The field to jump to (a <see cref="FieldCatalog"/> key), or null.</summary>
	public string FieldKey { get; init; }

	/// <summary>The dismissal that sets this finding aside, or null while it is open.</summary>
	public QaDismissalRecord Dismissal { get; init; }

	public bool IsDismissed => Dismissal != null;
}

/// <summary>
/// A quality finding someone looked at and set aside, with their reason (Mac-only QaDismissal table, never exported).
/// The key is the finding's rule, object and detail.
/// </summary>
[Table("QaDismissal")]
public sealed class QaDismissalRecord
{
	[Key] public string RuleId { get; set; }

	/// <summary>The finding's <see cref="QaFinding.ObjectId"/>.</summary>
	[Key] public Guid ObjectID { get; set; }

	/// <summary>The finding's <see cref="QaFinding.Detail"/>.</summary>
	[Key] public string Detail { get; set; } = "";

	public string Reason { get; set; }

	public DateTime? DismissedOn { get; set; }

	public string DismissedBy { get; set; }
}

/// <summary>A quality check: its ID, name, usual severity, whether a finding can be set aside, and what it looks for.</summary>
public sealed record QaRuleInfo(string Id, string Title, QaSeverity Severity, bool CanDismiss, string Description)
{
	/// <summary>
	/// True for checks that compare two structures: a finding is reported on each of them, and setting one side aside
	/// sets the other side aside too.
	/// </summary>
	public bool Pairwise { get; init; }
}

/// <summary>The IDs of the quality checks, as stored in QaDismissal.RuleId.</summary>
public static class QaRuleIds
{
	public const string DuplicateParcel = "DuplicateParcel";
	public const string DuplicateAddress = "DuplicateAddress";
	public const string DuplicateLocation = "DuplicateLocation";
	public const string MissingCoordinates = "MissingCoordinates";
	public const string MissingPhotos = "MissingPhotos";
	public const string MissingDamageDate = "MissingDamageDate";
	public const string MissingMailingAddress = "MissingMailingAddress";
	public const string ValuePerSqFt = "ValuePerSqFt";
	public const string DepthVsNeighbours = "DepthVsNeighbours";
	public const string DepthVsHighWaterMarks = "DepthVsHighWaterMarks";
	public const string NearThreshold = "NearThreshold";
	public const string UnacceptedSuggestions = "UnacceptedSuggestions";
	public const string InspectedBeforeDamage = "InspectedBeforeDamage";
	public const string ZoneDiffersFromNfhl = "ZoneDiffersFromNfhl";
	public const string SeveralDamageDates = "SeveralDamageDates";
}

/// <summary>An event's ID and name, for the checks that look at an event as a whole.</summary>
public sealed record QaEvent(Guid EventId, string Name);

/// <summary>Thresholds for the quality checks. The defaults suit most events.</summary>
public sealed class QaOptions
{
	/// <summary>Two structures closer than this are probably the same building.</summary>
	public double DuplicateDistanceMeters { get; init; } = 5;

	/// <summary>The value per square foot check needs at least this many comparable assessments.</summary>
	public int MinimumForValueCheck { get; init; } = 8;

	/// <summary>Values farther than this many (scaled) median absolute deviations from the median are flagged.</summary>
	public double MadLimit { get; init; } = 3;

	/// <summary>Neighbours within this distance are compared for the flood elevation check.</summary>
	public double NeighbourRadiusMeters { get; init; } = 300;

	/// <summary>The flood elevation check needs at least this many neighbours with a flood elevation.</summary>
	public int MinimumNeighbours { get; init; } = 3;

	/// <summary>A flood elevation more than this many feet from its neighbours' median is flagged.</summary>
	public decimal NeighbourToleranceFt { get; init; } = 2.0m;

	/// <summary>A flood elevation more than this many feet from the high-water mark estimate is flagged.</summary>
	public decimal HighWaterMarkToleranceFt { get; init; } = 1.0m;

	/// <summary>The "close to the threshold" band, in percent damaged (inclusive).</summary>
	public decimal NearThresholdLow { get; init; } = 45m;

	public decimal NearThresholdHigh { get; init; } = 55m;
}

/// <summary>
/// Everything the quality checks look at. Only <see cref="Assessments"/> and <see cref="Reference"/> are needed; each
/// optional input turns on the checks that use it.
/// </summary>
public sealed record QaInput
{
	/// <summary>The assessments to check (each with its structure). Structure checks look at the structures among them.</summary>
	public IReadOnlyList<AssessmentFile> Assessments { get; init; } = Array.Empty<AssessmentFile>();

	/// <summary>FEMA's lookups: street suffixes for comparing addresses, FIRM zone names.</summary>
	public ReferenceData Reference { get; init; }

	/// <summary>Depth-table suggestions nobody has accepted yet, counted by assessment.</summary>
	public IReadOnlyDictionary<Guid, int> SuggestionCounts { get; init; }

	/// <summary>Which event each assessment belongs to (assessments in no event are left out).</summary>
	public IReadOnlyDictionary<Guid, Guid> EventOf { get; init; }

	/// <summary>The events, by ID, for their names.</summary>
	public IReadOnlyDictionary<Guid, QaEvent> Events { get; init; }

	/// <summary>The flood zone FEMA's flood map (NFHL) shows at each structure, by PropertyID, e.g. "AE".</summary>
	public IReadOnlyDictionary<Guid, string> NfhlZones { get; init; }

	/// <summary>The flood elevation the high-water marks suggest for each assessment, in feet in the structure's datum.</summary>
	public IReadOnlyDictionary<Guid, decimal> HighWaterMarkEstimates { get; init; }

	/// <summary>Findings set aside earlier; matching findings come back with <see cref="QaFinding.Dismissal"/> set.</summary>
	public IReadOnlyList<QaDismissalRecord> Dismissals { get; init; }

	public QaOptions Options { get; init; } = new QaOptions();
}
