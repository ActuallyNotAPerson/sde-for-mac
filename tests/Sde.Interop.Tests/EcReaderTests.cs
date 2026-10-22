using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Sde.Core.ElevationCertificates;
using Sde.Interop.ElevationCertificates;

namespace Sde.Interop.Tests;

/// <summary>Reading elevation certificates from synthetic PDFs shaped like FEMA's fillable form (see <see cref="EcTestForms"/>).</summary>
public class EcReaderTests
{
	private static void AssertFilled(ElevationCertificateValues v)
	{
		Assert.Equal((39.712345m, -84.178901m, "NAD 1983", "1B"), (v.Latitude.Value, v.Longitude.Value, v.HorizontalDatum, v.BuildingDiagram));
		Assert.Equal(("Exampleville, City of", "399990", "39113C0157", "E"), (v.CommunityName, v.CommunityNumber, v.PanelNumber, v.PanelSuffix));
		Assert.Equal((new DateTime(2011, 1, 6), "AE", 742.5m, "NAVD 1988", "NAVD 1988"), (v.PanelEffectiveDate.Value, v.FloodZone, v.Bfe.Value, v.BfeDatum, v.ElevationDatum));
		Assert.Equal(739.8m, v.TopOfBottomFloor.Feet);
		Assert.Equal(738.4m, v.LowestAdjacentGrade.Feet);
		Assert.Equal(("123 Example Road", "Exampleville", "OH", "43702"), (v.StreetAddress, v.City, v.State, v.ZipCode));
	}

	[Fact]
	public void EncryptedXfaForm_LikeFemas_IsRead()
	{
		byte[] pdf = EcTestForms.Xfa(EcTestForms.Datasets(EcTestForms.FilledValues()));
		// Like FEMA's form: PDFsharp needs the owner password to change it, but not to read it.
		Assert.Throws<PdfReaderException>(() => PdfReader.Open(new MemoryStream(pdf), "", PdfDocumentOpenMode.Modify));

		EcReadResult r = EcReader.Read(pdf);
		Assert.Equal(EcSource.XfaForm, r.Source);
		Assert.True(r.HasValues);
		AssertFilled(r.Values);
		// Unticked check boxes are left out; ticked ones and every value typed are kept.
		Assert.False(r.Values.Fields.ContainsKey("A5_1"));
		Assert.Equal("1", r.Values.Fields["A5_2"]);
		Assert.Equal(27, r.Values.Fields.Count);
		Assert.Equal("Read 27 values from the elevation certificate. Check them before saving.", r.Message);
	}

	[Fact]
	public void BlankForm_IsRecognised_ButHasNothingToUse()
	{
		EcReadResult r = EcReader.Read(EcTestForms.Xfa(EcTestForms.Datasets(EcTestForms.BlankValues())));
		Assert.Equal(EcSource.XfaForm, r.Source);
		Assert.False(r.HasValues);
		Assert.Empty(r.Values.Fields);
		Assert.Equal("This is FEMA's elevation certificate, but nothing is filled in. Enter the values by hand.", r.Message);
	}

	[Fact]
	public void FormSavedAsUpdates_LikeAdobeReaderDoes_IsRead()
	{
		byte[] blank = EcTestForms.Xfa(EcTestForms.Datasets(EcTestForms.BlankValues()));
		Dictionary<string, string> started = EcTestForms.BlankValues();
		started["A5Latitude"] = "39.1";
		byte[] once = EcTestForms.SaveLikeReader(blank, EcTestForms.Datasets(started));
		Assert.Equal(39.1m, EcReader.Read(once).Values.Latitude);

		// Saved a second time: now an update in the middle of the file doesn't list the encryption dictionary either,
		// which PDFsharp 6.2.4 can't open by itself (FEMA's own form already has such an update, so one save is enough
		// there). If a later PDFsharp can, PdfUpdateRepair is no longer needed.
		byte[] twice = EcTestForms.SaveLikeReader(once, EcTestForms.Datasets(EcTestForms.FilledValues()));
		InvalidOperationException limit = Assert.Throws<InvalidOperationException>(() => PdfReader.Open(new MemoryStream(twice), "", PdfDocumentOpenMode.Import));
		Assert.Contains("Indirect reference without value", limit.Message);
		using (PdfDocument repaired = PdfReader.Open(new MemoryStream(PdfUpdateRepair.RelistEncryption(twice)), "", PdfDocumentOpenMode.Import))
		{
			Assert.Equal(1, repaired.PageCount);
		}

		EcReadResult r = EcReader.Read(twice);
		Assert.Equal(EcSource.XfaForm, r.Source);
		AssertFilled(r.Values);   // the newest values, not the ones underneath
	}

