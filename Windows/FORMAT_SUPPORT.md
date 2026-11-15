# Windows playback and verification scope

The current Windows GUI sends general local media **directly to an independently installed libVLC 3 runtime**. libVLC performs container parsing, compressed decoding, video and subtitle rendering, audio output and A/V synchronization. It supplies its own codec modules; this route does not depend on Microsoft Media Foundation having an optional HEVC extension installed.

The previous 256/512 MiB whole-file prototype limits, AVC/HEVC-to-MP4 remux gate and `hev1` compatibility-copy step are no longer the GUI playback path. Files are opened by path and native seeking is used. Broad runtime compatibility is not a guarantee for every profile, malformed file, protected/DRM stream, plugin configuration, GPU or audio device. No exact codec count or VLC feature-parity percentage is claimed.

## Current review checks

The Windows native smoke is designed to require direct local AVC display, raw `hev1` HEVC Main10 with AAC 5.1 decoded audio/video, native snapshots and displayed-picture counters, pause/seek/rate/volume, fullscreen player retention, external subtitle selection/delay, selectable Matroska ASS/UTF-8 tracks, AVI/FLAC/ADPCM, queue resume and explicit local-sharing pairing/revocation.

Inspect the result for the exact source commit. A cross-build, prior Media Foundation success, package presence or a filename is not current libVLC playback evidence. WPF `RenderTargetBitmap` images show the app UI; native libVLC frame snapshots and counters are checked separately. Counters do not establish physical audibility. Test-only dummy audio is permitted only when the smoke's Core Audio probe observes zero active output endpoints, and that limitation must remain explicit.

Runtime redistribution is not approved by these tests. The current app artifact excludes stock VLC DLLs/plugins. A separately installed official VLC3 runtime is used for review; an optional bundled runtime needs package/source/dependency audit approval. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Retained original core experiments

These original, bounded implementations and their tests remain in the repository for reference and further development, but are **not selected by the Windows GUI for normal playback**:

- Integer PCM WAVE, G.711, IMA ADPCM and Microsoft ADPCM sample reconstruction.
- Native FLAC sample reconstruction, including the existing 42 original goldens.
- Signed PCM/G.711 AIFF/AIFC and AU/SND reconstruction and the existing 60 format/depth/channel vectors.
- Video-only AVI RGB24/RGB32, RLE8, baseline MJPEG and Microsoft Video 1 decoding and exact pixel goldens.
- Bounded Matroska AVC/HEVC/AAC packet parsing, original decode-timeline adaptation, ISO MP4 writing, plain-text subtitle extraction and a conservative `hev1`/`hvc1` signaling normalizer.

Their original geometry, sample-count, memory, codec/profile and carrier limits still apply when the library APIs are called directly. A successful original golden test proves the scoped reconstruction, not the new GUI route or audible native output. Independent macOS decoding of original container outputs also remains separate from Windows playback validation.

## Local sharing

Local sharing follows the [v1 protocol](../Networking/PROTOCOL.md): explicit start, private/local source addresses, expiring six-digit pairing codes, bounded attempts, 256-bit in-memory tokens, opaque playlist IDs, original-file range streaming and stop/revoke cancellation. No upload, remote path selection or desktop playback-control endpoint is provided.

The live shared response contains at most 1,000 items and reports `truncated` plus `totalItems`. Titles are limited to 512 valid UTF-16 units, times to 31,536,000 seconds, and JSON to 4 MiB. Portable tests exercise a real listener with authentication, GET/HEAD ranges, invalid requests, missing/removal behavior and cancellation. Windows DNS-SD registration and actual client discovery need their own native/network evidence; the displayed manual URLs remain the fallback.
