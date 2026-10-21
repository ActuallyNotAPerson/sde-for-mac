using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Records;
using SkiaSharp;

namespace Sde.Reports.Tests;

public class EventSummaryReportTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "sde-event-" + Guid.NewGuid().ToString("N"));

	public EventSummaryReportTests() => Directory.CreateDirectory(_folder);

	public void Dispose() => Directory.Delete(_folder, recursive: true);

	private static AssessmentFile Make(string street, decimal percent, decimal? lat, decimal? lon)
	{
		PropertyRecord p = new PropertyRecord { PropertyID = Guid.NewGuid(), StreetNumber = "1", StreetName = street, NFIPCommunityName = "Sample Town", Latitude = lat, Longitude = lon };
		AssessmentRecord a = new AssessmentRecord
		{
			AssessmentID = Guid.NewGuid(), PropertyID = p.PropertyID, InspectorName = "Ann", PercentDamage = percent,
			DamageMessage = percent >= 50m ? Determinations.SubstantialMessage : Determinations.NotSubstantialMessage,
			ValueOfBuildingID = LookupIds.ValueComputedAcv, CostOfRepairsID = LookupIds.RepairComputedDamages,
			ComputedActualCashValue = 100000m, ComputedDamages = percent * 1000m,
			DateDamageOccurred = new DateTime(2026, 5, 1), DateofInspection = new DateTime(2026, 5, 4),
		};
		return new AssessmentFile { Property = p, Assessment = a };
	}

	private string Render(EventSummary s, EventSummaryOptions o)
	{
		string path = Path.Combine(_folder, "summary.pdf");
		EventSummaryReport.RenderToFile(s, o, path);
		return path;
	}

	[Fact]
	public void OnePage_WithHeadlineClassTableAndStreets()
	{
		List<AssessmentFile> files = new()
		{
			Make("Oak Street", 75m, 40.000m, -90.000m), Make("Oak Street", 45m, 40.002m, -90.001m), Make("Elm Street", 10m, 40.004m, -90.003m),
		};
		EventSummary s = EventSummary.Build(files, files.Select(f => f.Property).Append(new PropertyRecord { PropertyID = Guid.NewGuid() }), "Spring flood");
		string pdf = Render(s, new EventSummaryOptions { Title = "Spring Flood Summary", CommunityName = "Sample Town", GeneratedOn = new DateTime(2026, 6, 1) });

		Assert.Equal(1, Poppler.Pages(pdf));
		string text = Poppler.Text(pdf);
		Assert.Contains("Spring Flood Summary", text);
		Assert.Contains("Sample Town", text);
		Assert.Contains("Damage occurred May 1, 2026", text);
		Assert.Contains("3 assessed, 1 not yet", text);
		Assert.Contains("Substantially damaged", text);
		Assert.Contains("Close", text);
		Assert.Contains("Not substantial", text);
		Assert.Contains("Most affected streets", text);
		Assert.Contains("Oak Street", text);
		Assert.Contains("$300,000", text);
		Assert.Contains("$130,000", text);
		Assert.Contains("Where the structures are", text);
		Assert.NotEmpty(Poppler.Images(pdf));
	}

	[Fact]
	public void NoCoordinates_SkipsThePlot()
	{
		EventSummary s = EventSummary.Build(new[] { Make("Oak Street", 75m, null, null) });
		string pdf = Render(s, null);
		string text = Poppler.Text(pdf);
		Assert.DoesNotContain("Where the structures are", text);
		Assert.Empty(Poppler.Images(pdf));
		Assert.Contains("Event Summary", text);
	}

	[Fact]
	public void MapImage_IsUsedInsteadOfTheDotPlot()
	{
		string map = Path.Combine(_folder, "map.png");
		using (SKBitmap bmp = new SKBitmap(400, 200))
		{
			bmp.Erase(SKColors.LightBlue);
			using SKImage image = SKImage.FromBitmap(bmp);
			using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
			File.WriteAllBytes(map, data.ToArray());
		}
		EventSummary s = EventSummary.Build(new[] { Make("Oak Street", 75m, 40m, -90m) });
		string pdf = Render(s, new EventSummaryOptions { MapImagePath = map });
		Assert.DoesNotContain("Where the structures are", Poppler.Text(pdf));
		Assert.Single(Poppler.Images(pdf));
	}

	[Fact]
	public void ManyStreets_StillOnePage_AndSaysHowManyAreHidden()
	{
		List<AssessmentFile> files = Enumerable.Range(0, 30).Select(i => Make("Street " + i, 60m, 40m + i * 0.001m, -90m)).ToList();
		string pdf = Render(EventSummary.Build(files), new EventSummaryOptions());
		Assert.Equal(1, Poppler.Pages(pdf));
		Assert.Contains("22 more street(s) not shown", Poppler.Text(pdf));
	}

	[Fact]
	public void DotPlot_IsAPng_AndNullWithoutPoints()
	{
		Assert.Null(EventSummaryReport.DotPlot(new List<EventPoint>(), 400, 300));
		byte[] png = EventSummaryReport.DotPlot(new[] { new EventPoint(40m, -90m, DeterminationClass.Substantial) }, 400, 300);
		using SKBitmap bmp = SKBitmap.Decode(png);
		Assert.Equal(400, bmp.Width);
		Assert.Equal(300, bmp.Height);
	}
}
