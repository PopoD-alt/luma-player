#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
bash build.sh
PROBE="$PWD/build/tests/Luma Runtime Probe.app"
rm -rf "$PROBE"
ditto "$PWD/build/Luma Player.app" "$PROBE"
python3 - "$PROBE/Contents/Info.plist" <<'PY'
from pathlib import Path
import plistlib, sys
p = Path(sys.argv[1]); x = plistlib.loads(p.read_bytes())
x['CFBundleIdentifier'] = 'dev.sdas.luma.runtime-probe'
x['CFBundleDisplayName'] = 'Luma Runtime Probe'
p.write_bytes(plistlib.dumps(x))
PY
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -D LUMA_UI_TEST -parse-as-library \
  -module-cache-path "$PWD/.cache/modules" Sources/*.swift Tests/VLCPlaybackSmoke.swift -o "$PROBE/Contents/MacOS/LumaPlayer"
codesign --force --sign - "$PROBE"
codesign --verify --deep --strict "$PROBE"
env -u LUMA_LIBVLC_PATH LUMA_REQUIRE_BUNDLED_RUNTIME=1 "$PROBE/Contents/MacOS/LumaPlayer" "$PWD/Tests/Fixtures"
