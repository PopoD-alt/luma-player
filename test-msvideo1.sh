#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
python3 Tests/GenerateMSVideo1Fixtures.py
xcrun swiftc -O -module-cache-path "$PWD/.cache/modules" Sources/OriginalMSVideo1.swift Tests/MSVideo1Smoke.swift -o build/tests/msvideo1-smoke
build/tests/msvideo1-smoke "$PWD/Tests/Fixtures/msvideo1"
