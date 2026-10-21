using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Sde.Core.Records;

namespace Sde.Core.Exchange;

/// <summary>
/// Reads and writes SDE 3.0's data-exchange folder, the format FEMA's "Export SDE data" / "Import SDE data" use
/// (decomp/wpfSDE/wpfSDE.ImportExport/ucExportSDEdata.xaml.cs:312-362, ucImportSDEdata.xaml.cs:158-167):
///
///   SDE_&lt;n&gt;/
///     Export.json   Newtonsoft.Json, Formatting.Indented, UTF-8 without BOM, CRLF line breaks
///     photos/       the attachment files, by their FileName
///
/// A folder written here can be imported by FEMA's SDE on Windows and vice versa.
/// </summary>
public static class SdeExchangeFolder
{
	public const string JsonFileName = "Export.json";
	public const string PhotosFolderName = "photos";

	/// <summary>Legacy SDE 2.x exports contain this file instead of Export.json (not supported yet).</summary>
	public const string LegacySummaryFileName = "SummaryData.xml";

	private static readonly UTF8Encoding Utf8NoBom = new(false);

	public static string Serialize(SdeDataset data)
	{
		ApplyScales(data);
		using StringWriter sw = new StringWriter(System.Globalization.CultureInfo.InvariantCulture) { NewLine = "\r\n" };
		JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings { Formatting = Formatting.Indented });
		serializer.Serialize(sw, data);
		return sw.ToString();
	}

	public static SdeDataset Deserialize(string json)
	{
		// DateParseHandling.None, as the original importer used.
		SdeDataset data = JsonConvert.DeserializeObject<SdeDataset>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })
			?? new SdeDataset();
		data.Properties ??= new();
		data.Assessments ??= new();
		data.Attachments ??= new();
		data.ElementPercentages ??= new();
		data.CostAdjustments ??= new();
		data.AdditionalAdjustments ??= new();
		data.CustomFieldsResults ??= new();
		data.Shapes ??= new();
		return data;
	}

	/// <summary>
	/// Writes a new SDE_&lt;n&gt; folder inside <paramref name="parentFolder"/> (n = highest existing SDE_n + 1)
	/// and copies attachment files from <paramref name="attachmentsFolder"/> into its photos folder.
	/// Returns the folder path and the names of photos that could not be found.
	/// </summary>
	public static (string Folder, IReadOnlyList<string> MissingPhotos) Write(SdeDataset data, string parentFolder, string attachmentsFolder)
	{
		string folder = Path.Combine(parentFolder, NextFolderName(parentFolder));
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, JsonFileName), Serialize(data), Utf8NoBom);
		string photos = Path.Combine(folder, PhotosFolderName);
		Directory.CreateDirectory(photos);
		List<string> missing = new();
		foreach (AttachmentRecord a in data.Attachments)
		{
			if (string.IsNullOrEmpty(a.FileName))
			{
				continue;
			}
			string source = Path.Combine(attachmentsFolder ?? "", a.FileName);
			if (File.Exists(source))
			{
				File.Copy(source, Path.Combine(photos, Path.GetFileName(a.FileName)), overwrite: true);
			}
			else
			{
				missing.Add(a.FileName);
			}
		}
		return (folder, missing);
	}

	/// <summary>True when <paramref name="folder"/> is an SDE 3 exchange folder.</summary>
	public static bool IsExchangeFolder(string folder) => File.Exists(Path.Combine(folder, JsonFileName));

	public static bool IsLegacyFolder(string folder) => File.Exists(Path.Combine(folder, LegacySummaryFileName));

	public static SdeDataset Read(string folder)
	{
		if (!IsExchangeFolder(folder))
		{
			throw new InvalidDataException(IsLegacyFolder(folder)
				? $"{folder} is an SDE 2.x export (SummaryData.xml). Import it from the app's Import & Export page."
				: $"{folder} does not contain a valid SDE data set (no {JsonFileName}).");
		}
		return Deserialize(File.ReadAllText(Path.Combine(folder, JsonFileName), Encoding.UTF8));
	}

	public static string PhotosFolder(string exchangeFolder) => Path.Combine(exchangeFolder, PhotosFolderName);

	/// <summary>"SDE_" + (highest existing SDE_n + 1). The original added instead of taking the max (a bug).</summary>
	public static string NextFolderName(string parentFolder)
	{
		int max = 0;
		if (Directory.Exists(parentFolder))
		{
			foreach (string dir in Directory.GetDirectories(parentFolder))
			{
				Match m = Regex.Match(Path.GetFileName(dir), @"^sde_(\d+)$", RegexOptions.IgnoreCase);
				if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > max)
				{
					max = n;
				}
			}
		}
		return "SDE_" + (max + 1);
	}

	private static void ApplyScales(SdeDataset d)
	{
		JetScaling.ApplyAll(d.Properties);
		JetScaling.ApplyAll(d.Assessments);
		JetScaling.ApplyAll(d.ElementPercentages);
		JetScaling.ApplyAll(d.CostAdjustments);
		JetScaling.ApplyAll(d.AdditionalAdjustments);
		JetScaling.ApplyAll(d.Shapes);
	}
}
