using System.Text;
using Newtonsoft.Json;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Interop;

/// <summary>What <see cref="UserSettingsFile.Import(SdeStore, string)"/> did.</summary>
public sealed class UserSettingsImportResult
{
	public int NotesAdded { get; set; }
	public int MappingsAdded { get; set; }
	public bool DefaultsReplaced { get; set; }

	public override string ToString() =>
		$"{NotesAdded} note(s) and {MappingsAdded} Enterprise Import mapping(s) added" + (DefaultsReplaced ? "; default values replaced" : "");
}

/// <summary>Which sections a user-settings file contains (null when a section was not exported).</summary>
public sealed record UserSettingsContents(int? Notes, int? EnterpriseSettings, bool DefaultValues);

/// <summary>
/// "Import/Export User Settings": notes, saved Enterprise Import mappings and the "Enter Default Data" values as one
/// JSON file, to move them between computers.
///
/// Port of decomp/wpfSDE/wpfSDE.ImportExport/UserSettings.xaml.cs. The file is FEMA's UserSettingImportExportModel
/// (decomp/BLL/BLL.ViewModel/UserSettingImportExportModel.cs) written by Newtonsoft with Formatting.Indented:
/// <c>{ "Notes": [...]|null, "EnterpriseSettings": [...]|null, "DefaultValues": {...}|null }</c>, each object with the
/// keys and order of NotesViewModel, EnterpriseImportSettingsViewModel (note its lowercase "zip") and DefaultViewModel.
/// A section that wasn't chosen is written as null. Files are interchangeable with FEMA's SDE.
///
/// Import adds notes and mappings as new rows (new IDs) and overwrites the default values, as the original did.
/// </summary>
public static class UserSettingsFile
{
	public static void Export(SdeStore store, string path, bool notes, bool enterpriseSettings, bool defaultValues)
	{
		SettingsModel model = new SettingsModel
		{
			Notes = notes ? store.GetNotes().Select(NoteJson.From).ToList() : null,
			EnterpriseSettings = enterpriseSettings ? store.GetEnterpriseMappings().Select(EnterpriseSettingJson.From).ToList() : null,
			// DefaultValueService.GetDefaultValues never returned null (an empty model when there was no row) and
			// didn't copy DefaultValueID, so the file always says 0.
			DefaultValues = defaultValues ? DefaultValuesJson.From(store.GetDefaults() ?? new DefaultValuesRecord()) : null,
		};
		using StringWriter sw = new StringWriter(System.Globalization.CultureInfo.InvariantCulture) { NewLine = "\r\n" };
		JsonSerializer.Create(new JsonSerializerSettings { Formatting = Formatting.Indented }).Serialize(sw, model);
		File.WriteAllText(path, sw.ToString(), new UTF8Encoding(false));
	}

	/// <summary>Imports every section the file contains.</summary>
	public static UserSettingsImportResult Import(SdeStore store, string path) => Import(store, path, true, true, true);

	/// <summary>Imports the chosen sections (a section missing from the file is skipped).</summary>
	public static UserSettingsImportResult Import(SdeStore store, string path, bool notes, bool enterpriseSettings, bool defaultValues)
	{
		SettingsModel model = Load(path);
		UserSettingsImportResult result = new UserSettingsImportResult();
		if (enterpriseSettings && model.EnterpriseSettings != null)
		{
			foreach (EnterpriseSettingJson s in model.EnterpriseSettings.Where(x => x != null))
			{
				EnterpriseImportSettingsRecord r = s.ToRecord();
				r.EIS_ID = 0;
				store.SaveEnterpriseMapping(r);
				result.MappingsAdded++;
			}
		}
		if (notes && model.Notes != null)
		{
			foreach (NoteJson n in model.Notes.Where(x => x != null))
			{
				store.SaveNote(new NoteRecord { NoteID = Guid.Empty, Notes = n.Notes, NoteName = n.NoteName, Active = n.Active });
				result.NotesAdded++;
			}
		}
		if (defaultValues && model.DefaultValues != null)
		{
			// DefaultValueService.SaveDefaultValues: copy every value into the existing row (or a new one).
			DefaultValuesRecord d = store.GetDefaults() ?? new DefaultValuesRecord();
			model.DefaultValues.CopyTo(d);
			store.SaveDefaults(d);
			result.DefaultsReplaced = true;
		}
		return result;
	}

	/// <summary>Reads which sections a file has, for the import screen's check boxes.</summary>
	public static UserSettingsContents Inspect(string path)
	{
		SettingsModel model = Load(path);
		return new UserSettingsContents(model.Notes?.Count, model.EnterpriseSettings?.Count, model.DefaultValues != null);
	}

