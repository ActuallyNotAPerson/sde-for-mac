using System.Globalization;
using Sde.Core.Assessments;
using Sde.Core.Geo;
using Sde.Core.Records;

namespace Sde.Core.Depth;

/// <summary>
/// A high-water mark: a surveyed elevation of the highest water left on a building, a tree or a bank (debris, a seed
/// line, a stain). From a file or from USGS's Short-Term Network (STN). Never carries the names of the people who
/// flagged or surveyed it.
/// </summary>
public sealed record HighWaterMark
{
	/// <summary>The source's ID for the mark (USGS's HWM ID, or the file's row), for showing and tracing it.</summary>
	public string Id { get; init; }

	/// <summary>The source's label for the mark, e.g. "VT 027 BW 29".</summary>
	public string Label { get; init; }

	/// <summary>Decimal degrees (NAD83 or WGS 84; the difference, a metre or two, doesn't matter here).</summary>
	public double Latitude { get; init; }

	public double Longitude { get; init; }

	/// <summary>The water's elevation at the mark, in feet above <see cref="DatumName"/>.</summary>
	public decimal ElevationFt { get; init; }

	/// <summary>The vertical datum as the source wrote it ("NAVD88"); null or blank when it didn't say.</summary>
	public string DatumName { get; init; }

	/// <summary>USGS's quality rating, 1 (excellent, within 0.05 ft) to 5 (very poor, worse than 0.4 ft), 6 for unknown or historical; null when not given.</summary>
	public int? Quality { get; init; }

	/// <summary>What the mark is ("Debris", "Seed line", "Mud"), when the source says.</summary>
	public string Kind { get; init; }

	/// <summary>The datum, read from <see cref="DatumName"/>.</summary>
	public VerticalDatum Datum => VerticalDatums.Parse(DatumName);
}

/// <summary>Settings for <see cref="HighWaterMarks.Estimate"/>.</summary>
public sealed class HighWaterMarkOptions
{
	/// <summary>At most this many of the nearest marks are used.</summary>
	public int NearestCount { get; init; } = 5;

	/// <summary>Marks farther than this from the structure are never used.</summary>
	public double RadiusMeters { get; init; } = 1000;

	/// <summary>How fast a mark's weight falls with distance (2: a mark twice as far counts a quarter as much).</summary>
	public double Power { get; init; } = 2;

	/// <summary>Marks rated worse than this are left out (1 is the best rating, 6 unknown). 6 keeps every mark.</summary>
	public int WorstQuality { get; init; } = 6;
}

/// <summary>A mark that went into an estimate, how far it is from the structure and its share of the weight (0 to 1).</summary>
public sealed record UsedMark(HighWaterMark Mark, double Meters, double Weight);

/// <summary>How far to trust an estimate.</summary>
public enum EstimateConfidence
{
	/// <summary>No estimate: no usable marks.</summary>
	None,
	Low,
	Medium,
	High,
}

/// <summary>The water-surface elevation estimated at a structure from nearby high-water marks.</summary>
public sealed class WaterSurfaceEstimate
{
	/// <summary>The estimated elevation in feet, in <see cref="DatumName"/>; null when no mark could be used.</summary>
	public decimal? ElevationFt { get; init; }

	/// <summary>The datum of the marks used, as the first of them wrote it (null when they didn't say).</summary>
	public string DatumName { get; init; }

	public VerticalDatum Datum => VerticalDatums.Parse(DatumName);

	/// <summary>The marks used, nearest first.</summary>
	public IReadOnlyList<UsedMark> Marks { get; init; } = Array.Empty<UsedMark>();

	public EstimateConfidence Confidence { get; init; }

	/// <summary>A plain-English note on how the estimate was made and why it has its confidence.</summary>
	public string Note { get; init; }
}

