#!/bin/bash
# Makes tests/Sde.Gis.Tests/Fixtures/town: synthetic parcels and structures (tools/gis/make_fixtures.py), then the
# same data converted by GDAL (ogr2ogr) and QGIS (qgis_process), so the tests read files other programs wrote:
#   parcels.geojson              WGS 84 source (written by the Python script)
#   parcels-stateplane.shp       NAD83 / Ohio South (ftUS), EPSG:3735, ESRI .prj, UTF-8 .cpg (GDAL)
#   parcels-cp1252.shp           the same in WGS 84 with a Windows-1252 .dbf and .cpg (GDAL)
#   parcels-stateplane.geojson   EPSG:3735 with a 2008-style "crs" member (GDAL)
#   parcels-wkt.csv              WKT geometry column (GDAL)
#   parcels-qgis.gpkg            GeoPackage in EPSG:3735 made by QGIS's "Reproject layer"
#   structures.csv               points by Latitude and Longitude (the Python script)
#   structures-xy.csv            the same points as State Plane X and Y in US survey feet (GDAL)
# The projected copies are made from the source read as NAD83 longitude and latitude (EPSG:4269), so GDAL and QGIS
# apply no datum shift: SDE treats NAD83 and WGS 84 as the same, and the tests compare the projection maths alone.
# Needs QGIS 4 (it uses the GDAL and qgis_process inside the app). Usage: tools/gis/make_fixtures.sh [/Applications/QGIS.app]
set -euo pipefail
QGIS_APP="${1:-$(ls -d /Applications/QGIS*.app | head -1)}"
BIN="$QGIS_APP/Contents/MacOS"
export PROJ_DATA="$QGIS_APP/Contents/Resources/qgis/proj"
export GDAL_DATA="$QGIS_APP/Contents/Resources/qgis/gdal"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../../tests/Sde.Gis.Tests/Fixtures/town"
mkdir -p "$OUT"
cd "$OUT"
rm -f parcels* structures*
python3 "$HERE/make_fixtures.py" "$OUT"
"$BIN/ogr2ogr" -f "ESRI Shapefile" -s_srs EPSG:4269 -t_srs EPSG:3735 -lco ENCODING=UTF-8 parcels-stateplane.shp parcels.geojson -nln parcels-stateplane
"$BIN/ogr2ogr" -f "ESRI Shapefile" -lco ENCODING=CP1252 parcels-cp1252.shp parcels.geojson -nln parcels-cp1252
"$BIN/ogr2ogr" -f GeoJSON -s_srs EPSG:4269 -t_srs EPSG:3735 -lco RFC7946=NO -lco COORDINATE_PRECISION=4 parcels-stateplane.geojson parcels.geojson
"$BIN/ogr2ogr" -f CSV -lco GEOMETRY=AS_WKT parcels-wkt.csv parcels.geojson
"$BIN/ogr2ogr" -f CSV -s_srs EPSG:4269 -t_srs EPSG:3735 -oo X_POSSIBLE_NAMES=Longitude -oo Y_POSSIBLE_NAMES=Latitude -oo KEEP_GEOM_COLUMNS=NO -lco GEOMETRY=AS_XY structures-xy.csv structures.csv
TMP="$(mktemp -d)"
"$BIN/ogr2ogr" -f GPKG -a_srs EPSG:4269 "$TMP/parcels-nad83.gpkg" parcels.geojson -nln parcels
QT_QPA_PLATFORM=offscreen "$BIN/qgis_process" run native:reprojectlayer --INPUT="$TMP/parcels-nad83.gpkg|layername=parcels" --TARGET_CRS=EPSG:3735 --OUTPUT="$OUT/parcels-qgis.gpkg" >/dev/null
rm -rf "$TMP"
ls -la "$OUT"
