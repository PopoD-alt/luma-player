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
rm -rf "$APP/Contents/Frameworks/Sparkle.framework"
rm -f "$APP/Contents/Resources/Licenses/Sparkle.txt"
cp LICENSE "$APP/Contents/Resources/Licenses/Luma.txt"
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
