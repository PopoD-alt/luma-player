# Luma Player 0.10.0 — local review, unreleased

Android **0.4.0-preview / version code 4**, Mac **build 12**, Windows **review build 7**. Public packages and update feeds remain unchanged.

- Android home and playback adopt the supplied glass design references, with Continue Watching, real-frame thumbnails, ordered Up Next and hamburger connection settings.
- Android picture-in-picture retains visible native streaming and offers OS Play/Pause/Close controls; expanding preserves playback intent and fullscreen presentation.
- Left/right double-taps rewind/advance ten seconds, alongside existing single-tap and swipe controls.
- New video loads resume the newest saved checkpoint from either device. Normal paused Play can accept newer remote progress; deliberate paused seeks retain their destination. Incoming reports leave ongoing playback undisturbed.
- Launch screens show the latest watched item without starting it automatically. Cached Android details refresh after pairing.

Validation passes: **84 Android JVM tests and lint**, all five native cases against each production desktop sharing server using the identical final APK, Mac native resume/startup and existing skip/audio regressions, and [native Windows CI](https://github.com/PopoD-alt/luma-player/actions/runs/43954997802). Physical-device PiP, audio and Wi-Fi behavior still need owner review.

---

# Luma Player 0.9.0 — auto-skip review, unreleased

Mac **build 11**, Windows **review build 6**, Android **0.3.0-preview / version code 3** on `feature/auto-skip`. Public 0.7.0 downloads, `main`'s approved 0.8.0 source and update feeds remain unchanged pending review of this feature.

- Opt-in Auto-skip plus manual Skip intro/credits controls on all three platforms.
- Conservative labelled-chapter ranges and saved manual start/end timings. Desktop timings are shared as read-only playlist metadata; Android can keep a local override.
- Separate post-credit chapter boundaries are preserved. Pausing, deliberate seeks, rapid seeks and late metadata retain user intent; skip requests are consumed before asynchronous seeking to prevent loops.
- Optional local desktop repeated-audio comparison of two episodes, bounded to three-minute head/tail windows, with progress, cancellation, file-change checks and confirmation before saving. No AI model, cloud upload or implicit acceptance.
- Existing libraries default to auto-skip off. Playback progress/bookmarks remain separate from skip ranges.

Validation: Android **52 JVM tests/lint**, actual Mac-server **38.819s** and Windows-Core-server **38.81s** runs (two native tests each); Mac native intro/credits clock and confirmed-audio workflow; [native Windows CI](https://github.com/PopoD-alt/luma-player/actions/runs/43789628252) including all final guards and preview/save controls.

General AI recognition of credits in any arbitrary video is not implemented. Repeated audio may be story material; suggestions require review, and generic chapter names are not guessed. Physical-device, real-episode and hardware/audio coverage remains separate from authored fixture checks. Review builds retain the existing ad-hoc/unsigned/debug signing model.

---

# Luma Player 0.8.0 — latest source, unreleased

Android companion **0.2.0-preview / version code 2**; Mac **build 10**; Windows **review build 5**. These changes are included on `main` after the owner approved the updated interface. The public 0.7.0 downloads and stable update feeds are unchanged.

- Android fullscreen now uses vector play/pause, previous/next, ten-second jump, playlist and fullscreen buttons, with fading glass/gradient controls.
- Horizontal swipes preview and commit a seek. Vertical gestures control player volume on the right and window brightness on the left, with temporary indicators and restored brightness on exit.
- The Android playlist opens as a left overlay drawer while the existing video keeps playing.
- Mac, Windows and Android distinguish Computer and Phone positions, percentages, completion and saved phone updates. Explicit Resume from phone/computer actions transfer the saved position; arriving reports never seek another running player.
- Authenticated, bounded progress reporting adds optional fields to protocol version 1. Older hosts retain read-only streaming compatibility. Desktop libraries persist the phone checkpoint without overwriting the desktop position or media duration.

Approved-runtime Android builds pass **40 JVM tests and lint**, plus native ARM64 playback/UI/progress tests against both actual desktop server implementations. Mac's native sharing, persistence and explicit-resume UI checks pass. [Native Windows CI](https://github.com/PopoD-alt/luma-player/actions/runs/43781716148) passes the approved app-local runtime, new progress UI and explicit native resume. The final Android APK also passes an in-place upgrade from the public preview and the native UI/progress test. Broader physical-device testing of gestures, speaker output and Wi-Fi continuity remains separate.

AI-assisted opening/credit detection remains proposed. No AI model or automatic skip behavior is included in this build.

---

# Luma Player 0.7.0 — owner-tested preview

The owner tested the supplied copies and reported that they work as expected. The [0.7.0 preview release](https://github.com/PopoD-alt/luma-player/releases/tag/v0.7.0-preview.1) provides an Apple Silicon Mac DMG, Windows x64 portable ZIP and Android ARM64 debug APK with matching source/notices. [INSTALL.md](INSTALL.md) and the [README](README.md) explain installation, controls, Wi-Fi pairing and troubleshooting. Android's internal app version remains 0.1.0-preview. These packages preserve the reviewed app binaries; production signing and automatic-update feeds are separate.

This branch adds direct libVLC playback on Mac/Windows, fixes Mac Space play/pause in normal and fullscreen views, and implements paired local playlist streaming to a new Android companion. Files stay on the desktop; the phone buffers streamed bytes without a saved media download. Sharing is opt-in, follows the live playlist and revokes access when stopped.

The approved Mac and Windows custom runtimes are bundled in owner-review builds. [Native app-local Windows verification](https://github.com/PopoD-alt/luma-player/actions/runs/43765447494) passed HEVC Main10 MP4/MKV, PNG frame capture, captions, transport, sharing and active-stream idle-sleep prevention without a separate VLC installation.

The custom Android ARM64 LGPL-profile library component passed independent technical source/license/replacement checks. The owner-review APK passes true-cold quiet startup, real native clock/precise seeking, pause/play, surface restoration, fullscreen and AVC/HEVC/AAC streaming against both desktop servers, including independent re-signing and ten video switches. Android streams desktop original files over the same trusted Wi-Fi, using discovery or a manual desktop URL; it has no standalone local-file picker.

Android now handles the stale Wi-Fi binding diagnosed in fresh emulator tests. New connections select a current Wi-Fi route and verify the paired desktop identity before sending credentials on a replacement route. Missing Wi-Fi or changed identity produces a reconnect error. The repair adds no cellular/VPN fallback or generic connection retries. Regression and native route-revalidation checks accompany the repair; physical phones and actual Wi-Fi changes still need review.

Known test limitation: [current x64 CI](https://github.com/PopoD-alt/luma-player/actions/runs/43773929495) passes an initial associated-Wi-Fi precondition, then the emulator loses Wi-Fi and uses cellular. The app correctly refuses the stream with no upstream connection. This run remains a failure and does not establish HEVC/stress success; its later network change is not explained. The approved ARM64 local proofs pass, while physical phone Wi-Fi continuity remains an owner-review task.

Mac and Windows request idle-sleep prevention only during active media streams. The host must remain on with Luma open and sharing enabled; forced sleep and lid closure still interrupt playback. Native evidence and remaining limits are in the [README](README.md), [roadmap](ROADMAP.md), and platform guides. Component approval is not legal/patent clearance. The preview does not change the live stable update feed. The older downloads below do not contain this rebuild.

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
