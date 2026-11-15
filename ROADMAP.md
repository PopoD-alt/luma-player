# Luma Player roadmap

The active `feature/vlc-lan-android` branch rebuilds playback around libVLC 3 while retaining Luma's native interfaces and proprietary original code. It adds local desktop-to-Android playlist streaming. Published Mac 0.6.1 and Windows build 3 remain older versions; this preview has not replaced them.

## Current work

- **Mac:** custom LGPL-profile arm64 libVLC component built from pinned source, bundled playback verified, and source/notices/replacement checks recorded. Space play/pause now passes native event tests in normal/fullscreen views. Authenticated local sharing passes actual AVC/HEVC streaming, seeking, large-file ranges and stream revocation checks.
- **Windows:** direct libVLC playback, native audio/subtitle selection, saved library and local sharing implemented. Native runner playback has rendered AVC and HEVC Main10/AAC5.1. The fresh native Windows run passed lifecycle/resume, sharing UI, revocation and DNS-SD shutdown after its deadlock fix. A separately installed VLC 3 runtime is used for development; bundled runtime audit remains open.
- **Android:** native pairing, discovery/manual URL, live playlist and bounded in-memory streaming relay implemented. Development APKs, unit tests and lint build locally. Native streaming passes on local ARM64 and fresh x64 CI emulators. Mac-to-Android streaming and one-time software recovery also pass a native-output fault test. The custom runtime's final source/license/replacement audit remains work in progress.

## Before review packaging

Finish native Windows and Android integration runs; verify complete corresponding source/notices delivery and replacement for each bundled runtime. Show the owner the apps before creating public production packages. No production APK/EXE/DMG or update-feed change is part of the evidence workflow.

## Real-device testing

Test Android phones against both desktop hosts on actual Wi-Fi, including multicast discovery, firewall prompts, client-isolated networks, host sleep/wake, large playlists and long paused streams. Verify physical audio output, surround routing, A/V synchronization and hardware decoder fallbacks. Generated fixtures and emulator counters do not cover every real file or device.

## Formats

Broad support now comes from selected libVLC modules rather than a promise to recreate every codec. Curated builds exclude modules outside their selected license profile. Track tested codec/profile/container combinations and report native failures clearly. Do not treat historical original-decoder catalogs, module counts or recognized extensions as a VLC parity percentage.

The original Luma decoder/demux implementations remain reference work and retain their existing bounded conformance tests. There is no announced completion date for universal format support.

## Distribution and updates

Retain the proprietary updater and configure distinct platform feeds only after artifact review. The Mac signed feed still points to 0.6.1; Windows has no live feed, and Android publishing is not configured. Signed Windows installers, DeveloperID/notarized Mac packages, Android production signing and release automation remain distribution work. Free public download is the intended model; third-party license obligations still apply.

## Platforms

Current targets are macOS 14+ Apple Silicon, Windows 11 x64 and Android8+. iOS remains planned with no implemented or verified build. Intel Macs, Windows ARM and additional device/OS matrices require separate work.
