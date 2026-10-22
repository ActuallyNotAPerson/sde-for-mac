Recorded ArcGIS REST answers for `ArcGisRestClientTests`, written by hand in the shapes ArcGIS Server 10.2 to 11.3 send.
Everything is synthetic: made-up parcels, flood zones and levees in the made-up town of Cedar Ford.

- `parcels-*`: a FeatureServer layer that pages (maxRecordCount 2). Page 1 flags `exceededTransferLimit` at the top
  level, page 2 under `properties` (as some servers do), page 3 is the last.
- `zones-*`: a MapServer layer without pagination: the first answer is cut off, so the client asks for the object ids
  and fetches them in batches.
- `old-*`: a 10.2 layer that can't answer in GeoJSON, so it is read as Esri JSON (a polygon with a hole and two parts).
- `error-*`, `raster-layer.json`: error answers and a layer that isn't features.
