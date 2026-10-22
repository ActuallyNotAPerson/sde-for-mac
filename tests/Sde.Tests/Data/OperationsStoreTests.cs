using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>The v2.5 stores behind field operations and office work: teams and assignments, review comments, compliance cases, photo markup, documents.</summary>
public class OperationsStoreTests
{
	[Fact]
	public void Assigning_GivesEachStructureOneTeamPerEvent_WithAWorksheetCode()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		Guid pid = f.Property.PropertyID;
		TeamRecord one = t.Store.SaveTeam(new TeamRecord { Name = "Team 1" });
		TeamRecord two = t.Store.SaveTeam(new TeamRecord { Name = "Team 2" });
		Assert.Throws<ArgumentException>(() => t.Store.SaveTeam(new TeamRecord { Name = "team 1" }));

		AssignmentRecord a = Assert.Single(t.Store.AssignToTeam(new[] { pid }, one.TeamID));
		Assert.Matches("^[2-9A-HJKMNP-Z]{6}$", a.ShortCode);
		Assert.Equal(a.AssignmentID, t.Store.FindAssignmentByCode(" " + a.ShortCode.ToLowerInvariant().Insert(3, " ")).AssignmentID);

		// Moving it to another team keeps the code and the single assignment.
		AssignmentRecord moved = Assert.Single(t.Store.AssignToTeam(new[] { pid }, two.TeamID));
		Assert.Equal(a.AssignmentID, moved.AssignmentID);
		Assert.Equal(two.TeamID, Assert.Single(t.Store.GetAssignments()).TeamID);
		Assert.Contains(t.Store.GetPropertyHistory(pid), h => h.Label == "Assigned to Team 2");
		// The structure's own rows are untouched unless FEMA's team name is asked for.
		Assert.Null(t.Store.GetProperty(pid).TeamName);
		t.Store.AssignToTeam(new[] { pid }, one.TeamID, alsoSetFemaTeamName: true);
		Assert.Equal("Team 1", t.Store.GetProperty(pid).TeamName);

