using System.Text;
using System.Text.RegularExpressions;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>SDE Export.json compatibility and import behaviour.</summary>
public class ExchangeTests
{
	/// <summary>
	/// Export.json keys must come out in FEMA's order. Compares each record's property names, in order, against
	/// the declarations in the decompiled DAL models (decomp/DAL/DAL.Models/*.cs, snapshotted in tests/fixtures/fema-facts).
	/// </summary>
	[Theory]
	[InlineData(typeof(PropertyRecord), "PropertyModel")]
	[InlineData(typeof(AssessmentRecord), "AssessmentModel")]
	[InlineData(typeof(AttachmentRecord), "AttachmentModel")]
	[InlineData(typeof(ElementPercentageRecord), "ElementPercentageModel")]
	[InlineData(typeof(CostAdjustmentResultRecord), "CostAdjustmentResultModel")]
	[InlineData(typeof(AdditionalAdjustmentRecord), "AdditionalAdjustmentModel")]
	[InlineData(typeof(CustomFieldResultRecord), "CustomFieldResultModel")]
	[InlineData(typeof(ShapeRecord), "ShapesExportModel")]
	[InlineData(typeof(SdeDataset), "SDEExportModel")]
	public void RecordShape_MatchesFemaModel(Type record, string femaModel)
	{
		List<(string Type, string Name)> fema = FemaFacts.DalModel(femaModel).Select(p => (Normalize(p.Type), p.Name)).ToList();
		List<(string Type, string Name)> ours = record.GetProperties()
			.Where(p => p.Name != "Count")
			.Select(p => (Normalize(TypeName(p.PropertyType)), p.Name)).ToList();
		Assert.Equal(fema, ours);
	}

	private static string TypeName(Type t)
	{
		if (Nullable.GetUnderlyingType(t) is Type u)
		{
			return TypeName(u) + "?";
		}
		if (t.IsGenericType)
		{
			return "List<" + TypeName(t.GetGenericArguments()[0]) + ">";
		}
		return t.Name;
	}

	private static string Normalize(string t) => t
		.Replace("PropertyModel", "PropertyRecord").Replace("AssessmentModel", "AssessmentRecord").Replace("AttachmentModel", "AttachmentRecord")
		.Replace("ElementPercentageModel", "ElementPercentageRecord").Replace("CostAdjustmentResultModel", "CostAdjustmentResultRecord")
		.Replace("AdditionalAdjustmentModel", "AdditionalAdjustmentRecord").Replace("CustomFieldResultModel", "CustomFieldResultRecord")
		.Replace("ShapesExportModel", "ShapeRecord")
		.Replace("Int32", "int").Replace("Decimal", "decimal").Replace("String", "string").Replace("Boolean", "bool");

	[Fact]
	public void ExportJson_HasSdeByteFormat()
	{
		using TempDatabase t = new TempDatabase();
		SampleCases.RanchFlood(t.Store);
		ExchangeService x = new ExchangeService(t.Store);
		(string folder, _) = x.Export(x.ReadDataset(), t.Folder);
		Assert.Equal("SDE_1", Path.GetFileName(folder));
		Assert.True(Directory.Exists(Path.Combine(folder, "photos")));
		byte[] bytes = File.ReadAllBytes(Path.Combine(folder, "Export.json"));
		Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "no BOM");
		string json = Encoding.UTF8.GetString(bytes);
		Assert.StartsWith("{\r\n  \"Properties\": [\r\n    {\r\n      \"PropertyID\": \"", json);
		Assert.DoesNotMatch(@"[^\r]\n", json);
		Assert.Contains("\"BaseCost\": 118.7500,", json);                // Currency → 4 decimals
		Assert.Contains("\"SquareFootage\": 1450.00,", json);            // Numeric(18,2)
		Assert.Contains("\"DurationOfFlood\": 2.0,", json);              // Numeric(18,0) as Newtonsoft writes it
		Assert.Contains("\"PercentDamage\": 75.0,", json);               // Numeric(4,1)
		Assert.Matches("\"ItemCost\": \"\\d+\\.\\d{4}\"", json);         // string in FEMA's model
		Assert.Matches("\"DateofInspection\": \"\\d{4}-\\d\\d-\\d\\dT00:00:00\"", json);
		Assert.Matches("\"PropertyID\": \"[0-9a-f]{8}-[0-9a-f]{4}-", json);

