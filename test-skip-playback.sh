#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules build/review/auto-skip/mac
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -D LUMA_UI_TEST -parse-as-library -module-cache-path "$PWD/.cache/modules" \
  Sources/*.swift Tests/SkipPlaybackSmoke.swift -o build/tests/skip-playback-smoke
build/tests/skip-playback-smoke "$PWD/Tests/Fixtures/Luma-Skip-Chapters.mkv" "$PWD/build/review/auto-skip/mac"
