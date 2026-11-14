# Luma Player for Windows — review preview

This is a native WPF port targeting **Windows 11 x64**. It uses .NET 10 and Windows desktop composition, with a DWM Mica backdrop when supported and an opaque fallback. Other Windows versions and Windows on ARM are unverified. A successful portable core test run on macOS is not proof of Windows playback or appearance; inspect the Windows CI result and its screenshots before treating this port as tested.

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

The app includes a local media library, bookmarks and resume positions, plus SRT/WebVTT subtitles. Verify these behaviors and audible playback on the target machine before using the preview with important state. Keep copies of your media.

## Formats and updates

Original portable tests cover integer WAVE, G.711, IMA/Microsoft ADPCM, 42 FLAC goldens, and the supported video-only AVI decoders. Windows-native compressed playback requires installed codec support and actual verification on the target Windows machine. The macOS Matroska remuxer, embedded ASS/PGS subtitle paths and full VLC compatibility are not ported. See `FORMAT_SUPPORT.md` for limits.

Windows uses a separate ECDSA-P256/SHA-256 signed-update configuration in `appsettings.windows.json`. Its initial feed and public key are blank, so there is no live Windows update service. It must never consume the Mac Ed25519 manifest or DMG feed. Review ZIPs are installed manually; quit the old app before replacing its extracted folder. Signed Windows update publishing, Authenticode signing and installer distribution need their own validation before a public Windows release.

Report the exact commit, Windows version, codec/container and reproducible behavior when providing feedback. Do not attach private media, saved library state or credentials.