		(string second, _) = x.Export(x.ReadDataset(), t.Folder);
		Assert.Equal("SDE_2", Path.GetFileName(second));
	}

	[Fact]
	public void ExportImportExport_RoundTripsExactly()
	{
		using TempDatabase a = new TempDatabase();
		using TempDatabase b = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(a.Store);
		string photo = Path.Combine(a.Folder, "p.jpg");
		File.WriteAllBytes(photo, Encoding.UTF8.GetBytes("photo"));
		f.Attachments.Add(a.Store.ImportPhoto(f.Assessment.AssessmentID, photo, "front"));
		a.Store.SaveAssessment(f);

		ExchangeService xa = new ExchangeService(a.Store);
		(string folder, _) = xa.Export(xa.ReadDataset(), a.Folder);
		ImportResult r = new ExchangeService(b.Store).ImportFolder(folder, ExistingRecordAction.Skip);
		Assert.Equal(1, r.PropertiesAdded);
		Assert.Equal(1, r.AssessmentsAdded);
		Assert.Equal(1, r.PhotosCopied);
		Assert.Empty(r.Warnings);

		string original = Comparable(xa.ReadDataset());
		string roundTripped = Comparable(new ExchangeService(b.Store).ReadDataset());
		Assert.Equal(original, roundTripped);
		AttachmentRecord imported = b.Store.LoadAssessment(f.Assessment.AssessmentID).Attachments.Single();
		Assert.Equal("photo", File.ReadAllText(b.Store.PhotoPath(imported)));
	}

	/// <summary>Serialized data with the values that legitimately change on import (regenerated row IDs, timestamps) blanked.</summary>
	private static string Comparable(SdeDataset d)
	{
		string json = SdeExchangeFolder.Serialize(d);
		return Regex.Replace(json, "\"(LastUpdate|ElementID|ResultID|AdditionalAdjustmentID|AttachmentID|ShapeID)\": [^,\r\n]+", "\"$1\": X");
	}

	[Fact]
	public void Import_ExistingRecords_SkipOverwriteCopy()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		ExchangeService x = new ExchangeService(t.Store);
		SdeDataset data = x.ReadDataset();
		data.Assessments[0].InspectorName = "Changed In File";

		ImportResult skip = x.Import(data, null, ExistingRecordAction.Skip);
		Assert.Equal(1, skip.Skipped);
		Assert.Equal("Inspector A", t.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment.InspectorName);

		ImportResult overwrite = x.Import(data, null, ExistingRecordAction.Overwrite);
		Assert.Equal(1, overwrite.AssessmentsUpdated);
		Assert.Equal("Changed In File", t.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment.InspectorName);
		Assert.Equal((1, 1), t.Store.Counts());

		ImportResult copy = x.Import(data, null, ExistingRecordAction.Copy);
		Assert.Equal((2, 2), t.Store.Counts());
		Guid copyId = copy.ImportedAssessmentIds.Single();
		Assert.NotEqual(f.Assessment.AssessmentID, copyId);
		AssessmentFile copied = t.Store.LoadAssessment(copyId);
		Assert.NotEqual(f.Property.PropertyID, copied.Property.PropertyID);
		Assert.Equal(75.0m, copied.Assessment.PercentDamage);
		// Child rows got new IDs (the original reused them and could hit primary-key collisions).
		Assert.Empty(copied.ElementPercentages.Select(e => e.ElementID).Intersect(f.ElementPercentages.Select(e => e.ElementID)));
	}

	[Fact]
	public void Import_LegacySde2Folder_GivesClearMessage()
	{
		using TempDatabase t = new TempDatabase();
		string legacy = Path.Combine(t.Folder, "old");
		Directory.CreateDirectory(legacy);
		File.WriteAllText(Path.Combine(legacy, "SummaryData.xml"), "<NewDataSet/>");
		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => new ExchangeService(t.Store).ImportFolder(legacy, ExistingRecordAction.Skip));
		Assert.Contains("SDE 2.x", ex.Message);
	}

	[Fact]
	public void ImportMdb_FemaShippedDatabase_IsEmpty()
	{
		using TempDatabase t = new TempDatabase();
		ImportResult r = new ExchangeService(t.Store).ImportMdb(TestPaths.FemaMdb, ExistingRecordAction.Skip);
		Assert.Equal(0, r.PropertiesAdded + r.AssessmentsAdded);
	}
}
