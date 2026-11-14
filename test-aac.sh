#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -O -module-cache-path "$PWD/.cache/modules" \
  Sources/AACConfiguration.swift Tests/AACConfigurationSmoke.swift \
  -framework AudioToolbox -o build/tests/aac-configuration-smoke
build/tests/aac-configuration-smoke
