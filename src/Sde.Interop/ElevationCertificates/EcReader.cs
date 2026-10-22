using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Sde.Core.ElevationCertificates;

namespace Sde.Interop.ElevationCertificates;

/// <summary>Where an elevation certificate's values came from, or why there were none.</summary>
public enum EcSource
{
	/// <summary>FEMA's fillable form (an XFA form): the values were read from its data.</summary>
	XfaForm,

	/// <summary>An ordinary PDF form whose fields have the fillable form's names.</summary>
	AcroForm,

	/// <summary>A scan, a print or a flattened copy: no form data to read.</summary>
	NoFormData,

	/// <summary>A PDF form, but not FEMA's elevation certificate.</summary>
	NotAnElevationCertificate,

	/// <summary>The file couldn't be read (damaged, not a PDF, or protected by a password).</summary>
	Unreadable,
}

/// <summary>What <see cref="EcReader"/> found in a PDF.</summary>
public sealed class EcReadResult
{
	public EcSource Source { get; init; }

	/// <summary>The certificate's values; null unless <see cref="Source"/> is XfaForm or AcroForm.</summary>
	public ElevationCertificateValues Values { get; init; }

	/// <summary>A plain-English sentence for the import sheet.</summary>
	public string Message { get; init; }

	/// <summary>Technical detail for the log when the file couldn't be read.</summary>
	public string Detail { get; init; }

	/// <summary>True when there are values to show on the confirm sheet.</summary>
	public bool HasValues => Values?.HasAnyValue == true;
}

/// <summary>
/// Reads an elevation certificate PDF. FEMA's current fillable form (FF-206-FY-22-152) is a dynamic XFA form, encrypted
/// with an owner password: there are no ordinary form fields, but the values typed into it are kept as XML (the XFA
/// "datasets" packet), which PDFsharp can decrypt and read. Ordinary PDF forms (AcroForm) with the same field names
/// are read too. Scans, prints and flattened copies have no form data: the person types the values into the entry
/// sheet instead. Reading never changes the file.
/// </summary>
public static class EcReader
{
	/// <summary>The message when there's nothing to read, so the entry sheet takes over.</summary>
	public const string NoDataMessage = "No fillable data in this PDF: enter the values by hand.";

