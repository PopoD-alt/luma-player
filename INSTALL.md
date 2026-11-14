# Luma Player 0.6.1 alpha — installation

Publisher: sdas · GitHub: https://github.com/PopoD-alt/luma-player

This first release supports Apple Silicon Macs (M1 or later), running macOS 14
or later. It is an experimental, free preview. There are no Windows, Intel Mac,
iOS or Android builds in this release.

## Install

1. Download the unnotarized-preview DMG from the repository's Releases page.
2. Open the DMG, then drag **Luma Player.app** onto the **Applications** shortcut.
3. Eject the DMG and open Luma Player from Applications.

This preview is ad-hoc signed. It has not been signed with an Apple Developer ID
or notarized by Apple. macOS may block the first launch. If you trust the release,
try opening the installed app, then use **System Settings → Privacy & Security →
Open Anyway**, where that option is available, and confirm opening this app.
Managed Macs may not allow an exception. Apple explains the per-app approval at
https://support.apple.com/en-us/102445 .

## Play media

Use **File → Open Media…** or drag a local file onto Luma. Double-click the video
or press **Control-Command-F** for video fullscreen; Escape exits. Use **Help →
Format Support…** for the supported formats and their limits. Full VLC
compatibility is unfinished; an MKV/AVI extension alone does not establish codec
support. The built-in Help demos exercise the available playback paths.

## Updates

Release builds configured with the publisher's signed feed check on launch and
daily while active. Prompts wait until playback and fullscreen are idle. The
**Luma Player** menu includes manual checks and an automatic-check preference.

If Luma offers a newer version, choose **Download Update**. After verification,
quit Luma, open the downloaded DMG, and replace Luma Player in Applications.
Installation is manual. Do not run the app directly from the mounted DMG.
Saved file references, playback progress and bookmarks remain outside the app
bundle and are retained when replacing the app. Keep copies of your media.

## Source, support and roadmap

The source is visible under the included proprietary LICENSE; official app
downloads are free. Report problems through the GitHub repository's Issues.
Include the app version, macOS version and codec/container details. Do not attach
private media or credentials. ROADMAP.md describes planned compatibility and
platform work; no future-platform release dates are promised.
