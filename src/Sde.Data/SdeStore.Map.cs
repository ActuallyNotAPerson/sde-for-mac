using Dapper;
using Sde.Core.Records;

namespace Sde.Data;

/// <summary>One structure for the map: where it is (if known) and the result of its latest assessment.</summary>
public sealed record MapPointRow(
	Guid PropertyID,
	Guid? AssessmentID,
	string Address,
	string City,
	decimal? Latitude,
	decimal? Longitude,
	decimal? PercentDamage,
	string DamageMessage);

public sealed partial class SdeStore
{
	/// <summary>
	/// One row per active structure, with its latest active assessment (the newest inspection date, then the newest
	/// change) or no assessment. Structures without coordinates are included so the map can count and place them.
	/// The percentage is rounded to one decimal, exactly as in <see cref="ListStructures"/>, so a structure falls in the
	/// same result class on the map as in the list.
	/// </summary>
	public List<MapPointRow> ListMapPoints()
	{
		const string sql = """
			SELECT p.PropertyID, a.AssessmentID,
			       TRIM(COALESCE(p.StreetNumber, '') || ' ' || COALESCE(p.StreetName, '') || ' ' || COALESCE(s.LookupFieldName, '')) AS Address,
			       p.City, p.Latitude, p.Longitude, a.PercentDamage, a.DamageMessage
			FROM Property p
			LEFT JOIN Assessment a ON a.AssessmentID = (
				SELECT a2.AssessmentID FROM Assessment a2
				WHERE a2.PropertyID = p.PropertyID AND a2.Active = 1
				ORDER BY a2.DateofInspection DESC, a2.LastUpdate DESC LIMIT 1)
			LEFT JOIN Lookup s ON s.LookupID = p.StreetSuffixID
			WHERE p.Active = 1
			ORDER BY p.StreetName, p.StreetNumber
			""";
		return Database.Read(c => c.Query<MapRow>(sql).Select(r => new MapPointRow(
			r.PropertyID, r.AssessmentID, r.Address, r.City, r.Latitude, r.Longitude,
			r.PercentDamage is decimal pd ? JetScaling.WithScale(pd, 1) : null, r.DamageMessage)).ToList());
	}

	/// <summary>
	/// The same, limited to assessments <paramref name="includeAssessment"/> accepts (the event chosen in the sidebar):
	/// each structure shows its latest such assessment, and structures with none are left out.
	/// </summary>
	public List<MapPointRow> ListMapPoints(Func<Guid, bool> includeAssessment)
	{
		ArgumentNullException.ThrowIfNull(includeAssessment);
		const string sql = """
			SELECT p.PropertyID, a.AssessmentID,
			       TRIM(COALESCE(p.StreetNumber, '') || ' ' || COALESCE(p.StreetName, '') || ' ' || COALESCE(s.LookupFieldName, '')) AS Address,
			       p.City, p.Latitude, p.Longitude, a.PercentDamage, a.DamageMessage, p.StreetName, p.StreetNumber
			FROM Property p
			JOIN Assessment a ON a.PropertyID = p.PropertyID AND a.Active = 1
			LEFT JOIN Lookup s ON s.LookupID = p.StreetSuffixID
			WHERE p.Active = 1
			ORDER BY a.DateofInspection DESC, a.LastUpdate DESC
			""";
		return Database.Read(c => c.Query<MapRow>(sql)
			.Where(r => r.AssessmentID is Guid id && includeAssessment(id))
			.GroupBy(r => r.PropertyID)
			.Select(g => g.First())
			.OrderBy(r => r.StreetName, StringComparer.Ordinal).ThenBy(r => r.StreetNumber, StringComparer.Ordinal)
			.Select(r => new MapPointRow(r.PropertyID, r.AssessmentID, r.Address, r.City, r.Latitude, r.Longitude,
				r.PercentDamage is decimal pd ? JetScaling.WithScale(pd, 1) : null, r.DamageMessage))
			.ToList());
	}

	private sealed class MapRow
	{
		public Guid PropertyID { get; set; }
		public Guid? AssessmentID { get; set; }
		public string Address { get; set; }
		public string City { get; set; }
		public decimal? Latitude { get; set; }
		public decimal? Longitude { get; set; }
		public decimal? PercentDamage { get; set; }
		public string DamageMessage { get; set; }
		public string StreetName { get; set; }
		public string StreetNumber { get; set; }
	}
}
