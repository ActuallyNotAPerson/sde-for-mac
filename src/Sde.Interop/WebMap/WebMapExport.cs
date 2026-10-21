using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Records;
using Sde.Interop.Internal;

namespace Sde.Interop.WebMap;

/// <summary>Who the web map is for, which decides how much it shows.</summary>
public enum WebMapPrivacy
{
	/// <summary>Full detail for the office and partners: one dot per structure, with its address and the chosen fields.</summary>
	Internal,
	/// <summary>For the public: no owners, no exact locations. Structures are counted in squares of the grid.</summary>
	Public,
}

/// <summary>How a public web map names places.</summary>
public enum PublicAddressDetail
{
	/// <summary>No addresses at all.</summary>
	Hidden,
	/// <summary>"100 block of River St".</summary>
	Block,
}

/// <summary>The background map tiles. A web map opens without internet, but tiles need it.</summary>
public enum WebMapBasemap
{
	UsgsTopo,
	OpenStreetMap,
	None,
}

/// <summary>Choices for a web map.</summary>
public sealed class WebMapOptions
{
	public string Title { get; init; } = "Damage assessments";

	public WebMapPrivacy Privacy { get; init; } = WebMapPrivacy.Internal;

	public PublicAddressDetail AddressDetail { get; init; } = PublicAddressDetail.Block;

	/// <summary>The side of a public map's grid squares, in metres.</summary>
	public double CellMeters { get; init; } = 200;

	/// <summary>FieldCatalog keys shown in each structure's popup (internal maps only; owner fields are never shown on a public map).</summary>
	public IReadOnlyList<string> PopupFields { get; init; } = Array.Empty<string>();

	public WebMapBasemap Basemap { get; init; } = WebMapBasemap.UsgsTopo;

	/// <summary>An event summary shown beside the map, or null.</summary>
	public EventSummary Summary { get; init; }

	public decimal SubstantialPercent { get; init; } = Determinations.DefaultSubstantialPercent;

	public decimal ClosePercent { get; init; } = Determinations.DefaultClosePercent;

	public DateTime? GeneratedOn { get; init; }
}

/// <summary>What a web map export wrote.</summary>
public sealed class WebMapResult
{
	/// <summary>The folder's index.html, which opens the map in any browser.</summary>
	public string IndexPath { get; init; }

	/// <summary>Dots (internal) or grid squares (public) on the map.</summary>
	public int Features { get; set; }

	/// <summary>Assessments left out because their structure has no coordinates.</summary>
	public int SkippedNoCoordinates { get; set; }
}

