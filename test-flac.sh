#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -O -module-cache-path "$PWD/.cache/modules" Sources/OriginalMSADPCM.swift Sources/OriginalCodecs.swift Sources/OriginalJPEG.swift Sources/OriginalMSVideo1.swift Sources/OriginalFLAC.swift Tests/FLACSmoke.swift -o build/tests/flac-smoke
build/tests/flac-smoke "$PWD/Tests/Fixtures/flac"
