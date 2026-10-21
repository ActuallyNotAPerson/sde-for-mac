using Dapper;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// Reading a populated FEMA SDE database (.mdb). FEMA ships SDEDatabase.mdb empty and there is no Windows SDE to
/// fill one, so tests/fixtures/sde-install/Database/SDEDatabase.mdb is FEMA's empty database with the rows of
/// tests/fixtures/mdb-source/Export.json written into it by Jackcess, the standard Java library for Access files
/// (tools/mdb-fixture). Reading it back through mdbtools must give the source data exactly.
///
/// The source covers every table SDE imports, every column type (Replication ID, Currency, Numeric(p,s), Double,
/// Memo, Boolean, DateTime), and text that is awkward in CSV (quotes, commas, line breaks, accents).
/// Regenerate both with: SDE_WRITE_MDB_FIXTURE=1 dotnet test tests/Sde.Tests --filter MdbFixtureSource,
/// then tools/mdb-fixture/make_fixture.sh.
/// </summary>
public class MdbImportTests
{
	private static string FixtureRoot => Path.Combine(TestPaths.Root, "tests", "fixtures");

	private static string SourceFolder => Path.Combine(FixtureRoot, "mdb-source");

	private static string MdbPath => Path.Combine(FixtureRoot, "sde-install", "Database", "SDEDatabase.mdb");

	/// <summary>Two different 1×1 PNGs.</summary>
	private static readonly byte[] BluePng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
	private static readonly byte[] RedPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

