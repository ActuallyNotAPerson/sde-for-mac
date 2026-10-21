#!/bin/sh
# Signs the SDE.app bundles that package_app.sh built (and any helper programs placed inside them afterwards, such as
# mdbtools), then writes out/SDE-<version>-<rid>.zip again. Run after package_app.sh and build_mdbtools.sh.
#
# Usage: tools/packaging/sign_app.sh [osx-arm64|osx-x64 ...]
#
# Environment:
#   SDE_SIGN_IDENTITY      Developer ID Application identity (the name or SHA-1 of a certificate in the keychain).
#                          Unset: the app is signed ad hoc, which is enough to run it on the Mac that built it.
#   SDE_NOTARY_APPLE_ID, SDE_NOTARY_TEAM_ID, SDE_NOTARY_PASSWORD
#                          Apple ID, team ID and app-specific password for notarization. All three needed; notarization
#                          is skipped (and the app only signed) when any is missing, or when SDE_SIGN_IDENTITY is unset.
set -eu
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Sde.App/Sde.App.csproj)
RIDS="${*:-osx-arm64 osx-x64}"
ENTITLEMENTS="tools/packaging/entitlements.plist"
IDENTITY="${SDE_SIGN_IDENTITY:-}"

NOTARIZE=0
if [ -n "$IDENTITY" ] && [ -n "${SDE_NOTARY_APPLE_ID:-}" ] && [ -n "${SDE_NOTARY_TEAM_ID:-}" ] && [ -n "${SDE_NOTARY_PASSWORD:-}" ]; then
  NOTARIZE=1
fi

for RID in $RIDS; do
  APP="out/$RID/SDE.app"
  ZIP="out/SDE-$VERSION-$RID.zip"
  [ -d "$APP" ] || { echo "$APP not found; run tools/packaging/package_app.sh $RID first" >&2; exit 1; }
  echo "== $RID"

  if [ -z "$IDENTITY" ]; then
    echo "   signing ad hoc"
    codesign --force --deep --sign - "$APP" >/dev/null 2>&1
  else
    echo "   signing with the Developer ID identity, hardened runtime"
    # Inside out: every Mach-O file below the bundle's main program first, then the main program, then the bundle.
    find "$APP/Contents" -type f ! -path "$APP/Contents/MacOS/SDE" | while IFS= read -r f; do
      if file -b "$f" | grep -q "Mach-O"; then
        codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" "$f"
      fi
    done
    codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" "$APP/Contents/MacOS/SDE"
    codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" "$APP"
  fi
  codesign --verify --deep --strict "$APP"

  rm -f "$ZIP"
  ditto -c -k --keepParent "$APP" "$ZIP"

  if [ "$NOTARIZE" = 1 ]; then
    echo "   notarizing (this waits for Apple, usually a few minutes)"
    LOG="out/notary-$RID.txt"
    xcrun notarytool submit "$ZIP" --apple-id "$SDE_NOTARY_APPLE_ID" --team-id "$SDE_NOTARY_TEAM_ID" --password "$SDE_NOTARY_PASSWORD" --wait | tee "$LOG"
    if ! grep -q "status: Accepted" "$LOG"; then
      echo "Notarization of $RID was not accepted; see $LOG" >&2
      exit 1
    fi
    xcrun stapler staple "$APP"
    rm -f "$ZIP"
    ditto -c -k --keepParent "$APP" "$ZIP"
  fi
  echo "   $ZIP ($(du -sh "$ZIP" | cut -f1))"
done
