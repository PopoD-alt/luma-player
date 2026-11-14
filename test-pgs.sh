#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -O -swift-version 6 -strict-concurrency=complete -module-cache-path "$PWD/.cache/modules" Sources/OriginalPGS.swift Tests/PGSSmoke.swift -o build/tests/pgs-smoke
build/tests/pgs-smoke "$@"