	/// <summary>The data written into the fixture database.</summary>
	internal static void BuildSource(SdeStore store, string scratch)
	{
		List<Guid> customFields = store.Database.Read(c => c.Query<Guid>("SELECT CustomFieldID FROM CustomField ORDER BY zOrder").ToList());

		// 1. A flooded ranch with photos, a shape, custom fields, awkward text, and a re-inspection.
		AssessmentFile ranch = SampleCases.RanchFlood(store);
		PropertyRecord p = ranch.Property;
		p.OwnerLastName = "O'Brien-Peña";
		p.StructureInformation = "Split-level, \"bonus\" room over the garage,\r\nwater line at 26 in.";
		p.Latitude = 39.098712m;
		p.Longitude = -84.507634m;
		p.FirstFloorElevation = 486.5m;
		p.BFE = 489.25m;
		p.FIRMPanelNumber = "39061C0232";
		p.PanelSuffix = "E";
		p.FIRMPanelDate = new DateTime(2010, 9, 17);
		p.Subdivision = "Riverside Addition";
		p.ParcelNumber = "092-0004-0118-00";
		store.SaveProperty(p);
		store.SaveShapes(p.PropertyID, new[]
		{
			new ShapeRecord { ShapeType = "l", PropertyID = p.PropertyID, LengthA = 40, WidthA = 25, StoriesA = 1, LengthB = 18, WidthB = 15, StoriesB = 1, DescriptionA = "Main house", DescriptionB = "Garage wing" },
		});
		store.SaveCustomFieldResults(p.PropertyID, "property", new[] { new CustomFieldResultRecord { CustomFieldID = customFields[0], Result = "Hurricane \"Ida\", 2026", zOrder = 1 } });

		AssessmentRecord a = ranch.Assessment;
		a.DateofInspection = new DateTime(2026, 9, 21, 14, 35, 0);
		a.InspectorPhoneNumber = "(555) 555-0100";
		a.DepthOfFlood = 2.5m;
		a.DepthOfFloodAboveFFE = 4.25m;
		a.CauseOfDamageDescription = "Creek overtopped — water ½ ft above the sill; café sign on the porch.";
		a.MarketAppraisal = 182_500m;
		a.TaxAssessedValue = 96_310m;
		a.FactorAdjustment = 1.43m;
		a.ContractorEstimate = 98_765.43m;
		a.CostData = "RSMeans 2026, Q3";
		a.CostDataDate = new DateTime(2026, 7, 1);
		string front = Path.Combine(scratch, "front.png");
		string kitchen = Path.Combine(scratch, "kitchen.png");
		File.WriteAllBytes(front, BluePng);
		File.WriteAllBytes(kitchen, RedPng);
		AttachmentRecord photo1 = store.ImportPhoto(a.AssessmentID, front, "Front, from the street");
		photo1.DefaultImage = true;
		AttachmentRecord photo2 = store.ImportPhoto(a.AssessmentID, kitchen, "Kitchen \"before\" photo");
		ranch.Attachments.Add(photo1);
		ranch.Attachments.Add(photo2);
		store.Calculator.Recalculate(ranch);
		store.SaveAssessment(ranch);
		store.SaveCustomFieldResults(a.AssessmentID, "assessment", new[] { new CustomFieldResultRecord { CustomFieldID = customFields[1], Result = "Team 2", zOrder = 2 } });

		AssessmentFile reinspection = store.CreateAssessment(p.PropertyID, applyDefaults: false);
		reinspection.Assessment.DateofInspection = new DateTime(2026, 10, 2, 9, 0, 0);
		reinspection.Assessment.BaseCost = 118.75m;
		reinspection.Assessment.GeographicAdjustment = 0.93m;
		store.Calculator.NormalizeRows(reinspection);
		store.Calculator.ApplyDepreciationRating(reinspection, 385);
		foreach (ElementPercentageRecord e in reinspection.ElementPercentages)
		{
			e.UserDamage = e.ElementName is "Interior Finish" or "Floor Finish" ? 35 : 5;
		}
		store.Calculator.Recalculate(reinspection);
		store.SaveAssessment(reinspection);

		// 2. A non-residential building assessed on an appraisal and a contractor's estimate.
		PropertyRecord office = store.SaveProperty(new PropertyRecord
		{
			StreetNumber = "700", StreetName = "Riverside", StreetSuffixID = 172, City = "Exampleville", StateID = 36, ZipCode = "437021234",
			StructureTypeID = 2, StoryTypeID = 102, StructureUseID = 115, SprinklerSystem = 99, Conveyance = 2, StructureQualityID = 54,
			SquareFootage = 12_480.5m, YearOfConstruction = 1987, OwnerFirstName = "Queen City", OwnerLastName = "Properties, LLC",
		});
		AssessmentFile o = store.CreateAssessment(office.PropertyID, applyDefaults: false);
		o.Assessment.DateofInspection = new DateTime(2026, 9, 22);
		o.Assessment.BaseCost = 212.40m;
		o.Assessment.GeographicAdjustment = 0.95m;
		o.Assessment.MarketAppraisal = 1_850_000m;
		o.Assessment.ValueOfBuildingID = 391;
		o.Assessment.ContractorEstimate = 612_345.67m;
		o.Assessment.CostOfRepairsID = 394;
		store.Calculator.NormalizeRows(o);
		store.Calculator.ApplyDepreciationRating(o, 387);
		store.Calculator.Recalculate(o);
		store.SaveAssessment(o);

		// 3. Damage undetermined (resident refused the inspection).
		PropertyRecord refused = store.SaveProperty(new PropertyRecord { StreetNumber = "12", StreetName = "Elm", City = "Exampleville", StateID = 36, StructureTypeID = 1 });
		AssessmentFile r = store.CreateAssessment(refused.PropertyID, applyDefaults: false);
		r.Assessment.PhysicalDamage = true;
		r.Assessment.UndeterminedDamageTypeID = 362;
		store.Calculator.NormalizeRows(r);
		store.Calculator.Recalculate(r);
		store.SaveAssessment(r);

		// 4. A structure with no assessment yet (e.g. from Enterprise Import).
		store.SaveProperty(new PropertyRecord { StreetNumber = "48", StreetName = "Mill Creek", City = "Exampleville", StateID = 36, StructureTypeID = 1, ImportDate = new DateTime(2026, 9, 15, 8, 30, 0) });
	}

	/// <summary>Writes tests/fixtures/mdb-source (Export.json + photos). Run with SDE_WRITE_MDB_FIXTURE=1.</summary>
	[Fact]
	public void MdbFixtureSource()
	{
		if (Environment.GetEnvironmentVariable("SDE_WRITE_MDB_FIXTURE") != "1")
		{
			return;
		}
		using TempDatabase t = new TempDatabase();
		BuildSource(t.Store, t.Folder);
		ExchangeService x = new ExchangeService(t.Store);
		(string written, IReadOnlyList<string> missing) = x.Export(x.ReadDataset(), t.Folder);
		Assert.Empty(missing);
		if (Directory.Exists(SourceFolder))
		{
			Directory.Delete(SourceFolder, recursive: true);
		}
		Directory.CreateDirectory(Path.Combine(SourceFolder, "photos"));
		File.Copy(Path.Combine(written, "Export.json"), Path.Combine(SourceFolder, "Export.json"));
		foreach (string f in Directory.GetFiles(Path.Combine(written, "photos")))
		{
			File.Copy(f, Path.Combine(SourceFolder, "photos", Path.GetFileName(f)));
		}
	}

