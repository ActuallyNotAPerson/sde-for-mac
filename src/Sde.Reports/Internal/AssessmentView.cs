using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Reports.Internal;

/// <summary>How the determination is colored.</summary>
internal enum Determination
{
	Substantial,
	NotSubstantial,
	/// <summary>"Data Entry Incomplete" or a "damage undetermined" reason.</summary>
	Other,
}

/// <summary>
/// One assessment as the report datasets saw it: the columns of the SQL in decomp/DAL/DAL.Services/ReportsService.cs
/// with lookup IDs turned into names, plus the values decomp/BLL/BLL.Service/ReportsService.cs added
/// (ValueOfBuilding, CostOfRepair, element/adjustment/custom-field columns).
/// </summary>
internal sealed class AssessmentView
{
	public const string NotProvided = "Not Provided";

	private readonly ReferenceData _ref;

	public AssessmentView(AssessmentFile file, ReferenceData reference)
	{
		File = file ?? throw new ArgumentNullException(nameof(file));
		_ref = reference;
		P = file.Property ?? new PropertyRecord();
		A = file.Assessment ?? new AssessmentRecord();
	}

	public AssessmentFile File { get; }
	public PropertyRecord P { get; }
	public AssessmentRecord A { get; }

	/// <summary>Property.StructureTypeID 1 = residential; anything else prints the non-residential layout.</summary>
	public bool IsResidential => P.StructureTypeID == 1;

	public string Name(int? lookupId) => _ref.LookupName(lookupId) ?? "";

	public LookupRecord Lookup(int? lookupId) => _ref.Lookup(lookupId);

	// ───────── Structure address ─────────

	/// <summary>StreetNumber StreetName StreetSuffix (the suffix's Lookup name, e.g. "Street").</summary>
	public string StreetAddress => Fmt.Join(" ", P.StreetNumber, P.StreetName, Name(P.StreetSuffixID));
	public string City => P.City ?? "";
	public string State => _ref.StateName(P.StateID) ?? "";
	public string County => _ref.CountyName(P.CountyID) ?? "";
	public string Zip => Fmt.Zip(P.ZipCode);
	public string OwnerName => Fmt.LastFirst(P.OwnerLastName, P.OwnerFirstName);

	/// <summary>"City, State Zip".</summary>
	public string CityStateZip => Fmt.Join(" ", Fmt.Join(", ", City, State), Zip);

	/// <summary>The percent-damaged/community reports' address line: Number Name Suffix, City, State Zip.</summary>
	public string FullAddress => Fmt.Join(", ", StreetAddress, City, Fmt.Join(" ", State, Zip));

	// ───────── Mailing address ─────────

	public string MailingOwnerName => Fmt.LastFirst(P.MailingOwnerLastName, P.MailingOwnerFirstName);
	public string MailingStreetAddress => Fmt.Join(" ", P.MailingStreetNumber, P.MailingStreetName, Name(P.MailingStreetSuffixID));
	public string MailingState => _ref.StateName(P.MailingStateID) ?? "";
	public string MailingCounty => _ref.CountyName(P.MailingCountyID) ?? "";

	// ───────── Structure ─────────

	/// <summary>Residence type (residential) or structure use (non-residential); the community report's "Type of Structure".</summary>
	public string TypeOfStructure => IsResidential ? Name(P.ResidenceTypeID) : Name(P.StructureUseID);

	/// <summary>The non-residential detail report's IIf(Sprinkler Is Nothing, "No", Sprinkler).</summary>
	public string Sprinkler => _ref.LookupName(P.SprinklerSystem) ?? "No";

	/// <summary>The non-residential detail report's IIf(Conveyance Is Nothing, "No", Conveyance).</summary>
	public string Conveyance => _ref.LookupName(P.Conveyance) ?? "No";

	public string DurationOfFlood => Fmt.Join(" ", Fmt.Plain(A.DurationOfFlood), Name(A.DurationOfFloodUnitsID));

	public string FirstFloorElevation => P.FirstFloorElevation is decimal d ? Fmt.Raw(d) + " ft." : "";

	public string AdjustedTaxAssessedValue =>
		A.TaxAssessedValue is decimal t && A.FactorAdjustment is decimal f ? Fmt.Money(t * f) : "";

	// ───────── Determination ─────────

	public string DamageMessage => A.DamageMessage ?? "";

