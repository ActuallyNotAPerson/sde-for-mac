#!/usr/bin/env python3
"""Writes src/Sde.Gis/Projections/EpsgUs.csv: the EPSG projected coordinate systems Sde.Gis knows by code.

These are the US State Plane (SPCS83) zones on NAD83, NAD83(HARN), NAD83(NSRS2007) and NAD83(2011), in metres,
US survey feet and international feet; the UTM zones on those datums and on WGS 84; WGS 84 / Pseudo-Mercator (3857);
and the CONUS Albers systems. Parameters come from the EPSG Dataset as copied into PROJ's proj.db (QGIS ships one).
Only the methods Sde.Gis implements are kept (Transverse Mercator, Lambert Conic Conformal 1SP and 2SP, Albers
Equal Area, Pseudo-Mercator). Angles are written in decimal degrees and false eastings and northings in metres.

Usage: python3 tools/gis/make_epsg_table.py /Applications/QGIS-final-4_2_2.app/Contents/Resources/qgis/proj/proj.db
"""
import os
import sqlite3
import sys
from decimal import Decimal

DATUMS = {4269: "NAD83", 4152: "NAD83(HARN)", 4759: "NAD83(NSRS2007)", 6318: "NAD83(2011)", 4326: "WGS 84"}
METHODS = {9807: "TM", 9802: "LCC2", 9801: "LCC1", 9822: "AEA", 1024: "WEBM"}
UNITS = {9001: 1.0, 9002: 0.3048, 9003: 1200.0 / 3937.0}
UNIT_NAMES = {9001: "m", 9002: "ft", 9003: "ftUS"}


def degrees(value, uom):
    if value is None:
        return None
    if uom == 9102:
        return float(value)
    if uom == 9110:  # sexagesimal DMS: DDD.MMSSsss
        text = format(Decimal(repr(float(value))), "f")
        negative = text.startswith("-")
        text = text.lstrip("-")
        whole, _, frac = text.partition(".")
        frac = (frac + "0000")
        minutes = int(frac[0:2])
        seconds = Decimal(frac[2:4] + "." + frac[4:]) if len(frac) > 4 else Decimal(frac[2:4])
        result = float(Decimal(whole) + Decimal(minutes) / 60 + seconds / 3600)
        return -result if negative else result
    if uom == 9201:  # unity (scale factor)
        return float(value)
    raise ValueError("unexpected angle unit %s" % uom)


def main():
    db = sqlite3.connect(sys.argv[1])
    rows = db.execute("""
        SELECT p.code, p.name, p.geodetic_crs_code, c.method_code, p.coordinate_system_code,
               c.param1_code, c.param1_value, c.param1_uom_code, c.param2_code, c.param2_value, c.param2_uom_code,
               c.param3_code, c.param3_value, c.param3_uom_code, c.param4_code, c.param4_value, c.param4_uom_code,
               c.param5_code, c.param5_value, c.param5_uom_code, c.param6_code, c.param6_value, c.param6_uom_code,
               c.param7_code, c.param7_value, c.param7_uom_code
        FROM projected_crs p JOIN conversion_table c ON c.auth_name = p.conversion_auth_name AND c.code = p.conversion_code
        WHERE p.auth_name = 'EPSG' AND p.deprecated = 0 AND p.geodetic_crs_auth_name = 'EPSG'
          AND p.geodetic_crs_code IN (4269, 4152, 4759, 6318, 4326)
          AND (c.name LIKE 'SPCS83 %' OR c.name LIKE 'UTM zone %' OR p.code IN (3857, 5070, 6350))
        ORDER BY p.code""").fetchall()
    out = ["code,name,datum,method,lat0,lon0,sp1,sp2,k0,fe_m,fn_m,unit"]
    for r in rows:
        code, name, geod, method, cs = r[0:5]
        if method not in METHODS:
            continue
        unit = db.execute("SELECT uom_code FROM axis WHERE coordinate_system_auth_name = 'EPSG' AND coordinate_system_code = ? LIMIT 1", (cs,)).fetchone()[0]
        p = {}
        for i in range(7):
            pc, pv, pu = r[5 + i * 3], r[6 + i * 3], r[7 + i * 3]
            if pc is None:
                continue
            pc = int(pc)
            if pc in (8806, 8807, 8826, 8827):
                p[pc] = float(pv) * UNITS[int(pu)]
            else:
                p[pc] = degrees(pv, int(pu))
        lat0 = p.get(8801, p.get(8821, 0.0))
        lon0 = p.get(8802, p.get(8822, 0.0))
        sp1 = p.get(8823, "")
        sp2 = p.get(8824, "")
        k0 = p.get(8805, 1.0)
        fe = p.get(8806, p.get(8826, 0.0))
        fn = p.get(8807, p.get(8827, 0.0))
        out.append(",".join([str(code), '"' + name.replace('"', "'") + '"', DATUMS[geod], METHODS[method], repr(lat0), repr(lon0),
                             repr(sp1) if sp1 != "" else "", repr(sp2) if sp2 != "" else "", repr(k0), repr(fe), repr(fn),
                             UNIT_NAMES[int(unit)]]))
    target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "Sde.Gis", "Projections", "EpsgUs.csv")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "w", newline="\n") as f:
        f.write("\n".join(out) + "\n")
    print("%d coordinate systems written to %s" % (len(out) - 1, os.path.normpath(target)))


if __name__ == "__main__":
    main()
