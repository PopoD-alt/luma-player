#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -module-cache-path "$PWD/.cache/modules" Sources/MediaClock.swift Tests/MediaClockSmoke.swift -o build/tests/media-clock-smoke
build/tests/media-clock-smoke
