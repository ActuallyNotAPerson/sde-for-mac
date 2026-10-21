"""Writes the synthetic legacy .xls fixtures used by tests/Sde.Interop.Tests.

Needs xlwt (BSD): python3 -m pip install xlwt, then python3 -I tools/fixtures/make_xls.py
Writes tests/fixtures/sheets/parcels.xls and tests/fixtures/accela/inspections-wide.xls. All data is invented.
"""
import csv
import datetime
import os
import re

import xlwt

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

ZIP = xlwt.easyxf(num_format_str="00000")
DATE = xlwt.easyxf(num_format_str="YYYY-MM-DD")
STAMP = xlwt.easyxf(num_format_str="YYYY-MM-DD HH:MM:SS")
BOLD = xlwt.easyxf("font: bold on")


def parcels(path):
    """A parcel list in the columns Enterprise Import expects, with every kind of cell the reader must handle."""
    wb = xlwt.Workbook(encoding="utf-8")
    ws = wb.add_sheet("Parcels")
    # Two blank rows and a blank column above and left of the table, as exports often have.
    top, left = 2, 1
    header = ["Owner", "Address", "City", "ST", "County", "Zip", "Lat", "Lon", "Year", "Panel Date", "Inspected", "Vacant", "Notes", "SqFt"]
    for c, h in enumerate(header):
        ws.write(top, left + c, h, BOLD)
    rows = [
        ["Doe, John", "123 Martin Luther King Blvd", "Exampleville", "OH", "Union County", (5012, ZIP), 39.1031, -84.512, 1962,
         (datetime.date(2015, 9, 30), DATE), (datetime.datetime(2026, 10, 7, 14, 30), STAMP), True, "Café on the corner", 1450.5],
        ["Smith, Jane Q", "45 Elm St.", "Exampleville", "ohio", "union", (43703, ZIP), None, None, None,
         None, None, False, None, None],
        [None] * 14,
        ["Roe, Rick", "77 Oak Lane", "Dayton", "OH", "Montgomery", "45402", 39.7589, -84.1916, 1950,
         (datetime.date(2019, 3, 1), DATE), None, None, "text zip stays text", 980],
    ]
    for r, row in enumerate(rows):
        for c, value in enumerate(row):
            if value is None:
                continue
            if isinstance(value, tuple):
                ws.write(top + 1 + r, left + c, value[0], value[1])
            else:
                ws.write(top + 1 + r, left + c, value)
    second = wb.add_sheet("Other")
    second.write(0, 0, "Name")
    second.write(0, 1, "Value")
    second.write(1, 0, "alpha")
    second.write(1, 1, 7)
    wb.save(path)


def accela(csv_path, path):
    """The Accela fixture as a workbook: plain whole numbers become number cells, everything else text."""
    wb = xlwt.Workbook(encoding="utf-8")
    ws = wb.add_sheet("Inspections")
    with open(csv_path, newline="", encoding="utf-8") as f:
        for r, row in enumerate(csv.reader(f)):
            for c, value in enumerate(row):
                if value == "":
                    continue
                if re.fullmatch(r"[1-9][0-9]*", value):
                    ws.write(r, c, int(value))
                else:
                    ws.write(r, c, value)
    wb.save(path)


if __name__ == "__main__":
    parcels(os.path.join(ROOT, "tests", "fixtures", "sheets", "parcels.xls"))
    accela(os.path.join(ROOT, "tests", "fixtures", "accela", "inspections-wide.csv"),
           os.path.join(ROOT, "tests", "fixtures", "accela", "inspections-wide.xls"))
