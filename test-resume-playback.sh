#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules build/review/mobile-pip/mac
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -D LUMA_UI_TEST -parse-as-library -module-cache-path "$PWD/.cache/modules" \
  Sources/*.swift Tests/ResumePlaybackSmoke.swift -o build/tests/resume-playback-smoke
build/tests/resume-playback-smoke "$PWD/Tests/Fixtures/Luma-Skip-Chapters.mkv" "$PWD/build/review/mobile-pip/mac"
