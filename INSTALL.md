# Install and use Luma Player

Publisher: **sdas** · [Official downloads](https://github.com/PopoD-alt/luma-player/releases/tag/v0.7.0-preview.1) · [Usage guide](https://github.com/PopoD-alt/luma-player/blob/main/README.md)

This guide covers the desktop **0.7.0 preview** and its Android **0.1.0-preview companion**. The owner tested these builds and reported that they work as expected. They are preview downloads with the platform/signing limits below.

## Mac

Requires macOS 14 or later on an Apple Silicon Mac (M1 or later).

1. Download **Luma-0.7.0-macOS-arm64-unnotarized-preview.dmg** from the official release.
2. Open it and drag **Luma Player.app** onto the **Applications** shortcut.
3. Eject the disk image, then open Luma from Applications.
4. Choose **Open Media**, press **⌘O**, or drag local media files into the window.

The preview is ad-hoc signed, without Apple Developer ID signing or notarization. If macOS blocks the first launch and you trust the official download, first try opening the installed app, then use **System Settings → Privacy & Security → Open Anyway** where that per-app option is available. Managed Macs may restrict it. [Apple's opening guidance](https://support.apple.com/en-us/102445) explains the system flow. Do not disable system protection globally.

Use **Space** to play/pause, **←/→** to seek ten seconds, and **⌃⌘F** or double-click the video for fullscreen. **Escape** exits fullscreen. Text fields continue accepting spaces.

## Windows

Requires Windows 11 x64.

1. Download **Luma-0.7.0-Windows-x64-preview.zip**.
2. Extract the complete ZIP to a normal writable folder.
3. Open **Luma-Player-Windows-x64**, then run **Luma Player.exe**.
4. Use **Open Media**, **Ctrl+O**, or drag local media into Luma.

Keep the entire app folder together, including its playback libraries, sources and notices. Do not run the EXE from inside the ZIP. No separate VLC or .NET installation is needed. This is an unsigned portable preview, without a setup wizard or uninstaller; Windows may show an unknown-publisher warning. Confirm the official source and your local policy before running it.

Use **Space** to play/pause, **←/→** to seek ten seconds, **F11** for fullscreen and **Escape** to exit. To remove the portable app, quit it and delete its app folder; saved user data is separate.

## Android

Requires Android 8/API 26 or later and an ARM64 device. The companion streams a desktop playlist; it has no standalone local-file picker.

1. Download **Luma-0.7.0-Android-arm64-preview.apk** onto the phone.
2. Open the APK. If Android requests it, allow installation from the specific browser/file manager used to open this official download, then install Luma. You can revoke that installation permission afterward.
3. Open Luma and allow local-network access when requested.
4. Follow **Stream over Wi-Fi** below.

The APK is debug-signed and is not a Play Store release. The matching **Luma-0.7.0-Android-arm64-source-and-notices.zip** contains the complete application/native sources and licenses for this exact APK. It does not need to be installed on the phone. Retain that companion when redistributing the APK.

First playback may show **Preparing playback for first use…** while native fonts are prepared. Keep Luma in the foreground. Use play/pause, the seek bar, **±10 seconds**, volume and **Full screen**. Tap fullscreen video to show its controls, then choose **Exit full screen**. **Playlist** returns to the live list. Backgrounding stops the stream.

## Stream over Wi-Fi

1. Add local videos to Luma on the desktop.
2. On Mac, click the Wi-Fi sharing icon and choose **Start sharing this playlist**. On Windows, choose **Share playlist → Start sharing**.
3. Keep both devices on the same trusted, non-guest Wi-Fi. Keep the computer awake, Luma open and sharing enabled. Allow Luma through the desktop's private-network firewall if prompted.
4. On Android, choose the nearby computer or enter its displayed `http://IP-address:port`. Enter the six-digit code shown on the desktop.
5. Choose a video and play it. The original file remains on the desktop; Android buffers streaming bytes instead of saving a downloaded movie. Playback is independent of the desktop player, not synchronized handoff or remote control.

If discovery is blocked, use the displayed numeric address. Do not enter `localhost` or `127.0.0.1` on the phone. VPNs, guest/client isolation and blocked multicast can prevent connection. No router port forwarding is needed. Local HTTP is intended for a network you trust.

Codes expire after ten minutes. On Mac generate a new pairing code; on Windows stop/restart sharing. Existing paired access lasts until sharing stops. Use **Stop sharing and disconnect** on Mac or **Stop and revoke** on Windows to close streams and revoke access. Restarting sharing requires re-pairing. Removing a video also closes its active stream. Closing the laptop lid or forcing host sleep interrupts playback.

## Update or report a problem

Quit the app, download the newer preview and replace the Mac app or complete Windows folder. On Android install a newer APK signed with the same key. Saved desktop library data is outside the app; keep copies of the original media files.

These preview downloads do not activate a new automatic-update feed. A source push alone does not update installed apps; see [RELEASING.md](https://github.com/PopoD-alt/luma-player/blob/main/RELEASING.md).

For problems, open a [GitHub issue](https://github.com/PopoD-alt/luma-player/issues) with the app version, OS/device, codec/container details, error text and whether the desktop remains reachable. Do not upload private media or pairing credentials. Read [README.md](https://github.com/PopoD-alt/luma-player/blob/main/README.md#support-and-preview-limits) for verified coverage and the unresolved x64 emulator Wi-Fi limitation. Earlier Mac 0.6.1 downloads use a different engine; use their versioned release notes when troubleshooting them.
