# Online services: what to verify live

The online lookups in `src/Sde.Interop/Online` were written from the services' documented formats on a machine that could not reach census.gov or fema.gov. The tests use hand-written fixtures (`tests/Sde.Interop.Tests/Fixtures/online`). Before relying on them, check each item below on a machine with internet access.

## Census geocoder

- URL: `https://geocoding.geo.census.gov/geocoder/locations/onelineaddress?address=<text>&benchmark=Public_AR_Current&format=json`
- Confirm the `Public_AR_Current` benchmark still exists (`CensusGeocoder.Benchmark` changes it).
- Confirm the answer has `result.addressMatches[]`, and that each match has `matchedAddress` and `coordinates.x` (longitude) and `coordinates.y` (latitude).
- Confirm an address with no hit gives an empty `addressMatches` array (SDE then returns no match) rather than an error.

## FEMA National Flood Hazard Layer (NFHL)

- Base URL: `https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer` (`NfhlClient.BaseUrl`).
- Query: `{layer}/query?geometry=<lon>,<lat>&geometryType=esriGeometryPoint&inSR=4326&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&f=json`
- Layer IDs (`NfhlClient.FloodZoneLayer`, `PanelLayer`, `CommunityLayer`). Open the MapServer page and confirm the names match the numbers:
  - 28: Flood Hazard Zones. Fields used: `FLD_ZONE`, `ZONE_SUBTY`, `STATIC_BFE`, `V_DATUM`.
  - 3: FIRM Panels. Fields used: `FIRM_PAN`, `SUFFIX`, `EFF_DATE` (epoch milliseconds).
  - 22: Political Jurisdictions. Fields used: `CID`, `POL_NAME1`.
- Confirm `STATIC_BFE` is -9999 when there is no BFE (SDE maps it to none), and that `ZONE_SUBTY` is `FLOODWAY` for floodway polygons (SDE tests for the word, ignoring case).
- Confirm the exact `FLD_ZONE` spellings for zone X, zone A99, and V zones, and what `ZONE_SUBTY` says for shaded X, so `FloodInfoMapper` maps them correctly.
- A point on a boundary can hit several zone polygons. SDE reports the strictest: a floodway, then a V zone, then any other A zone, then shaded X, then unshaded X (`ZONE_SUBTY` "AREA OF MINIMAL FLOOD HAZARD" or blank), then anything else. Confirm that every shaded X subtype differs from the unshaded one.
- Confirm that a point outside any mapped area returns an empty `features` array, and that an overloaded server returns either an HTTP error or HTTP 200 with an `error` object (both are handled).
- Check that the service accepts the request without a token or referrer, and note its rate limits before using it for large batches (`OnlineBatch` defaults to 4 at a time).
- Check that `EFF_DATE` is the date wanted for SDE's FIRM panel date.

## USGS high-water marks (STN), v2.5

Used by Flood depth from high-water marks (`src/Sde.Interop/Online/StnClient.cs`). The base address can be changed in
Settings, Online services, in case USGS moves the service.

- Base URL: `https://api.waterdata.usgs.gov/stnservices/` (`StnClient.DefaultBaseUrl`).
- Events: `events/` (JSON array). Confirm each event has an id, a name and start and end dates, and that the list is
  not paged.
- Marks: `hwms/filteredhwms/?event=<id>&states=<codes>`, with comma-separated two-letter state codes. Confirm the
  answer is a JSON array and that marks carry `latitude_dd` (or `latitude`), `longitude_dd` (or `longitude`),
  `elev_ft`, `vertical_datum_name`, `hwm_quality_id` (or `hwm_quality_name`) and `hwm_type_name`.
- SDE reads only those fields. Confirm nothing else in a mark is needed, and that marks not yet surveyed have no
  `elev_ft` (SDE skips them and counts them).
- Confirm the datum names used in practice (for example "NAVD88" and "NGVD29") are the ones `VerticalDatums`
  recognises.
- Ask USGS whether an app may call the service directly, and whether it wants a User-Agent or a key. SDE sends
  "SDE-for-Mac/<version>" and retries 502, 503 and 504 twice.
- Check a large event (thousands of marks in one state) for size and time; the timeout is in `StnClient.Timeout`.

## FEMA flood zones as map tiles (NFHL export), v2.5

The Map page draws FEMA's flood zones over the background map by asking NFHL's MapServer for one picture per
256-pixel tile (`Basemap.FloodZones` in `src/Sde.App/Maps/Basemap.cs`).

- URL: `https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/export?bbox=<tile>&bboxSR=3857&imageSR=3857&size=256,256&dpi=96&format=png32&transparent=true&f=image&layers=show:28`
- Confirm layer 28 is still Flood Hazard Zones, that the answer is a transparent PNG, and how the server copes with a
  map's worth of tiles at once (TileCache asks a few at a time and keeps them on disk).
- Confirm the attribution FEMA wants shown on the map and on printed maps.

## Background maps, v2.5

- USGS The National Map: `https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/{z}/{y}/{x}`
  and `USGSImageryTopo`. Confirm the tile order ({z}/{y}/{x}), the maximum zoom, and the attribution text.
- OpenStreetMap (optional, and in web maps): `https://tile.openstreetmap.org/{z}/{x}/{y}.png`. Its tile usage policy
  asks for a real User-Agent (SDE sends one), caching (TileCache keeps tiles on disk), no bulk downloading, and the
  "© OpenStreetMap contributors" attribution. Check the policy again before each release.
- Web maps load their tiles in the viewer's browser, not through SDE.

## ArcGIS REST layers, v2.5

Map, Add layer, From an ArcGIS web address reads any public ArcGIS FeatureServer or MapServer layer
(`src/Sde.Gis/Online/ArcGisRestClient.cs`).

- It asks the layer's JSON for its fields, geometry type, `maxRecordCount` and whether it supports pagination and
  GeoJSON, then queries `query?where=1=1&outFields=*&outSR=4326&f=geojson` limited to the map's extent, paged with
  `resultOffset` until `exceededTransferLimit` is false. Layers that can't be paged are read by object id in batches.
- Try a few real servers (a county parcel FeatureServer, an older MapServer that answers only Esri JSON, and one
  behind a slow connection) and confirm the paging, the fallback, and the messages for a secured or missing layer.

## Photos

`PhotoMetadata` uses the MetadataExtractor library (Apache-2.0). It was tested against synthetic JPEGs only. Try a few real phone and camera photos (including HEIC if those are expected, which MetadataExtractor reads) to confirm the date and GPS position come through.