/// <summary>
/// A web map that can be shared as a folder or put on any web server: index.html with Leaflet (BSD-2-Clause) inlined, the
/// data as GeoJSON, a legend and popups. It opens without internet (only the background tiles need it). A public map
/// shows no owners and no exact locations: structures are counted in grid squares, and addresses are hidden or cut to
/// the block.
/// </summary>
public static class WebMapExport
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private static readonly DeterminationClass[] LegendOrder =
	{
		DeterminationClass.Substantial, DeterminationClass.Close, DeterminationClass.NotSubstantial, DeterminationClass.Undetermined, DeterminationClass.Incomplete,
	};

	/// <summary>The dot colour for a result, the same as the app's map and the event summary.</summary>
	public static string Colour(DeterminationClass c) => c switch
	{
		DeterminationClass.Substantial => "#C62828",
		DeterminationClass.Close => "#F08C00",
		DeterminationClass.NotSubstantial => "#2E8B4B",
		_ => "#8A94A3",
	};

	/// <summary>Writes index.html and structures.geojson into <paramref name="folder"/> (made if needed).</summary>
	public static WebMapResult Write(IEnumerable<AssessmentFile> assessments, ReferenceData reference, WebMapOptions options, string folder)
	{
		options ??= new WebMapOptions();
		Directory.CreateDirectory(folder);
		List<AssessmentFile> files = assessments.Where(f => f?.Assessment != null && f.Property != null).ToList();
		List<AssessmentFile> placed = files.Where(f => HasLocation(f.Property)).ToList();
		WebMapResult result = new WebMapResult { IndexPath = Path.Combine(folder, "index.html"), SkippedNoCoordinates = files.Count - placed.Count };

		string geoJson = options.Privacy == WebMapPrivacy.Public
			? PublicFeatures(placed, reference, options, result)
			: InternalFeatures(placed, reference, options, result);
		File.WriteAllText(Path.Combine(folder, "structures.geojson"), geoJson, new UTF8Encoding(false));
		File.WriteAllText(result.IndexPath, Page(geoJson, options), new UTF8Encoding(false));
		File.WriteAllText(Path.Combine(folder, "LICENSE-Leaflet.txt"), Asset("LICENSE"), new UTF8Encoding(false));
		return result;
	}

	private static bool HasLocation(PropertyRecord p) => p.Latitude is decimal lat && p.Longitude is decimal lon
		&& !(lat == 0m && lon == 0m) && Math.Abs(lat) <= 90m && Math.Abs(lon) <= 180m;

	private static string InternalFeatures(List<AssessmentFile> files, ReferenceData reference, WebMapOptions options, WebMapResult result)
	{
		List<FieldInfo> fields = options.PopupFields.Select(FieldCatalog.Get).Where(f => f != null).ToList();
		using MemoryStream buffer = new MemoryStream();
		using (Utf8JsonWriter w = new Utf8JsonWriter(buffer))
		{
			w.WriteStartObject();
			w.WriteString("type", "FeatureCollection");
			w.WriteStartArray("features");
			foreach (AssessmentFile f in files)
			{
				DeterminationClass c = Determinations.Classify(f.Assessment, options.SubstantialPercent, options.ClosePercent);
				string address = Text.Join(f.Property.StreetNumber, f.Property.StreetName, reference?.LookupName(f.Property.StreetSuffixID));
				StringBuilder popup = new StringBuilder();
				popup.Append("<strong>").Append(Html(string.IsNullOrEmpty(address) ? "Structure with no address" : address)).Append("</strong>");
				popup.Append("<br>").Append(Html(Determinations.Label(c)));
				if (f.Assessment.PercentDamage is decimal pd)
				{
					popup.Append(": ").Append(Html(pd.ToString("0.0", Us))).Append("% damaged");
				}
				foreach (FieldInfo field in fields)
				{
					string value = FieldCatalog.Display(field, FieldCatalog.GetValue(f, field), reference);
					if (!string.IsNullOrWhiteSpace(value))
					{
						popup.Append("<br>").Append(Html(field.Label)).Append(": ").Append(Html(value));
					}
				}
				Point(w, (double)f.Property.Longitude!.Value, (double)f.Property.Latitude!.Value, c, 1, popup.ToString());
				result.Features++;
			}
			w.WriteEndArray();
			w.WriteEndObject();
		}
		return Encoding.UTF8.GetString(buffer.ToArray());
	}

	/// <summary>Grid squares of about <see cref="WebMapOptions.CellMeters"/> with how many structures of each result they hold, drawn at the square's centre.</summary>
	private static string PublicFeatures(List<AssessmentFile> files, ReferenceData reference, WebMapOptions options, WebMapResult result)
	{
		double meanLat = files.Count == 0 ? 0 : files.Average(f => (double)f.Property.Latitude!.Value);
		double cellLat = options.CellMeters / 111_320.0;
		double cellLon = options.CellMeters / (111_320.0 * Math.Max(0.1, Math.Cos(meanLat * Math.PI / 180)));
		IEnumerable<IGrouping<(long Row, long Col), AssessmentFile>> cells = files.GroupBy(f => (
			Row: (long)Math.Floor((double)f.Property.Latitude!.Value / cellLat),
			Col: (long)Math.Floor((double)f.Property.Longitude!.Value / cellLon)));
		using MemoryStream buffer = new MemoryStream();
		using (Utf8JsonWriter w = new Utf8JsonWriter(buffer))
		{
			w.WriteStartObject();
			w.WriteString("type", "FeatureCollection");
			w.WriteStartArray("features");
			foreach (IGrouping<(long Row, long Col), AssessmentFile> cell in cells.OrderBy(g => g.Key.Row).ThenBy(g => g.Key.Col))
			{
				Dictionary<DeterminationClass, int> counts = cell.GroupBy(f => Determinations.Classify(f.Assessment, options.SubstantialPercent, options.ClosePercent))
					.ToDictionary(g => g.Key, g => g.Count());
				DeterminationClass worst = LegendOrder.First(c => counts.ContainsKey(c));
				StringBuilder popup = new StringBuilder();
				int n = cell.Count();
				popup.Append("<strong>").Append(n.ToString(Us)).Append(n == 1 ? " structure" : " structures").Append("</strong>");
				foreach (DeterminationClass c in LegendOrder.Where(counts.ContainsKey))
				{
					popup.Append("<br>").Append(Html(Determinations.Label(c))).Append(": ").Append(counts[c].ToString(Us));
				}
				if (options.AddressDetail == PublicAddressDetail.Block)
				{
					foreach (string block in cell.Select(f => Block(f.Property, reference)).Where(b => b != null).Distinct().OrderBy(b => b).Take(6))
					{
						popup.Append("<br>").Append(Html(block));
					}
				}
				double lat = (cell.Key.Row + 0.5) * cellLat;
				double lon = (cell.Key.Col + 0.5) * cellLon;
				Point(w, lon, lat, worst, n, popup.ToString());
				result.Features++;
			}
			w.WriteEndArray();
			w.WriteEndObject();
		}
		return Encoding.UTF8.GetString(buffer.ToArray());
	}

	/// <summary>"100 block of River St" for 123 River St; null without a street.</summary>
	public static string Block(PropertyRecord p, ReferenceData reference)
	{
		string street = Text.Join(p.StreetName, reference?.LookupName(p.StreetSuffixID));
		if (string.IsNullOrWhiteSpace(street))
		{
			return null;
		}
		string digits = new string((p.StreetNumber ?? "").TakeWhile(char.IsDigit).ToArray());
		return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
			? $"{number / 100 * 100} block of {street}"
			: street;
	}

	private static void Point(Utf8JsonWriter w, double lon, double lat, DeterminationClass c, int count, string popup)
	{
		w.WriteStartObject();
		w.WriteString("type", "Feature");
		w.WriteStartObject("geometry");
		w.WriteString("type", "Point");
		w.WriteStartArray("coordinates");
		w.WriteNumberValue(Math.Round(lon, 6));
		w.WriteNumberValue(Math.Round(lat, 6));
		w.WriteEndArray();
		w.WriteEndObject();
		w.WriteStartObject("properties");
		w.WriteString("result", Determinations.Label(c));
		w.WriteString("colour", Colour(c));
		w.WriteNumber("count", count);
		w.WriteString("popup", popup);
		w.WriteEndObject();
		w.WriteEndObject();
	}

	private static string Html(string s) => WebUtility.HtmlEncode(s ?? "");

	private static string Page(string geoJson, WebMapOptions o)
	{
		string generated = (o.GeneratedOn ?? DateTime.Now).ToString("MMMM d, yyyy", Us);
		string tiles = o.Basemap switch
		{
			WebMapBasemap.UsgsTopo => "{url: 'https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/{z}/{y}/{x}', attribution: 'Map: <a href=\"https://www.usgs.gov/programs/national-geospatial-program/national-map\">USGS The National Map</a>', maxZoom: 16}",
			WebMapBasemap.OpenStreetMap => "{url: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png', attribution: '&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors', maxZoom: 19}",
			_ => "null",
		};
		StringBuilder legend = new StringBuilder();
		foreach (DeterminationClass c in LegendOrder)
		{
			legend.Append("<div><span class=\"dot\" style=\"background:").Append(Colour(c)).Append("\"></span>").Append(Html(Determinations.Label(c))).Append("</div>");
		}
		StringBuilder summary = new StringBuilder();
		if (o.Summary is EventSummary s)
		{
			summary.Append("<section class=\"summary\"><h2>").Append(Html(s.EventName ?? "Summary")).Append("</h2>");
			if (s.Event?.DateRange is string dates)
			{
				summary.Append("<p>").Append(Html(dates)).Append("</p>");
			}
			summary.Append("<p>").Append(s.AssessmentCount.ToString("N0", Us)).Append(" assessments</p><ul>");
			foreach (DeterminationClass c in LegendOrder)
			{
				summary.Append("<li>").Append(Html(Determinations.Label(c))).Append(": ").Append(s.Count(c).ToString("N0", Us)).Append("</li>");
			}
			summary.Append("</ul></section>");
		}
		string note = o.Privacy == WebMapPrivacy.Public
			? $"Structures are counted in squares about {o.CellMeters.ToString("N0", Us)} m across; exact locations and owners are not shown."
			: "Each dot is one structure, coloured by its result.";
		return $$"""
			<!DOCTYPE html>
			<html lang="en">
			<head>
			<meta charset="utf-8">
			<meta name="viewport" content="width=device-width, initial-scale=1">
			<title>{{Html(o.Title)}}</title>
			<style>
			{{Asset("leaflet.css")}}
			html, body { margin: 0; height: 100%; font: 14px/1.4 -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; color: #1F2328; background: #FFFFFF; }
			#map { position: absolute; inset: 0; }
			.panel { position: absolute; top: 12px; left: 56px; z-index: 1000; max-width: 300px; background: rgba(255,255,255,0.96); border: 1px solid #D0D7DE; border-radius: 10px; padding: 12px 14px; }
			.panel h1 { font-size: 17px; margin: 0 0 4px; }
			.panel h2 { font-size: 14px; margin: 10px 0 2px; }
			.panel p { margin: 2px 0; color: #59636E; }
			.panel ul { margin: 4px 0 0; padding-left: 18px; }
			.legend div { margin: 2px 0; }
			.dot { display: inline-block; width: 11px; height: 11px; border-radius: 50%; margin-right: 6px; vertical-align: -1px; border: 1px solid rgba(0,0,0,0.35); }
			@media (max-width: 600px) { .panel { left: 12px; right: 12px; top: auto; bottom: 24px; max-width: none; } }
			</style>
			</head>
			<body>
			<div id="map" role="application" aria-label="Map of {{Html(o.Title)}}"></div>
			<div class="panel">
			<h1>{{Html(o.Title)}}</h1>
			<p>{{Html(note)}}</p>
			<div class="legend">{{legend}}</div>
			{{summary}}
			<p>Made with SDE for Mac on {{Html(generated)}}.</p>
			</div>
			<!-- Leaflet 1.9.4, BSD-2-Clause license: Copyright (c) 2010-2023, Volodymyr Agafonkin; Copyright (c) 2010-2011, CloudMade. See LICENSE-Leaflet.txt. -->
			<script>
			{{Asset("leaflet.js")}}
			</script>
			<script>
			var data = {{geoJson}};
			var tiles = {{tiles}};
			var map = L.map('map', { preferCanvas: true });
			if (tiles) { L.tileLayer(tiles.url, { attribution: tiles.attribution, maxZoom: tiles.maxZoom }).addTo(map); }
			var layer = L.geoJSON(data, {
				pointToLayer: function (f, latlng) {
					var r = f.properties.count > 1 ? Math.min(22, 6 + 3 * Math.sqrt(f.properties.count)) : 6;
					return L.circleMarker(latlng, { radius: r, color: '#1F2328', weight: 1, fillColor: f.properties.colour, fillOpacity: 0.85 });
				},
				onEachFeature: function (f, l) { l.bindPopup(f.properties.popup); }
			}).addTo(map);
			if (data.features.length > 0) { map.fitBounds(layer.getBounds(), { padding: [40, 40], maxZoom: 17 }); } else { map.setView([39.8, -98.6], 4); }
			</script>
			</body>
			</html>
			""";
	}

	private static string Asset(string name)
	{
		using Stream s = typeof(WebMapExport).Assembly.GetManifestResourceStream("Sde.Interop.Leaflet." + name)
			?? throw new InvalidOperationException("Missing embedded resource Sde.Interop.Leaflet." + name);
		using StreamReader r = new StreamReader(s);
		return r.ReadToEnd();
	}
}
