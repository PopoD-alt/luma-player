# Luma Player for Windows — review preview

This is a native WPF port targeting **Windows 11 x64**. It uses .NET 10 and Windows desktop composition, with a DWM Mica backdrop when supported and an opaque fallback. Other Windows versions and Windows on ARM are unverified. The current source adds original AIFF/AIFC/AU decoding and a bounded AVC/HEVC + AAC-LC Matroska remux path. Use the matching review artifact and per-path smoke results to see which additions reached native Windows playback. A portable test or successful decoding of an output file on macOS is not proof of Windows playback, appearance or audible output.

Luma's source is proprietary and published for inspection. Official binaries are free to use under the repository LICENSE, included with builds as `LICENSE.txt`. No VLC, FFmpeg, libFLAC or other third-party codec engine is bundled. Microsoft's runtime and WPF have their own licenses and third-party notices, preserved under `Licenses/Microsoft/` in self-contained builds.

## Build a Windows review ZIP

Use a Windows x64 desktop with PowerShell 7 and the official [.NET SDK 10.0.404](https://dotnet.microsoft.com/download/dotnet/10.0). From the repository root:

```powershell
pwsh -File .\Windows\build.ps1
```

The script runs the console core suites against `Tests/Fixtures`, then publishes the WPF app as a self-contained `win-x64` build. Managed assemblies are bundled into the executable; native runtime libraries can extract at startup. Trimming is disabled. Keep the complete published folder, its configuration, demo and notices together.

The script launches `Luma Player.exe --smoke-test <fixturesRoot> <outputFolder>` with a timeout. That mode exercises the app's own window, original AVI playback and fullscreen path, writes PNGs and `smoke-results.json`, and must exit successfully before a ZIP is produced. The build also requires a passed native MP4 result and an explicit audio result. `BUILD-INFO.json` and `review.json` retain these per-path `smokeResults`; review them and the images, rather than reading the overall visual check as proof of audio hardware output.

The core suite verifies exact decoded samples independently of audio hardware. The native original-audio check reports `originalAudio: "passed"` when its Windows playback check succeeds. A headless runner can instead report `"unverified-no-active-render-endpoint"` only when playback returns `NS_E_WMP_AUDIO_HW_PROBLEM` and Core Audio enumeration finds zero active render endpoints. That result must record `activeAudioRenderEndpoints: 0`; it does not establish audible output. Other playback errors remain failures. Endpoint counts may be null when unavailable, but a null count cannot justify skipping the audio hardware check. A hosted run also does not cover every GPU, multi-monitor/DPI combination or installed codec; verify audible playback on a real Windows audio device before claiming it works there.

Outputs are `Windows/artifacts/win-x64/`, `Windows/artifacts/review/`, and `Windows/artifacts/Luma-Player-windows-x64-review.zip` with its SHA-256 file. Running the script replaces these regenerable outputs. It does not create a GitHub tag or release, upload files, sign with a publisher certificate, or update an app feed.

The **Windows review build** Actions workflow can be started manually. Pushes to the `windows-port` branch also run it when Windows code, synthetic fixtures, the workflow or LICENSE changes. It uses a GitHub-hosted Windows x64 runner, retains review artifacts temporarily, and has no repository-write permissions. Download its ZIP and smoke-test evidence from the workflow run; these are review artifacts rather than a published Windows release.

## Install and try the review build

1. Obtain the review ZIP from a source and commit you trust. If downloaded as an Actions artifact, first extract GitHub's outer archive, then extract `Luma-Player-windows-x64-review.zip` into a normal folder.
2. Keep the extracted files together. Run **Luma Player.exe** from that folder; do not run it from inside a ZIP.
3. This preview is unsigned and has no established Windows publisher reputation. Windows may show a security warning. Review the provenance and local policy before choosing to run it; do not disable system protection globally.
4. Open your own local media or the included synthetic demo. Check `FORMAT_SUPPORT.md` for the original decoder paths and the machine-dependent Windows playback route.

The app includes a local media library, bookmarks and resume positions, external SRT/WebVTT subtitles and selectable supported Matroska plain-text caption tracks. For a successfully prepared MKV, use **More… → Audio tracks** to choose an AAC-LC track or turn audio off, and **More… → Embedded captions** to select readable UTF-8, WebVTT or ASS/SSA text. PGS packets have no Windows bitmap renderer yet. Verify these behaviors and audible playback on the target machine before using the preview with important state. Keep copies of your media.

## Formats and updates

Original portable tests cover integer WAVE, G.711, IMA/Microsoft ADPCM, 42 FLAC goldens, supported video-only AVI decoders, and 60 exact AIFF/AIFC/AU format/depth/channel vectors. AIFF/AIFC adds signed PCM 8/16/24/32-bit, `NONE`/`twos`/`sowt` variants and G.711; AU/SND adds signed big-endian PCM and G.711. Audio input is bounded to 256 MiB and 64 million decoded interleaved samples. Floating-point WAVE/AIFF/AU, CAF and arbitrary compressed AIFC/AU variants are not covered by these original decoders.

The new original Matroska/MP4 code retains compressed AVC/HEVC and AAC-LC bytes, coding order, timing and relative track starts. It supports one video track, selectable mono/stereo AAC-LC or audio off, and selectable embedded plain UTF-8/WebVTT/ASS/SSA text. It does **not** create new AVC, HEVC or AAC decoders: Windows media facilities still perform compressed decoding. The path is bounded to 512 MiB, 500,000 packets, known-size Clusters and supported timelines; reordered video needs a complete bounded CFR timeline. AAC is LC-only, 1,024 samples per frame and 7,350–64,000 Hz. HE/SBR/PS, other Matroska audio mappings, WebM, HDR/display metadata preservation and PGS rendering remain outside this path. ASS fonts, styling, positioning, animation, karaoke and vector drawing are not reproduced.

Structural/golden tests exercise the container, timeline and subtitle code. An independent native macOS check matched 360 remuxed AVC frames and retained a 250 ms AAC offset within 25 ms; that is container-output evidence, not Windows device-output proof. The earlier native Windows run verified the synthetic AVC MP4 video route, fullscreen and existing player behavior, while audible output remained unverified because it had no active audio device. New Windows Matroska/HEVC/audio integration still needs its own native evidence.

The Windows-specific [support inventory](CodecSupport.json) is separate from the Mac catalog and is not exhaustive or a parity percentage. Only the previously verified synthetic Windows AVC MP4 route is labeled `system`; installed codecs and portable reconstruction alone do not establish supported playback. Full VLC compatibility is unfinished. See [FORMAT_SUPPORT.md](FORMAT_SUPPORT.md) for per-format boundaries and evidence.

Windows uses a separate ECDSA-P256/SHA-256 signed-update configuration in `appsettings.windows.json`. Its initial feed and public key are blank, so there is no live Windows update service. It must never consume the Mac Ed25519 manifest or DMG feed. Review ZIPs are installed manually; quit the old app before replacing its extracted folder. Signed Windows update publishing, Authenticode signing and installer distribution need their own validation before a public Windows release.

Report the exact commit, Windows version, codec/container and reproducible behavior when providing feedback. Do not attach private media, saved library state or credentials.
