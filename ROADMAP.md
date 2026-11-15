# Luma Player roadmap

The 0.7.0 preview uses libVLC 3 for native Mac/Windows playback and adds an Android Wi-Fi streaming companion. The owner tested the delivered copies and reported them working as expected. Current source and usage documentation are on `main`; the earlier Mac 0.6.1 and Windows build 3 remain older engines.

## Current work

- **Mac:** approved custom LGPL-profile arm64 libVLC component built from pinned source, bundled playback verified, and source/notices/replacement checks recorded. Direct HEVC Main10 Matroska playback and Space play/pause in normal/fullscreen views pass native tests. Authenticated local sharing passes actual AVC/HEVC streaming, seeking, large-file ranges and stream revocation checks.
- **Windows:** approved source-built x64 component, complete matching sources/notices, direct libVLC playback and local sharing. [Actual app-local native verification](https://github.com/PopoD-alt/luma-player/actions/runs/43765447494) passes AVC/HEVC Main10 MP4/MKV, snapshots, subtitles, transport, fullscreen/lifecycle, sharing and active-stream power management without an installed VLC fallback. The portable local owner-review folder is complete.
- **Android:** reviewed source-built ARM64 component and debug owner copy with matching application/native sources and notices. Cold quiet startup, actual native clock/precise seeking, pause/play, surface restoration, fullscreen and AVC/HEVC/AAC streaming pass against both desktop implementations, including independent re-signing and ten video switches. [Current x64 CI](https://github.com/PopoD-alt/luma-player/actions/runs/43773929495) fails after losing emulator Wi-Fi despite passing its initial Wi-Fi precondition. Physical-phone network continuity needs review. This remains a desktop-streaming companion without a standalone local-file picker.

## Preview distribution

Owner review is complete for the supplied copies. The [0.7.0 preview release](https://github.com/PopoD-alt/luma-player/releases/tag/v0.7.0-preview.1) provides a Mac DMG, Windows portable ZIP and Android APK with matching sources/notices. These remain ad-hoc/unsigned/debug-signed previews. The evidence workflow does not publish app downloads, and the stable update feeds are unchanged. Broader physical-device testing and production signing remain separate work.

## Real-device testing

Test Android phones against both desktop hosts on actual Wi-Fi, including multicast discovery, firewall prompts, client-isolated networks, host sleep/wake, large playlists and long paused streams. Verify physical audio output, surround routing, A/V synchronization and hardware decoder fallbacks. Generated fixtures and emulator counters do not cover every real file or device.

The intermittent pre-response Android failure was traced to a removed Wi-Fi route retained by the app. Validate the replacement-route identity gate, reconnect behavior and cancellation on physical devices, including Wi-Fi with no Internet and Wi-Fi changes while cellular is enabled. Keep native playback/seek assertions and same-session transition coverage; safe network/host counters remain available for new failures.

## Formats

Broad support now comes from selected libVLC modules rather than a promise to recreate every codec. Curated builds exclude modules outside their selected license profile. Track tested codec/profile/container combinations and report native failures clearly. Do not treat historical original-decoder catalogs, module counts or recognized extensions as a VLC parity percentage.

The original Luma decoder/demux implementations remain reference work and retain their existing bounded conformance tests. There is no announced completion date for universal format support.

## Distribution and updates

Retain the proprietary updater and configure distinct platform feeds only after artifact review. The Mac signed feed still points to 0.6.1; Windows has no live feed, and Android store publishing and automatic updates are not configured. Signed Windows installers, DeveloperID/notarized Mac packages, Android production signing and release automation remain distribution work. Free public download is the intended model; third-party license obligations still apply.

## Platforms

Current targets are macOS 14+ Apple Silicon, Windows 11 x64 and Android8+. iOS remains planned with no implemented or verified build. Intel Macs, Windows ARM and additional device/OS matrices require separate work.
