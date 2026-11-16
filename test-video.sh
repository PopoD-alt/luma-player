#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
python3 Tests/GenerateOriginalFixtures.py
xcrun swiftc -swift-version 5 -module-cache-path "$PWD/.cache/modules" Sources/OriginalMSADPCM.swift Sources/OriginalCodecs.swift Sources/OriginalJPEG.swift Sources/OriginalMSVideo1.swift Sources/SubtitleCue.swift Sources/OriginalFLAC.swift Sources/AACConfiguration.swift Sources/AVCDecodeTimeline.swift Sources/MediaClock.swift Sources/MediaLanguage.swift Sources/Opus*.swift Sources/HEVCConfiguration.swift Sources/OriginalPGS.swift Sources/PGSSubtitleTrack.swift Sources/Matroska*.swift Sources/SkipSegments.swift Sources/VLCBridge.swift Sources/PlaybackEngine.swift Tests/VideoSmoke.swift -o build/tests/video-smoke
LUMA_EXPERIMENTAL_ENGINE=1 build/tests/video-smoke "$PWD/Tests/Fixtures" "$@"
