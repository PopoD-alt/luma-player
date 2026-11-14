#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
if [[ ! -f Tests/Fixtures/Luma-HEVC8.mov || ! -f Tests/Fixtures/Luma-HEVC10.mov ]]; then
  xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -O -parse-as-library -module-cache-path "$PWD/.cache/modules" Tests/GenerateHEVCFixture.swift -o build/tests/generate-hevc-fixture
  build/tests/generate-hevc-fixture "$PWD/Tests/Fixtures"
fi
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -O -module-cache-path "$PWD/.cache/modules" \
  Sources/HEVCConfiguration.swift Tests/HEVCConfigurationSmoke.swift \
  -o build/tests/hevc-configuration-smoke
build/tests/hevc-configuration-smoke "$PWD/Tests/Fixtures"
