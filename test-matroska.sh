#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -O -module-cache-path "$PWD/.cache/modules" Sources/AACConfiguration.swift Sources/AVCDecodeTimeline.swift Sources/MediaClock.swift Sources/MediaLanguage.swift Sources/Opus*.swift Sources/HEVCConfiguration.swift Sources/OriginalPGS.swift Sources/PGSSubtitleTrack.swift Sources/Matroska*.swift Tests/MatroskaSmoke.swift -o build/tests/matroska-smoke
build/tests/matroska-smoke "$PWD/Tests/Fixtures"
