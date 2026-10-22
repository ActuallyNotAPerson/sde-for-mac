using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Sde.Core.ElevationCertificates;

namespace Sde.Interop.Tests;

/// <summary>
/// Small synthetic PDFs shaped like FEMA's elevation certificate, made with PDFsharp at test time (FEMA's own 7 MB
/// form isn't committed). The XFA ones copy what matters about the real form (FF-206-FY-22-152): a dynamic XFA form
/// with an empty /Fields array, an XFA array of template and datasets packets, and encryption with an owner password
/// only (AES-128, no change permission). Every value in them is made up.
/// </summary>
internal static class EcTestForms
{
	/// <summary>The owner password of the synthetic forms (FEMA's is unknown; reading never needs it).</summary>
	public const string OwnerPassword = "synthetic-owner";

	private const string XhtmlBody = "http://www.w3.org/1999/xhtml";

	/// <summary>Check boxes, as the real template's ui elements say.</summary>
	private static readonly HashSet<string> CheckBoxes = new(StringComparer.Ordinal)
	{
		"A5_1", "A5_2", "A5_3", "b11_1", "b11_2", "b11_3", "c2_1", "c2_2", "c2_3",
		"c2a_Feet", "c2a_Meters", "c2b_Feet", "c2b_Meters", "c2c_Feet", "c2c_Meters", "c2f_Feet", "c2f_Meters", "c2g_Feet", "c2g_Meters",
		"c2f_Natural", "c2f_Finished",
	};

	/// <summary>A filled certificate's values (made up), by the form's field names.</summary>
	public static Dictionary<string, string> FilledValues() => new Dictionary<string, string>
	{
		["headerBuildingStreetAddress"] = "123 Example Road", ["headerCity"] = "Exampleville", ["headerState"] = "35", ["headerZIP"] = "43702",
		["A1_BuildingOwnersName"] = "Pat Example",
		["A5Latitude"] = "39.712345", ["A5Longitude"] = "-84.178901", ["A5_1"] = "0", ["A5_2"] = "1", ["A5_3"] = "0",
		["A7_BuildingDiagramNumber"] = "1B",
		["B1.a_Community_Name"] = "Exampleville, City of", ["B1.b_Community_Number"] = "399990", ["B2_County_Name"] = "Example County", ["B3_State"] = "35",
		["B4_Map-Panel_Number"] = "39113C0157", ["B5_Suffix"] = "E", ["B6_FIRM_Index_date"] = "2011-01-06", ["B7_FIRM_Effective-Revised_date"] = "2011-01-06",
		["B8_Flood_Zones"] = "AE", ["B9_BFE"] = "742.5", ["b11_1"] = "0", ["b11_2"] = "1", ["b11_3"] = "0",
		["C2_Benchmark"] = "RM 12", ["C2_Vert_Datum"] = "", ["c2_1"] = "0", ["c2_2"] = "1", ["c2_3"] = "0",
		["c2.a_measurement"] = "739.8", ["c2a_Feet"] = "1", ["c2a_Meters"] = "0",
		["c2.f_measurement"] = "738.4", ["c2f_Feet"] = "1", ["c2f_Meters"] = "0", ["c2f_Natural"] = "1", ["c2f_Finished"] = "0",
	};

	/// <summary>The same fields with nothing filled in: what the blank form holds (check boxes "0", text empty).</summary>
	public static Dictionary<string, string> BlankValues() =>
		FilledValues().ToDictionary(kv => kv.Key, kv => CheckBoxes.Contains(kv.Key) ? "0" : kv.Key == "A7_BuildingDiagramNumber" ? " " : "");

	/// <summary>
	/// An XFA datasets packet laid out like FEMA's: the A2 header fields under form1, sections A and B under
	/// ec_form_sections_A_B, section C under ec_form_sections_C_D. <paramref name="richText"/> fields hold XHTML.
	/// </summary>
	public static string Datasets(IReadOnlyDictionary<string, string> values, params string[] richText)
	{
		StringBuilder ab = new StringBuilder();
		StringBuilder cd = new StringBuilder();
		StringBuilder header = new StringBuilder();
		foreach (KeyValuePair<string, string> kv in values)
		{
			string inner = richText.Contains(kv.Key)
				? $"<body xmlns=\"{XhtmlBody}\"><p>{SecurityElement.Escape(kv.Value)}</p></body>"
				: SecurityElement.Escape(kv.Value);
			string element = string.IsNullOrEmpty(kv.Value) ? $"<{kv.Key}\n/>" : $"<{kv.Key}\n>{inner}</{kv.Key}\n>";
			(kv.Key.StartsWith("header", StringComparison.Ordinal) ? header : kv.Key.StartsWith("c2", StringComparison.OrdinalIgnoreCase) ? cd : ab).Append(element);
		}
		return "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form1>"
			+ "<EC_Cover xfa:dataNode=\"dataGroup\"/>"
			+ $"<ec_form_sections_A_B>{ab}</ec_form_sections_A_B><ec_form_sections_C_D><recurringHeader xfa:dataNode=\"dataGroup\"/>{cd}</ec_form_sections_C_D>{header}"
			+ "</form1></xfa:data></xfa:datasets>";
	}

