#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/tests .cache/modules
xcrun swiftc -swift-version 5 -module-cache-path "$PWD/.cache/modules" \
  Sources/SkipSegments.swift Sources/ResumePolicy.swift Sources/LibraryStore.swift Tests/LibrarySmoke.swift -o build/tests/library-smoke
build/tests/library-smoke
xcrun swiftc -swift-version 5 -module-cache-path "$PWD/.cache/modules" \
  -framework AppKit -framework Combine Sources/OriginalMSADPCM.swift Sources/OriginalCodecs.swift Sources/OriginalJPEG.swift Sources/OriginalMSVideo1.swift Sources/SubtitleCue.swift Sources/OriginalFLAC.swift Sources/AACConfiguration.swift Sources/AVCDecodeTimeline.swift Sources/MediaClock.swift Sources/MediaLanguage.swift Sources/Opus*.swift Sources/HEVCConfiguration.swift Sources/OriginalPGS.swift Sources/PGSSubtitleTrack.swift Sources/Matroska*.swift Sources/SkipSegments.swift Sources/VLCBridge.swift Sources/PlaybackEngine.swift Tests/EngineSmoke.swift \
  -o build/tests/engine-smoke
python3 - <<'PY'
import wave
with wave.open('build/tests/engine-silence.wav', 'wb') as stream:
    stream.setnchannels(1)
    stream.setsampwidth(2)
    stream.setframerate(48000)
    stream.writeframes(b'\0' * 48000 * 2 * 10)
PY
LUMA_EXPERIMENTAL_ENGINE=1 build/tests/engine-smoke "$PWD/build/tests/engine-silence.wav"

LUMA_EXPERIMENTAL_ENGINE=1 build/tests/engine-smoke "$PWD/Tests/Fixtures/Luma-IMA.wav"

LUMA_EXPERIMENTAL_ENGINE=1 build/tests/engine-smoke "$PWD/Tests/Fixtures/Luma-Silence.flac"

LUMA_EXPERIMENTAL_ENGINE=1 build/tests/engine-smoke "$PWD/Tests/Fixtures/Luma-MSADPCM.wav"
