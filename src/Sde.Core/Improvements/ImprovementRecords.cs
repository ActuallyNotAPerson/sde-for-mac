using Sde.Core.Records;

namespace Sde.Core.Improvements;

/// <summary>
/// One improvement project (usually a building permit) on a structure, used for substantial improvement (SI)
/// determinations. Mac-only table ImprovementProject; property names and types match its columns in Schema.sql.
/// </summary>
[Table("ImprovementProject")]
public sealed class ImprovementProjectRecord
{
	[Key] public Guid ProjectID { get; set; }

	public Guid PropertyID { get; set; }

	/// <summary>The permit number, when the project has one.</summary>
	public string PermitNumber { get; set; }

	/// <summary>What the work is, e.g. "Kitchen remodel and new roof".</summary>
	public string Description { get; set; }

	/// <summary>When the project was permitted or started. Used for the look-back period.</summary>
	public DateTime? ProjectDate { get; set; }

	/// <summary>The cost of the improvement (all of it, including donated labor and materials at market rates).</summary>
	public decimal? Cost { get; set; }

	/// <summary>Where the cost came from, e.g. "Contractor's estimate", "Owner's itemized list", "Cost data".</summary>
	public string CostBasis { get; set; }

	/// <summary>False when the official decided the cost does not count toward SI; <see cref="ExclusionReason"/> says why.</summary>
	public bool Included { get; set; } = true;

	public string ExclusionReason { get; set; }

	public DateTime? CreatedOn { get; set; }
}

/// <summary>
/// A substantial improvement determination made for a structure (Mac-only table SIDetermination). Property names
/// and types match its columns in Schema.sql. <see cref="SiResult.ToRecord"/> fills one in from a calculation.
/// </summary>
[Table("SIDetermination")]
public sealed class SIDeterminationRecord
{
	[Key] public Guid DeterminationID { get; set; }

	public Guid PropertyID { get; set; }

	public DateTime DeterminedOn { get; set; }

	/// <summary>Market value of the structure only (not the land), before the improvement.</summary>
	public decimal? MarketValue { get; set; }

	/// <summary>Where the market value came from, e.g. "Adjusted tax assessed value" or "Professional appraisal".</summary>
	public string MarketValueBasis { get; set; }

	/// <summary>The look-back period in years used (0 = this project only).</summary>
	public int? LookbackYears { get; set; }

	public decimal ThresholdPercent { get; set; }

	/// <summary>The improvement cost counted: this project plus any prior projects in the look-back period.</summary>
	public decimal CumulativeCost { get; set; }

	/// <summary>Cost as a percentage of market value (e.g. 52.3); null when there was no market value.</summary>
	public decimal? Percent { get; set; }

	/// <summary><see cref="SiResult.SubstantialImprovement"/>, <see cref="SiResult.NotSubstantialImprovement"/> or <see cref="SiResult.CannotDetermine"/>.</summary>
	public string Result { get; set; }

	public string DeterminedBy { get; set; }

	public string Notes { get; set; }
}
