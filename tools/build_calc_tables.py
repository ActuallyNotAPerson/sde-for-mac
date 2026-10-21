#!/usr/bin/env python3
"""Build the compact element tables embedded in Sde.Calculation from FEMA's SDE JSON.

FEMA ships JSON/Residential.json (90 MB, 378,000 rows, 94,500 of them blank),
JSON/Commercial.json and JSON/Lookup.json. The calculation engine only ever uses
rows whose Element is non-blank, so we keep exactly those rows, in file order,
with the Percentage kept as FEMA's original *string* (decimal scale matters).

Output (src/Sde.Calculation/Data/):
  residential.tsv.gz  ResidenceID FoundationID SuperstructureID RoofCoveringID ExteriorFinishID HvacID StoryID Element Percentage
  commercial.tsv.gz   StoryID StructureUseID SprinklerID ConveyanceID Element Percentage
  lookup.json         byte-for-byte copy of FEMA's Lookup.json

Usage: tools/build_calc_tables.py [path/to/extracted/JSON]
Tests (CalculationParityTests) prove the port loading these files produces results
identical to FEMA's original engine loading the original JSON.
"""
import gzip
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
src = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "extracted" / "JSON"
out = ROOT / "src" / "Sde.Calculation" / "Data"
out.mkdir(parents=True, exist_ok=True)


def write_tsv(path: Path, header: list[str], rows: list[list[str]]) -> None:
    # mtime=0 keeps the output byte-identical between runs.
    with open(path, "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0, compresslevel=9) as gz:
        gz.write(("\t".join(header) + "\n").encode("utf-8"))
        for r in rows:
            for v in r:
                if "\t" in v or "\n" in v:
                    raise ValueError(f"unexpected separator in {v!r}")
            gz.write(("\t".join(r) + "\n").encode("utf-8"))


res = json.loads((src / "Residential.json").read_text(encoding="utf-8-sig"))
res_keys = ["ResidenceID", "FoundationID", "SuperStructureID", "RoofCoveringID", "ExteriorFinishID", "HvacID", "StoryID"]
res_rows = [[str(x[k]) for k in res_keys] + [x["Element"], x["Percentage"]] for x in res if x["Element"] != ""]
write_tsv(out / "residential.tsv.gz",
          ["ResidenceID", "FoundationID", "SuperstructureID", "RoofCoveringID", "ExteriorFinishID", "HvacID", "StoryID", "Element", "Percentage"],
          res_rows)

com = json.loads((src / "Commercial.json").read_text(encoding="utf-8-sig"))
com_keys = ["StoryID", "StructureUseID", "SprinklerID", "ConveyanceID"]
com_rows = [[str(x[k]) for k in com_keys] + [x["Element"], x["Percentage"]] for x in com if x["Element"] != ""]
write_tsv(out / "commercial.tsv.gz", com_keys + ["Element", "Percentage"], com_rows)

shutil.copyfile(src / "Lookup.json", out / "lookup.json")

print(f"residential: {len(res_rows):,} rows (of {len(res):,})  -> {(out / 'residential.tsv.gz').stat().st_size:,} bytes")
print(f"commercial:  {len(com_rows):,} rows (of {len(com):,})  -> {(out / 'commercial.tsv.gz').stat().st_size:,} bytes")
print(f"lookup.json: {(out / 'lookup.json').stat().st_size:,} bytes")
