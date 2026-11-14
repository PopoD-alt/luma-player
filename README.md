# Luma Player

**A free native media player for macOS.** Luma combines a saved media queue, playback controls, bookmarks and subtitles in a simple Mac interface.

**Initial public alpha: 0.6.1 · macOS 14 or later · Apple Silicon**

[Download releases](https://github.com/PopoD-alt/luma-player/releases) · [Installation guide](INSTALL.md) · [Roadmap](ROADMAP.md) · [Release notes](RELEASE_NOTES.md)

> Experimental alpha. Downloadable DMGs are attached to published releases; source archives alone do not contain an installed app.

## Install and start watching

1. Download the macOS DMG from the release page.
2. Open the DMG and drag **Luma Player** into **Applications**.
3. Open Luma, then use **Open Media**, press **⌘O**, or drop local video/audio files into the window.

This alpha is not Apple-notarized. If macOS blocks the first launch and you trust the official download, try opening it once, then use **System Settings → Privacy & Security → Open Anyway**, where your Mac permits that per-app exception. See [INSTALL.md](INSTALL.md) and [Apple’s opening guidance](https://support.apple.com/en-us/102445). Managed Macs may restrict this option.

Your queue, playback position and bookmarks are saved locally outside the app. Replacing the app preserves that data; media files are referenced rather than copied into the library.

## Everyday controls

- **Space:** play/pause. **← / →:** jump back/forward 10 seconds.
- **Double-click video** or **⌃⌘F:** video fullscreen. **Escape:** exit fullscreen.
- **⌘B:** bookmark a moment. The Details panel lists saved moments.
- Use the subtitle and audio menus to select available tracks. Add UTF-8 SRT/VTT files with **⌘⇧S**.
- Speed, volume, subtitle timing and repeat controls are available below the video.
- **Help → Format Support…** shows the current searchable compatibility catalog. The playback footer identifies the decoder route being used.

## Current format support

Luma is an alpha with bounded format support. The file’s container, codec, profile and encoding details determine whether it plays; a recognized filename extension is not a compatibility guarantee.

**Original Luma implementations** include:

- PCM, G.711 A-law/μ-law, IMA ADPCM and Microsoft ADPCM in supported WAVE files. PCM 24/32-bit input is currently reduced to 16-bit output.
- Native FLAC files with fixed/LPC predictors, Rice residuals, stereo reconstruction and checksum checks. This uses a bounded full-file decoder.
- Uncompressed RGB, complete RLE8 keyframes and Microsoft Video 1 in supported AVI files. **These AVI paths play video only.** Microsoft Video 1 supports 8-bit indexed/16-bit RGB555 blocks and delta seeking, with dimension/palette limits.
- Limited baseline MJPEG AVI: 8-bit sequential Huffman JPEG with explicit tables. Progressive, missing-table and interlaced-field variants remain unsupported.
- External UTF-8 SRT/WebVTT captions, plus supported Matroska text and bounded PGS bitmap subtitle decoding/rendering.

**macOS supplies compressed decoding** for compatible H.264/HEVC MP4/MOV, AAC, MP3, ALAC and supported AIFF/CAF files. Six standard ProRes MOV profiles and an H.263 MOV route have native playback/seek evidence. Machine, profile and carrier limits still apply.

**Matroska compatibility is partial.** Luma’s original demuxer remuxes supported local MKV/MKA tracks to a temporary MOV before playback. Tested paths include AVC/HEVC, selectable alternate audio, AAC-LC/HE-AAC, MP3/FLAC/PCM and limited mono/stereo Opus. Compressed decoding on this route uses macOS. Reordered video requires a complete bounded constant-frame-rate timeline. Remuxing needs temporary disk space and completes before playback begins.

Embedded ASS/SSA currently renders readable text; advanced fonts, styling, positioning, animation and drawings are not reproduced. PGS supports bitmap placement, palette updates, clears, selection, seeking and timing adjustment within its limits; Blu-ray disc/PES parsing and 3D/HDR extensions are unsupported. VobSub/DVB subtitles remain missing. AC-3/E-AC-3 framing is experimental; full payload playback is unverified.

The [catalog](CodecSupport.json) tracks **46 format groups: 9 original implementation groups, 8 macOS routes, 6 partial groups and 23 unimplemented groups**. That leaves **29 partial or unimplemented groups** for further work. These are grouped development labels, not an exact codec count or a VLC parity percentage. AV1, VP8/VP9, MPEG-1/2, DivX/Xvid, WMV/VC-1, DTS/TrueHD and other cataloged families still need implementation or verification.

Codec compatibility development is continuing. See [ROADMAP.md](ROADMAP.md) for priorities and platform plans.

## Updates

The initial alpha and its [signed update feed](Updates/manifest.json) are published. Release builds are configured with that stable HTTPS feed and the publisher’s public verification key. A Git push alone does not release an update: upload the matching DMG first, then publish its signed manifest. [RELEASING.md](RELEASING.md) describes the owner’s release steps.

Configured installations check on launch when due and at most once per 24 hours while active. New-release prompts wait until playback/fullscreen are idle and no app modal dialog or sheet is open. Automatic failures and up-to-date results stay quiet. **Luma Player → Automatically Check for Updates** controls the preference; **Check for Updates…** remains available manually. **Not Now** defers automatic reminders for a day.

Before revealing a downloaded DMG or legacy ZIP, Luma verifies the publisher’s Ed25519 signature, the newer numeric build, HTTPS, exact file size and SHA-256. You install the verified download manually. The updater never opens the archive, executes it or replaces the running application. A build without valid feed/key settings makes no update requests.

## Platforms and licensing

The available app targets **Apple Silicon Macs running macOS 14+**. Windows, iOS and Android are planned targets in the [cross-platform roadmap](ROADMAP.md#platforms); no builds or implementation progress for those platforms are verified in this release.

Luma is made by **sdas** and is free for end users. Published source is available for inspection under the proprietary [LICENSE](LICENSE); making the source visible does not grant broad reuse, modification or redistribution rights. Apple frameworks retain their own terms. Luma does not bundle or require VLC, FFmpeg or Sparkle implementations.

## Build and test

Developers need macOS 14+, Apple Silicon and Apple’s Swift command-line tools. No third-party package download is required.

```sh
bash build.sh
```

This creates an ad-hoc signed app in `build/Luma Player.app`; it does not publish a release or create a production-notarized DMG.

Representative checks:

```sh
bash test-codecs.sh
bash test-flac.sh
bash test-jpeg.sh
bash test-matroska.sh
bash test-pgs.sh
bash test-pgs-playback.sh
bash test-native-formats.sh
bash test-updates.sh
bash test-release-plan.sh
bash test-fullscreen.sh
```

Decoder tests use original generated fixtures and exact/reference comparisons. Native playback/fullscreen tests need normal access to macOS media/window services. Update tests use synthetic data and isolated preferences without a live feed. Release-plan tests stub packaging/signing commands; passing them does not establish production notarization or publication.

`CodecSupport.json` is the support catalog; `Progress/work-items.json` records development milestones. Local check logs/snapshots are generated outputs. `python3 Scripts/progress.py check CHECK_ID` records a selected check, and `python3 Scripts/progress.py refresh` refreshes the local progress snapshot. The dashboard records work; it does not run codec development in the background.

Original implementations use published format descriptions, including [Microsoft AVI RIFF](https://learn.microsoft.com/en-us/windows/win32/directshow/avi-riff-file-reference), [RFC 3551 audio algorithms](https://www.rfc-editor.org/rfc/rfc3551), [RFC 9639: FLAC](https://www.rfc-editor.org/rfc/rfc9639), [ITU-T T.81: JPEG](https://www.itu.int/rec/T-REC-T.81-199209-I/en), [RFC 9559: Matroska](https://www.rfc-editor.org/rfc/rfc9559), and the [official Matroska mappings](https://www.matroska.org/technical/codec_specs.html). Implementation-specific references and bounds are documented in the source.
