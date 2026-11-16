#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -parse-as-library -module-cache-path "$PWD/.cache/modules" \
  Sources/RepeatedAudioMatcher.swift Tests/RepeatedAudioAnalysisSmoke.swift -o build/tests/repeated-audio-policy
build/tests/repeated-audio-policy
if [[ "${1:-}" == "--native" ]]; then
  xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -parse-as-library -module-cache-path "$PWD/.cache/modules" \
    Sources/RepeatedAudioMatcher.swift Sources/SkipAudioAnalysis.swift Tests/SkipAudioAnalysisNative.swift -o build/tests/skip-audio-analysis-native
  build/tests/skip-audio-analysis-native
fi
