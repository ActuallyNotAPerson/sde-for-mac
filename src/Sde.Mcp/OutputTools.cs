using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Interop;
using Sde.Interop.WebMap;
using Sde.Reports;

namespace Sde.Mcp;

/// <summary>
/// The output tools: a PDF report, a GeoJSON file and a web map, made the way the app makes them. They are always
/// allowed (they change nothing in the database) and write only into the "AI outputs" folder beside the database,
/// never where an assistant asks; each result gives the path.
/// </summary>
[McpServerToolType]
public sealed class OutputTools(SdeMcpContext sde, ILogger<OutputTools> log)
{
	/// <summary>The most assessments one report holds.</summary>
	public const int MaxReportAssessments = 500;

	/// <summary>The most structures one GeoJSON file or web map holds.</summary>
	public const int MaxMapAssessments = 50000;

	private const string Outputs = " The file goes in the \"AI outputs\" folder beside the database (the result gives its path); it works whether or not AI edits are on and changes nothing in the database.";

	[McpServerTool(Name = "make_report", Title = "Make a PDF report", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Makes one of SDE's PDF reports as the app prints it: the summary report (one page per assessment) or the detailed report (everything about each assessment), with photos and their markup unless asked not to. Choose assessments by ID, or an event for the latest assessment of each structure in it." + Outputs)]
	public ReportOutput MakeReport(
		[Description("The assessments to print, in this order (find_structures lists their IDs). At most 500.")] string[] assessmentIds = null,
		[Description("An event's name or ID: print the latest assessment of each structure in it (or, with assessmentIds, only those in it).")] string @event = null,
		[Description("\"summary\" (the default) or \"detailed\".")] string kind = "summary",
		[Description("Print the photos, with their markup (default true).")] bool includePhotos = true,
		[Description("A name for the PDF, without a folder. SDE picks one when left out and adds a number when the name is taken.")] string fileName = null)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		ReportKind report = (kind ?? "summary").Trim().ToLowerInvariant() switch
		{
			"" or "summary" => ReportKind.Summary,
			"detail" or "detailed" => ReportKind.Detail,
			_ => throw new McpException("kind must be \"summary\" or \"detailed\"."),
		};
		if ((assessmentIds == null || assessmentIds.Length == 0) && string.IsNullOrWhiteSpace(@event))
		{
			throw new McpException("Choose the assessments to print (assessmentIds) or an event.");
		}
		EventRecord e = Lookups.OptionalEvent(store, @event);
		(List<Guid> ids, List<string> skipped, _) = Lookups.Choose(store, assessmentIds, e, MaxReportAssessments);
		if (ids.Count == 0)
		{
			throw new McpException("None of those assessments can be printed: they aren't in this database, are in Recently deleted, or aren't in the event.");
		}
		Dictionary<Guid, int> order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
		List<AssessmentFile> files = store.LoadAssessments(ids).OrderBy(f => order[f.Assessment.AssessmentID]).ToList();
		string name = report == ReportKind.Summary ? "Summary report" : "Detailed report";
		string what = e != null && (assessmentIds == null || assessmentIds.Length == 0)
			? e.Label
			: files.Count == 1 ? Describe.Address(files[0].Property, store.Reference) : $"{files.Count} assessments";
		string path = AiOutputs.FreePath(sde.DatabasePath, fileName, $"{name} for {what}", ".pdf");
		ReportRequest request = new ReportRequest
		{
			Kind = report,
			Assessments = files,
			IncludePhotos = includePhotos,
			PhotosFolder = store.AttachmentsFolder,
			// Photos print with their markup (water lines, arrows, labels), as in the app; the photo files never change.
			PhotoMarkups = includePhotos
				? files.SelectMany(f => store.GetPhotoMarkups(f.Assessment.AssessmentID)).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value)
				: null,
		};
		Write(path, () => ReportGenerator.RenderToFile(request, store.Reference, path), "the report");
		log.LogInformation("make_report wrote {Path} ({Count} assessments)", path, files.Count);
		return new ReportOutput(sde.DatabasePath, path, ReportGenerator.Title(report), files.Count, includePhotos, skipped,
			skipped.Count == 0 ? null : $"{skipped.Count} of the IDs given {(skipped.Count == 1 ? "was" : "were")} left out (see skipped).");
	}

