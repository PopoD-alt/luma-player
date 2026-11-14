#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -D LUMA_UI_TEST -parse-as-library \
  -module-cache-path "$PWD/.cache/modules" \
  Sources/*.swift Tests/FullscreenSmoke.swift -o build/tests/fullscreen-smoke
build/tests/fullscreen-smoke "$PWD/Tests/Fixtures/Luma-Demo.mp4"
