#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -O -parse-as-library -module-cache-path "$PWD/.cache/modules" Sources/AACConfiguration.swift Sources/AVCDecodeTimeline.swift Sources/MediaLanguage.swift Sources/MediaClock.swift Sources/OpusConfiguration.swift Sources/OpusPrerollPatcher.swift Sources/HEVCConfiguration.swift Sources/OriginalPGS.swift Sources/PGSSubtitleTrack.swift Sources/Matroska*.swift Tests/OpusMatroskaSmoke.swift -o build/tests/opus-matroska-smoke
build/tests/opus-matroska-smoke "$PWD/Tests/Fixtures"
