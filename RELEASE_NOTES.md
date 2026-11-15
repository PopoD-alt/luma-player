# Luma Player 0.7.0 — unreleased review preview

This branch adds direct libVLC playback on Mac/Windows, fixes Mac Space play/pause in normal and fullscreen views, and implements paired local playlist streaming to a new Android companion. Files stay on the desktop; the phone buffers streamed bytes without a saved media download. Sharing is opt-in, follows the live playlist and revokes access when stopped.

The approved Mac and Windows custom runtimes are bundled in owner-review builds. [Native app-local Windows verification](https://github.com/PopoD-alt/luma-player/actions/runs/43765447494) passed HEVC Main10 MP4/MKV, PNG frame capture, captions, transport, sharing and active-stream idle-sleep prevention without a separate VLC installation.

The custom Android ARM64 LGPL-profile library component passed independent technical source/license/replacement checks. The owner-review APK passes true-cold quiet startup, real native clock/precise seeking, pause/play, surface restoration, fullscreen and AVC/HEVC/AAC streaming against both desktop servers, including independent re-signing and ten video switches. Android streams desktop original files over the same trusted Wi-Fi, using discovery or a manual desktop URL; it has no standalone local-file picker.

Android now handles the stale Wi-Fi binding diagnosed in fresh emulator tests. New connections select a current Wi-Fi route and verify the paired desktop identity before sending credentials on a replacement route. Missing Wi-Fi or changed identity produces a reconnect error. The repair adds no cellular/VPN fallback or generic connection retries. Regression and native route-revalidation checks accompany the repair; physical phones and actual Wi-Fi changes still need review.

Known test limitation: [current x64 CI](https://github.com/PopoD-alt/luma-player/actions/runs/43773929495) passes an initial associated-Wi-Fi precondition, then the emulator loses Wi-Fi and uses cellular. The app correctly refuses the stream with no upstream connection. This run remains a failure and does not establish HEVC/stress success; its later network change is not explained. The approved ARM64 local proofs pass, while physical phone Wi-Fi continuity remains an owner-review task.

Mac and Windows request idle-sleep prevention only during active media streams. The host must remain on with Luma open and sharing enabled; forced sleep and lid closure still interrupt playback. Native evidence and remaining limits are in the [README](README.md), [roadmap](ROADMAP.md), and platform guides. Component approval is not legal/patent clearance. No production package or live update feed has been published for 0.7.0; owner review remains required before production packaging/publication. The older downloads below do not contain this rebuild.

---

# Luma Player 0.6.1 alpha

**Initial public release · by sdas · free for end users**

Requires **macOS 14 or later on Apple Silicon**. Download the DMG from the [initial GitHub alpha release](https://github.com/PopoD-alt/luma-player/releases/tag/v0.6.1). See [INSTALL.md](INSTALL.md) for DMG installation and the per-app macOS opening steps for this unnotarized alpha.

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

The initial alpha is not Apple-notarized. Its signed public update feed points to the released DMG; future higher-numbered signed builds can trigger update prompts. The updater does not open or install downloads automatically.

Codec compatibility updates are ongoing. Windows, iOS and Android are planned in the [roadmap](ROADMAP.md#platforms); no builds or implementation progress for those platforms are verified in this release.

Source is published for inspection under the proprietary [LICENSE](LICENSE). Public visibility does not turn it into an unrestricted reuse grant.
