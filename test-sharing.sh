#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -D LUMA_UI_TEST -parse-as-library -module-cache-path "$PWD/.cache/modules" \
  Sources/*.swift Tests/SharingSmoke.swift -o build/tests/sharing-smoke
build/tests/sharing-smoke "$PWD/Tests/Fixtures"