/// <summary>
/// Depth values an estimate suggests for one assessment. Nothing is saved until someone accepts them; accepted values
/// go through <see cref="FieldInput"/> like any other value typed outside the editor.
/// </summary>
public sealed class DepthProposal
{
	/// <summary>The flood elevation for Assessment.DepthOfFlood (which SDE's form calls "Flood elevation"), in the structure's datum.</summary>
	public decimal? FloodElevationFt { get; init; }

	/// <summary>The depth above the lowest floor for Assessment.DepthOfFloodAboveFFE: flood elevation minus the structure's FirstFloorElevation. Negative when the water stayed below the floor.</summary>
	public decimal? DepthAboveLowestFloorFt { get; init; }

	/// <summary>The depth above the ground (flood elevation minus the ground elevation given), for information; SDE has no field for it.</summary>
	public decimal? DepthAboveGroundFt { get; init; }

	/// <summary>The marks and the structure use different datums and there was no offset to convert with, so nothing is proposed.</summary>
	public bool DatumMismatch { get; init; }

	/// <summary>The event's datum offset that was added to the estimate, when one was needed and given.</summary>
	public decimal? OffsetAppliedFt { get; init; }

	/// <summary>Plain-English notes for the confirm sheet (conversions, warnings).</summary>
	public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

	/// <summary>True when there is something to fill in.</summary>
	public bool CanApply => FloodElevationFt != null && !DatumMismatch;

	/// <summary>The proposed values as field changes (FieldCatalog keys), ready for <see cref="FieldInput.Apply"/>.</summary>
	public IReadOnlyList<FieldInputChange> Changes
	{
		get
		{
			List<FieldInputChange> list = new List<FieldInputChange>();
			if (!CanApply)
			{
				return list;
			}
			list.Add(new FieldInputChange(DepthOfFloodKey, FloodElevationFt.Value.ToString("0.0", CultureInfo.InvariantCulture)));
			if (DepthAboveLowestFloorFt is decimal d)
			{
				list.Add(new FieldInputChange(DepthAboveFloorKey, d.ToString("0.0", CultureInfo.InvariantCulture)));
			}
			return list;
		}
	}

	/// <summary>The FieldCatalog key of Assessment.DepthOfFlood.</summary>
	public static string DepthOfFloodKey => FieldCatalog.Find(FieldModel.Assessment, nameof(AssessmentRecord.DepthOfFlood)).Key;

	/// <summary>The FieldCatalog key of Assessment.DepthOfFloodAboveFFE.</summary>
	public static string DepthAboveFloorKey => FieldCatalog.Find(FieldModel.Assessment, nameof(AssessmentRecord.DepthOfFloodAboveFFE)).Key;
}

/// <summary>
/// Flood depth from high-water marks: the water-surface elevation at a structure by inverse-distance weighting of the
/// nearest marks, and the depth values that elevation suggests for an assessment. Pure functions; the Depth tool's
/// sheet, the quality checks and the AI assistant server all use them.
/// </summary>
public static class HighWaterMarks
{
	/// <summary>Within this distance a mark is treated as being at the structure (its weight stops growing).</summary>
	public const double MinimumDistanceMeters = 1.0;

	/// <summary>
	/// How much a mark's USGS quality rating counts: excellent 1.0, good 0.8, fair 0.6, poor 0.4, very poor and
	/// unknown 0.2. A mark with no rating (a file without that column) counts 0.5.
	/// </summary>
	public static double QualityWeight(int? quality) => quality switch
	{
		1 => 1.0,
		2 => 0.8,
		3 => 0.6,
		4 => 0.4,
		5 or 6 => 0.2,
		null => 0.5,
		_ => 0.2,
	};

