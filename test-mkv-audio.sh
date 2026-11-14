#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -O -parse-as-library -module-cache-path "$PWD/.cache/modules" Sources/MatroskaAudioFormat.swift Tests/MatroskaAudioFormatSmoke.swift -o build/tests/mkv-audio-smoke
build/tests/mkv-audio-smoke
