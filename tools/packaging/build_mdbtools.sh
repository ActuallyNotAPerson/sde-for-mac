#!/bin/sh
# Builds mdbtools' mdb-export and mdb-tables from source and puts them in SDE.app/Contents/MacOS/mdbtools/, where
# Sde.Data's MdbTools looks first, so importing a FEMA .mdb database works on a Mac without Homebrew.
# Run after package_app.sh and before sign_app.sh (adding files breaks package_app.sh's signature until it re-signs).
#
# Usage: tools/packaging/build_mdbtools.sh [osx-arm64|osx-x64 ...]     (add --force to rebuild)
#
# mdbtools 1.0.1 (GPL-2.0-or-later) comes from its release tarball, pinned by SHA-256 (the same file Homebrew builds).
# It is built with mdbtools' own small GLib replacement (--disable-glib) and a static libmdb, so the two programs need
# only macOS's libiconv and libSystem. The Intel build is cross-compiled with clang -arch x86_64, so both build on an
# Apple Silicon Mac, including the release runner. Needs only the Xcode command line tools and curl.
# Builds are cached in out/mdbtools/<rid>/.
set -eu
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
MDBTOOLS_VERSION="1.0.1"
MDBTOOLS_URL="https://github.com/mdbtools/mdbtools/releases/download/v$MDBTOOLS_VERSION/mdbtools-$MDBTOOLS_VERSION.tar.gz"
MDBTOOLS_SHA256="ff9c425a88bc20bf9318a332eec50b17e77896eef65a0e69415ccb4e396d1812"
MIN_MACOS="12.0"   # LSMinimumSystemVersion in package_app.sh
FIXTURE="tests/fixtures/sde-install/Database/SDEDatabase.mdb"

FORCE=0
RIDS=""
for a in "$@"; do
  case "$a" in
    --force) FORCE=1 ;;
    *) RIDS="$RIDS $a" ;;
  esac
done
RIDS="${RIDS:-osx-arm64 osx-x64}"

WORK="out/mdbtools"
TARBALL="$WORK/mdbtools-$MDBTOOLS_VERSION.tar.gz"
mkdir -p "$WORK"
if [ ! -f "$TARBALL" ]; then
  echo "Downloading $MDBTOOLS_URL"
  curl -fL --retry 3 -o "$TARBALL.part" "$MDBTOOLS_URL"
  mv "$TARBALL.part" "$TARBALL"
fi
ACTUAL="$(shasum -a 256 "$TARBALL" | cut -d' ' -f1)"
if [ "$ACTUAL" != "$MDBTOOLS_SHA256" ]; then
  echo "$TARBALL has SHA-256 $ACTUAL, expected $MDBTOOLS_SHA256. Deleted it; run again to download it again." >&2
  rm -f "$TARBALL"
  exit 1
fi

for RID in $RIDS; do
  case "$RID" in
    osx-arm64) ARCH=arm64; HOST=aarch64-apple-darwin ;;
    osx-x64) ARCH=x86_64; HOST=x86_64-apple-darwin ;;
    *) echo "Unknown runtime $RID (use osx-arm64 or osx-x64)" >&2; exit 1 ;;
  esac
  APP="out/$RID/SDE.app"
  [ -d "$APP" ] || { echo "$APP not found; run tools/packaging/package_app.sh $RID first" >&2; exit 1; }
  echo "== $RID"

  BUILD="$WORK/$RID"
  if [ "$FORCE" = 1 ] || [ ! -x "$BUILD/bin/mdb-export" ] || [ ! -x "$BUILD/bin/mdb-tables" ]; then
    rm -rf "$BUILD"
    mkdir -p "$BUILD/src" "$BUILD/bin"
    tar -xzf "$TARBALL" -C "$BUILD/src" --strip-components 1
    # --build and --host always given, so configure never runs test programs for the other architecture.
    # Without Bison 3 (macOS ships 2.3) the SQL engine and mdb-sql are left out, which SDE does not use.
    echo "   building mdbtools $MDBTOOLS_VERSION for $ARCH (log: $BUILD/build.log)"
    if ! (
      cd "$BUILD/src" &&
      ./configure --build="$(uname -m | sed 's/^arm64$/aarch64/')-apple-darwin" --host="$HOST" \
        --disable-shared --enable-static --disable-glib --disable-man --without-bash-completion-dir \
        CC="clang -arch $ARCH" CFLAGS="-O2 -mmacosx-version-min=$MIN_MACOS" LDFLAGS="-mmacosx-version-min=$MIN_MACOS" &&
      make -C src/libmdb &&
      make -C src/util mdb-export mdb-tables
    ) > "$BUILD/build.log" 2>&1; then
      tail -n 30 "$BUILD/build.log" >&2
      echo "Building mdbtools for $RID failed; the full log is $BUILD/build.log" >&2
      exit 1
    fi
    grep -q '^#define MDBTOOLS_H_HAVE_ICONV_H 1' "$BUILD/src/include/mdbtools.h" || { echo "mdbtools was configured without iconv; see $BUILD/build.log" >&2; exit 1; }
    cp "$BUILD/src/src/util/mdb-export" "$BUILD/src/src/util/mdb-tables" "$BUILD/bin/"
    cp "$BUILD/src/COPYING" "$BUILD/COPYING"
  fi

  # Each program must be for this architecture and link nothing outside macOS itself.
  for exe in "$BUILD/bin/mdb-export" "$BUILD/bin/mdb-tables"; do
    [ "$(lipo -archs "$exe")" = "$ARCH" ] || { echo "$exe is $(lipo -archs "$exe"), expected $ARCH" >&2; exit 1; }
    if otool -L "$exe" | tail -n +2 | grep -v -e '^[[:space:]]*/usr/lib/' -e '^[[:space:]]*/System/'; then
      echo "$exe links the libraries above, which are not part of macOS" >&2
      exit 1
    fi
  done

  DEST="$APP/Contents/MacOS/mdbtools"
  rm -rf "$DEST"
  mkdir -p "$DEST"
  cp "$BUILD/bin/mdb-export" "$BUILD/bin/mdb-tables" "$BUILD/COPYING" "$DEST/"
  cat > "$DEST/README.txt" <<README
mdb-export and mdb-tables from mdbtools $MDBTOOLS_VERSION, which SDE runs to read Microsoft Access (.mdb) databases.
They are separate programs licensed under the GNU GPL version 2 or later (see COPYING), not part of SDE's own code.
Source: $MDBTOOLS_URL
Built with tools/packaging/build_mdbtools.sh in SDE's repository.
README

  # Run the programs on the test database when this Mac can (Intel builds need Rosetta on Apple Silicon).
  if arch "-$ARCH" /usr/bin/true 2>/dev/null; then
    tables="$(env -i "$DEST/mdb-tables" -1 "$FIXTURE")"
    echo "$tables" | grep -qx 'Property' || { echo "$DEST/mdb-tables did not list Property in $FIXTURE" >&2; exit 1; }
    env -i "$DEST/mdb-export" "$FIXTURE" Property > /dev/null
    echo "   checked: lists $(echo "$tables" | wc -l | tr -d ' ') tables in $FIXTURE"
  else
    echo "   (not run: this Mac cannot run $ARCH programs)"
  fi
  echo "   $DEST: mdb-export, mdb-tables ($(du -sh "$DEST" | cut -f1))"
done
echo "Now run tools/packaging/sign_app.sh$RIDS to sign the apps again and rewrite the zips."