	/// <summary>
	/// Estimates the water-surface elevation at a point from the nearest marks within the radius. Marks are weighted by
	/// quality divided by distance to the power (inverse-distance weighting). Marks in different datums are never
	/// mixed: the estimate uses the datum of the nearest mark that says its datum, and leaves out marks in any other
	/// (marks that don't say are used only when no mark does). Confidence is high with at least three marks, the nearest
	/// within 200 m and all within 1 ft of each other; low with one mark, the nearest beyond 500 m, a spread over 2 ft,
	/// or no datum; medium otherwise.
	/// </summary>
	public static WaterSurfaceEstimate Estimate(IEnumerable<HighWaterMark> marks, double latitude, double longitude, HighWaterMarkOptions options = null)
	{
		HighWaterMarkOptions o = options ?? new HighWaterMarkOptions();
		List<(HighWaterMark Mark, double Meters)> inRange = (marks ?? Enumerable.Empty<HighWaterMark>())
			.Where(m => m != null && (m.Quality == null || m.Quality <= o.WorstQuality))
			.Select((m, i) => (Mark: m, Meters: GeoMath.Meters(latitude, longitude, m.Latitude, m.Longitude), Order: i))
			.Where(x => x.Meters <= o.RadiusMeters)
			.OrderBy(x => x.Meters).ThenBy(x => x.Order)
			.Select(x => (x.Mark, x.Meters))
			.ToList();
		if (inRange.Count == 0)
		{
			return new WaterSurfaceEstimate
			{
				Confidence = EstimateConfidence.None,
				Note = $"No high-water marks within {Distance(o.RadiusMeters)}.",
			};
		}

		// One datum only: the nearest mark that names one decides; marks that don't name one are a last resort.
		(HighWaterMark Mark, double Meters) leader = inRange.FirstOrDefault(x => x.Mark.Datum != VerticalDatum.Unknown);
		string datumName = leader.Mark?.DatumName;
		List<(HighWaterMark Mark, double Meters)> sameDatum = leader.Mark == null
			? inRange
			: inRange.Where(x => x.Mark.Datum != VerticalDatum.Unknown && !VerticalDatums.Differ(x.Mark.DatumName, datumName)).ToList();
		int otherDatum = inRange.Count(x => x.Mark.Datum != VerticalDatum.Unknown) - sameDatum.Count;
		int noDatum = leader.Mark == null ? 0 : inRange.Count(x => x.Mark.Datum == VerticalDatum.Unknown);

		List<(HighWaterMark Mark, double Meters)> used = sameDatum.Take(Math.Max(1, o.NearestCount)).ToList();
		double[] raw = used.Select(x => QualityWeight(x.Mark.Quality) / Math.Pow(Math.Max(x.Meters, MinimumDistanceMeters), o.Power)).ToArray();
		double total = raw.Sum();
		double estimate = 0;
		for (int i = 0; i < used.Count; i++)
		{
			estimate += (double)used[i].Mark.ElevationFt * raw[i] / total;
		}
		List<UsedMark> usedMarks = used.Select((x, i) => new UsedMark(x.Mark, x.Meters, raw[i] / total)).ToList();

		decimal spread = used.Max(x => x.Mark.ElevationFt) - used.Min(x => x.Mark.ElevationFt);
		double nearest = used[0].Meters;
		bool noDatumAtAll = leader.Mark == null;
		EstimateConfidence confidence =
			used.Count == 1 || nearest > 500 || spread > 2.0m || noDatumAtAll ? EstimateConfidence.Low
			: used.Count >= 3 && nearest <= 200 && spread <= 1.0m ? EstimateConfidence.High
			: EstimateConfidence.Medium;

		List<string> notes = new List<string>
		{
			used.Count == 1
				? $"From 1 mark, {Distance(nearest)} away."
				: $"From {used.Count} marks within {Distance(used[^1].Meters)}, the nearest {Distance(nearest)} away. They differ by up to {spread.ToString("0.0#", CultureInfo.InvariantCulture)} ft.",
		};
		if (otherDatum > 0)
		{
			notes.Add($"{Count(otherDatum, "mark")} in another datum left out.");
		}
		if (noDatum > 0)
		{
			notes.Add($"{Count(noDatum, "mark")} with no datum left out.");
		}
		if (noDatumAtAll)
		{
			notes.Add("The marks don't say which datum they use.");
		}
		return new WaterSurfaceEstimate
		{
			ElevationFt = Math.Round((decimal)estimate, 2, MidpointRounding.AwayFromZero),
			DatumName = datumName,
			Marks = usedMarks,
			Confidence = confidence,
			Note = string.Join(" ", notes),
		};
	}

