#!/bin/sh
# Exports FEMA's reference tables from SDEDatabase.mdb into src/Sde.Data/Seed/*.csv (embedded in Sde.Data).
# A new SDE database on the Mac is created from these, exactly like FEMA shipped an empty SDEDatabase.mdb.
# Requires mdbtools (brew install mdbtools). Usage: tools/build_seed.sh [path/to/SDEDatabase.mdb]
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MDB="${1:-$ROOT/extracted/Database/SDEDatabase.mdb}"
OUT="$ROOT/src/Sde.Data/Seed"
mkdir -p "$OUT"
for t in LookupType Lookup State County StreetSuffixAbbreviation CostAdjustment_ResidenceType DepreciationValue \
         ValidationRule LatLongValidation InspectionStatus ResidenceType RoofCoveringType StructureShape \
         CustomField UserDefault DatabaseInfo; do
  mdb-export -D '%Y-%m-%d %H:%M:%S' -T '%Y-%m-%d %H:%M:%S' "$MDB" "$t" > "$OUT/$t.csv"
  printf '%-30s %6s rows\n' "$t" "$(($(wc -l < "$OUT/$t.csv") - 1))"
done
