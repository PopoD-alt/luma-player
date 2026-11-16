#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -module-cache-path "$PWD/.cache/modules" \
  Sources/SkipSegments.swift Sources/ResumePolicy.swift Sources/LibraryStore.swift Tests/ResumePolicySmoke.swift -o build/tests/resume-policy-smoke
build/tests/resume-policy-smoke
