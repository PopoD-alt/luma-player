#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -module-cache-path "$PWD/.cache/modules" Sources/MediaLanguage.swift Tests/MediaLanguageSmoke.swift -o build/tests/media-language-smoke
build/tests/media-language-smoke