	private static SettingsModel Load(string path)
	{
		string json = File.ReadAllText(path);
		try
		{
			return JsonConvert.DeserializeObject<SettingsModel>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })
				?? throw new FormatException("The file is empty.");
		}
		catch (JsonException ex)
		{
			throw new FormatException("This is not an SDE user-settings file: " + ex.Message, ex);
		}
	}

	// ───────────────────────── FEMA's view models, same key names and order ─────────────────────────

	/// <summary>UserSettingImportExportModel.</summary>
	private sealed class SettingsModel
	{
		public List<NoteJson> Notes { get; set; }
		public List<EnterpriseSettingJson> EnterpriseSettings { get; set; }
		public DefaultValuesJson DefaultValues { get; set; }
	}

	/// <summary>NotesViewModel (decomp/BLL/BLL.ViewModel/NotesViewModel.cs).</summary>
	private sealed class NoteJson
	{
		public Guid NoteID { get; set; }
		public string Notes { get; set; }
		public string NoteName { get; set; }
		public bool Active { get; set; }

		public static NoteJson From(NoteRecord n) => new() { NoteID = n.NoteID, Notes = n.Notes, NoteName = n.NoteName, Active = n.Active };
	}

	/// <summary>EnterpriseImportSettingsViewModel (decomp/BLL/BLL.ViewModel/EnterpriseImportSettingsViewModel.cs).</summary>
	private sealed class EnterpriseSettingJson
	{
		public string AddressLine1 { get; set; }
		public string AddressLine1Parsing { get; set; }
		public string AddressLine2 { get; set; }
		public string AddressLine2Parsing { get; set; }
		public string ApartmentUnit { get; set; }
		public string BFE { get; set; }
		public string City { get; set; }
		public string County { get; set; }
		public string DateOfFirmPanel { get; set; }
		public string Datum { get; set; }
		public int EIS_ID { get; set; }
		public string EISGuid { get; set; }
		public string EISName { get; set; }
		public string FFE { get; set; }
		public string FirmPanel { get; set; }
		public string FirmZone { get; set; }
		public string Latitude { get; set; }
		public string Longitude { get; set; }
		public string LotNum { get; set; }
		public string NFIPID { get; set; }
		public string NFIPName { get; set; }
		public string NFIPSuffix { get; set; }
		public string OwnersFirstName { get; set; }
		public string OwnersFirstNameParsing { get; set; }
		public string OwnersLastName { get; set; }
		public string OwnersLastNameParsing { get; set; }
		public string ParcelNum { get; set; }
		public string PhoneNumber { get; set; }
		public string PostGeoDir { get; set; }
		public string RegFloodway { get; set; }
		public string State { get; set; }
		public string StreetSuffix { get; set; }
		public string StreetSuffixParsing { get; set; }
		public string StructureType { get; set; }
		public string Subdivision { get; set; }
		public string TotalSqFt { get; set; }
		public string YrOfCon { get; set; }
		public string zip { get; set; }

		public static EnterpriseSettingJson From(EnterpriseImportSettingsRecord r)
		{
			EnterpriseSettingJson j = new EnterpriseSettingJson();
			Copy(r, j);
			j.zip = r.Zip;
			return j;
		}

		public EnterpriseImportSettingsRecord ToRecord()
		{
			EnterpriseImportSettingsRecord r = new EnterpriseImportSettingsRecord();
			Copy(this, r);
			r.Zip = zip;
			return r;
		}

		/// <summary>Copies the identically named properties (everything except zip/Zip).</summary>
		private static void Copy(object from, object to)
		{
			foreach (System.Reflection.PropertyInfo source in from.GetType().GetProperties())
			{
				System.Reflection.PropertyInfo target = to.GetType().GetProperty(source.Name);
				if (target != null && target.PropertyType == source.PropertyType && target.CanWrite)
				{
					target.SetValue(to, source.GetValue(from));
				}
			}
		}
	}

	/// <summary>DefaultViewModel (decomp/BLL/BLL.ViewModel/DefaultViewModel.cs); same names and order as the Default table.</summary>
	private sealed class DefaultValuesJson
	{
		public string BFE { get; set; }
		public string CauseOfDamage { get; set; }
		public string City { get; set; }
		public string CommunityUse { get; set; }
		public string CostDataDate { get; set; }
		public string CostDataRef { get; set; }
		public int? County { get; set; }
		public string DateDamagedOccurred { get; set; }
		public string DateOfFirmPanel { get; set; }
		public string DateOfInspection { get; set; }
		public string Datum { get; set; }
		public int DefaultValueID { get; set; }
		public string DurationOfFlood { get; set; }
		public string DurationOfFloodUnit { get; set; }
		public string FirmPanelNumber { get; set; }
		public int? FirmZone { get; set; }
		public string InspectedBy { get; set; }
		public string InspectedPhone { get; set; }
		public string LocalMultiplier { get; set; }
		public string NFIPCommunityID { get; set; }
		public string NFIPCommunityName { get; set; }
		public int? RegulatoryFloodway { get; set; }
		public int? State { get; set; }
		public string Suffix { get; set; }
		public string YearOfConstruction { get; set; }
		public string Zip { get; set; }
		public decimal? BaseCost { get; set; }

		public static DefaultValuesJson From(DefaultValuesRecord d)
		{
			DefaultValuesJson j = new DefaultValuesJson();
			foreach (System.Reflection.PropertyInfo p in typeof(DefaultValuesJson).GetProperties())
			{
				p.SetValue(j, typeof(DefaultValuesRecord).GetProperty(p.Name)!.GetValue(d));
			}
			j.DefaultValueID = 0;
			return j;
		}

		/// <summary>Every value except DefaultValueID (the existing row keeps its ID).</summary>
		public void CopyTo(DefaultValuesRecord d)
		{
			foreach (System.Reflection.PropertyInfo p in typeof(DefaultValuesJson).GetProperties().Where(x => x.Name != nameof(DefaultValueID)))
			{
				typeof(DefaultValuesRecord).GetProperty(p.Name)!.SetValue(d, p.GetValue(this));
			}
		}
	}
}
