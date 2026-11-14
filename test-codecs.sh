#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
python3 Tests/GenerateOriginalFixtures.py
python3 Tests/GenerateMSVideo1Fixtures.py
xcrun swiftc -swift-version 5 -module-cache-path "$PWD/.cache/modules" Sources/OriginalMSADPCM.swift Sources/OriginalCodecs.swift Sources/OriginalJPEG.swift Sources/OriginalMSVideo1.swift Sources/SubtitleCue.swift Tests/CodecSmoke.swift -o build/tests/codec-smoke
build/tests/codec-smoke "$PWD/Tests/Fixtures"