	[McpServerTool(Name = "export_geojson", Title = "Export structures as GeoJSON", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Writes the structures as a GeoJSON file (RFC 7946 points in longitude and latitude) for GIS: each structure's latest assessment, or one event's, or chosen assessments, with SDE's usual columns (address, parcel, percent damaged, result, building value, repair cost, inspection date, FIRM zone) and any other fields asked for. Owner names and other owner details are left out unless includeOwnerNames is true. Structures without coordinates are skipped and counted." + Outputs)]
	public GeoJsonOutput ExportGeoJson(
		[Description("Only these assessments (find_structures lists their IDs).")] string[] assessmentIds = null,
		[Description("Only this event: its name or ID (list_events lists them).")] string @event = null,
		[Description("More fields to add as properties, by field key (get_assessment and field_help list them), each written as SDE shows it.")] string[] fields = null,
		[Description("Include owner names and other owner and mailing details. They are personal information: only for people allowed to see them.")] bool includeOwnerNames = false,
		[Description("A name for the file, without a folder. SDE picks one when left out and adds a number when the name is taken.")] string fileName = null)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		List<FieldInfo> extra = (fields ?? Array.Empty<string>()).Select(ReadTools.RequireField).Distinct().ToList();
		FieldInfo personal = extra.FirstOrDefault(IsPersonal);
		if (personal != null && !includeOwnerNames)
		{
			throw new McpException($"{personal.Key} ({personal.Label}) is personal information about the owner. Set includeOwnerNames to true to include owner and mailing details, and share the file only with people allowed to see them.");
		}
		EventRecord e = Lookups.OptionalEvent(store, @event);
		(List<Guid> ids, List<string> skipped, _) = Lookups.Choose(store, assessmentIds, e, MaxMapAssessments);
		List<AssessmentFile> files = Ordered(store, ids);
		ReferenceData reference = store.Reference;
		List<GisRow> rows = GisRow.From(files, reference);
		string path = AiOutputs.FreePath(sde.DatabasePath, fileName, e != null ? e.Label + " structures" : "SDE structures", ".geojson");
		List<string> properties = new List<string> { "structure_id", "assessment_id", "address", "parcel", "damage_pct", GisExport.ClassField, "building_value", "repair_cost", "inspection_date", "firm_zone" };
		if (includeOwnerNames)
		{
			properties.Add("owner_name");
		}
		properties.AddRange(extra.Select(f => f.Key));
		int written = 0;
		int noLocation = 0;
		Write(path, () =>
		{
			using MemoryStream buffer = new MemoryStream();
			using (Utf8JsonWriter w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
			{
				w.WriteStartObject();
				w.WriteString("type", "FeatureCollection");
				w.WriteStartArray("features");
				for (int i = 0; i < rows.Count; i++)
				{
					GisRow row = rows[i];
					if (!row.HasLocation)
					{
						noLocation++;
						continue;
					}
					w.WriteStartObject();
					w.WriteString("type", "Feature");
					w.WriteStartObject("geometry");
					w.WriteString("type", "Point");
					w.WriteStartArray("coordinates");
					w.WriteNumberValue(row.Longitude.Value);
					w.WriteNumberValue(row.Latitude.Value);
					w.WriteEndArray();
					w.WriteEndObject();
					w.WriteStartObject("properties");
					w.WriteString("structure_id", row.StructureID.ToString());
					w.WriteString("assessment_id", row.AssessmentID.ToString());
					Text(w, "address", row.Address);
					Text(w, "parcel", row.Parcel);
					Number(w, "damage_pct", row.PercentDamage);
					w.WriteString(GisExport.ClassField, GisExport.ClassName(row.Class));
					Number(w, "building_value", row.BuildingValue);
					Number(w, "repair_cost", row.RepairCost);
					Text(w, "inspection_date", Describe.Date(row.InspectionDate));
					Text(w, "firm_zone", row.FirmZone);
					if (includeOwnerNames)
					{
						Text(w, "owner_name", row.OwnerName);
					}
					foreach (FieldInfo f in extra)
					{
						Text(w, f.Key, FieldCatalog.Display(f, FieldCatalog.GetValue(files[i], f), reference));
					}
					w.WriteEndObject();
					w.WriteEndObject();
					written++;
				}
				w.WriteEndArray();
				w.WriteEndObject();
			}
			File.WriteAllBytes(path, buffer.ToArray());
		}, "the GeoJSON file");
		log.LogInformation("export_geojson wrote {Path} ({Count} structures)", path, written);
		string note = written == 0 ? "No structure had coordinates, so the file has no features." : null;
		return new GeoJsonOutput(sde.DatabasePath, path, written, noLocation, properties, includeOwnerNames, skipped, note);
	}

	[McpServerTool(Name = "make_web_map", Title = "Make a web map", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Makes a web map folder to share (index.html with the map, a legend and popups; it opens in any browser without internet, though the background map needs it), as SDE's Share a web map does. \"internal\" shows every structure as a dot with its address and chosen fields; \"public\" shows no owners and no exact locations: structures are counted in grid squares, with addresses hidden or cut to the block. With an event, the map shows the event's summary beside it." + Outputs)]
	public WebMapOutput MakeWebMap(
		[Description("The title at the top of the map (default: the event's name, or \"Damage assessments\").")] string title = null,
		[Description("\"internal\" (full detail, the default) or \"public\".")] string privacy = "internal",
		[Description("Public maps only: \"block\" (\"100 block of River St\", the default) or \"hidden\".")] string addressDetail = "block",
		[Description("Internal maps only: field keys to show in each structure's popup (get_assessment lists them).")] string[] popupFields = null,
		[Description("The background map: \"usgs\" (USGS topo, the default), \"osm\" (OpenStreetMap) or \"none\".")] string basemap = "usgs",
		[Description("An event's name or ID: map the latest assessment of each structure in it, with its summary.")] string @event = null,
		[Description("Only these assessments (find_structures lists their IDs).")] string[] assessmentIds = null)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		WebMapPrivacy who = (privacy ?? "internal").Trim().ToLowerInvariant() switch
		{
			"" or "internal" => WebMapPrivacy.Internal,
			"public" => WebMapPrivacy.Public,
			_ => throw new McpException("privacy must be \"internal\" or \"public\"."),
		};
		PublicAddressDetail detail = (addressDetail ?? "block").Trim().ToLowerInvariant() switch
		{
			"" or "block" => PublicAddressDetail.Block,
			"hidden" or "none" => PublicAddressDetail.Hidden,
			_ => throw new McpException("addressDetail must be \"block\" or \"hidden\"."),
		};
		WebMapBasemap background = new string((basemap ?? "usgs").Where(char.IsLetter).ToArray()).ToLowerInvariant() switch
		{
			"" or "usgs" or "usgstopo" or "topo" => WebMapBasemap.UsgsTopo,
			"osm" or "openstreetmap" => WebMapBasemap.OpenStreetMap,
			"none" or "nobasemap" => WebMapBasemap.None,
			_ => throw new McpException("basemap must be \"usgs\", \"osm\" or \"none\"."),
		};
		List<FieldInfo> popups = (popupFields ?? Array.Empty<string>()).Select(ReadTools.RequireField).Distinct().ToList();
		EventRecord e = Lookups.OptionalEvent(store, @event);
		(List<Guid> ids, List<string> skipped, _) = Lookups.Choose(store, assessmentIds, e, MaxMapAssessments);
		List<AssessmentFile> files = Ordered(store, ids);
		string heading = string.IsNullOrWhiteSpace(title) ? e?.Label ?? "Damage assessments" : title.Trim();
		WebMapOptions options = new WebMapOptions
		{
			Title = heading,
			Privacy = who,
			AddressDetail = detail,
			PopupFields = who == WebMapPrivacy.Internal ? popups.Select(f => f.Key).ToList() : Array.Empty<string>(),
			Basemap = background,
			Summary = e == null ? null : ReadTools.BuildSummary(store, e, out _),
		};
		string folder = AiOutputs.FreePath(sde.DatabasePath, heading + " web map", "Web map", "");
		WebMapResult result = null;
		Write(folder, () => result = WebMapExport.Write(files, store.Reference, options, folder), "the web map");
		log.LogInformation("make_web_map wrote {Folder} ({Privacy}, {Count} features)", folder, who, result.Features);
		List<string> notes = new List<string>();
		if (who == WebMapPrivacy.Public && popups.Count > 0)
		{
			notes.Add("Public maps show no fields in their popups, so popupFields was not used.");
		}
		if (result.Features == 0)
		{
			notes.Add("No structure had coordinates, so the map is empty.");
		}
		return new WebMapOutput(sde.DatabasePath, folder, result.IndexPath, who == WebMapPrivacy.Public ? "Public" : "Internal", result.Features,
			result.SkippedNoCoordinates, options.Summary != null, skipped, notes.Count == 0 ? null : string.Join(" ", notes));
	}

	/// <summary>
	/// True for the owner's own details (name, phones, mailing address), which GIS files leave out unless asked: they
	/// are personal information.
	/// </summary>
	internal static bool IsPersonal(FieldInfo f) => f.Model == FieldModel.Property
		&& (f.Member.StartsWith("Owner", StringComparison.Ordinal) || f.Member.StartsWith("Mailing", StringComparison.Ordinal) || f.Member is "PhoneNumber" or "CellPhone");

	/// <summary>The assessments loaded in the order given.</summary>
	private static List<AssessmentFile> Ordered(SdeStore store, List<Guid> ids)
	{
		Dictionary<Guid, int> order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
		return store.LoadAssessments(ids).OrderBy(f => order[f.Assessment.AssessmentID]).ToList();
	}

	/// <summary>
	/// Runs a writer, turning a file system failure into a tool error that names what couldn't be written. When the
	/// writer fails, the name taken for the output is given back.
	/// </summary>
	private void Write(string path, Action write, string what)
	{
		try
		{
			write();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AiOutputs.Release(path);
			log.LogWarning(ex, "Couldn't write {Path}", path);
			throw new McpException($"SDE couldn't write {what} in {Path.GetDirectoryName(path)}: {ex.Message}");
		}
		catch
		{
			AiOutputs.Release(path);
			throw;
		}
	}

	private static void Text(Utf8JsonWriter w, string name, string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			w.WriteNull(name);
		}
		else
		{
			w.WriteString(name, value);
		}
	}

	private static void Number(Utf8JsonWriter w, string name, decimal? value)
	{
		if (value is decimal d)
		{
			w.WriteNumber(name, d);
		}
		else
		{
			w.WriteNull(name);
		}
	}
}
