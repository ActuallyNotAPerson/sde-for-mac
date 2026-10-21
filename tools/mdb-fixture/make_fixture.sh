#!/bin/bash
# Builds tests/fixtures/sde-install (FEMA's SDE layout: Database/SDEDatabase.mdb + attachments/) from
# tests/fixtures/mdb-source (Export.json + photos), by writing the rows into a copy of FEMA's empty database.
#
# Needs: extracted/Database/SDEDatabase.mdb (the unpacked installer), Java 8 (for jjs), and these jars from
# Maven Central in $JACKCESS_JARS (Apache-2.0; test tooling only, nothing ships):
#   com/healthmarketscience/jackcess/jackcess/4.0.5/jackcess-4.0.5.jar   sha1 78e71fb55d742715b7a2dc8e0a6c2dce218c8e17
#   org/apache/commons/commons-lang3/3.14.0/commons-lang3-3.14.0.jar     sha1 1ed471194b02f2c6cb734a0cd6f6f107c673afae
#   commons-logging/commons-logging/1.2/commons-logging-1.2.jar           sha1 4bfc12adfe4842bf07b657f0369c4cb522955686
set -euo pipefail
cd "$(dirname "$0")/../.."
: "${JACKCESS_JARS:?set JACKCESS_JARS to the folder holding the three jars}"
JJS="${JJS:-$(/usr/libexec/java_home -v 1.8 2>/dev/null)/bin/jjs}"
[ -x "$JJS" ] || JJS="/Library/Internet Plug-Ins/JavaAppletPlugin.plugin/Contents/Home/bin/jjs"
OUT=tests/fixtures/sde-install
rm -rf "$OUT"
mkdir -p "$OUT/Database" "$OUT/attachments"
cp extracted/Database/SDEDatabase.mdb "$OUT/Database/SDEDatabase.mdb"
chmod u+w "$OUT/Database/SDEDatabase.mdb"
CP="$JACKCESS_JARS/jackcess-4.0.5.jar:$JACKCESS_JARS/commons-lang3-3.14.0.jar:$JACKCESS_JARS/commons-logging-1.2.jar"
"$JJS" -cp "$CP" tools/mdb-fixture/write_mdb.js -- tests/fixtures/mdb-source/Export.json "$OUT/Database/SDEDatabase.mdb"
cp tests/fixtures/mdb-source/photos/* "$OUT/attachments/"
echo "Wrote $OUT"
