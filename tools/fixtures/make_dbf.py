"""Writes the synthetic dBASE (.dbf) shapefile-attribute fixtures used by tests/Sde.Interop.Tests. Pure Python, no packages.

Run: python3 -I tools/fixtures/make_dbf.py
Writes into tests/fixtures/sheets/:
  parcels.dbf         Windows-1252 (language driver 0x03), no .cpg; one deleted record, a memo field, every field type
  parcels-utf8.dbf    UTF-8, announced by parcels-utf8.cpg; parcels-utf8.shp is an empty stand-in so a .shp path can be tested
  cyrillic.dbf        Windows-1251 named only by the language driver byte 0xC9
All data is invented.
"""
import datetime
import os
import struct

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "tests", "fixtures", "sheets")

# (name, type, length, decimals)
PARCEL_FIELDS = [
    ("PARCEL", "C", 14, 0),
    ("OWNER", "C", 24, 0),
    ("ACRES", "N", 10, 2),
    ("YEAR", "N", 4, 0),
    ("VALUE", "F", 12, 3),
    ("SALEDATE", "D", 8, 0),
    ("VACANT", "L", 1, 0),
    ("NOTES", "M", 10, 0),
]


def text(value, length, encoding):
    raw = value.encode(encoding)
    assert len(raw) <= length, value
    return raw + b" " * (length - len(raw))


def number(value, length, decimals):
    if value is None:
        return b" " * length
    s = ("%.*f" % (decimals, value)) if decimals else str(int(value))
    return s.rjust(length).encode("ascii")


def write_dbf(path, fields, records, language_driver, encoding):
    """records: list of (deleted, [values]) with values already python str / float / date / bool / None."""
    record_size = 1 + sum(f[2] for f in fields)
    header_size = 32 + 32 * len(fields) + 1
    today = datetime.date(2026, 10, 20)
    out = bytearray()
    out += struct.pack("<BBBBIHH", 0x03, today.year - 1900, today.month, today.day, len(records), header_size, record_size)
    out += bytes(17) + bytes([language_driver]) + bytes(2)
    for name, ftype, length, decimals in fields:
        out += name.encode("ascii").ljust(11, b"\0") + ftype.encode("ascii") + bytes(4) + bytes([length, decimals]) + bytes(14)
    out += b"\x0d"
    for deleted, values in records:
        out += b"*" if deleted else b" "
        for (name, ftype, length, decimals), value in zip(fields, values):
            if ftype == "C":
                out += text(value, length, encoding)
            elif ftype in "NF":
                out += number(value, length, decimals)
            elif ftype == "D":
                out += (value.strftime("%Y%m%d") if value else "").ljust(length).encode("ascii")
            elif ftype == "L":
                out += (b"?" if value is None else b"T" if value else b"F")
            else:
                out += b" " * length
    out += b"\x1a"
    with open(path, "wb") as f:
        f.write(out)


def parcel_records(owner_cafe):
    return [
        (False, ["0123-0045-0067", "Pat Example", 0.25, 1962, 145000.5, datetime.date(2015, 9, 30), True, None]),
        (True, ["9999-DELETED", "Gone Away", 1.0, 2000, 1.0, None, False, None]),
        (False, ["0123-0045-0070", owner_cafe, 12.5, 1978, 210000, datetime.date(2019, 3, 1), False, None]),
        (False, ["0145-0001-0001", "Blank Numbers", None, None, None, None, None, None]),
    ]


def main():
    os.makedirs(OUT, exist_ok=True)
    write_dbf(os.path.join(OUT, "parcels.dbf"), PARCEL_FIELDS, parcel_records("Renée Café"), 0x03, "cp1252")
    write_dbf(os.path.join(OUT, "parcels-utf8.dbf"), PARCEL_FIELDS, parcel_records("Renée Café"), 0x00, "utf-8")
    with open(os.path.join(OUT, "parcels-utf8.cpg"), "w") as f:
        f.write("UTF-8")
    # A shapefile's main file begins with big-endian file code 9994; the reader never opens it.
    with open(os.path.join(OUT, "parcels-utf8.shp"), "wb") as f:
        f.write(struct.pack(">i", 9994) + bytes(96))
    cyrillic_fields = [("NAME", "C", 20, 0), ("ID", "N", 5, 0)]
    write_dbf(os.path.join(OUT, "cyrillic.dbf"), cyrillic_fields,
              [(False, ["Привет мир", 7])], 0xC9, "cp1251")


if __name__ == "__main__":
    main()
