using Sde.Core.Assessments;

namespace Sde.Reports;

/// <summary>The four SDE reports (each original came as an RDLC layout with and without photos).</summary>
public enum ReportKind
{
	/// <summary>One page per assessment (rptResidentialSummaryReport / rptNonResidentialSummary), variant chosen per structure type.</summary>
	Summary,
	/// <summary>Everything about one assessment (rptResidentialDetailReport / rptNonResidentialDetailReport), one section per assessment.</summary>
	Detail,
	/// <summary>Structure and Percent Damage list grouped by NFIP community (rptPercentDamaged).</summary>
	PercentDamaged,
	/// <summary>Community report: landscape, grouped by NFIP community → structure → assessment (rptCommunityRes).</summary>
	Community,
}

/// <summary>What to print. Assessments are printed in the order given (lists group them by community and structure in order of first appearance).</summary>
public sealed class ReportRequest
{
	public ReportKind Kind { get; init; }

	public IReadOnlyList<AssessmentFile> Assessments { get; init; }

	/// <summary>The original's "with photos" / "NoPhotos" layout choice.</summary>
	public bool IncludePhotos { get; init; } = true;

	/// <summary>Folder holding the photo files (SdeStore.AttachmentsFolder); photo path = Path.Combine(PhotosFolder, attachment.FileName).</summary>
	public string PhotosFolder { get; init; }

	/// <summary>Date printed in the footer; default DateTime.Now (the original's code.GetTheDate()).</summary>
	public DateTime? GeneratedOn { get; init; }

	/// <summary>Markup JSON by photo file name (see <c>SdeStore.GetPhotoMarkups</c>), drawn on the printed photos. Null: none.</summary>
	public IReadOnlyDictionary<string, string> PhotoMarkups { get; init; }
}
