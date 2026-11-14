# Luma Player roadmap

Luma’s first public alpha is **0.6.1 for macOS 14+ on Apple Silicon**. Codec compatibility work is continuing, with original Luma implementations and verified macOS decoding routes identified separately.

The current catalog has 46 grouped entries: 9 original implementation groups, 8 macOS routes, 6 partial groups and 23 unimplemented groups. The 29 partial/unimplemented groups are work ahead, not a count of individual codecs or a parity percentage. [CodecSupport.json](CodecSupport.json) and **Help → Format Support…** describe the current limits.

## Codec and subtitle work

- Broaden conformance testing for the new Microsoft Video 1, Microsoft ADPCM and PGS implementations, including independent real-world streams and edge cases.
- Expand compatibility within the existing FLAC, baseline MJPEG and AVI paths. AVI audio and several palette/orientation/edge variants remain missing.
- Extend Matroska handling while preserving every enabled supported track and A/V timing. Reordered variable-frame-rate video and other explicit limits remain open work.
- Improve subtitle presentation beyond ASS/SSA readable text. Advanced styling/font attachments, VobSub and DVB need additional implementations.
- Work through missing video/audio families in the catalog, including AV1, VP8/VP9, MPEG video, MPEG-4 Part 2, WMV/VC-1, DTS/TrueHD, Vorbis and other listed formats. Each route needs implementation and native playback/seek evidence before it is described as supported.

## Distribution and updates

The macOS updater now supports quiet daily checks, idle-only new-release prompts, a persistent automatic-check preference, reminder deferral and verified DMG/ZIP downloads. The initial GitHub alpha and stable signed Mac feed are published. A Git push alone does not notify installed apps: a published signed manifest must point to the exact downloadable release artifact. Real update installation on additional target Macs remains part of release testing.

Apple Developer ID signing/notarization and broader testing on target Macs remain distribution work. The initial alpha uses manual installation; the updater does not replace or execute the app automatically.

## Platforms

**A Windows 11 x64 review preview is available.** See the [Windows download instructions](README.md#windows-preview) and the [windows-port source branch](https://github.com/PopoD-alt/luma-player/tree/windows-port). The native WPF app includes original bounded audio/video decoder ports, a persistent queue and bookmarks, SRT/WebVTT and a separate signed-update protocol. Windows CI passed codec goldens, native video output, fullscreen, MP4 seeking, captions and saved-queue checks. Audio hardware output remains unverified because the hosted runner had no active audio render endpoint.

Windows work ahead includes owner review, audible playback on real devices, Matroska remux and embedded ASS/PGS ports, a setup installer, signing and a live Windows update feed. The current preview is an unsigned folder-based build with manual replacement, and does not establish full macOS feature or codec parity. The public macOS 0.6.1 release remains unchanged.

**iOS and Android remain planned targets.** No builds or implementation progress for those platforms have been verified.

Platform work requires adapting the interface, media output, storage and distribution to each operating system. Availability dates are not announced. The current macOS SwiftUI/AppKit/AVFoundation implementation should not be read as a finished portable player core.

## Release criteria

Support claims follow tested behavior, with decoder route and limitations visible to users. New work should retain saved queues/progress/bookmarks, preserve timing and track selection, reject unsupported variants clearly, and include meaningful generated/reference media checks.

See [RELEASE_NOTES.md](RELEASE_NOTES.md) for the current alpha and [README.md](README.md) to install and use it.
