#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -module-cache-path "$PWD/.cache/modules" \
  Sources/SignedRelease.swift Sources/UpdateConfiguration.swift Sources/UpdateCheckPolicy.swift Tests/UpdateSmoke.swift -o build/tests/update-smoke
build/tests/update-smoke
xcrun swiftc -swift-version 5 -module-cache-path "$PWD/.cache/modules" \
  Sources/UpdateCheckPolicy.swift Tests/UpdatePolicySmoke.swift -o build/tests/update-policy-smoke
build/tests/update-policy-smoke
xcrun swiftc -swift-version 5 -module-cache-path "$PWD/.cache/modules" \
  Sources/SignedRelease.swift Sources/UpdateConfiguration.swift Sources/UpdateCheckPolicy.swift Sources/AppUpdater.swift \
  Tests/UpdaterPreferencesSmoke.swift -o build/tests/updater-preferences-smoke
build/tests/updater-preferences-smoke
