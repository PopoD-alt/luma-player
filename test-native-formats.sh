#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -O -parse-as-library -module-cache-path "$PWD/.cache/modules" Tests/NativeFormatEvidence.swift -o build/tests/native-format-evidence
build/tests/native-format-evidence
