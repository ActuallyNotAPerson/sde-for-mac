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
- Confirm that a point outside any mapped area returns an empty `features` array, and that an overloaded server returns either an HTTP error or HTTP 200 with an `error` object (both are handled).
- Check that the service accepts the request without a token or referrer, and note its rate limits before using it for large batches (`OnlineBatch` defaults to 4 at a time).
- Check that `EFF_DATE` is the date wanted for SDE's FIRM panel date.

## Photos

`PhotoMetadata` uses the MetadataExtractor library (Apache-2.0). It was tested against synthetic JPEGs only. Try a few real phone and camera photos (including HEIC if those are expected, which MetadataExtractor reads) to confirm the date and GPS position come through.
