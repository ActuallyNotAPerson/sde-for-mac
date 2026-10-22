#!/usr/bin/env python3
"""Writes the synthetic GIS fixtures' source files: made-up parcels and structures in the made-up town of Cedar Ford.

Nothing here is real: the parcel ids, streets, values and structures are invented. The coordinates are WGS 84
longitude and latitude in southwest Ohio so the State Plane (Ohio South) versions make sense.

Run tools/gis/make_fixtures.sh, which calls this and then converts the files with GDAL and QGIS.
"""
import json
import sys

LON0, LAT0 = -84.0500, 39.6200
DX, DY = 0.0004, 0.0003
COLS, ROWS = 6, 5
STREETS = ["Mill Race Road", "Rue Élan", "Cedar Ford Lane", "Sycamore Bend", "Old Ferry Road"]
ZONES = ["R-1", "R-2", "C-1"]


def r(v):
    return round(v, 7)


def ring(x0, y0, x1, y1):
    # Counter-clockwise outer ring (RFC 7946).
    return [[r(x0), r(y0)], [r(x1), r(y0)], [r(x1), r(y1)], [r(x0), r(y1)], [r(x0), r(y0)]]


def main(out):
    parcels = []
    n = 0
    for row in range(ROWS):
        for col in range(COLS):
            n += 1
            x0, y0 = LON0 + col * DX, LAT0 + row * DY
            x1, y1 = x0 + DX, y0 + DY
            rings = [ring(x0, y0, x1, y1)]
            geometry = {"type": "Polygon", "coordinates": rings}
            if n == 8:
                # A courtyard: a hole, clockwise.
                hole = ring(x0 + DX * 0.3, y0 + DY * 0.3, x0 + DX * 0.7, y0 + DY * 0.7)
                hole.reverse()
                geometry = {"type": "Polygon", "coordinates": [rings[0], hole]}
            if n == 30:
                # A two-part parcel: the lot and a detached strip across a lane.
                strip = ring(x1 + DX * 0.1, y0, x1 + DX * 0.3, y1)
                geometry = {"type": "MultiPolygon", "coordinates": [[rings[0]], [strip]]}
            props = {
                "parcel_id": "CF-%04d" % n,
                "house_no": 100 + 2 * col,
                "street": STREETS[row],
                "zone_code": ZONES[col % 3],
                "flood_zone": "AE" if row < 2 else ("A" if row == 2 else "X"),
                "land_value": 20000 + 1500 * n + 0.25,
                "year_built": None if n % 7 == 0 else 1950 + n,
                "has_garage": n % 2 == 0,
                "survey_date": "2024-03-%02d" % (1 + n % 28),
            }
            parcels.append({"type": "Feature", "id": n, "geometry": geometry, "properties": props})
    with open(out + "/parcels.geojson", "w", encoding="utf-8", newline="\n") as f:
        json.dump({"type": "FeatureCollection", "name": "parcels", "features": parcels}, f, ensure_ascii=False, indent=1)

    lines = ["Structure ID,Latitude,Longitude,Occupancy,First floor (ft),Tax code"]
    s = 0
    for row in range(ROWS):
        for col in range(COLS):
            if (row * COLS + col) % 3 == 2:
                continue
            s += 1
            lon = LON0 + (col + 0.5) * DX
            lat = LAT0 + (row + 0.5) * DY
            lines.append("S-%03d,%.7f,%.7f,%s,%.1f,%06d" % (s, lat, lon, "Residential" if col < 4 else "\"Commercial, small\"", 1.5 + 0.1 * s, 120 + s))
    # One on the shared edge of parcels 1 and 2, one outside town, and one with no location.
    lines.append("S-900,%.7f,%.7f,Residential,2.0,000900" % (LAT0 + 0.5 * DY, LON0 + DX))
    lines.append("S-901,%.7f,%.7f,Residential,2.0,000901" % (LAT0 + 0.5 * DY, LON0 + 9 * DX))
    lines.append("S-902,,,Residential,2.0,000902")
    with open(out + "/structures.csv", "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("wrote parcels.geojson (%d parcels) and structures.csv (%d rows)" % (len(parcels), len(lines) - 1))


if __name__ == "__main__":
    main(sys.argv[1])