	/// <summary>
	/// The depth values an estimate suggests for an assessment of <paramref name="structure"/>: the flood elevation,
	/// and the depth above the lowest floor when the structure's FirstFloorElevation is known (0 counts as not entered,
	/// as in FEMA's lists). When the marks and the structure name different datums, the event's
	/// <paramref name="datumOffsetFt"/> (added to the marks' elevations) converts the estimate; without one nothing is
	/// proposed. <paramref name="groundElevationFt"/>, when known (an elevation certificate's lowest adjacent grade),
	/// gives the depth above the ground for information. Values are rounded to 0.1 ft.
	/// </summary>
	public static DepthProposal Propose(WaterSurfaceEstimate estimate, PropertyRecord structure, decimal? datumOffsetFt = null, decimal? groundElevationFt = null)
	{
		List<string> notes = new List<string>();
		if (estimate?.ElevationFt is not decimal wse)
		{
			notes.Add(estimate?.Note ?? "No estimate.");
			return new DepthProposal { Notes = notes };
		}
		string structureDatum = structure?.Datum;
		decimal? offsetApplied = null;
		if (VerticalDatums.Differ(estimate.DatumName, structureDatum))
		{
			string from = VerticalDatums.Describe(estimate.DatumName);
			string to = VerticalDatums.Describe(structureDatum);
			if (datumOffsetFt is not decimal offset)
			{
				notes.Add($"The marks are in {from} but this structure's elevations are in {to}. Enter the event's datum offset, then try again.");
				return new DepthProposal { DatumMismatch = true, Notes = notes };
			}
			wse += offset;
			offsetApplied = offset;
			notes.Add($"Converted from {from} to {to} with the event's offset of {Signed(offset)} ft.");
		}
		else if (estimate.Datum == VerticalDatum.Unknown)
		{
			notes.Add(string.IsNullOrWhiteSpace(structureDatum)
				? "Neither the marks nor this structure say which datum they use. Check they match."
				: $"The marks don't say which datum they use. Check they are in {VerticalDatums.Describe(structureDatum)}.");
		}
		else if (string.IsNullOrWhiteSpace(structureDatum))
		{
			notes.Add($"This structure has no datum. The marks are in {VerticalDatums.Describe(estimate.DatumName)}.");
		}

		decimal flood = Round(wse);
		decimal? aboveFloor = null;
		if (structure?.FirstFloorElevation is decimal ffe && ffe != 0m)
		{
			aboveFloor = Round(wse - ffe);
			if (aboveFloor < 0m)
			{
				notes.Add("The water stayed below the lowest floor.");
			}
		}
		else
		{
			notes.Add("The elevation of the lowest floor isn't entered, so there's no depth above it.");
		}
		decimal? aboveGround = groundElevationFt is decimal g ? Round(wse - g) : null;
		if (estimate.Confidence == EstimateConfidence.Low)
		{
			notes.Add("Low confidence: check the marks before using this.");
		}
		return new DepthProposal
		{
			FloodElevationFt = flood,
			DepthAboveLowestFloorFt = aboveFloor,
			DepthAboveGroundFt = aboveGround,
			OffsetAppliedFt = offsetApplied,
			Notes = notes,
		};
	}

	private static decimal Round(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

	private static string Signed(decimal value) => (value >= 0 ? "+" : "") + value.ToString("0.0#", CultureInfo.InvariantCulture);

	private static string Count(int n, string noun) => n == 1 ? "1 " + noun : n.ToString(CultureInfo.InvariantCulture) + " " + noun + "s";

	/// <summary>A distance for messages: "45 m", "1.2 km".</summary>
	internal static string Distance(double meters) => meters < 1000
		? Math.Round(meters).ToString("0", CultureInfo.InvariantCulture) + " m"
		: (meters / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km";
}
