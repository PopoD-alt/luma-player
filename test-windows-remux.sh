#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
export LUMA_WINDOWS_MEDIA_EXPORT="$PWD/build/windows-remux-native"
bash test-windows-core.sh
swiftc -parse-as-library -suppress-warnings Windows/Validation/RemuxSmoke.swift -o "$LUMA_WINDOWS_MEDIA_EXPORT/validate-remux"
"$LUMA_WINDOWS_MEDIA_EXPORT/validate-remux" "$LUMA_WINDOWS_MEDIA_EXPORT" "$PWD/Tests/Fixtures"