		Assert.Equal(1, t.Store.Unassign(new[] { pid }));
		Assert.Empty(t.Store.GetAssignments());
	}

	[Fact]
	public void TeamProgress_CountsStructuresAssessedSinceTheyWereAssigned()
	{
		using TempDatabase t = new TempDatabase();
		t.Store.Clock = () => new DateTime(2026, 9, 20, 9, 0, 0);
		AssessmentFile visited = SampleCases.RanchFlood(t.Store);
		visited.Assessment.DateofInspection = new DateTime(2026, 9, 21);
		t.Store.SaveAssessment(visited);
		PropertyRecord waiting = t.Store.SaveProperty(new PropertyRecord { StreetNumber = "9", StreetName = "Elm", StructureTypeID = 1 });
		TeamRecord team = t.Store.SaveTeam(new TeamRecord { Name = "North" });
		List<AssignmentRecord> assigned = t.Store.AssignToTeam(new[] { visited.Property.PropertyID, waiting.PropertyID }, team.TeamID);
		t.Store.SetAssignmentStatus(assigned[1].AssignmentID, AssignmentStatuses.Skipped);
		t.Store.SaveRouteOrder(new[] { assigned[1].AssignmentID, assigned[0].AssignmentID });

		TeamProgress p = Assert.Single(t.Store.GetTeamProgress());
		Assert.Equal((2, 1, 0, 1), (p.Assigned, p.Assessed, p.Done, p.Skipped));
		Assert.Equal(assigned[1].AssignmentID, t.Store.GetAssignments(team.TeamID).First().AssignmentID);
	}

	[Fact]
	public void ReviewComments_ThreadAndResolve_WithoutChangingTheAssessment()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		Guid id = f.Assessment.AssessmentID;
		t.Store.SetStatus(id, AssessmentStatus.Final);
		VersionStamp stamp = t.Store.GetStamp(id);

		ReviewCommentRecord question = t.Store.AddReviewComment(id, "Is the depth right?", "Assessment.DepthOfFlood");
		t.Store.AddReviewComment(id, "Checked against the HWM: yes.", parentId: question.CommentID, context: new SaveContext { User = "Assistant (AI)", Via = SaveVia.AiAssistant });
		Assert.Equal(1, t.Store.OpenCommentCounts()[id]);
		Assert.Throws<ArgumentException>(() => t.Store.AddReviewComment(id, "x", "No.Such.Field"));
		Assert.Throws<ArgumentException>(() => t.Store.AddReviewComment(id, "  "));

		t.Store.ResolveReviewComment(question.CommentID);
		Assert.All(t.Store.GetReviewComments(id), c => Assert.True(c.IsResolved));
		Assert.False(t.Store.OpenCommentCounts().ContainsKey(id));
		Assert.Equal(SaveVia.AiAssistant, t.Store.GetReviewComments(id)[1].Via);
		Assert.Equal(stamp, t.Store.GetStamp(id));
	}

	[Fact]
	public void AComplianceCase_OpensOnlyForSubstantialDamage_AndKeepsATimeline()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		ComplianceCaseRecord complianceCase = t.Store.OpenComplianceCase(f.Assessment.AssessmentID);
		Assert.Equal("SD", complianceCase.Kind);
		Assert.Equal(complianceCase.CaseID, t.Store.OpenComplianceCase(f.Assessment.AssessmentID).CaseID);

		complianceCase.Path = CompliancePaths.Elevate;
		complianceCase.Deadline = new DateTime(2027, 3, 1);
		complianceCase.IccStatus = IccStatuses.Filed;
		t.Store.SaveComplianceCase(complianceCase);
		complianceCase.Path = CompliancePaths.Floodproof;
		Assert.Throws<ArgumentException>(() => t.Store.SaveComplianceCase(complianceCase));   // the ranch is residential
		complianceCase.Path = CompliancePaths.Elevate;
		Assert.True(t.Store.GetComplianceCase(complianceCase.CaseID).IsOverdue(new DateTime(2027, 3, 2)));
		complianceCase.Status = ComplianceStatuses.Closed;
		complianceCase.ClosedReason = "Elevated and inspected";
		t.Store.SaveComplianceCase(complianceCase);

		List<string> timeline = t.Store.GetComplianceEntries(complianceCase.CaseID).Select(e => e.Text).ToList();
		Assert.Contains("Path: Elevate", timeline);
		Assert.Contains("ICC claim: Filed", timeline);
		Assert.Contains("Case closed: Elevated and inspected", timeline);
		Assert.Empty(t.Store.GetComplianceCases(openOnly: true));

		AssessmentFile low = t.Store.CreateAssessment(f.Property.PropertyID, applyDefaults: false);
		Assert.Throws<InvalidOperationException>(() => t.Store.OpenComplianceCase(low.Assessment.AssessmentID));
	}

	[Fact]
	public void PhotoMarkup_IsKeptBesideThePhoto_AndLockedWithFinalAssessments()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		f.Attachments.Add(new AttachmentRecord { FileName = "front.jpg", Active = true, AttachmentType = "Photo" });
		t.Store.SaveAssessment(f);
		ExchangeService x = new ExchangeService(t.Store);
		(string before, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "a"));

		t.Store.SavePhotoMarkup("front.jpg", "[{\"kind\":\"waterLine\",\"y\":0.4}]");
		Assert.Contains("waterLine", t.Store.GetPhotoMarkup("front.jpg"));
		Assert.Single(t.Store.GetPhotoMarkups(f.Assessment.AssessmentID));
		(string after, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "b"));
		Assert.Equal(File.ReadAllBytes(Path.Combine(before, "Export.json")), File.ReadAllBytes(Path.Combine(after, "Export.json")));

		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		Assert.Throws<AssessmentLockedException>(() => t.Store.SavePhotoMarkup("front.jpg", null));
		Assert.NotNull(t.Store.GetPhotoMarkup("front.jpg"));
	}

	[Fact]
	public void Documents_AreCopiedIntoTheDocumentsFolder_AndRecorded()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		string source = Path.Combine(t.Folder, "EC 123 River.pdf");
		File.WriteAllText(source, "%PDF-1.4 test");

		DocumentRecord d = t.Store.AddDocument(f.Property.PropertyID, source, DocumentKinds.ElevationCertificate, fieldsJson: "{\"C2a\":\"712.4\"}");
		Assert.Equal("EC 123 River", d.Title);
		Assert.True(File.Exists(t.Store.DocumentPath(d)));
		Assert.Equal(t.Database.DocumentsFolder, Path.GetDirectoryName(t.Store.DocumentPath(d)));
		Assert.Single(t.Store.GetDocuments(f.Property.PropertyID));
		Assert.Contains(t.Store.GetPropertyHistory(f.Property.PropertyID), h => h.Label == "Document added: EC 123 River");

		t.Store.RemoveDocument(d.DocumentID);
		Assert.Empty(t.Store.GetDocuments(f.Property.PropertyID));
		Assert.False(File.Exists(t.Store.DocumentPath(d)));
	}
}