	public Determination Determination => DamageMessage switch
	{
		"Substantially Damaged" => Determination.Substantial,
		"Not Substantially Damaged" => Determination.NotSubstantial,
		_ => Determination.Other,
	};

	/// <summary>Lookup name of Assessment.ValueOfBuildingID (390 Computed Actual Cash Value, 391 Professional Market Appraisal, 392 Adj. Tax Assessed Value).</summary>
	public string BasisValue => Name(A.ValueOfBuildingID);

	/// <summary>Lookup name of Assessment.CostOfRepairsID (393 Computed Damages, 394 Contractor Estimate, 395 Community Estimate).</summary>
	public string BasisCost => Name(A.CostOfRepairsID);

	/// <summary>
	/// The value the percentage was computed against. Port of BLL ReportsService.AddBuildingValueToDataSet
	/// (decomp/BLL/BLL.Service/ReportsService.cs:319): "Not Provided" when the percent is missing or 0, otherwise
	/// the amount for the chosen basis. A missing amount printed as an error in the original
	/// (Convert.ToDecimal("") threw); here it is "Not Provided".
	/// </summary>
	public string ValueOfBuilding => AssessmentAmounts.BuildingValue(A) is decimal d ? Fmt.Money(d) : NotProvided;

	/// <summary>
	/// The repair cost behind the percentage. Port of BLL ReportsService.AddRepairCostToDataSet
	/// (decomp/BLL/BLL.Service/ReportsService.cs:356): "Not Provided" when the percent is missing, "$0.00" when it is 0,
	/// otherwise the amount for the chosen basis.
	/// </summary>
	public string CostOfRepair => AssessmentAmounts.RepairCost(A) is decimal d ? Fmt.Money(d) : NotProvided;

	// ───────── Detail report rows ─────────

	/// <summary>
	/// Standard cost adjustments (BLL AddCostAdjustmentsToDataSet → Adjustment1..7 columns). The original query had
	/// no ORDER BY; rows are printed in the order the app shows them (Lookup zOrder, NULLs first).
	/// </summary>
	public IReadOnlyList<CostAdjustmentResultRecord> CostAdjustments =>
		File.CostAdjustments
			.OrderBy(c => _ref.Lookup(c.CostAdjustmentLookupID)?.zOrder ?? int.MinValue)
			.ThenBy(c => c.CostAdjustmentLookupID)
			.ToList();

	/// <summary>Additional adjustments that have something in them (the original printed all 7 rows, mostly blank).</summary>
	public IReadOnlyList<AdditionalAdjustmentRecord> AdditionalAdjustments =>
		File.AdditionalAdjustments
			.Where(a => !string.IsNullOrWhiteSpace(a.AdjustmentName) || a.Quantity is > 0 || a.UnitCost is > 0 || a.TotalCost is > 0)
			.OrderBy(a => a.zOrder ?? int.MaxValue)
			.ToList();

	/// <summary>Element rows in saved order (BLL AddElementPercentagesToDataSet → Damage1..12 columns).</summary>
	public IReadOnlyList<ElementPercentageRecord> Elements => File.ElementPercentages;

	/// <summary>
	/// Assessment custom fields as (name, value). The original (BLL AddCustomFieldsToDataSet) inner-joined the
	/// results to CustomField and filled CF1..CF3 (a 4th result crashed it). Here every defined field is listed
	/// in zOrder with its value (blank if none), followed by any result whose definition is missing.
	/// </summary>
	public IReadOnlyList<(string Name, string Value)> CustomFields
	{
		get
		{
			List<(string, string)> list = new();
			List<CustomFieldResultRecord> results = File.CustomFields ?? new();
			HashSet<Guid> defined = new();
			foreach (CustomFieldRecord f in (_ref.CustomFields ?? Array.Empty<CustomFieldRecord>()).OrderBy(f => f.zOrder))
			{
				defined.Add(f.CustomFieldID);
				CustomFieldResultRecord r = results.FirstOrDefault(x => x.CustomFieldID == f.CustomFieldID);
				list.Add((f.CustomField ?? "", r?.Result ?? ""));
			}
			foreach (CustomFieldResultRecord r in results.Where(x => !defined.Contains(x.CustomFieldID)).OrderBy(x => x.zOrder))
			{
				list.Add(("Custom Field", r.Result ?? ""));
			}
			return list;
		}
	}
}
