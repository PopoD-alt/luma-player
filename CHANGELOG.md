# Changelog

This log tracks published source changes. Installable releases and update feeds are listed separately; a source update does not replace the public downloads.

## 0.10.0 — 2026-11-16

Desktop source: **Mac build 12 / Windows build 7**. Android companion: **0.4.0-preview / version code 4**. This update brings the completed 0.9 and 0.10 work onto `main`.

### Added

- Android glass interface based on the supplied design references: violet/rose backdrop, rounded controls, Continue Watching artwork, playlist search, ordered Up Next and a floating navigation dock.
- Connection discovery, pairing, manual desktop addresses and disconnect controls inside the Android hamburger menu.
- Android picture-in-picture with Home auto-entry, an explicit PiP button, OS Play/Pause/Close controls, and restoration of fullscreen and pause state when expanded. Long-press the PiP button to change Home auto-entry.
- Left/right double-tap seeking by ten seconds, preserving single-tap controls, horizontal seek swipes, and vertical volume/brightness gestures.
- Small private artwork previews captured from actual playback. The cache is bounded to 24 images / 4 MB; full videos remain on the desktop.

### Changed and fixed

- Opening a video uses its newest valid saved computer/phone checkpoint. Normal Play on paused playback can accept newer progress from the other device.
- Deliberate seeks while paused retain their target on the next Play. Incoming progress leaves ongoing playback undisturbed; completed videos restart from the beginning.
- Desktop launch displays the most recently watched item without autoplay. Android displays cached last-played details until pairing refreshes the desktop state.
- A just-played phone item stays visible while its saved checkpoint reaches the desktop, avoiding a temporary return to older home-screen information.
- Fullscreen transport buttons stay clear of the header and footer. Cancelled contacts, holds and swipes do not become double-tap seeks.
- Visible PiP retains the existing native player, surface and authenticated stream. Closing PiP, or leaving with PiP disabled, releases playback. Audio-focus and surface-recovery paths preserve pause/seek intent.

### Also included from the unreleased 0.9 work

- Optional intro/credits skipping from recognised named chapters or saved manual ranges on Mac, Windows and Android; it starts off.
- Manual Skip intro / Skip credits controls, separate post-credit chapter preservation, and phone overrides for desktop timing ranges.
- Local desktop repeated-audio comparison of adjacent episodes, with bounded analysis, cancellation and explicit confirmation before saving suggestions. It uses no cloud service or trained AI model.

### Verification

- **84 Android JVM tests and lint pass.**
- The identical final Android app/test APKs pass **five native cases against each production desktop sharing server**: playback/progress, auto-skip, double-tap gestures, actual OS PiP transitions/controls, and home/checkpoint/cache behavior.
- Mac native tests pass startup, new-load and paused-Play resume, manual seek priority, known-zero checkpoints, incoming-progress isolation, and existing skip/audio regressions.
- [Windows native CI](https://github.com/PopoD-alt/luma-player/actions/runs/43954997802) passes the matching production source/runtime, including native clock/frame handoff and existing regressions. The supplied local Windows review EXE is cross-built; those exact local binary bytes were not executed in CI.

### Distribution and remaining review

- Public downloads remain **desktop 0.7.0 / Android 0.1.0-preview**. This source push does not publish a new DMG, Windows package or Android release, or activate an update feed.
- Saved progress can lag several seconds. Both devices need the same trusted Wi-Fi and an awake desktop with sharing enabled for fresh updates.
- Physical-phone Wi-Fi discovery, speaker routing, audio interruptions and additional hardware still need review. Emulator checks do not establish every VLC format on every device.
- See [release notes](RELEASE_NOTES.md), the [Android guide](Android/README.md) and the [Windows guide](Windows/README.md) for usage and earlier version history.
