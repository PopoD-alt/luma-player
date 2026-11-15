# Luma Player

**A free native media player by sdas.** This branch contains the **0.7.0 libVLC rebuild for Mac and Windows**, plus the first **Android local-streaming companion**. It is a review preview, not a new production release.

[Mac source](Sources) · [Windows guide](Windows/README.md) · [Android guide](Android/README.md) · [Local sharing protocol](Networking/PROTOCOL.md) · [Roadmap](ROADMAP.md) · [Third-party components](THIRD_PARTY_NOTICES.md)

## Current preview

- **Mac:** macOS 14+, Apple Silicon. Native SwiftUI/AppKit interface, saved playlist, resume/bookmarks, fullscreen, audio/subtitle tracks and local sharing. The custom libVLC runtime is bundled in the local review build. Space now toggles playback in normal and video fullscreen views while leaving text input alone.
- **Windows:** Windows 11 x64, native WPF interface. Direct libVLC playback replaces the old whole-file remux and installed-system HEVC dependency. Keep a separately installed 64-bit VLC 3 runtime available for development; no stock VLC binaries are redistributed in the current Windows review folder.
- **Android:** Android 8/API 26 or later, native companion preview. Pair with a desktop, browse its live playlist and stream the original media. A development APK exists; a production APK/store release is not published.

The [cross-platform verification workflow](https://github.com/PopoD-alt/luma-player/actions/workflows/cross-platform-review.yml) preserves test evidence only. It does not publish APKs, EXEs, DMGs or updates. Owner review comes before packaging this rebuild.

## Stream the desktop playlist to Android

1. Add local videos to Luma on your Mac or Windows computer.
2. Open **Share playlist** and choose **Start sharing**. Keep Luma open and the computer awake.
3. Connect Android to the same trusted Wi-Fi. Open Luma and allow local-network access when requested.
4. Choose the nearby desktop, or enter its displayed `http://IP-address:port` if discovery is unavailable. Enter the six-digit pairing code.
5. Select a video. Playback, seeking and volume run on the phone; the original file remains on the computer.

The phone buffers streaming bytes in memory; Luma does not save a downloaded video. It refreshes the playlist every five seconds while foregrounded. Removing an item cuts off its stream. **Stop sharing** closes streams and revokes paired access. Sharing is off at desktop launch. No cloud account or router port forwarding is needed.

This preview uses local HTTP on a trusted network. Wi-Fi client isolation, firewalls, VPNs or blocked multicast can prevent connection/discovery. Manual private-IP addresses are supported. Discovery across physical devices and physical speaker output still need real-device testing. Mac prevents idle system sleep while a stream is active; keep the Windows host awake yourself. Closing a laptop lid or forcing sleep interrupts playback.

## Playback and format support

Playback uses **libVLC 3**, rather than recreating VLC's codecs. Common containers, codecs and subtitles are handled by the selected runtime and its compiled modules. Native tests have decoded and displayed synthetic H.264, raw `hev1` HEVC Main10 with AAC 5.1, and AVC/AAC Matroska on Mac and Windows. Tests separately check tracks, captions, seeking and fullscreen. Android streaming passes against the production portable/Windows and native Mac servers, including AVC/HEVC output and software recovery. [Native Windows/Android CI evidence](https://github.com/PopoD-alt/luma-player/actions/runs/43752264480) records the tested checkpoint; physical devices remain a separate check.

A plugin inventory is not proof that every VLC format/profile/device works. The curated runtime omits modules that do not meet its selected license profile. DRM, interactive disc menus, unusual profiles and physical audio/A/V synchronization are not universally verified. The older [Mac catalog](CodecSupport.json) and [Windows catalog](Windows/CodecSupport.json) document **historical original-codec experiments**, not the active libVLC backend's coverage or a current pending-codec count. Those implementations/tests remain available for reference.

On Mac use **Space** to play/pause, **←/→** to seek, **⌘O** to open media and **⌃⌘F** or a video double-click for fullscreen. Editable text fields still accept spaces. Windows uses **Space**, **←/→** and **F11**. Audio/subtitle menus select native tracks; external caption files and timing controls are available.

## Build the review apps

### Mac

Use Apple Silicon, macOS 14+ and Swift command-line tools. The approved custom runtime is built from pinned source; large native/source artifacts are not committed to git.

```sh
python3 Scripts/prepare-libvlc.py status
# Build/stage the custom component as described in ThirdParty/libvlc/REBUILD.md.
bash build.sh
```

The local result is `build/Luma Player.app`, ad-hoc signed for review. With the approved runtime staged, `build.sh` bundles and verifies it automatically. An existing local VLC 3 installation can be used for personal development when no runtime is bundled; `LUMA_LIBVLC_PATH` selects an explicit matching library. This is not a notarized production app.

Useful native checks: `bash test-vlc-bundle.sh`, `bash test-vlc.sh`, `bash test-sharing.sh`, `bash test-fullscreen.sh`. Runtime/release gates: `python3 Scripts/prepare-libvlc-tests.py`, `bash test-release-plan.sh`. Native checks need macOS media/window/network services. All media fixtures are original synthetic test files.

### Windows and Android

Follow [Windows/README.md](Windows/README.md) for PowerShell/.NET 10 builds and a separate libVLC runtime. Follow [Android/README.md](Android/README.md) for JDK 17/Android SDK builds and APK installation. Development binaries are unsigned or debug-signed; no production signing keys are included.

## Ownership, licensing and updates

Luma's original code belongs to **sdas** under the proprietary [LICENSE](LICENSE); free downloads and visible source do not grant unrestricted reuse rights. libVLC, FFmpeg and other dependencies retain their own licenses. Their notices, corresponding sources, module inventory and replacement requirements are separate from Luma's ownership. The custom Mac component has passed this repository's technical distribution gates. Windows and Android runtime redistribution work remains in progress; stock packages stay development-only. These checks are not a legal opinion or a guarantee against claims.

Luma retains its **own updater**. The published Mac 0.6.1 feed uses signed manifests, HTTPS, exact size and SHA256 verification, then reveals a download for manual installation. A source push alone does not notify installed apps. This preview does not change that live feed. Windows has a separate updater protocol but no configured public feed; Android update publishing is not configured. See [RELEASING.md](RELEASING.md).

## Previous public downloads

The published **Mac 0.6.1 alpha** is an older, more limited playback engine: [DMG releases](https://github.com/PopoD-alt/luma-player/releases), [release notes](RELEASE_NOTES.md), [installation guide](INSTALL.md). Its support limits differ from this branch. It is not Apple-notarized.

The earlier **Windows build 3** is a temporary unsigned folder-based [review artifact](https://github.com/PopoD-alt/luma-player/actions/runs/43632569709/artifacts/14128368227), retained until November 28, 2026. It does not contain this libVLC rebuild or local sharing. New preview binaries will follow review; the stable release/update feed remains unchanged.