	/// <summary>A small XFA template naming the fields, with the check boxes' ui as checkButton like the real one.</summary>
	public static string Template() =>
		"<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"><subform name=\"form1\"><subform name=\"ec_form_sections_A_B\">"
		+ string.Concat(ElevationCertificateValues.Names.All.Concat(CheckBoxes).Distinct().Select(n =>
			$"<field name=\"{n}\"><ui>{(CheckBoxes.Contains(n) ? "<checkButton/>" : "<textEdit/>")}</ui></field>"))
		+ "</subform></subform></template>";

	/// <summary>A dynamic XFA form with these packets, encrypted like FEMA's unless <paramref name="encrypt"/> is false.</summary>
	public static byte[] Xfa(string datasets, string template = null, bool encrypt = true, string userPassword = null)
	{
		PdfDocument doc = new PdfDocument();
		doc.AddPage();
		PdfDictionary t = Stream(doc, template ?? Template());
		PdfDictionary d = Stream(doc, datasets);
		PdfDictionary acro = new PdfDictionary(doc);
		acro.Elements["/Fields"] = new PdfArray(doc);
		acro.Elements["/XFA"] = new PdfArray(doc, new PdfString("template"), t.Reference, new PdfString("datasets"), d.Reference);
		doc.Internals.AddObject(acro);
		doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;
		if (encrypt || userPassword != null)
		{
			Encrypt(doc, userPassword);
		}
		return Save(doc);
	}

	/// <summary>An ordinary PDF form (AcroForm) with fields named as a converted XFA form names them ("form1[0].ec_form_sections_A_B[0].A5Latitude[0]").</summary>
	public static byte[] AcroForm(IReadOnlyDictionary<string, string> values)
	{
		PdfDocument doc = new PdfDocument();
		doc.AddPage();
		PdfDictionary root = new PdfDictionary(doc);
		root.Elements["/T"] = new PdfString("form1[0]");
		PdfDictionary section = new PdfDictionary(doc);
		section.Elements["/T"] = new PdfString("ec_form_sections_A_B[0]");
		doc.Internals.AddObject(root);
		doc.Internals.AddObject(section);
		section.Elements["/Parent"] = root.Reference;
		PdfArray kids = new PdfArray(doc);
		foreach (KeyValuePair<string, string> kv in values)
		{
			PdfDictionary f = new PdfDictionary(doc);
			bool box = CheckBoxes.Contains(kv.Key);
			f.Elements["/FT"] = new PdfName(box ? "/Btn" : "/Tx");
			f.Elements["/T"] = new PdfString(kv.Key.Replace(".", "\\.") + "[0]");
			if (box)
			{
				f.Elements["/V"] = new PdfName(kv.Value == "1" ? "/1" : "/Off");
			}
			else if (!string.IsNullOrEmpty(kv.Value))
			{
				f.Elements["/V"] = new PdfString(kv.Value);
			}
			doc.Internals.AddObject(f);
			f.Elements["/Parent"] = section.Reference;
			kids.Elements.Add(f.Reference);
		}
		section.Elements["/Kids"] = kids;
		root.Elements["/Kids"] = new PdfArray(doc, section.Reference);
		PdfDictionary acro = new PdfDictionary(doc);
		acro.Elements["/Fields"] = new PdfArray(doc, root.Reference);
		doc.Internals.AddObject(acro);
		doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;
		return Save(doc);
	}

	/// <summary>A PDF with a page and no form at all, like a print or a scan.</summary>
	public static byte[] Plain()
	{
		PdfDocument doc = new PdfDocument();
		doc.AddPage();
		return Save(doc);
	}