	[Fact]
	public void UnencryptedXfa_RichText_AndTheWholeXdpInOneStream_AreRead()
	{
		Dictionary<string, string> values = EcTestForms.FilledValues();
		EcReadResult plain = EcReader.Read(EcTestForms.Xfa(EcTestForms.Datasets(values, "B1.a_Community_Name"), encrypt: false));
		Assert.Equal(EcSource.XfaForm, plain.Source);
		AssertFilled(plain.Values);

		// /XFA can also be one stream holding the whole XDP document.
		string xdp = "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">" + EcTestForms.Template() + EcTestForms.Datasets(values) + "</xdp:xdp>";
		PdfDocument doc = new PdfDocument();
		doc.AddPage();
		PdfDictionary stream = new PdfDictionary(doc);
		stream.CreateStream(System.Text.Encoding.UTF8.GetBytes(xdp));
		doc.Internals.AddObject(stream);
		PdfDictionary acro = new PdfDictionary(doc);
		acro.Elements["/Fields"] = new PdfArray(doc);
		acro.Elements["/XFA"] = stream.Reference;
		doc.Internals.Catalog.Elements["/AcroForm"] = acro;
		using MemoryStream ms = new MemoryStream();
		doc.Save(ms);
		EcReadResult whole = EcReader.Read(ms.ToArray());
		Assert.Equal(EcSource.XfaForm, whole.Source);
		AssertFilled(whole.Values);
	}

	[Fact]
	public void AcroFormWithTheSameFieldNames_IsRead()
	{
		EcReadResult r = EcReader.Read(EcTestForms.AcroForm(EcTestForms.FilledValues()));
		Assert.Equal(EcSource.AcroForm, r.Source);
		AssertFilled(r.Values);
		Assert.False(r.Values.Fields.ContainsKey("A5_1"));   // unticked
	}

	[Fact]
	public void OtherForms_PrintsScans_AndBadFiles_SayWhatToDo()
	{
		EcReadResult other = EcReader.Read(EcTestForms.Xfa(EcTestForms.Datasets(new Dictionary<string, string> { ["PermitNumber"] = "B-1", ["Applicant"] = "Example" })));
		Assert.Equal(EcSource.NotAnElevationCertificate, other.Source);
		Assert.Null(other.Values);
		Assert.Equal("This PDF has a form, but it isn't FEMA's elevation certificate. Enter the values by hand.", other.Message);
		Assert.Equal(EcSource.NotAnElevationCertificate, EcReader.Read(EcTestForms.AcroForm(new Dictionary<string, string> { ["Applicant"] = "Example" })).Source);

		EcReadResult print = EcReader.Read(EcTestForms.Plain());
		Assert.Equal(EcSource.NoFormData, print.Source);
		Assert.Equal("No fillable data in this PDF: enter the values by hand.", print.Message);

		EcReadResult locked = EcReader.Read(EcTestForms.Xfa(EcTestForms.Datasets(EcTestForms.FilledValues()), userPassword: "secret"));
		Assert.Equal(EcSource.Unreadable, locked.Source);
		Assert.Equal("This PDF needs a password to open. Save a copy without the password and try again.", locked.Message);

		EcReadResult garbage = EcReader.Read(System.Text.Encoding.ASCII.GetBytes("not a pdf at all"));
		Assert.Equal(EcSource.Unreadable, garbage.Source);
		Assert.Equal("SDE couldn't read this PDF. It may be damaged, or not a PDF.", garbage.Message);

		EcReadResult missing = EcReader.Read(Path.Combine(Path.GetTempPath(), "no-such-ec-" + Guid.NewGuid().ToString("N") + ".pdf"));
		Assert.Equal(EcSource.Unreadable, missing.Source);
	}

	[Fact]
	public void DamagedFormData_IsUnreadable_NotACrash()
	{
		EcReadResult r = EcReader.Read(EcTestForms.Xfa("<xfa:datasets><unclosed>"));
		Assert.Equal(EcSource.Unreadable, r.Source);
		Assert.Equal("This PDF's form data is damaged, so SDE couldn't read it. Enter the values by hand.", r.Message);

		// A DTD (a way to smuggle in outside files) is refused.
		string dtd = "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/hosts\">]><xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form1><A5Latitude>&e;</A5Latitude></form1></xfa:data></xfa:datasets>";
		Assert.Equal(EcSource.Unreadable, EcReader.Read(EcTestForms.Xfa(dtd)).Source);
	}

	[Fact]
	public void TheRepair_LeavesOtherPdfsAlone()
	{
		Assert.Null(PdfUpdateRepair.RelistEncryption(EcTestForms.Plain()));
		Assert.Null(PdfUpdateRepair.RelistEncryption(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 nothing here")));
		Assert.Null(PdfUpdateRepair.RelistEncryption(null));
	}
}
