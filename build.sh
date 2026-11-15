#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
APP="$PWD/build/Luma Player.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$PWD/.cache/modules"
if [[ ! -f build/Luma.icns || Scripts/MakeIcon.swift -nt build/Luma.icns ]]; then
  xcrun swift -module-cache-path "$PWD/.cache/modules" Scripts/MakeIcon.swift build
fi
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -O -parse-as-library \
  -module-cache-path "$PWD/.cache/modules" \
  -framework SwiftUI -framework AppKit -framework Combine -framework AVFoundation \
  Sources/*.swift -o "$APP/Contents/MacOS/LumaPlayer"
python3 Scripts/configure-bundle.py "$APP/Contents/Info.plist"
mkdir -p "$APP/Contents/Resources/Licenses"
RUNTIME_ROOT="${LUMA_LIBVLC_RUNTIME_ROOT:-}"
if [[ -z "$RUNTIME_ROOT" ]]; then
  STAGED_RUNTIME="$(python3 -c 'import json; print("ThirdParty/runtime/" + json.load(open("ReleaseConfig.json")).get("libvlcPackage", "not-configured"))')"
  if [[ -f "$STAGED_RUNTIME/luma-libvlc-runtime.json" ]]; then RUNTIME_ROOT="$PWD/$STAGED_RUNTIME"; fi
fi
if [[ "${LUMA_DISTRIBUTION_BUILD:-0}" == "1" && -z "$RUNTIME_ROOT" ]]; then
  echo "Distribution needs an explicitly audited libVLC runtime root." >&2
  exit 1
fi
rm -rf "$APP/Contents/Frameworks/libvlc-runtime"
rm -rf "$APP/Contents/Resources/libvlc-runtime"
if [[ -n "$RUNTIME_ROOT" ]]; then
  if [[ "${LUMA_DISTRIBUTION_BUILD:-0}" == "1" ]]; then
    python3 Scripts/prepare-libvlc.py verify --runtime "$RUNTIME_ROOT" --distribution
  else
    python3 Scripts/prepare-libvlc.py verify --runtime "$RUNTIME_ROOT"
  fi
  # Keep the manifest/notices alongside this complete replaceable runtime.
  # Arbitrary metadata under Contents/Frameworks is interpreted as nested code
  # by codesign. The root signature seals the reviewed runtime as a resource.
  ditto "$RUNTIME_ROOT" "$APP/Contents/Resources/libvlc-runtime"
fi
rm -rf "$APP/Contents/Frameworks/Sparkle.framework"
rm -f "$APP/Contents/Resources/Licenses/Sparkle.txt"
cp LICENSE "$APP/Contents/Resources/Licenses/Luma.txt"
cp THIRD_PARTY_NOTICES.md "$APP/Contents/Resources/Licenses/THIRD_PARTY_NOTICES.md"
cp ThirdParty/libvlc/COPYING.LIB "$APP/Contents/Resources/Licenses/libVLC-LGPL-2.1.txt"
cp CodecSupport.json "$APP/Contents/Resources/CodecSupport.json"
if [[ -f Tests/Fixtures/Luma-Demo.mp4 ]]; then
  cp Tests/Fixtures/Luma-Demo.mp4 "$APP/Contents/Resources/Luma-Demo.mp4"
fi
cp Tests/Fixtures/Luma-RGB.avi Tests/Fixtures/Luma-RLE8.avi Tests/Fixtures/Luma-IMA.wav "$APP/Contents/Resources/"
cp Tests/Fixtures/Luma-MJPEG.avi Tests/Fixtures/Luma-AVC-AAC.mkv "$APP/Contents/Resources/"
cp Tests/Fixtures/Luma-Silence.flac "$APP/Contents/Resources/"
cp Tests/Fixtures/Luma-MSVideo1-8.avi Tests/Fixtures/Luma-MSVideo1-16.avi Tests/Fixtures/Luma-MSADPCM.wav Tests/Fixtures/Luma-PGS.mkv "$APP/Contents/Resources/"
if [[ -f build/Luma.icns ]]; then
  cp build/Luma.icns "$APP/Contents/Resources/Luma.icns"
fi
codesign --force --sign - "$APP" >/dev/null
echo "Built: $APP"
