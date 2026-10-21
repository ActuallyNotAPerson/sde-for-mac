using System.Data;
using System.Globalization;
using Dapper;
using Sde.Core.Improvements;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// Substantial improvement (SI): a structure's improvement projects (permits) and the determinations made from them.
/// Mac-only tables (ImprovementProject, SIDetermination) that never reach FEMA's export. Nothing here touches an
/// assessment: SI is a separate finding and never changes PercentDamage or DamageMessage.
/// </summary>
public sealed partial class SdeStore
{
	/// <summary>The MacSetting key of the community's SI rules (<see cref="SiSettings"/>).</summary>
	public const string SiSettingsKey = "SI.Settings";

	private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

	/// <summary>This database's SI rules (threshold, look-back, prior projects); the NFIP minimum when none are saved.</summary>
	public SiSettings GetSiSettings() => GetSetting<SiSettings>(SiSettingsKey) ?? new SiSettings();

	public void SetSiSettings(SiSettings settings) => SetSetting(SiSettingsKey, settings);

	/// <summary>A structure's improvement projects, newest first (projects with no date last).</summary>
	public List<ImprovementProjectRecord> GetImprovementProjects(Guid propertyId) =>
		Database.Read(c => TableMap<ImprovementProjectRecord>.Select(c, "PropertyID = @propertyId", new { propertyId },
			orderBy: "ProjectDate IS NULL, ProjectDate DESC, CreatedOn DESC").ToList());

	/// <summary>
	/// Adds a project (ProjectID = Guid.Empty gets a new ID and CreatedOn) or saves changes to one, and records it in
	/// the structure's history: "Improvement project added" for a new one, what changed for an existing one.
	/// </summary>
	public ImprovementProjectRecord SaveImprovementProject(ImprovementProjectRecord project, SaveContext context = null)
	{
		ArgumentNullException.ThrowIfNull(project);
		if (project.PropertyID == Guid.Empty)
		{
			throw new ArgumentException("An improvement project needs a structure.", nameof(project));
		}
		SaveContext ctx = context ?? SaveContext.App;
		Database.InTransaction((c, tx) =>
		{
			ImprovementProjectRecord before = project.ProjectID == Guid.Empty ? null : TableMap<ImprovementProjectRecord>.Get(c, new { project.ProjectID }, tx);
			if (project.ProjectID == Guid.Empty)
			{
				project.ProjectID = Guid.NewGuid();
			}
			project.CreatedOn ??= before?.CreatedOn ?? Clock();
			if (project.Included)
			{
				project.ExclusionReason = null;
			}
			TableMap<ImprovementProjectRecord>.Upsert(c, project, tx);
			if (before == null)
			{
				WriteEvent(c, tx, project.PropertyID, null, "Improvement project added: " + Describe(project), ctx);
				return;
			}
			string changes = ProjectChanges(before, project);
			if (changes.Length > 0)
			{
				WriteEvent(c, tx, project.PropertyID, null, $"Improvement project changed: {Describe(project)} ({changes})", ctx);
			}
		});
		return project;
	}

	/// <summary>Deletes a project for good (to leave it out of the SI total but keep it on record, exclude it instead).</summary>
	public void DeleteImprovementProject(Guid projectId, SaveContext context = null) => Database.InTransaction((c, tx) =>
	{
		ImprovementProjectRecord p = TableMap<ImprovementProjectRecord>.Get(c, new { ProjectID = projectId }, tx);
		if (p == null)
		{
			return;
		}
		TableMap<ImprovementProjectRecord>.Delete(c, "ProjectID = @projectId", new { projectId }, tx);
		WriteEvent(c, tx, p.PropertyID, null, "Improvement project deleted: " + Describe(p), context ?? SaveContext.App);
	});

	/// <summary>Records an SI determination (a new row each time; earlier ones are kept) and adds it to the structure's history.</summary>
	public SIDeterminationRecord SaveSiDetermination(SIDeterminationRecord determination, SaveContext context = null)
	{
		ArgumentNullException.ThrowIfNull(determination);
		if (determination.PropertyID == Guid.Empty)
		{
			throw new ArgumentException("A determination needs a structure.", nameof(determination));
		}
		SaveContext ctx = context ?? SaveContext.App;
		if (determination.DeterminationID == Guid.Empty)
		{
			determination.DeterminationID = Guid.NewGuid();
		}
		if (determination.DeterminedOn == default)
		{
			determination.DeterminedOn = Clock();
		}
		determination.DeterminedBy ??= ctx.User ?? UserName;
		Database.InTransaction((c, tx) =>
		{
			TableMap<SIDeterminationRecord>.Insert(c, determination, tx);
			string pct = determination.Percent is decimal p ? $" ({p.ToString("0.0", UsCulture)}%)" : "";
			WriteEvent(c, tx, determination.PropertyID, null, "SI determination: " + (determination.Result ?? "recorded") + pct, ctx);
		});
		return determination;
	}

	/// <summary>A structure's SI determinations, newest first.</summary>
	public List<SIDeterminationRecord> GetSiDeterminations(Guid propertyId) =>
		Database.Read(c => TableMap<SIDeterminationRecord>.Select(c, "PropertyID = @propertyId", new { propertyId }, orderBy: "DeterminedOn DESC").ToList());

	/// <summary>"BP-0002 Second-story addition", or whichever of the two the project has.</summary>
	private static string Describe(ImprovementProjectRecord p)
	{
		string text = string.Join(" ", new[] { p.PermitNumber, p.Description }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
		return text.Length == 0 ? "(no permit or description)" : text;
	}

	/// <summary>What changed between two copies of a project, in a few words ("cost $1.00 to $2.00; excluded").</summary>
	private static string ProjectChanges(ImprovementProjectRecord a, ImprovementProjectRecord b)
	{
		List<string> parts = new();
		void Text(string label, string x, string y)
		{
			if (!string.Equals((x ?? "").Trim(), (y ?? "").Trim(), StringComparison.Ordinal))
			{
				parts.Add($"{label} {Show(x)} to {Show(y)}");
			}
		}
		Text("permit", a.PermitNumber, b.PermitNumber);
		Text("description", a.Description, b.Description);
		Text("date", a.ProjectDate?.ToString("MM/dd/yyyy", UsCulture), b.ProjectDate?.ToString("MM/dd/yyyy", UsCulture));
		Text("cost", a.Cost?.ToString("C2", UsCulture), b.Cost?.ToString("C2", UsCulture));
		Text("cost basis", a.CostBasis, b.CostBasis);
		if (a.Included != b.Included)
		{
			parts.Add(b.Included ? "included" : "excluded" + (string.IsNullOrWhiteSpace(b.ExclusionReason) ? "" : ": " + b.ExclusionReason.Trim()));
		}
		else if (!b.Included)
		{
			Text("exclusion reason", a.ExclusionReason, b.ExclusionReason);
		}
		return string.Join("; ", parts);
	}

	private static string Show(string v) => string.IsNullOrWhiteSpace(v) ? "(blank)" : v.Trim();
}
