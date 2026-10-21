using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Tests.Data;

public class StoreTests
{
	[Fact]
	public void RanchFlood_CalculatesHandCheckedResult()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		AssessmentRecord a = t.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment;
		// 118.75 × 1450 × 0.93 = 160,134.375; + garage 240 × 32.50 + shed 2,500 = 170,434.375
		Assert.Equal(170434.38m, a.TotalReplacementCost);
		Assert.Equal(10300m, a.TotalAdjustments);
		Assert.Equal(24.2m, a.DepreciationValue);
		Assert.Equal(129189.26m, a.ComputedActualCashValue);   // 170,434.375 × 0.758
		Assert.Equal(96900.46m, a.ComputedDamages);
		Assert.Equal(75.0m, a.PercentDamage);
		Assert.Equal("Substantially Damaged", a.DamageMessage);
	}

	/// <summary>
	/// Regression: SQLite NUMERIC columns stored 75.0 as INTEGER and 49.9 as REAL, and Dapper (which compiles one
	/// conversion per query) crashed reading a list that mixed them. Lists must read any mix of whole/fractional values.
	/// </summary>
	[Fact]
	public void Lists_ReadMixedWholeAndFractionalNumbers()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile whole = SampleCases.RanchFlood(t.Store);                 // 75.0 %
		AssessmentFile fractional = SampleCases.RanchFlood(t.Store);
		fractional.ElementPercentages[3].UserDamage = 33.3m;
		fractional.Assessment.BaseCost = 101.37m;
		t.Store.SaveAssessment(fractional);
		AssessmentFile third = SampleCases.RanchFlood(t.Store);
		third.Property.SquareFootage = 1000m;
		t.Store.SaveAssessment(third);

		Assert.Equal(3, t.Store.ListStructures().Count);
		Assert.Equal(3, t.Store.LoadAllAssessments().Count);
		Assert.Equal(3, new Sde.Data.ExchangeService(t.Store).ReadDataset().Assessments.Count);
		Assert.Contains(t.Store.ListStructures(), s => s.PercentDamage == 75.0m);
		Assert.Contains(t.Store.ListStructures(), s => s.PercentDamage != 75.0m);
	}

	[Fact]
	public void SaveAndLoad_PreservesEverything()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile saved = SampleCases.RanchFlood(t.Store);
		AssessmentFile loaded = t.Store.LoadAssessment(saved.Assessment.AssessmentID);
		AssertSameRecord(saved.Property, loaded.Property, "LastUpdate", "ImportDate");
		AssertSameRecord(saved.Assessment, loaded.Assessment, "LastUpdate");
		Assert.Equal(12, loaded.ElementPercentages.Count);
		Assert.Equal(7, loaded.AdditionalAdjustments.Count);
		foreach (ElementPercentageRecord e in saved.ElementPercentages)
		{
			AssertSameRecord(e, loaded.ElementPercentages.Single(x => x.ElementID == e.ElementID));
		}
	}

	[Fact]
	public void ManufacturedHome_ShowsSkirtingInsteadOfFoundation()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1, ResidenceTypeID = 94, StreetName = "Lot 4" });
		AssessmentFile f = t.Store.CreateAssessment(p.PropertyID, false);
		Assert.Contains(f.ElementPercentages, e => e.CalcID == 16 && e.ElementName == "Skirting / Forms Piers");
		Assert.DoesNotContain(f.ElementPercentages, e => e.CalcID == 4);
	}

	[Fact]
	public void NonResidential_HasSevenElements()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 2, StoryTypeID = 101, StructureUseID = 106 });
		AssessmentFile f = t.Store.CreateAssessment(p.PropertyID, false);
		Assert.Equal(new[] { "Foundation", "Superstructure", "Roof Covering", "Plumbing", "Electrical", "HVAC", "Interiors" },
			f.ElementPercentages.Select(e => e.ElementName));
	}

	[Fact]
	public void DeleteProperty_RemovesEverything()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		string photo = Path.Combine(t.Folder, "photo.jpg");
		File.WriteAllBytes(photo, new byte[] { 1, 2, 3 });
		AttachmentRecord att = t.Store.ImportPhoto(f.Assessment.AssessmentID, photo, "front");
		f.Attachments.Add(att);
		t.Store.SaveAssessment(f);
		Assert.True(File.Exists(t.Store.PhotoPath(att)));

		t.Store.DeleteProperty(f.Property.PropertyID);
		Assert.Null(t.Store.LoadAssessment(f.Assessment.AssessmentID));
		Assert.Null(t.Store.GetProperty(f.Property.PropertyID));
		Assert.False(File.Exists(t.Store.PhotoPath(att)));
		Assert.Equal((0, 0), t.Store.Counts());
	}

	[Fact]
	public void Shapes_SetSquareFootage()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1 });
		decimal total = t.Store.SaveShapes(p.PropertyID, new[]
		{
			new ShapeRecord { ShapeType = "r", LengthA = 40, WidthA = 30, StoriesA = 1, TotalSqFt = 1200 },
			new ShapeRecord { ShapeType = "r", LengthA = 10, WidthA = 12, StoriesA = 2, TotalSqFt = 240 },
		});
		Assert.Equal(1440m, total);
		Assert.Equal(1440m, t.Store.GetProperty(p.PropertyID).SquareFootage);
		Assert.Equal(2, t.Store.GetShapes(p.PropertyID).Count);
	}

	internal static void AssertSameRecord<T>(T expected, T actual, params string[] ignore)
	{
		foreach (var prop in typeof(T).GetProperties().Where(p => !ignore.Contains(p.Name)))
		{
			object e = prop.GetValue(expected);
			object a = prop.GetValue(actual);
			Assert.True(Equals(e, a), $"{typeof(T).Name}.{prop.Name}: expected {e ?? "null"}, got {a ?? "null"}");
		}
	}
}