	/// <summary>
	/// Saves new values into an encrypted XFA form the way Adobe Reader does: an incremental update at the end of the
	/// file holding only the new datasets stream (encrypted with the document's key), whose trailer names the encryption
	/// dictionary but doesn't list it again. PDFsharp 6.2.4 can't open such a file on its own.
	/// </summary>
	public static byte[] SaveLikeReader(byte[] pdf, string datasets)
	{
		int objectNumber;
		byte[] o;
		int p;
		using (PdfDocument doc = PdfReader.Open(new MemoryStream(pdf), "", PdfDocumentOpenMode.Import))
		{
			PdfDictionary acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
			PdfArray xfa = (PdfArray)(acro.Elements["/XFA"] is PdfReference r ? r.Value : acro.Elements["/XFA"]);
			objectNumber = ((PdfReference)xfa.Elements[3]).ObjectNumber;
			o = Encoding.Latin1.GetBytes(doc.SecurityHandler.Elements.GetString("/O"));
			p = doc.SecurityHandler.Elements.GetInteger("/P");
		}
		string text = Encoding.Latin1.GetString(pdf);
		Match trailer = Regex.Matches(text, @"trailer\s*<<(.*?)>>", RegexOptions.Singleline)[^1];
		string id0 = Regex.Match(trailer.Groups[1].Value, @"/ID\s*\[\s*<([0-9A-Fa-f]+)>").Groups[1].Value;
		string id = Regex.Match(trailer.Groups[1].Value, @"/ID\s*\[[^\]]*\]").Value;
		string root = Regex.Match(trailer.Groups[1].Value, @"/Root\s+\d+\s+\d+\s+R").Value;
		string encrypt = Regex.Match(trailer.Groups[1].Value, @"/Encrypt\s+\d+\s+\d+\s+R").Value;
		string size = Regex.Match(trailer.Groups[1].Value, @"/Size\s+\d+").Value;
		long previous = long.Parse(Regex.Matches(text, @"startxref\s+(\d+)")[^1].Groups[1].Value, CultureInfo.InvariantCulture);

		byte[] key = FileKey(o, p, Convert.FromHexString(id0));
		byte[] body = EncryptAes(Encoding.UTF8.GetBytes(datasets), key, objectNumber);
		using MemoryStream ms = new MemoryStream();
		ms.Write(pdf);
		void Write(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
		Write("\n");
		long objectOffset = ms.Position;
		Write($"{objectNumber} 0 obj\n<</Length {body.Length}>>\nstream\n");
		ms.Write(body);
		Write("\nendstream\nendobj\n");
		long xrefOffset = ms.Position;
		Write($"xref\n0 1\n0000000000 65535 f \n{objectNumber} 1\n{objectOffset:D10} 00000 n \n");
		Write($"trailer\n<<{size}{root}{encrypt}{id}/Prev {previous}>>\nstartxref\n{xrefOffset}\n%%EOF\n");
		return ms.ToArray();
	}

	private static PdfDictionary Stream(PdfDocument doc, string xml)
	{
		PdfDictionary d = new PdfDictionary(doc);
		d.CreateStream(Encoding.UTF8.GetBytes(xml));
		doc.Internals.AddObject(d);
		return d;
	}

	private static void Encrypt(PdfDocument doc, string userPassword)
	{
		doc.SecuritySettings.OwnerPassword = OwnerPassword;
		if (userPassword != null)
		{
			doc.SecuritySettings.UserPassword = userPassword;
		}
		doc.SecuritySettings.PermitModifyDocument = false;
		doc.SecuritySettings.PermitAnnotations = false;
		doc.SecuritySettings.PermitFormsFill = true;
		doc.SecurityHandler.SetEncryptionToV4UsingAES(encryptMetadata: false);
	}

	private static byte[] Save(PdfDocument doc)
	{
		using MemoryStream ms = new MemoryStream();
		doc.Save(ms);
		doc.Dispose();
		return ms.ToArray();
	}

	/// <summary>The file key for a standard security handler, revision 4, empty user password, metadata not encrypted (ISO 32000-1, algorithm 2).</summary>
	private static byte[] FileKey(byte[] o, int p, byte[] id0)
	{
		byte[] pad = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");
		byte[] input = pad.Concat(o.Take(32)).Concat(BitConverter.GetBytes(p)).Concat(id0).Concat(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }).ToArray();
		byte[] hash = MD5.HashData(input);
		for (int i = 0; i < 50; i++)
		{
			hash = MD5.HashData(hash.AsSpan(0, 16));
		}
		return hash.AsSpan(0, 16).ToArray();
	}

	/// <summary>A stream encrypted for one object with AESV2 (algorithm 1: the object's key, then AES-128-CBC with the IV first).</summary>
	private static byte[] EncryptAes(byte[] plain, byte[] fileKey, int objectNumber)
	{
		byte[] input = fileKey.Concat(new[] { (byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16), (byte)0, (byte)0 })
			.Concat(Encoding.ASCII.GetBytes("sAlT")).ToArray();
		using Aes aes = Aes.Create();
		aes.Key = MD5.HashData(input);
		aes.GenerateIV();
		return aes.IV.Concat(aes.EncryptCbc(plain, aes.IV, PaddingMode.PKCS7)).ToArray();
	}
}
