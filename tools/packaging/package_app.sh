#!/bin/sh
# Builds SDE.app for Apple Silicon (osx-arm64) and Intel (osx-x64) Macs, ad-hoc signed, zipped in out/.
# Usage: [SDE_MANUAL_PDF=/path/to/SDE-User-Manual.pdf] tools/packaging/package_app.sh [osx-arm64|osx-x64 ...]
set -eu
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Sde.App/Sde.App.csproj)
RIDS="${*:-osx-arm64 osx-x64}"

# Icon: render once, then build the .icns iconset with sips + iconutil.
ICNS="out/icon/SDE.icns"
if [ ! -f "$ICNS" ]; then
  mkdir -p out/icon/SDE.iconset
  SRC=src/Sde.App/Assets/icon-1024.png
  for s in 16 32 128 256 512; do
    sips -z $s $s "$SRC" --out "out/icon/SDE.iconset/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2)); sips -z $d $d "$SRC" --out "out/icon/SDE.iconset/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns out/icon/SDE.iconset -o "$ICNS"
fi

for RID in $RIDS; do
  echo "== $RID"
  PUB="out/publish-$RID"
  rm -rf "$PUB"
  dotnet publish src/Sde.App -c Release -r "$RID" --self-contained -o "$PUB" -p:DebugType=None -p:DebugSymbols=false >/dev/null
  APP="out/$RID/SDE.app"
  rm -rf "$APP"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$PUB/." "$APP/Contents/MacOS/"
  cp "$ICNS" "$APP/Contents/Resources/SDE.icns"
  # FEMA's SDE User Manual (FEMA P-784, a federal document): bundled when a copy of the PDF is supplied, so Help can open it
  # at a field's page. Without it, Help says the manual is missing and links to FEMA's page.
  if [ -n "${SDE_MANUAL_PDF:-}" ]; then
    [ -f "$SDE_MANUAL_PDF" ] || { echo "SDE_MANUAL_PDF is not a file: $SDE_MANUAL_PDF" >&2; exit 1; }
    cp "$SDE_MANUAL_PDF" "$APP/Contents/Resources/SDE-User-Manual.pdf"
  else
    echo "   (SDE_MANUAL_PDF not set: the manual is not bundled)"
  fi
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SDE</string>
  <key>CFBundleDisplayName</key><string>Substantial Damage Estimator</string>
  <key>CFBundleIdentifier</key><string>io.github.actuallynotaperson.sde</string>
  <key>CFBundleExecutable</key><string>SDE</string>
  <key>CFBundleIconFile</key><string>SDE</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>SDE's phone form lets inspectors fill in assessments and add photos from a phone on the same Wi-Fi network. It runs only while you have started it in Settings.</string>
  <key>NSHumanReadableCopyright</key><string>Unofficial Mac port of FEMA's Substantial Damage Estimator 3.0. Not produced or endorsed by FEMA.</string>
</dict>
</plist>
PLIST
  codesign --force --deep --sign - "$APP" >/dev/null 2>&1
  codesign --verify --deep "$APP"
  (cd "out/$RID" && rm -f "../SDE-$VERSION-$RID.zip" && ditto -c -k --keepParent SDE.app "../SDE-$VERSION-$RID.zip")
  echo "   $APP  ($(du -sh "$APP" | cut -f1)), out/SDE-$VERSION-$RID.zip ($(du -sh "out/SDE-$VERSION-$RID.zip" | cut -f1))"
  file "$APP/Contents/MacOS/SDE" | sed 's/.*: /   /'
done
