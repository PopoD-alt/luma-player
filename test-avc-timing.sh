#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -O -module-cache-path "$PWD/.cache/modules" Sources/AVCDecodeTimeline.swift Tests/AVCDecodeTimelineSmoke.swift -o build/tests/avc-timing-smoke
build/tests/avc-timing-smoke
