# Luma Player roadmap

The public 0.7.0 preview uses libVLC 3 for native Mac/Windows playback and adds an Android Wi-Fi streaming companion. The owner tested the delivered copies and reported them working as expected. Current source and usage documentation are on `main`; the earlier Mac 0.6.1 and Windows build 3 remain older engines.

## Glass UI, PiP and handoff review

Desktop 0.10.0 / Android 0.4.0 source on `main`, updated **2026-11-16**, adds the reference-inspired Android glass home, Continue Watching, ordered Up Next and hamburger connectivity. Android includes visible picture-in-picture and left/right double-tap ten-second seeking. New video loads use the latest saved device position, and normal Play on paused playback can accept newer remote progress while retaining deliberate paused seeks. Startup displays recent activity without autoplay. The public downloads and update feeds remain unchanged; binary distribution and physical-device review remain separate work.

Physical-phone review should cover Home/expand/Close PiP transitions, gestures and interruptions from other audio apps, then switching devices after saved progress has reached the host. There is no cloud sync or automatic control of another running player.

## Auto-skip feature review

The current source on `main` includes opt-in chapter-based/manual intro and credits skipping across all platforms, introduced in desktop 0.9.0 / Android 0.3.0. Desktop audio matching can suggest ranges by comparing two episodes; results require confirmation and take up to about 12 minutes for the bounded maximum windows. Confirmed desktop timings are visible to the phone. New public binaries and the stable feeds have not been changed.

Expand real-episode coverage of repeated themes, cold opens, varied edits and post-credit scenes before considering more automatic detection. The current matcher is not a trained AI model, and a repeated audio passage alone cannot identify its narrative role. Keep explicit review, cancellation and manual range controls.

## Modern player source update

The earlier **desktop 0.8.0 / Android 0.2.0** source update introduced icon-based Android fullscreen controls, previous/next, horizontal swipe seeking, hidden vertical volume/brightness gestures, and a left playlist drawer that keeps video playing. Separate computer/phone progress and explicit resume actions are implemented across Mac, Windows and Android. Progress is persisted on the shared desktop without remotely seeking another running player. The owner approved the updated interface, and the source is on `main`. These changes are not in the public 0.7.0 assets or update feeds.

The current source on `main` also implements labelled chapters, saved ranges and repeated-audio suggestions with confirmation. More general scene/AI recognition remains future research.

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
