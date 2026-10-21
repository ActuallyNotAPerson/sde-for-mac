#!/bin/sh
# Sets up the answer key: FEMA's original SDE 3.0 files, which are not in the repo.
#
#   installer/   the unzipped installer
#   extracted/   the MSI unpacked (FEMA's JSON tables and empty SDEDatabase.mdb; the parity and seed tests read these)
#   decomp/      FEMA's assemblies decompiled with ILSpy, plus a dump of the database (reference only)
#   reference/OriginalCalc/OriginalCalc.csproj   compiles FEMA's decompiled engine, unedited, for the parity tests
#
# Without these the tests that need them are skipped ("Answer key not set up").
#
# Usage:
#   tools/answer-key/setup.sh path/to/SDE3_04062018.zip     (or the .msi, or a folder holding it)
#   SDE_INSTALLER_URL=https://... tools/answer-key/setup.sh  (downloads the zip first)
#   add --force to redo steps that are already done
#
# Needs: unzip, msiextract (msitools), mdbtools, the .NET SDK, and curl for a download.
# On macOS: brew install msitools mdbtools. On Debian/Ubuntu: apt install msitools mdbtools unzip.
set -eu
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/tools/answer-key"
ILSPY_VERSION="11.1.0.9782"
FORCE=0
SOURCE=""
for a in "$@"; do
  case "$a" in
    --force) FORCE=1 ;;
    *) SOURCE="$a" ;;
  esac
done

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "Missing $1. $2" >&2; exit 1; }
}
sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# 1. The installer.
mkdir -p "$ROOT/installer"
if [ -z "$SOURCE" ] && [ -n "${SDE_INSTALLER_URL:-}" ]; then
  need curl "Install curl."
  SOURCE="$ROOT/installer/SDE3_04062018.zip"
  echo "Downloading $SDE_INSTALLER_URL"
  curl -fL --retry 3 -o "$SOURCE" "$SDE_INSTALLER_URL"
fi
if [ -n "$SOURCE" ]; then
  case "$SOURCE" in
    *.zip) need unzip "Install unzip."; unzip -oq "$SOURCE" -d "$ROOT/installer" ;;
    *.msi) cp "$SOURCE" "$ROOT/installer/" ;;
    *) [ -d "$SOURCE" ] && [ "$(cd "$SOURCE" && pwd)" != "$ROOT/installer" ] && cp -R "$SOURCE"/. "$ROOT/installer/" ;;
  esac
fi
MSI="$(find "$ROOT/installer" -iname 'SDE3-Installer.msi' | head -n 1)"
if [ -z "$MSI" ]; then
  echo "FEMA's SDE 3.0 installer wasn't found. Pass the zip (SDE3_04062018.zip) or set SDE_INSTALLER_URL." >&2
  exit 1
fi

# The MSI must be the one the tests were written against.
PINNED="$(grep -v '^#' "$HERE/installer.sha256" | tr -d ' \n')"
ACTUAL="$(sha256 "$MSI")"
if [ "$PINNED" = "UNPINNED" ]; then
  echo "Warning: no pinned hash yet. This MSI's SHA-256 is $ACTUAL; put it in tools/answer-key/installer.sha256."
elif [ "$PINNED" != "$ACTUAL" ]; then
  echo "This MSI's SHA-256 is $ACTUAL, but tools/answer-key/installer.sha256 expects $PINNED. Not FEMA's SDE 3.0.0 build?" >&2
  exit 1
fi

# 2. Unpack it.
if [ "$FORCE" = 1 ] || [ ! -d "$ROOT/extracted/JSON" ]; then
  need msiextract "Install msitools."
  rm -rf "$ROOT/extracted"
  mkdir -p "$ROOT/extracted"
  msiextract -C "$ROOT/extracted" "$MSI" >/dev/null
  # msiextract keeps the MSI's install folder layout; the tests expect JSON/ and Database/ at the top.
  for d in JSON Database Reports; do
    found="$(find "$ROOT/extracted" -type d -name "$d" | head -n 1)"
    if [ -n "$found" ] && [ "$found" != "$ROOT/extracted/$d" ]; then
      mv "$found" "$ROOT/extracted/$d"
    fi
  done
  echo "Unpacked the installer into extracted/"
fi
MDB="$ROOT/extracted/Database/SDEDatabase.mdb"
[ -f "$MDB" ] || { echo "extracted/Database/SDEDatabase.mdb is missing after unpacking." >&2; exit 1; }

# 3. Decompile FEMA's assemblies (reference only; the parity tests compile CalculationEngine from here).
if [ "$FORCE" = 1 ] || [ ! -d "$ROOT/decomp/CalculationEngine" ]; then
  need dotnet "Install the .NET SDK."
  TOOLS="$HERE/.tools"
  if [ ! -x "$TOOLS/ilspycmd" ]; then
    dotnet tool install ilspycmd --version "$ILSPY_VERSION" --tool-path "$TOOLS" >/dev/null
  fi
  for asm in CalculationEngine.dll DAL.dll BLL.dll wpfSDE.exe; do
    file="$(find "$ROOT/extracted" -name "$asm" | head -n 1)"
    [ -n "$file" ] || { echo "$asm wasn't found in extracted/." >&2; exit 1; }
    name="${asm%.*}"
    rm -rf "$ROOT/decomp/$name"
    mkdir -p "$ROOT/decomp/$name"
    "$TOOLS/ilspycmd" -p -o "$ROOT/decomp/$name" "$file" >/dev/null
    echo "Decompiled $asm into decomp/$name"
  done
fi

# 4. Dump the database (reference only).
if [ "$FORCE" = 1 ] || [ ! -d "$ROOT/decomp/database" ]; then
  need mdb-export "Install mdbtools."
  DB="$ROOT/decomp/database"
  mkdir -p "$DB/csv"
  mdb-schema "$MDB" access > "$DB/schema.access.sql"
  mdb-schema "$MDB" postgres > "$DB/schema.postgres.sql"
  mdb-export "$MDB" MSysRelationships > "$DB/relationships.csv"
  mdb-tables -1 "$MDB" | while read -r t; do
    mdb-export "$MDB" "$t" > "$DB/csv/$t.csv"
  done
  echo "Dumped the database into decomp/database"
fi

# 5. The answer-key project.
if [ ! -f "$ROOT/reference/OriginalCalc/OriginalCalc.csproj" ]; then
  mkdir -p "$ROOT/reference/OriginalCalc"
  cp "$HERE/OriginalCalc.csproj.template" "$ROOT/reference/OriginalCalc/OriginalCalc.csproj"
  echo "Created reference/OriginalCalc"
fi

echo "Answer key ready. Run: dotnet test Sde.sln"
