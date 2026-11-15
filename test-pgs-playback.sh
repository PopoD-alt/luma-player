#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -D LUMA_UI_TEST -parse-as-library -module-cache-path "$PWD/.cache/modules" Sources/*.swift Tests/PGSPlaybackSmoke.swift -o build/tests/pgs-playback-smoke
LUMA_EXPERIMENTAL_ENGINE=1 build/tests/pgs-playback-smoke "$PWD/Tests/Fixtures"
