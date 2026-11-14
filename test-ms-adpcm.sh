#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
python3 Tests/GenerateMSADPCMFixtures.py
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -O -parse-as-library -module-cache-path "$PWD/.cache/modules" Sources/OriginalMSADPCM.swift Tests/MSADPCMSmoke.swift -o build/tests/ms-adpcm-smoke
build/tests/ms-adpcm-smoke "$PWD/Tests/Fixtures/ms-adpcm"
