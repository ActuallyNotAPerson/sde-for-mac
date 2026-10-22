using System.Text;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Reports.Tests;

/// <summary>The field kit (C5): a route sheet and one pre-filled worksheet per structure, with its code as a QR code.</summary>
public class FieldKitTests : IDisposable
{
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	[Fact]
	public void TheRouteSheetAndWorksheets_FollowTheRoute_WithCodesAndDirections()
	{
		AssessmentFile first = SampleCases.RanchFlood(_db.Store);
		_db.Store.Apply(first.Assessment.AssessmentID, f =>
		{
			f.Property.Latitude = 39.7589m;
			f.Property.Longitude = -84.1916m;
		});
		AssessmentFile second = SampleCases.RanchFlood(_db.Store);
		_db.Store.Apply(second.Assessment.AssessmentID, f => f.Property.StreetName = "Second");
		TeamRecord team = _db.Store.SaveTeam(new TeamRecord { Name = "Team 1", Members = "A. Inspector" });
		List<AssignmentRecord> assigned = _db.Store.AssignToTeam(new[] { first.Property.PropertyID, second.Property.PropertyID }, team.TeamID);
		// The route visits the second structure first.
		_db.Store.SaveRouteOrder(assigned.OrderByDescending(a => a.PropertyID == second.Property.PropertyID).Select(a => a.AssignmentID).ToList());

		List<FieldKitStop> stops = FieldKit.Stops(_db.Store, team.TeamID);
		Assert.Equal(new[] { second.Property.PropertyID, first.Property.PropertyID }, stops.Select(s => s.Property.PropertyID));
		Assert.Equal(new[] { 1, 2 }, stops.Select(s => s.Number));
		Assert.NotNull(stops[1].Latest);
		// Without coordinates the link goes to the address instead.
		Assert.StartsWith("https://maps.apple.com/?daddr=123%20Second", FieldKit.MapsLink(stops[0].Property, _db.Store.Reference));
		Assert.Equal("https://maps.apple.com/?daddr=39.7589,-84.1916", FieldKit.MapsLink(stops[1].Property, _db.Store.Reference));

		string route = Path.Combine(_db.Folder, "kit", "route.pdf");
		FieldKit.WriteRouteSheet(team, stops, _db.Store.Reference, new FieldKitOptions { CommunityName = "Sample Community" }, route);
		string text = Poppler.Text(route);
		Assert.Contains("Route sheet: Team 1", text);
		Assert.Contains("Second", text);
		Assert.Contains(stops[0].Assignment.ShortCode, text);
		Assert.Contains("Open in Maps", text);
		Assert.True(text.IndexOf("Second", StringComparison.Ordinal) < text.IndexOf(stops[1].Assignment.ShortCode, StringComparison.Ordinal));
		Assert.Contains("maps.apple.com", Encoding.Latin1.GetString(File.ReadAllBytes(route)));

		string sheets = Path.Combine(_db.Folder, "kit", "worksheets.pdf");
		int maps = 0;
		FieldKit.WriteWorksheets(team, stops, _db.Store.Reference, new FieldKitOptions { MapSnippet = s => { maps++; return FieldKit.QrPng("map stand-in"); } }, sheets);
		Assert.Equal(2, Poppler.Pages(sheets));
		Assert.Equal(2, maps);
		Assert.Contains("Code " + stops[0].Assignment.ShortCode, Poppler.Text(sheets, 1));
		Assert.Contains("Depth of flood", Poppler.Text(sheets, 2));
		Assert.Equal(2, Poppler.Images(sheets).Count(i => i.Page == 1));   // the QR code and the map

		Assert.Throws<InvalidOperationException>(() => FieldKit.WriteWorksheets(team, new List<FieldKitStop>(), _db.Store.Reference, null, sheets));
	}
}
