#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
if [[ ! -x "build/Luma Player.app/Contents/MacOS/LumaPlayer" ]]; then
  bash build.sh
fi
open "$PWD/build/Luma Player.app"
