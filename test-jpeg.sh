#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -O -module-cache-path "$PWD/.cache/modules" Sources/OriginalMSADPCM.swift Sources/OriginalCodecs.swift Sources/OriginalJPEG.swift Sources/OriginalMSVideo1.swift Tests/JPEGSmoke.swift -o build/tests/jpeg-smoke
build/tests/jpeg-smoke "$PWD/Tests/Fixtures"
python3 Tests/GenerateMJPEGFixture.py
