# Luma Player roadmap

The active `feature/vlc-lan-android` branch rebuilds playback around libVLC 3 while retaining Luma's native interfaces and proprietary original code. It adds local desktop-to-Android playlist streaming. Published Mac 0.6.1 and Windows build 3 remain older versions; this preview has not replaced them.

## Current work

- **Mac:** approved custom LGPL-profile arm64 libVLC component built from pinned source, bundled playback verified, and source/notices/replacement checks recorded. Direct HEVC Main10 Matroska playback and Space play/pause in normal/fullscreen views pass native tests. Authenticated local sharing passes actual AVC/HEVC streaming, seeking, large-file ranges and stream revocation checks.
- **Windows:** direct libVLC playback, native audio/subtitle selection, saved library and local sharing implemented. [Checkpoint `df964b2`](https://github.com/PopoD-alt/luma-player/actions/runs/43759294052) passed native stock-runtime tests, including AVC/HEVC Main10 Matroska, lifecycle/resume, sharing UI, revocation, DNS-SD shutdown and the active-stream idle-sleep request. The custom runtime's [compiler/linker probe passed](https://github.com/PopoD-alt/luma-player/actions/runs/43760224460); the complete source build and native/independent audit remain pending. The tested separate stock runtime is development-only.
- **Android:** native pairing, discovery/manual URL, live desktop playlist and bounded in-memory streaming relay implemented; there is no standalone local-file picker. The custom ARM64 LGPL-profile library has independent source/license/replacement approval for private owner review. Actual ARM64 streaming/recovery/re-signing checks passed, but stock-runtime x64 diagnostics identified blocking cold font initialization. The standard background preparation now passes cold-cache ARM64 tests; a fresh quiet x64 rerun remains pending.

## Before review packaging

Complete Android's fresh quiet stock-runtime x64 rerun after the cold font-preparation fix, then the Windows custom-runtime source build and native/independent audit. Preserve corresponding source/notices delivery and replacement for every bundled runtime, including matching Android application inputs. Private owner-review apps are distinct from production packaging/publication, which remains gated on owner review. No production APK/EXE/DMG or update-feed change is part of the evidence workflow.

## Real-device testing

Test Android phones against both desktop hosts on actual Wi-Fi, including multicast discovery, firewall prompts, client-isolated networks, host sleep/wake, large playlists and long paused streams. Verify physical audio output, surround routing, A/V synchronization and hardware decoder fallbacks. Generated fixtures and emulator counters do not cover every real file or device.

## Formats

Broad support now comes from selected libVLC modules rather than a promise to recreate every codec. Curated builds exclude modules outside their selected license profile. Track tested codec/profile/container combinations and report native failures clearly. Do not treat historical original-decoder catalogs, module counts or recognized extensions as a VLC parity percentage.

The original Luma decoder/demux implementations remain reference work and retain their existing bounded conformance tests. There is no announced completion date for universal format support.

## Distribution and updates

Retain the proprietary updater and configure distinct platform feeds only after artifact review. The Mac signed feed still points to 0.6.1; Windows has no live feed, and Android publishing is not configured. Signed Windows installers, DeveloperID/notarized Mac packages, Android production signing and release automation remain distribution work. Free public download is the intended model; third-party license obligations still apply.

## Platforms

Current targets are macOS 14+ Apple Silicon, Windows 11 x64 and Android8+. iOS remains planned with no implemented or verified build. Intel Macs, Windows ARM and additional device/OS matrices require separate work.
