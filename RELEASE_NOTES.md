# Luma Player 0.6.1 alpha

**Initial public release · by sdas · free for end users**

Requires **macOS 14 or later on Apple Silicon**. Download availability will be announced on the [GitHub releases page](https://github.com/PopoD-alt/luma-player/releases) when initial publication is complete. See [INSTALL.md](INSTALL.md) for DMG installation and the per-app macOS opening steps for this unnotarized alpha.

## Included

- Native Mac playback interface with light/dark appearance, video fullscreen, a saved queue, resume position, bookmarks, speed/volume controls and track menus.
- Original supported WAVE decoding, native FLAC decoding and video-only RGB/RLE8/Microsoft Video 1 AVI paths; limited original baseline MJPEG decoding.
- Compatible system-decoded MP4/MOV and audio playback, plus tested H.263 and six standard ProRes MOV routes.
- Limited original Matroska demux/remux with macOS AVC/HEVC/audio decoding, alternate audio selection and preserved relative track timing.
- External SRT/VTT, supported embedded text, readable-text ASS/SSA and bounded original PGS bitmap subtitle rendering with selection, delay, seeking, palette changes and clears.
- Automatic update checking for installations configured with the publisher’s signed feed: daily throttling, prompts deferred during playback/fullscreen, a persistent toggle, and a one-day **Not Now** reminder deferral. Manual checks remain available.
- Verified DMG and legacy ZIP downloads. Publisher signature, newer build, HTTPS, exact size and SHA-256 are checked before revealing the file; installation remains manual and preserves the saved library.

## Alpha limits

This is incomplete format coverage. The catalog currently tracks 46 groups: 9 original implementation groups, 8 macOS routes, 6 partial groups and 23 unimplemented groups. The 29 partial/unimplemented groups are not an exact codec count or a VLC parity metric.

AVI audio, advanced ASS styling/fonts/positioning, VobSub/DVB, many modern/legacy codecs and several container/profile variants remain unsupported or unverified. Matroska remuxing completes before playback and requires temporary disk space. PGS is a bounded bitmap subtitle route, not Blu-ray disc/PES or 3D/HDR support. Open **Help → Format Support…** for the detailed current catalog.

The initial alpha is not Apple-notarized. Live update notifications require the configured public feed and signed release artifacts; publication/feed activation is being prepared. The updater does not open or install downloads automatically.

Codec compatibility updates are ongoing. Windows, iOS and Android are planned in the [roadmap](ROADMAP.md#platforms); no builds or implementation progress for those platforms are verified in this release.

Source is published for inspection under the proprietary [LICENSE](LICENSE). Public visibility does not turn it into an unrestricted reuse grant.