	/// <summary>
	/// Lists in a fixed order, so two data sets can be compared as JSON. Empty text counts as NULL: mdbtools reads a
	/// zero-length Text or Memo value as NULL (mdb-export and mdb-json alike), so "" can't survive an .mdb import.
	/// FEMA's code treats the two the same (string.IsNullOrEmpty), and Export.json allows null in every text field.
	/// </summary>
	private static string Canonical(SdeDataset d)
	{
		foreach (object record in new IEnumerable<object>[] { d.Properties, d.Assessments, d.Attachments, d.ElementPercentages, d.CostAdjustments, d.AdditionalAdjustments, d.CustomFieldsResults, d.Shapes }.SelectMany(x => x))
		{
			foreach (System.Reflection.PropertyInfo prop in record.GetType().GetProperties().Where(x => x.PropertyType == typeof(string) && x.CanWrite))
			{
				if (prop.GetValue(record) is "")
				{
					prop.SetValue(record, null);
				}
			}
		}
		SdeDataset sorted = new SdeDataset
		{
			Properties = d.Properties.OrderBy(x => x.PropertyID).ToList(),
			Assessments = d.Assessments.OrderBy(x => x.AssessmentID).ToList(),
			Attachments = d.Attachments.OrderBy(x => x.FileName).ToList(),
			ElementPercentages = d.ElementPercentages.OrderBy(x => x.ElementID).ToList(),
			CostAdjustments = d.CostAdjustments.OrderBy(x => x.ResultID).ToList(),
			AdditionalAdjustments = d.AdditionalAdjustments.OrderBy(x => x.AdditionalAdjustmentID).ToList(),
			CustomFieldsResults = d.CustomFieldsResults.OrderBy(x => x.ObjectIdentifier).ThenBy(x => x.CustomFieldID).ToList(),
			Shapes = d.Shapes.OrderBy(x => x.PropertyID).ThenBy(x => x.ShapeType).ToList(),
		};
		return SdeExchangeFolder.Serialize(sorted);
	}

	[Fact]
	public void PopulatedFemaDatabase_ReadsBackExactly()
	{
		Assert.True(File.Exists(MdbPath), "Missing fixture; see the class comment for how to make it.");
		SdeDataset source = SdeExchangeFolder.Read(SourceFolder);
		(SdeDataset fromMdb, string photos) = ExchangeService.ReadMdb(MdbPath);

		Assert.Equal(4, fromMdb.Properties.Count);
		Assert.Equal(4, fromMdb.Assessments.Count);
		Assert.Equal(Path.Combine(FixtureRoot, "sde-install", "attachments"), photos);
		Assert.Equal(Canonical(source), Canonical(fromMdb));
	}

	[Fact]
	public void PopulatedFemaDatabase_ImportsWithResultsAndPhotos()
	{
		using TempDatabase t = new TempDatabase();
		SdeDataset source = SdeExchangeFolder.Read(SourceFolder);
		ImportResult result = new ExchangeService(t.Store).ImportMdb(MdbPath, ExistingRecordAction.Skip);
		Assert.Empty(result.Warnings);

		Assert.Equal(4, t.Store.ListStructures().Select(s => s.PropertyID).Distinct().Count());
		foreach (AssessmentRecord expected in source.Assessments)
		{
			AssessmentFile f = t.Store.LoadAssessment(expected.AssessmentID);
			Assert.Equal((expected.PercentDamage, expected.DamageMessage), (f.Assessment.PercentDamage, f.Assessment.DamageMessage));
		}
		AssessmentFile ranch = t.Store.LoadAssessment(source.Attachments[0].AssessmentID);
		Assert.Equal(2, ranch.Attachments.Count);
		Assert.All(ranch.Attachments, a => Assert.True(File.Exists(t.Store.PhotoPath(a))));
		Assert.Equal("O'Brien-Peña", ranch.Property.OwnerLastName);
		Assert.Equal("Split-level, \"bonus\" room over the garage,\r\nwater line at 26 in.", ranch.Property.StructureInformation);
	}
}
