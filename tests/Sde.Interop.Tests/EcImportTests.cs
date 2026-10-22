using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop.ElevationCertificates;

namespace Sde.Interop.Tests;

/// <summary>Putting an elevation certificate on an assessment (C11): preview, the ticked values, and the PDF kept.</summary>
public class EcImportTests
{
	private static string WritePdf(TempDatabase t, string name, byte[] pdf)
	{
		string path = Path.Combine(t.Folder, name);
		File.WriteAllBytes(path, pdf);
		return path;
	}

	[Fact]
	public void TheTickedValuesAreSaved_AsAnEcImport_AndThePdfIsKept()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		string pdf = WritePdf(t, "ec.pdf", EcTestForms.Xfa(EcTestForms.Datasets(EcTestForms.FilledValues())));

		EcImportPreview preview = EcImport.Preview(t.Store, f.Assessment.AssessmentID, pdf);
		Assert.Equal(EcSource.XfaForm, preview.Read.Source);
		EcProposedChange bfe = Assert.Single(preview.Changes, c => c.Key == "Property.BFE");
		Assert.True(bfe.Differs);
		Assert.Contains(preview.Changes, c => c.Key == "Property.FirstFloorElevation");
		Assert.Contains("123 Example Road", preview.AddressWarning);   // the sample ranch is on River Street
		Assert.Equal(f.Property.BFE, t.Store.LoadAssessment(f.Assessment.AssessmentID).Property.BFE);   // a preview changes nothing

		EcImportResult result = EcImport.Apply(t.Store, preview, new[] { "Property.BFE", "Property.FirstFloorElevation" });
		PropertyRecord now = t.Store.LoadAssessment(f.Assessment.AssessmentID).Property;
		Assert.Equal(742.5m, now.BFE);
		Assert.Equal(2, result.Changed);
		Assert.All(t.Store.GetBatch(result.BatchId).Where(h => h.Field != null), h => Assert.Equal(SaveVia.EcImport, h.Via));
		DocumentRecord doc = Assert.Single(t.Store.GetDocuments(f.Property.PropertyID));
		Assert.Equal(DocumentKinds.ElevationCertificate, doc.Kind);
		Assert.Equal(File.ReadAllBytes(pdf), File.ReadAllBytes(t.Store.DocumentPath(doc)));
		Assert.Contains("B9_BFE", doc.Fields);
	}

	[Fact]
	public void ACertificateForThisAddress_HasNoWarning_AndAFlatOneIsAttachedOnly()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		t.Store.Apply(f.Assessment.AssessmentID, x =>
		{
			x.Property.StreetNumber = "123";
			x.Property.StreetName = "Example";
			x.Property.StreetSuffixID = null;
			x.Property.ZipCode = "43702";
		});
		string filled = WritePdf(t, "ec.pdf", EcTestForms.Xfa(EcTestForms.Datasets(EcTestForms.FilledValues())));
		Assert.Null(EcImport.Preview(t.Store, f.Assessment.AssessmentID, filled).AddressWarning);

		string flat = WritePdf(t, "scan.pdf", EcTestForms.Plain());
		EcImportPreview preview = EcImport.Preview(t.Store, f.Assessment.AssessmentID, flat);
		Assert.Equal(EcSource.NoFormData, preview.Read.Source);
		Assert.Empty(preview.Changes);
		EcImportResult result = EcImport.Apply(t.Store, preview, Array.Empty<string>());
		Assert.Equal(0, result.Changed);
		Assert.Null(result.BatchId);
		Assert.Single(t.Store.GetDocuments(f.Property.PropertyID));
		Assert.Contains(t.Store.GetHistory(f.Assessment.AssessmentID), h => h.IsEvent && h.Label == "Elevation certificate imported (attached only)");
	}

	[Fact]
	public void AFinalAssessment_IsRefused_AndNothingIsKept()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		string pdf = WritePdf(t, "ec.pdf", EcTestForms.Xfa(EcTestForms.Datasets(EcTestForms.FilledValues())));
		EcImportPreview preview = EcImport.Preview(t.Store, f.Assessment.AssessmentID, pdf);
		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);

		Assert.Throws<AssessmentLockedException>(() => EcImport.Apply(t.Store, preview, new[] { "Property.BFE" }));
		Assert.Empty(t.Store.GetDocuments(f.Property.PropertyID));
		Assert.Equal(f.Property.BFE, t.Store.LoadAssessment(f.Assessment.AssessmentID).Property.BFE);
	}
}