	/// <summary>Reads the certificate at <paramref name="path"/>.</summary>
	public static EcReadResult Read(string path)
	{
		byte[] bytes;
		try
		{
			bytes = File.ReadAllBytes(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return new EcReadResult { Source = EcSource.Unreadable, Message = "SDE couldn't open this file. Check it still exists and try again.", Detail = ex.Message };
		}
		return Read(bytes);
	}

	/// <summary>Reads a certificate from the PDF's bytes.</summary>
	public static EcReadResult Read(byte[] pdf)
	{
		PdfDocument doc;
		try
		{
			doc = Open(pdf);
		}
		catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
		{
			return new EcReadResult { Source = EcSource.Unreadable, Message = "This PDF needs a password to open. Save a copy without the password and try again.", Detail = ex.Message };
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return new EcReadResult { Source = EcSource.Unreadable, Message = "SDE couldn't read this PDF. It may be damaged, or not a PDF.", Detail = ex.GetType().Name + ": " + ex.Message };
		}
		using (doc)
		{
			PdfDictionary acroForm = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
			byte[] datasets = acroForm == null ? null : XfaPacket(acroForm, "datasets");
			if (datasets != null)
			{
				Dictionary<string, string> fields;
				try
				{
					fields = XfaFields(datasets);
				}
				catch (XmlException ex)
				{
					return new EcReadResult { Source = EcSource.Unreadable, Message = "This PDF's form data is damaged, so SDE couldn't read it. Enter the values by hand.", Detail = ex.Message };
				}
				if (IsElevationCertificate(fields.Keys))
				{
					// Unticked check boxes hold "0" (or "2", the neutral state): they aren't values anyone entered.
					HashSet<string> checkBoxes = XfaCheckBoxes(XfaPacket(acroForm, "template"));
					foreach (string box in fields.Where(kv => kv.Value is "0" or "2" && checkBoxes.Contains(kv.Key)).Select(kv => kv.Key).ToList())
					{
						fields.Remove(box);
					}
					return Result(EcSource.XfaForm, fields);
				}
				return NotAnEc();
			}
			if (acroForm != null && Resolve(acroForm.Elements["/Fields"]) is PdfArray list && list.Elements.Count > 0)
			{
				Dictionary<string, string> byFullName = new Dictionary<string, string>(StringComparer.Ordinal);
				CollectAcroFields(list, null, byFullName, depth: 0);
				Dictionary<string, string> fields = MatchNames(byFullName);
				return fields.Count > 0 && IsElevationCertificate(fields.Keys) ? Result(EcSource.AcroForm, fields) : NotAnEc();
			}
			return new EcReadResult { Source = EcSource.NoFormData, Message = NoDataMessage };
		}
	}

	/// <summary>
	/// Opens the PDF for reading. An encrypted PDF needs no password to read when, like FEMA's form, it has only an
	/// owner password. When the newest update doesn't list the encryption dictionary, the repaired copy is opened
	/// instead (see <see cref="PdfUpdateRepair"/>).
	/// </summary>
	private static PdfDocument Open(byte[] pdf)
	{
		try
		{
			return PdfReader.Open(new MemoryStream(pdf, writable: false), "", PdfDocumentOpenMode.Import);
		}
		catch (InvalidOperationException ex) when (ex.Message.Contains("Indirect reference without value", StringComparison.Ordinal))
		{
			byte[] repaired = PdfUpdateRepair.RelistEncryption(pdf);
			if (repaired == null)
			{
				throw;
			}
			return PdfReader.Open(new MemoryStream(repaired, writable: false), "", PdfDocumentOpenMode.Import);
		}
	}

	private static EcReadResult Result(EcSource source, Dictionary<string, string> fields)
	{
		ElevationCertificateValues values = ElevationCertificateValues.FromFields(fields);
		string message = values.HasAnyValue
			? $"Read {values.Fields.Count} {(values.Fields.Count == 1 ? "value" : "values")} from the elevation certificate. Check them before saving."
			: "This is FEMA's elevation certificate, but nothing is filled in. Enter the values by hand.";
		return new EcReadResult { Source = source, Values = values, Message = message };
	}

	private static EcReadResult NotAnEc() => new EcReadResult
	{
		Source = EcSource.NotAnElevationCertificate,
		Message = "This PDF has a form, but it isn't FEMA's elevation certificate. Enter the values by hand.",
	};

	/// <summary>True when at least three of the certificate's distinctive field names are among the names found.</summary>
	internal static bool IsElevationCertificate(IEnumerable<string> names)
	{
		HashSet<string> set = new HashSet<string>(names, StringComparer.Ordinal);
		return ElevationCertificateValues.Names.Distinctive.Count(set.Contains) >= 3;
	}

	// ───────────────────────── XFA ─────────────────────────

	/// <summary>
	/// One packet of an XFA form ("datasets", "template"), decoded. /XFA is either an array of name and stream pairs or
	/// one stream holding the whole XDP document, from which the packet's element is cut out.
	/// </summary>
	internal static byte[] XfaPacket(PdfDictionary acroForm, string packet)
	{
		PdfItem xfa = Resolve(acroForm.Elements["/XFA"]);
		if (xfa is PdfArray array)
		{
			for (int i = 0; i + 1 < array.Elements.Count; i += 2)
			{
				if (Resolve(array.Elements[i]) is PdfString name && name.Value == packet && Resolve(array.Elements[i + 1]) is PdfDictionary stream)
				{
					return stream.Stream?.UnfilteredValue;
				}
			}
			return null;
		}
		if (xfa is PdfDictionary whole && whole.Stream?.UnfilteredValue is byte[] xdp)
		{
			XDocument doc = LoadXml(xdp);
			XElement element = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == packet);
			return element == null ? null : System.Text.Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting));
		}
		return null;
	}

	/// <summary>
	/// The values in an XFA datasets packet, by field name: every element under xfa:data with no child elements (or only
	/// rich-text XHTML inside), with its text trimmed. When a name repeats, the first non-blank value wins.
	/// </summary>
	internal static Dictionary<string, string> XfaFields(byte[] datasets)
	{
		XDocument doc = LoadXml(datasets);
		XElement data = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "data") ?? doc.Root;
		Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
		void Walk(XElement parent)
		{
			foreach (XElement e in parent.Elements())
			{
				bool richText = e.HasElements && e.Elements().All(c => c.Name.NamespaceName == Xhtml);
				if (e.HasElements && !richText)
				{
					Walk(e);
					continue;
				}
				string value = e.Value.Trim();
				string name = e.Name.LocalName;
				if (!fields.TryGetValue(name, out string existing) || (existing.Length == 0 && value.Length > 0))
				{
					fields[name] = value;
				}
			}
		}
		Walk(data);
		return fields;
	}

	private const string Xhtml = "http://www.w3.org/1999/xhtml";

	/// <summary>
	/// The names of the check boxes in an XFA template (fields whose ui is a checkButton), read as a stream because
	/// FEMA's template carries several megabytes of images. Empty when there is no template or it can't be read.
	/// </summary>
	internal static HashSet<string> XfaCheckBoxes(byte[] template)
	{
		HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
		if (template == null)
		{
			return names;
		}
		try
		{
			XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
			using XmlReader reader = XmlReader.Create(new MemoryStream(template), settings);
			Stack<string> fields = new Stack<string>();
			while (reader.Read())
			{
				if (reader.NodeType == XmlNodeType.Element)
				{
					if (reader.LocalName == "field")
					{
						string name = reader.GetAttribute("name");
						if (reader.IsEmptyElement)
						{
							continue;
						}
						fields.Push(name);
					}
					else if (reader.LocalName == "checkButton" && fields.Count > 0 && fields.Peek() != null)
					{
						names.Add(fields.Peek());
					}
				}
				else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "field" && fields.Count > 0)
				{
					fields.Pop();
				}
			}
		}
		catch (XmlException)
		{
			// Without the template every value is kept, which is harmless.
		}
		return names;
	}

	/// <summary>Parses XML without DTDs or external entities.</summary>
	private static XDocument LoadXml(byte[] bytes)
	{
		XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
		using XmlReader reader = XmlReader.Create(new MemoryStream(bytes), settings);
		return XDocument.Load(reader);
	}

	// ───────────────────────── AcroForm ─────────────────────────

	/// <summary>
	/// Collects terminal fields by full name ("form1[0].page1[0].A5Latitude[0]"), with ticked check boxes as "1"
	/// (unticked ones are left out). The field dictionaries are read as they are: PDFsharp's typed field classes need
	/// fonts set up, which reading doesn't. Kids without a name are a field's widgets and belong to it.
	/// </summary>
	private static void CollectAcroFields(PdfArray fields, string parent, Dictionary<string, string> byFullName, int depth)
	{
		if (depth > 32)
		{
			return;
		}
		foreach (PdfItem item in fields.Elements)
		{
			if (Resolve(item) is not PdfDictionary field)
			{
				continue;
			}
			string name = (Resolve(field.Elements["/T"]) as PdfString)?.Value;
			string full = string.IsNullOrEmpty(name) ? parent : parent == null ? name : parent + "." + name;
			if (Resolve(field.Elements["/Kids"]) is PdfArray kids && kids.Elements.Any(k => Resolve(k) is PdfDictionary d && d.Elements.ContainsKey("/T")))
			{
				CollectAcroFields(kids, full, byFullName, depth + 1);
				continue;
			}
			if (full == null)
			{
				continue;
			}
			string value = Resolve(field.Elements["/V"]) switch
			{
				PdfString s => s.Value,
				PdfName n => n.Value.TrimStart('/').Equals("Off", StringComparison.OrdinalIgnoreCase) ? null : "1",
				PdfInteger i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
				PdfReal r => r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
				_ => null,
			};
			if (value != null)
			{
				byFullName.TryAdd(full, value);
			}
		}
	}

	/// <summary>
	/// The certificate's fields among AcroForm fields: a field matches when its full name, without "[0]" indexes and
	/// with "\." read as a dot, is the certificate's field name or ends with "." and it.
	/// </summary>
	private static Dictionary<string, string> MatchNames(Dictionary<string, string> byFullName)
	{
		List<(string Name, string Value)> cleaned = byFullName
			.Select(kv => (Name: Regex.Replace(kv.Key, @"\[\d+\]", "").Replace("\\.", "."), kv.Value)).ToList();
		Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string wanted in ElevationCertificateValues.Names.All)
		{
			(string Name, string Value) hit = cleaned.FirstOrDefault(c => c.Name == wanted || c.Name.EndsWith("." + wanted, StringComparison.Ordinal));
			if (hit.Name != null)
			{
				fields[wanted] = hit.Value;
			}
		}
		return fields;
	}

	private static PdfItem Resolve(PdfItem item) => item is PdfReference r ? r.Value : item;
}
