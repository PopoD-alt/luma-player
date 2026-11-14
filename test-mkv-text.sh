#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -O -module-cache-path "$PWD/.cache/modules" Sources/MatroskaTextSubtitles.swift Tests/MatroskaTextSmoke.swift -o build/tests/matroska-text-smoke
build/tests/matroska-text-smoke
