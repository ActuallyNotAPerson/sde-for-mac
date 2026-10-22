using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Quality;
using Sde.Data;
using Sde.Reports;

namespace Sde.Mcp;

/// <summary>The read tools for review: SDE's quality checks, review comments and compliance cases.</summary>
public sealed partial class ReadTools
{
	/// <summary>The most findings one page of qa_check returns (each carries a message, an address and IDs).</summary>
	public const int MaxFindingPage = 50;

	/// <summary>The most assessments one qa_check call checks when they are named one by one.</summary>
	public const int MaxQaAssessments = 1000;

	/// <summary>The most cases one page of list_compliance_cases returns (each carries some 25 values).</summary>
	public const int MaxCasePage = 50;

	[McpServerTool(Name = "qa_check", Title = "Run the quality checks", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Runs SDE's quality checks (the Review page's checks) over chosen assessments, one event, or the whole database, and lists what they find, grouped by check with its title and severity (errors first), a page at a time: likely duplicates, missing locations, photos or dates, unusual values per square foot, flood elevations unlike the neighbours', percentages close to the threshold, unaccepted depth-table suggestions and more. Findings someone set aside in SDE are left out unless includeDismissed is true. Nothing is changed." + Untrusted)]
	public QaCheckResult QaCheck(
		[Description("Check only these assessments (find_structures lists their IDs). Duplicates are then looked for among them only.")] string[] assessmentIds = null,
		[Description("Check only this event's assessments: its name or ID (list_events lists them). Leave out both this and assessmentIds for the whole database.")] string @event = null,
		[Description("Only this check: its ID or title, such as \"MissingPhotos\" or \"No photos\".")] string rule = null,
		[Description("Also list the findings someone set aside in SDE, with their reason.")] bool includeDismissed = false,
		[Description("The page to return, starting at 1.")] int page = 1,
		[Description("Findings per page, 1 to 50 (default 25).")] int pageSize = 25)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		QaRuleInfo onlyRule = null;
		if (!string.IsNullOrWhiteSpace(rule))
		{
			onlyRule = QaRules.Rule(rule.Trim())
				?? QaRules.Catalog.FirstOrDefault(r => string.Equals(r.Id, rule.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(r.Title, rule.Trim(), StringComparison.OrdinalIgnoreCase))
				?? throw new McpException("There's no check called \"" + Describe.Shorten(rule, 80) + "\". The checks are: " + string.Join(", ", QaRules.Catalog.Select(r => $"{r.Id} ({r.Title})")) + ".");
		}
		EventRecord e = Lookups.OptionalEvent(store, @event);
		List<Guid> ids;
		string scope;
		if (assessmentIds != null && assessmentIds.Length > 0)
		{
			(ids, _, scope) = Lookups.Choose(store, assessmentIds, e, MaxQaAssessments);
		}
		else if (e != null)
		{
			// Every assessment in the event, not only the latest of each structure: the checks compare them all.
			HashSet<Guid> active = Lookups.ActiveIds(store);
			ids = store.AssessmentsInEvent(e.EventID).Where(active.Contains).ToList();
			scope = $"the event {e.Label}";
		}
		else
		{
			ids = null;
			scope = "the whole database";
		}
		pageSize = Math.Clamp(pageSize, 1, MaxFindingPage);
		page = Math.Max(page, 1);

		QaInput input = store.LoadQaInput(ids);
		List<QaFinding> all = QaRules.Check(input).Where(f => onlyRule == null || f.RuleId == onlyRule.Id).ToList();
		int dismissed = all.Count(f => f.IsDismissed);
		List<QaFinding> listed = includeDismissed ? all : all.Where(f => !f.IsDismissed).ToList();
		List<QaRuleCount> rules = all.GroupBy(f => f.RuleId).Select(g =>
		{
			QaRuleInfo r = QaRules.Rule(g.Key);
			return new QaRuleCount(g.Key, r?.Title ?? g.Key, (r?.Severity ?? g.First().Severity).ToString(), g.Count(f => !f.IsDismissed), g.Count(f => f.IsDismissed));
		}).ToList();

		int pages = Math.Max(1, (listed.Count + pageSize - 1) / pageSize);
		Dictionary<Guid, AssessmentFile> byAssessment = input.Assessments.ToDictionary(f => f.Assessment.AssessmentID);
		Dictionary<Guid, AssessmentFile> byProperty = input.Assessments.GroupBy(f => f.Property.PropertyID).ToDictionary(g => g.Key, g => g.First());
		ReferenceData reference = store.Reference;
		List<QaGroup> groups = listed.Skip((page - 1) * pageSize).Take(pageSize).GroupBy(f => f.RuleId).Select(g =>
		{
			QaRuleInfo r = QaRules.Rule(g.Key);
			return new QaGroup(g.Key, r?.Title ?? g.Key, (r?.Severity ?? g.First().Severity).ToString(), r?.Description, r?.CanDismiss ?? false,
				g.Select(f => new QaFindingItem(f.AssessmentId?.ToString(), f.PropertyId?.ToString(), About(f, byAssessment, byProperty, input, reference),
					Describe.Shorten(f.Message), f.FieldKey, string.IsNullOrEmpty(f.Detail) ? null : Describe.Shorten(f.Detail, 120), f.IsDismissed,
					Describe.Shorten(f.Dismissal?.Reason), f.Dismissal?.DismissedBy, Describe.Date(f.Dismissal?.DismissedOn))).ToList());
		}).ToList();
		string next = page < pages ? $"Showing page {page} of {pages}. For more, call qa_check again with page = {page + 1} and the same arguments." : null;
		string note = "The flood map zone and high-water mark checks need a flood map lookup or a marks file, so they run only in SDE's Review page.";
		if (dismissed > 0 && !includeDismissed)
		{
			note += $" {dismissed} finding{(dismissed == 1 ? " was" : "s were")} set aside in SDE and {(dismissed == 1 ? "isn't" : "aren't")} listed (includeDismissed lists them).";
		}
		return new QaCheckResult(sde.DatabasePath, scope, input.Assessments.Count, listed.Count, dismissed, includeDismissed, page, pageSize, pages, rules, groups, next, note);
	}

	/// <summary>What a finding is about: the structure's address, or the event's name.</summary>
	private static string About(QaFinding f, Dictionary<Guid, AssessmentFile> byAssessment, Dictionary<Guid, AssessmentFile> byProperty, QaInput input, ReferenceData reference)
	{
		if (f.AssessmentId is Guid a && byAssessment.TryGetValue(a, out AssessmentFile file))
		{
			return Describe.Shorten(QaRules.Address(file.Property, reference), 120);
		}
		if (f.PropertyId is Guid p && byProperty.TryGetValue(p, out AssessmentFile owner))
		{
			return Describe.Shorten(QaRules.Address(owner.Property, reference), 120);
		}
		return input.Events != null && input.Events.TryGetValue(f.ObjectId, out QaEvent e) ? "Event " + Describe.Shorten(e.Name, 120) : null;
	}

	[McpServerTool(Name = "get_review_comments", Title = "Read an assessment's review comments", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Lists the review comments on one assessment (the Review page's threads), oldest first, each with its replies, who wrote it and whether it is resolved. Give a comment's ID to add_review_comment as replyTo to answer it." + Untrusted)]
	public ReviewCommentList GetReviewComments([Description("The assessment's ID (find_structures lists them).")] string assessmentId)
	{
		AssessmentFile file = sde.LoadAssessment(sde.Reader, assessmentId);
		Guid id = file.Assessment.AssessmentID;
		List<ReviewCommentRecord> all = sde.Reader.GetReviewComments(id);
		ILookup<Guid?, ReviewCommentRecord> replies = all.Where(c => c.ParentID != null).ToLookup(c => c.ParentID);
		List<ReviewThreadItem> threads = all.Where(c => c.ParentID == null || all.All(p => p.CommentID != c.ParentID)).Select(c => new ReviewThreadItem(
			c.CommentID.ToString(), c.FieldKey, c.FieldKey == null ? null : FieldCatalog.Get(c.FieldKey)?.Label, Describe.Shorten(c.Body, 2000), c.Author, c.Via,
			Describe.Time(c.CreatedOn), c.IsResolved, c.ResolvedBy,
			replies[c.CommentID].Select(r => new ReviewReplyItem(r.CommentID.ToString(), Describe.Shorten(r.Body, 2000), r.Author, r.Via, Describe.Time(r.CreatedOn))).ToList())).ToList();
		return new ReviewCommentList(sde.DatabasePath, id.ToString(), Describe.Address(file.Property, sde.Reader.Reference), threads.Count(t => !t.Resolved), threads);
	}

	[McpServerTool(Name = "list_compliance_cases", Title = "List compliance cases", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Lists the compliance cases (one per substantial damage or substantial improvement determination) as SDE's Compliance page and report show them: the path the owner takes, the permit and deadline, overdue days, the ICC claim, the elevation certificate, the final inspection and the certificate of compliance. Open cases first, by deadline, a page at a time. Filters combine (all must match)." + Untrusted)]
	public CompliancePage ListComplianceCases(
		[Description("\"open\" or \"closed\".")] string status = null,
		[Description("true for open cases past their deadline only.")] bool overdue = false,
		[Description("Only this path: Elevate, Relocate, Demolish, Floodproof, RepairBelowThreshold, or \"none\" for cases with no path chosen yet.")] string path = null,
		[Description("Only this ICC claim status: NotFiled, Filed, Approved, Paid or Denied.")] string iccStatus = null,
		[Description("Only cases for assessments in this event: its name or ID (list_events lists them).")] string @event = null,
		[Description("The page to return, starting at 1.")] int page = 1,
		[Description("Cases per page, 1 to 50 (default 25).")] int pageSize = 25)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		bool? wantOpen = string.IsNullOrWhiteSpace(status) ? null : Normalize(status) switch
		{
			"open" => true,
			"closed" => false,
			_ => throw new McpException("status must be \"open\" or \"closed\"."),
		};
		string wantPath = null;
		bool wantNoPath = false;
		if (!string.IsNullOrWhiteSpace(path))
		{
			if (Normalize(path) is "none" or "notchosen" or "notchosenyet")
			{
				wantNoPath = true;
			}
			else
			{
				wantPath = CompliancePaths.All.FirstOrDefault(p => Normalize(p) == Normalize(path) || Normalize(CompliancePaths.Label(p)) == Normalize(path))
					?? throw new McpException("path must be one of " + string.Join(", ", CompliancePaths.All) + ", or \"none\".");
			}
		}
		string wantIcc = string.IsNullOrWhiteSpace(iccStatus) ? null
			: IccStatuses.All.FirstOrDefault(s => Normalize(s) == Normalize(iccStatus) || Normalize(IccStatuses.Label(s)) == Normalize(iccStatus))
				?? throw new McpException("iccStatus must be one of " + string.Join(", ", IccStatuses.All) + ".");
		EventRecord e = Lookups.OptionalEvent(store, @event);
		HashSet<Guid> inEvent = e == null ? null : store.AssessmentsInEvent(e.EventID);
		pageSize = Math.Clamp(pageSize, 1, MaxCasePage);
		page = Math.Max(page, 1);

		DateTime today = store.Clock().Date;
		List<ComplianceRow> rows = ComplianceReport.Rows(store, today).Where(r =>
		{
			ComplianceCaseRecord c = r.Case;
			return (wantOpen == null || c.IsOpen == wantOpen)
				&& (!overdue || r.IsOverdue)
				&& (wantPath == null || c.Path == wantPath)
				&& (!wantNoPath || c.Path == null)
				&& (wantIcc == null || (c.IccStatus ?? IccStatuses.NotFiled) == wantIcc)
				&& (inEvent == null || (c.AssessmentID is Guid a && inEvent.Contains(a)));
		}).ToList();
		int pages = Math.Max(1, (rows.Count + pageSize - 1) / pageSize);
		List<ComplianceItem> items = rows.Skip((page - 1) * pageSize).Take(pageSize).Select(r =>
		{
			ComplianceCaseRecord c = r.Case;
			return new ComplianceItem(c.CaseID.ToString(), c.Kind, c.AssessmentID?.ToString(), c.PropertyID.ToString(), Blank(r.Address) ?? "(no address)", Blank(r.City),
				Blank(r.ParcelNumber), Blank(Describe.Shorten(r.Owner, 120)), CompliancePaths.Label(c.Path), Blank(c.PermitNumber), Describe.Date(c.PermitIssuedOn),
				Describe.Date(c.Deadline), r.IsOverdue, r.DaysOverdue, Blank(c.IccClaimNumber), IccStatuses.Label(c.IccStatus), c.IccAmount,
				Describe.Date(c.EcReceivedOn), Describe.Date(c.FinalInspectionOn), Describe.Date(c.ComplianceCertOn), c.Status ?? ComplianceStatuses.Open,
				Describe.Date(c.ClosedOn), Describe.Shorten(Blank(c.ClosedReason)), Describe.Shorten(Blank(c.Notes)));
		}).ToList();
		string next = page < pages ? $"Showing page {page} of {pages}. For more, call list_compliance_cases again with page = {page + 1} and the same filters." : null;
		return new CompliancePage(sde.DatabasePath, rows.Count, rows.Count(r => r.Case.IsOpen), rows.Count(r => r.IsOverdue), rows.Count(r => !r.Case.IsOpen),
			page, pageSize, pages, items, next);
	}
}
